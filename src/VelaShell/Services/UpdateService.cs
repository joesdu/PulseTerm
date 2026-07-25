using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Services.Update;

namespace VelaShell.Services;

/// <summary>
/// 便携式自更新服务:从 GitHub Releases 读取 CI 生成的 latest.json 清单,下载与当前
/// 平台匹配的压缩包到应用目录下的暂存目录,SHA-256 校验后由 <see cref="UpdateApplier" />
/// 解包,再交由本进程退出后才动手的外置换版进程完成换版与重启(见 <see cref="UpdateRunner" />)。
/// 应用装在哪里就更新哪里,不强制安装位置,也绝不触碰 %LocalAppData%/VelaShell 数据目录。
/// </summary>
public class UpdateService : IUpdateService
{
    private readonly IUpdateSource _source;
    private readonly Func<Task<string>> _channelProvider;
    private readonly UpdateApplier _applier;
    private readonly Action _shutdownForRestart;
    private UpdateManifest? _manifest;
    private UpdateAsset? _asset;
    private string? _downloadedArchivePath;

    /// <summary>
    /// 以 GitHub 仓库地址构造:更新源即该仓库的 Releases,无需自建服务器。
    /// <paramref name="channelProvider" /> 返回更新通道("preview" 时预发布版也纳入,
    /// 其余走稳定通道;beta 阶段没有正式版时稳定通道自动放宽到最新预发布)。
    /// </summary>
    public UpdateService(string repositoryUrl, Func<Task<string>>? channelProvider = null)
        : this(new GitHubReleaseSource(repositoryUrl), channelProvider)
    { }

    /// <summary>核心构造,测试可注入更新源、应用目录、版本号与"关闭应用"动作。</summary>
    public UpdateService(
        IUpdateSource source,
        Func<Task<string>>? channelProvider = null,
        string? applicationDirectory = null,
        string? currentVersionOverride = null,
        Action? shutdownForRestart = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _channelProvider = channelProvider ?? (static () => Task.FromResult("stable"));
        _applier = new(applicationDirectory
            ?? Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory);
        CurrentVersion = currentVersionOverride;
        _shutdownForRestart = shutdownForRestart ?? DefaultShutdown;
    }

    /// <summary>当前运行版本:程序集 InformationalVersion(含预发布后缀),读不到退回四段数字版。</summary>
    public string? CurrentVersion
    {
        get
        {
            if (field != null)
            {
                return field;
            }
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();
        }
    }

    /// <summary>最近一次检查发现的可用更新版本;无可用更新时为 null。</summary>
    public string? AvailableVersion => _manifest?.Version;

    /// <inheritdoc />
    public bool CanSelfUpdate =>
        UpdateManifest.CurrentRid() != null && _applier.IsApplicationDirectoryWritable();

    /// <summary>检查是否存在可用更新;平台不受支持或检查失败时返回 false。</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        _manifest = null;
        _asset = null;
        _downloadedArchivePath = null;
        if (UpdateManifest.CurrentRid() == null)
        {
            return false;
        }
        try
        {
            bool includePreRelease = string.Equals(
                await _channelProvider(), "preview", StringComparison.OrdinalIgnoreCase);
            UpdateManifest? manifest = await _source.GetLatestManifestAsync(includePreRelease);
            if (manifest == null
                || !UpdateVersion.TryParse(manifest.Version, out UpdateVersion latest)
                || !UpdateVersion.TryParse(CurrentVersion, out UpdateVersion current)
                || latest.CompareTo(current) <= 0
                || manifest.AssetForCurrentPlatform() is not { } asset)
            {
                return false;
            }
            _manifest = manifest;
            _asset = asset;
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载已检查到的更新包到暂存目录并做 SHA-256 校验,失败抛出异常。
    /// 支持断点续传:上次中断的半成品(<c>*.partial</c> 及其分段断点 <c>*.partial.meta</c>)
    /// 从断点继续;上次已完整下载且校验通过的包直接复用,不再耗费流量。
    /// 产物名带版本号与 RID,过期残留按名清理。校验优先采用下载过程中流式算出的
    /// 哈希(更新源返回),免去落盘后重读;源没给哈希时才读文件校验兜底。
    /// </summary>
    public async Task DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_manifest == null || _asset == null)
        {
            return;
        }
        _downloadedArchivePath = null;
        string staging = _applier.EnsureStagingDirectory();
        string archivePath = Path.Combine(staging, _asset.Name);
        string partialPath = archivePath + ".partial";
        string metaPath = partialPath + ".meta";
        foreach (string file in Directory.EnumerateFiles(staging))
        {
            if (file.Equals(archivePath, StringComparison.OrdinalIgnoreCase)
                || file.Equals(partialPath, StringComparison.OrdinalIgnoreCase)
                || file.Equals(metaPath, StringComparison.OrdinalIgnoreCase)
                // 换版日志归 UpdateApplier 管:删掉它,上一轮没收尾的备份就此失去归属,
                // 既没人回滚也没人清理——这正是老版本"升级失败后再也升不上去"的成因之一。
                || Path.GetFileName(file).StartsWith(UpdateApplier.JournalFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            TryDeleteFile(file);
        }
        // 子目录(payload/backup)同样归 UpdateApplier 管:换版前由 Stage 重建,
        // 启动时由 TryFinalizeStartup 收尾,下载流程一概不碰。
        if (File.Exists(archivePath))
        {
            if (await IsChecksumValidAsync(archivePath, _asset.Sha256))
            {
                _downloadedArchivePath = archivePath;
                progress?.Report(100);
                return;
            }
            File.Delete(archivePath);
        }
        string? streamedHash = await _source.DownloadAssetAsync(_manifest, _asset, archivePath, progress);
        bool valid = streamedHash != null
            ? streamedHash.Equals(_asset.Sha256, StringComparison.OrdinalIgnoreCase)
            : await IsChecksumValidAsync(archivePath, _asset.Sha256);
        if (!valid)
        {
            File.Delete(archivePath);
            throw new InvalidDataException(
                $"Update package checksum mismatch for {_asset.Name}; the corrupt download was discarded.");
        }
        _downloadedArchivePath = archivePath;
    }

    private static async Task<bool> IsChecksumValidAsync(string archivePath, string expectedSha256)
    {
        byte[] hash;
        await using (FileStream stream = File.OpenRead(archivePath))
        {
            hash = await SHA256.HashDataAsync(stream);
        }
        return Convert.ToHexStringLower(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理旧残留失败不阻断下载,留待下次再清。
        }
    }

    /// <inheritdoc />
    public string? LastUpdateError => _applier.ReadLastError();

    /// <inheritdoc />
    public void ClearUpdateError() => _applier.ClearLastError();

    /// <inheritdoc />
    public bool RepairUpdateState()
    {
        _manifest = null;
        _asset = null;
        _downloadedArchivePath = null;
        return _applier.TryRepair();
    }

    /// <summary>
    /// 解包 → 把换版交给外置进程 → 关闭当前应用。解包(<see cref="UpdateApplier.Stage" />)
    /// 不改动应用目录里的任何文件,因此磁盘不足、包损坏之类的失败都发生在"应用仍然完好"的状态下,
    /// 异常上抛后重试即可。换版本身由退出后才动手的外置进程完成(应用目录无文件被占用,
    /// 移动必然成功);非单文件发布或外置进程拉不起来时退回本进程原地换版。
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        if (_downloadedArchivePath == null || !File.Exists(_downloadedArchivePath))
        {
            throw new InvalidOperationException(
                "No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");
        }
        _applier.ClearLastError();
        _applier.Stage(_downloadedArchivePath);
        if (_applier.TryHandOffToExternalUpdater(Environment.ProcessId))
        {
            // 换版与重启都交给它了,本进程只管干净退出——退得越快,它等得越短。
            _shutdownForRestart();
            return;
        }
        _applier.SwapFromPayload();
        string exePath = Environment.ProcessPath
            ?? Path.Combine(_applier.ApplicationDirectory, "VelaShell" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!OperatingSystem.IsWindows())
        {
            // tar 包的可执行位在解包时已还原,这里兜底保证主程序可执行。
            File.SetUnixFileMode(exePath, File.GetUnixFileMode(exePath)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        Process.Start(new ProcessStartInfo(exePath)
        {
            WorkingDirectory = _applier.ApplicationDirectory,
            UseShellExecute = false,
            ArgumentList = { "--after-update" }
        });
        _shutdownForRestart();
    }

    /// <summary>默认的重启前关闭动作:走 Avalonia 生命周期正常退出,拿不到时硬退。</summary>
    private static void DefaultShutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
        else
        {
            Environment.Exit(0);
        }
    }
}

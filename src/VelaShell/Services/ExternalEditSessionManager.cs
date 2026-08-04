using System.Diagnostics;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>
/// 「使用默认编辑器打开」(WinSCP 式远程编辑):远程文件下载到本地 temp 的独立子目录,
/// 交给用户配置的编辑器;FileSystemWatcher 侦听保存(600ms 防抖)后自动上传回服务器。
/// 编辑器进程正常退出后延迟清理临时目录;启动即返回的单实例编辑器(如复用实例的
/// notepad++)无法据进程判断,保留监听,由应用退出时的 <see cref="CleanupAll" /> 统一清理。
/// </summary>
public static class ExternalEditSessionManager
{
    private static readonly List<ExternalEditSession> Sessions = [];

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "VelaShell", "remote-edit");

    /// <summary>把远程文件下载到本地独立临时目录并用指定编辑器打开,随后侦听本地保存并自动上传回服务器。</summary>
    public static async Task OpenAsync(
        ISftpService sftpService,
        Guid sessionId,
        string remotePath,
        string fileName,
        string editorCommand,
        Action<string>? onError,
        Func<string, string, Task>? uploadAsync = null,
        CancellationToken cancellationToken = default)
    {
        // 每次编辑独占一个子目录,避免同名文件互相覆盖。
        string directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
        if (!LocalPathSafety.TryResolveDestination(directory, fileName, out string localPath))
        {
            throw new InvalidOperationException(Strings.Get("KeySvc_InvalidName"));
        }
        Directory.CreateDirectory(directory);
        await sftpService.DownloadFileAsync(sessionId, remotePath, localPath, null, cancellationToken: cancellationToken);
        var session = new ExternalEditSession(sftpService, sessionId, remotePath, localPath, onError, uploadAsync);
        lock (Sessions)
        {
            Sessions.Add(session);
        }
        session.LaunchEditor(editorCommand);
    }

    /// <summary>应用退出时调用:停止所有监听并删除整个 remote-edit 临时树。</summary>
    public static void CleanupAll()
    {
        lock (Sessions)
        {
            foreach (ExternalEditSession session in Sessions)
            {
                session.Dispose();
            }
            Sessions.Clear();
        }
        TryDeleteDirectory(TempRoot);
    }

    internal static void Remove(ExternalEditSession session)
    {
        lock (Sessions)
        {
            Sessions.Remove(session);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 临时文件清理是尽力而为:被占用就留给下次启动/系统清理。
        }
    }
}

internal sealed class ExternalEditSession : IDisposable
{
    private readonly string _localPath;
    private readonly Action<string>? _onError;
    private readonly string _remotePath;
    private readonly Guid _sessionId;
    private readonly ISftpService _sftpService;
    private readonly Func<string, string, Task>? _uploadAsync;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly FileSystemWatcher _watcher;
    private Timer? _debounce;
    private bool _disposed;
    private DateTime _launchedAt;

    public ExternalEditSession(
        ISftpService sftpService,
        Guid sessionId,
        string remotePath,
        string localPath,
        Action<string>? onError,
        Func<string, string, Task>? uploadAsync)
    {
        _sftpService = sftpService;
        _sessionId = sessionId;
        _remotePath = remotePath;
        _localPath = localPath;
        _onError = onError;
        _uploadAsync = uploadAsync;
        _watcher = new(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        // 编辑器保存往往触发多个事件(写入+改名+属性),统一走防抖。
        _watcher.Changed += (_, _) => ScheduleUpload();
        _watcher.Created += (_, _) => ScheduleUpload();
        _watcher.Renamed += (_, _) => ScheduleUpload();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _debounce?.Dispose();
        _watcher.Dispose();
        ExternalEditSessionManager.TryDeleteDirectory(Path.GetDirectoryName(_localPath)!);
    }

    public void LaunchEditor(string editorCommand)
    {
        _launchedAt = DateTime.UtcNow;
        ProcessStartInfo startInfo = BuildEditorStartInfo(editorCommand.Trim().Trim('"'), _localPath);
        var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // 单实例编辑器的引导进程会立刻退出 —— 此时不能清理,保留监听到应用退出。
            if (DateTime.UtcNow - _launchedAt < TimeSpan.FromSeconds(3))
            {
                return;
            }

            // 正常退出:留 1.5s 让最后一次保存的事件与上传落地,再清理临时目录。
            _ = Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ =>
            {
                ExternalEditSessionManager.Remove(this);
                Dispose();
            });
        };
    }

    /// <summary>
    /// 按平台组装编辑器启动方式:
    /// Windows — ShellExecute(支持 exe 完整路径、PATH 命令名与 App Paths 注册名,如 notepad++);
    /// macOS — 配置的不是现存可执行文件时按应用名/.app 包走 `open -a`(GUI 应用的正规启动方式);
    /// Linux — 直接 exec,命令名经 PATH 解析(如 gedit、kate、code)。
    /// </summary>
    private static ProcessStartInfo BuildEditorStartInfo(string editor, string filePath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsMacOS() && !File.Exists(editor))
        {
            startInfo = new() { FileName = "open", UseShellExecute = false };
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add(editor);
            startInfo.ArgumentList.Add(filePath);
            return startInfo;
        }
        startInfo = new()
        {
            FileName = editor,
            UseShellExecute = OperatingSystem.IsWindows(),
            // 显式指定工作目录 = 被编辑文件所在的临时目录。不指定的话子进程继承 VelaShell 的
            // 工作目录(应用安装目录),编辑器的相对路径操作、swap/备份文件就会落到那儿 ——
            // 商店版的安装目录还是只读的,直接写失败(#120)。
            WorkingDirectory = Path.GetDirectoryName(filePath)
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        startInfo.ArgumentList.Add(filePath);
        return startInfo;
    }

    private void ScheduleUpload()
    {
        if (_disposed)
        {
            return;
        }
        _debounce?.Dispose();
        // ReSharper disable once RedundantAssignment
        // ReSharper disable once AllUnderscoreLocalParameterName
        _debounce = new(_ => _ = UploadAsync(), null, TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    private async Task UploadAsync()
    {
        if (_disposed || !File.Exists(_localPath))
        {
            return;
        }
        await _uploadGate.WaitAsync();
        try
        {
            // 编辑器保存后可能短暂持锁:先等到文件可读再上传,保证传输浮窗里只出现一行。
            await WaitUntilReadableAsync();
            if (_uploadAsync is not null)
            {
                await _uploadAsync(_localPath, _remotePath);
            }
            else
            {
                await _sftpService.UploadFileAsync(_sessionId, _localPath, _remotePath);
            }
        }
        catch (Exception ex)
        {
            _onError?.Invoke(Strings.Format("Svc_RemoteUpdateFailed", Path.GetFileName(_remotePath), ex.Message));
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private async Task WaitUntilReadableAsync()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await using FileStream _ = File.Open(_localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(300);
            }
        }
    }
}

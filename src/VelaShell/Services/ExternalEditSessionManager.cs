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

    /// <summary>
    /// 应用退出时调用:先把各会话未落地的改动传完,再删 remote-edit 临时树。
    /// </summary>
    /// <remarks>
    /// 退出路径和编辑器退出路径走同一套收尾规则,否则会出现"关编辑器安全、关应用丢改动"
    /// 这种只有特定顺序才复现的丢数据。传不完的那些会保留自己的临时子目录并提示路径,
    /// 所以这里<b>不再无条件删整棵树</b>。
    /// </remarks>
    /// <summary>
    /// 应用退出时的同步入口(<c>desktop.Exit</c> 是同步事件)。
    /// </summary>
    /// <remarks>
    /// <b>收尾在线程池上跑,这里只限时等它。</b>上传回调可能要回 UI 线程(传输浮窗),
    /// 而退出事件本身就在 UI 线程上 —— 直接 <c>GetResult()</c> 会把两边锁死。
    /// 等不到就当作没落地:草稿保留、提示路径,退出照常继续,绝不让一条烂链路把关闭卡住。
    /// </remarks>
    public static void CleanupAll()
    {
        // 5 秒是"退出别卡住"和"局域网上一次小文件上传"之间的折中;超时不丢东西,只是留草稿。
        var budget = TimeSpan.FromSeconds(5);
        try
        {
            Task.Run(() => CleanupAllAsync(budget)).Wait(budget + TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 收尾本身不该阻止进程退出。
        }
    }

    /// <param name="timeout">等待全部收尾的总时限;退出流程不能被一条烂链路无限拖住。</param>
    public static async Task CleanupAllAsync(TimeSpan timeout)
    {
        ExternalEditSession[] pending;
        lock (Sessions)
        {
            pending = [.. Sessions];
            Sessions.Clear();
        }
        try
        {
            await Task.WhenAll(pending.Select(s => s.ShutdownAsync(timeout))).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // 超时的那些会在下面的 Dispose 里保留本地副本并提示路径。
        }
        foreach (ExternalEditSession session in pending)
        {
            session.Dispose();
        }
        // 只清空壳:还留着草稿的子目录由 Dispose 决定保不保,这里不能一把全删。
        TryDeleteEmptyTree(TempRoot);
    }

    /// <summary>删掉临时树里的空目录,留下仍有草稿的那些。</summary>
    private static void TryDeleteEmptyTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为:清不掉留给下次启动。
        }
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

    /// <summary>已进入收尾:不再接受新的文件变化(但正在跑的收尾还要把最后一次改动传完)。</summary>
    private bool _closing;

    /// <summary>有一次改动还没落到远端(防抖还没到点,或者刚攒下)。0/1,用 Interlocked 读写。</summary>
    private int _pendingSave;

    /// <summary>最近一次上传是否失败(失败就保留本地副本,不删临时目录)。</summary>
    private bool _lastUploadFailed;

    /// <summary>
    /// 编辑器退出后,最多再等这么久让末次保存落到远端。
    /// </summary>
    /// <remarks>
    /// 这里以前是<b>无条件等 1.5 秒然后删目录</b>。1.5 秒要同时装下:600ms 防抖 +
    /// 等文件解锁(最多 3 次 × 300ms)+ 一次真实网络上传 —— 后者在慢链路或大文件上
    /// 根本不是 1.5 秒能完成的事。超时的后果不是"慢一点",而是把用户刚存的内容连同
    /// 本地副本一起删掉,远端还是旧的,且不报错。
    /// <para>
    /// 现在改成:等真实的上传结果;等不到就<b>保留本地副本</b>并告诉用户它在哪儿。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ShutdownUploadTimeout = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// 是否有改动还没落到远端(回归用例用它等 watcher 真的看见了那次写入,
    /// 而不是靠固定 Sleep 去赌)。
    /// </summary>
    internal bool HasPendingUploadForTest => Volatile.Read(ref _pendingSave) == 1;

    /// <summary>拆除会话。上传没能落地时<b>保留</b>本地副本,不删临时目录。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _closing = true;
        _watcher.EnableRaisingEvents = false;
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        _watcher.Dispose();
        if (_lastUploadFailed || Volatile.Read(ref _pendingSave) == 1)
        {
            // 远端没拿到这份内容 —— 本地副本是它唯一的存身之处,删了就真没了。
            _onError?.Invoke(Strings.Format("Svc_RemoteEditDraftKept", Path.GetFileName(_remotePath), _localPath));
            return;
        }
        ExternalEditSessionManager.TryDeleteDirectory(Path.GetDirectoryName(_localPath)!);
    }

    /// <summary>
    /// 收尾:停收新变化 → 把还没传的那一次传完 → 等在途上传结束。
    /// </summary>
    /// <returns>全部内容都已落到远端时为 <see langword="true" />。</returns>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        _closing = true;
        _watcher.EnableRaisingEvents = false;
        // 防抖还没到点的那一次不能就这么丢掉 —— 那正是"改完立刻关编辑器"的常见情形。
        // 计时器停掉,但 _pendingSave 保持置位,下面的 UploadAsync 会把它兑现。
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        try
        {
            // UploadAsync 要先拿上传闸,所以 await 它同时也等掉了正在跑的那一次上传。
            await UploadAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        return !_lastUploadFailed && Volatile.Read(ref _pendingSave) == 0;
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

            // 正常退出:等末次保存真的落到远端(而不是数 1.5 秒然后删目录)。
            _ = FinishAsync();
        };
    }

    private async Task FinishAsync()
    {
        await ShutdownAsync(ShutdownUploadTimeout).ConfigureAwait(false);
        ExternalEditSessionManager.Remove(this);
        // 落没落地由 Dispose 自己按 _lastUploadFailed / _pendingSave 判断,
        // 没落地就保留本地副本并提示路径。
        Dispose();
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
        if (_disposed || _closing)
        {
            return;
        }
        // 先记账再排防抖:计时器可能被收尾拆掉,而"有一次改动还没传"这件事必须留下来,
        // 否则改完立刻关编辑器就会把那次保存丢在防抖窗口里。
        Interlocked.Exchange(ref _pendingSave, 1);
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        // ReSharper disable once RedundantAssignment
        // ReSharper disable once AllUnderscoreLocalParameterName
        _debounce = new(_ => _ = UploadAsync(), null, TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 把待传的改动送上去。<b>拿到闸就说明在途的那一次已经结束</b>,所以 await 本方法
    /// 同时也是"等上传完成"。传完之后再看一眼:期间又存了就再传一轮,不留尾巴。
    /// </summary>
    private async Task UploadAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _uploadGate.WaitAsync();
        try
        {
            while (Interlocked.Exchange(ref _pendingSave, 0) == 1)
            {
                if (!File.Exists(_localPath))
                {
                    return; // 文件没了(编辑器改名保存后又删了?)—— 没有可传的东西。
                }
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
                    _lastUploadFailed = false;
                }
                catch (Exception ex)
                {
                    // 失败要留痕:本地副本是这份改动唯一的存身之处,Dispose 据此决定不删目录。
                    _lastUploadFailed = true;
                    Interlocked.Exchange(ref _pendingSave, 1);
                    _onError?.Invoke(Strings.Format("Svc_RemoteUpdateFailed", Path.GetFileName(_remotePath), ex.Message));
                    return;
                }
            }
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

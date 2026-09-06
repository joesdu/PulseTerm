using System.Threading.Channels;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Services;

/// <summary>
/// 会话日志(设置 → 常规 → 数据与存储):开启后把每个会话的原始终端输出追加写入
/// ~/.velashell/logs/session-*.log(含 ANSI 序列,同 script(1) 的产物);
/// 启动时按“日志保留天数”清理过期文件。
/// </summary>
public static class SessionLogService
{
    /// <summary>会话日志目录:~/.velashell/logs。</summary>
    public static string LogDirectory => Path.Combine(new VelaShellStoragePaths().RootDirectory, "logs");

    /// <summary>为一个会话开启日志;返回 null 表示无法创建日志文件(不影响会话)。</summary>
    /// <param name="sessionName">会话显示名(用于文件名)。</param>
    /// <param name="onStopped">日志中途因故停止时的回调(可直接展示的原因)。</param>
    public static SessionLogWriter? CreateWriter(string sessionName, Action<string>? onStopped = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string safeName = string.Concat(sessionName.Select(c =>
                char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            if (safeName.Length > 40)
            {
                safeName = safeName[..40];
            }
            string path = Path.Combine(LogDirectory,
                $"session-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            return new(path, onStopped);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除超过保留天数的 session-*.log(启动时后台执行,失败静默)。</summary>
    public static void CleanupExpired(int retentionDays)
    {
        if (retentionDays < 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return;
                }
                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string file in Directory.EnumerateFiles(LogDirectory, "session-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // 单个文件占用/无权限,跳过。
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动。
            }
        });
    }
}

/// <summary>
/// 单个会话的追加式日志写入器:入队即返回,落盘由后台单写者完成。
/// </summary>
/// <remarks>
/// <b><see cref="Write" /> 跑在终端读线程上</b>(桥的 <c>DataReceived</c>)。以前它直接
/// <c>FileStream.Write</c> —— 本地盘上通常很快,可日志目录完全可能在网络盘、被同步软件
/// 扫描、或者盘正忙,那一下阻塞的是**收终端输出的那条线程**,表现为整个会话卡住。
/// 现在写入排进一条有界队列,读线程只做一次入队。
/// <para>
/// 队列有上限:满了就停止记录并报告原因,而不是无声地攒在内存里 —— 日志是辅助功能,
/// 把用户正在用的终端拖垮不是它该付的代价。
/// </para>
/// </remarks>
public sealed class SessionLogWriter : IDisposable
{
    /// <summary>待写队列的字节上限。</summary>
    private const long MaxQueuedBytes = 8L * 1024 * 1024;

    /// <summary>收尾时最多等这么久把队列写完。</summary>
    private static readonly TimeSpan FinalDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly Task _writer;
    private readonly Action<string>? _onStopped;
    private FileStream? _stream;
    private long _queuedBytes;
    private int _stopped;

    /// <summary>以追加模式打开日志文件,准备写入会话原始输出。</summary>
    /// <param name="path">日志文件的完整路径。</param>
    /// <param name="onStopped">日志因故停止时的回调(可直接展示的原因)。</param>
    internal SessionLogWriter(string path, Action<string>? onStopped = null)
    {
        _onStopped = onStopped;
        _stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = Task.Run(DrainAsync);
    }

    /// <summary>当前排队待写的字节数(回归用例读它)。</summary>
    internal long QueuedBytesForTest => Interlocked.Read(ref _queuedBytes);

    /// <summary>把队列写完、刷新并关闭底层日志文件流。</summary>
    public void Dispose()
    {
        if (!_queue.Writer.TryComplete())
        {
            return; // 已经关过了。
        }
        try
        {
            _writer.Wait(FinalDrainTimeout);
        }
        catch (AggregateException)
        {
            // 收尾失败不该阻止会话关闭。
        }
        CloseStream();
    }

    /// <summary>把一段原始终端字节交给后台写入;不做 I/O,不阻塞读线程。</summary>
    /// <param name="data">待写入的原始字节。</param>
    public void Write(byte[] data)
    {
        if (data.Length == 0 || Volatile.Read(ref _stopped) == 1)
        {
            return;
        }
        if (Interlocked.Add(ref _queuedBytes, data.Length) > MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -data.Length);
            Stop(Strings.Format("SessionLog_Backlog", MaxQueuedBytes / (1024 * 1024)));
            return;
        }
        if (!_queue.Writer.TryWrite(data))
        {
            Interlocked.Add(ref _queuedBytes, -data.Length);
            Stop(Strings.Format("SessionLog_Backlog", MaxQueuedBytes / (1024 * 1024)));
        }
    }

    private async Task DrainAsync()
    {
        await foreach (byte[] chunk in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_stream is { } stream)
                {
                    await stream.WriteAsync(chunk).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 磁盘满/文件被删等:停止记录,不影响会话。
                Stop(ex.Message);
                CloseStream();
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -chunk.Length);
            }
        }
    }

    private void Stop(string reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }
        _onStopped?.Invoke(reason);
    }

    private void CloseStream()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }
        try
        {
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 关闭途中的失败无处可报,也无关紧要。
        }
        stream.Dispose();
    }
}

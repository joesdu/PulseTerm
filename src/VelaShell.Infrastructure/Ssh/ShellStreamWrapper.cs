using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 将 Tmds.Ssh 的 <see cref="RemoteProcess" /> 适配到 <see cref="IShellStreamWrapper" />。
/// </summary>
public class ShellStreamWrapper(RemoteProcess process) : IShellStreamWrapper
{
    private readonly RemoteProcess _process = process ?? throw new ArgumentNullException(nameof(process));
    private Stream? _outputStream;
    private volatile bool _disposed;
    private volatile bool _channelClosed;
    private bool _readEof;

    /// <summary>读端发出 EOF 前始终保持可读(Stream ReadAsync 返回 0 表示 EOF)。</summary>
    public bool CanRead => !_disposed && !_readEof;

    /// <summary>RemoteProcess 存活且通道未断时可写入。</summary>
    public bool CanWrite => !_disposed && !_channelClosed;

    /// <summary>当前是否有数据可读而不阻塞(对 Stream 式 IO 无意义)。</summary>
    public bool DataAvailable => false;

    /// <summary>不被调用。</summary>
    public string? Expect(string regex, TimeSpan timeout) =>
        throw new NotSupportedException("Expect is not supported. Use ReadAsync instead.");

    /// <summary>不被调用。</summary>
    public void WriteLine(string line) =>
        throw new NotSupportedException("WriteLine is not supported. Use WriteAsync instead.");

    /// <summary>从远程进程标准输出读原始字节。EOF 时返回 0 并设置 _readEof。</summary>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _readEof) return 0;
        try
        {
            _outputStream ??= _process.ReadAsStream(StderrHandler.Ignore);
            int bytesRead = await _outputStream
                .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0) _readEof = true;
            return bytesRead;
        }
        catch (SshChannelClosedException) { _readEof = true; return 0; }
        catch (ObjectDisposedException) { _readEof = true; return 0; }
        catch (IOException) { _readEof = true; return 0; }
        catch (OperationCanceledException) { _readEof = true; return 0; }
    }

    /// <summary>
    /// 向远程进程标准输入写入原始字节。通道已断 / 已释放时静默丢弃并把 <see cref="CanWrite" />
    /// 置为 false —— 与读端 EOF 返回 0 对称,也与另一个实现
    /// <c>PluginTerminalShellStream.WriteAsync</c> 的约定一致:「会话已断这件事由读循环收到 EOF
    /// 去改标签状态,写端不必再抛一遍」。
    /// <para>
    /// 旧实现在这里把 <c>SshChannelClosedException</c> 转成新建的 <c>ObjectDisposedException</c>
    /// 再抛,而唯一的消费者(桥的写循环)只是把它 catch 掉丢弃 —— 纯粹拿异常做控制流,
    /// 一次断线要多制造两条首次机会异常;更糟的是它顺手把 <c>_disposed</c> 置了位,
    /// 使随后真正的 <see cref="Dispose" /> 直接短路返回,<c>RemoteProcess</c> 再也不会被确定性释放。
    /// </para>
    /// </summary>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _channelClosed)
        {
            return;
        }
        try
        {
            await _process.WriteAsync(
                new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshChannelClosedException
                                       or SshConnectionClosedException
                                       or ObjectDisposedException
                                       or IOException)
        {
            // 通道已断:后续写入一律短路,不再逐次去撞库内异常(断线时键盘输入仍在入队)。
            _channelClosed = true;
        }
    }

    /// <summary>空操作(Tmds.Ssh RemoteProcess 通过异步写入自动保证送达)。</summary>
    public void Flush() { }

    /// <summary>发送 window-change 请求以调整远程终端尺寸。</summary>
    public void Resize(int columns, int rows)
    {
        // 通道已断时不再尝试:SetTerminalSize 会在库内抛,而这里除了吞掉它什么也做不了。
        if (_disposed || _channelClosed || columns <= 0 || rows <= 0) return;
        try
        {
            _process.SetTerminalSize(columns, rows);
        }
        catch (Exception ex) when (ex is SshChannelClosedException
                                      or SshConnectionClosedException
                                      or ObjectDisposedException)
        {
            // 只有"通道确实没了"才短路后续调用;其它原因(参数、瞬时状态)照旧吞掉,不永久禁写。
            _channelClosed = true;
        }
        catch
        {
            // 调整尺寸失败不影响会话本身。
        }
    }

    /// <summary>释放底层 RemoteProcess。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}

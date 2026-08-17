using System.Net.Sockets;
using System.Threading.Channels;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Telnet;

/// <summary>
/// 一条 Telnet 会话:TCP 之上的 <see cref="IProtocolTerminalSession" />。
/// <para>
/// 结构是一条**单出口管道**:后台泵读套接字 → 交给 <see cref="TelnetNegotiator" /> 消化协议字节 →
/// 净数据写进通道;<see cref="ReadAsync" /> 只从通道取。本地回显也写同一个通道 ——
/// 这样"没有远端回显时用户仍看得见自己敲的字"不必等下一次网络数据到达
/// (若把回显攒在旁路缓冲里,阻塞在套接字读上的读取方根本不会醒)。
/// </para>
/// <para>
/// 写侧串行化(<see cref="_writeGate" />):用户按键、NAWS 子协商、协商应答都是写,
/// 交织会把一个帧撕成两半 —— 对端看到的就是半个 IAC 序列。
/// </para>
/// </summary>
internal sealed class TelnetSession : IProtocolTerminalSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TelnetNegotiator _negotiator;
    private readonly TelnetConfig _config;
    private readonly IPluginLogger _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private ReadOnlyMemory<byte> _carry;
    private Task? _pump;
    private volatile bool _disposed;

    private TelnetSession(TcpClient client, NetworkStream stream, TelnetConfig config, IPluginLogger log)
    {
        _client = client;
        _stream = stream;
        _config = config;
        _log = log;
        _negotiator = new(config);
    }

    /// <summary>建立连接并发出第一批协商;端点不可达时抛 <see cref="ProtocolConnectionException" />。</summary>
    /// <param name="host">主机名或 IP。</param>
    /// <param name="port">端口。</param>
    /// <param name="config">协商策略。</param>
    /// <param name="options">终端初始参数。</param>
    /// <param name="log">插件日志。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已就绪的会话。</returns>
    public static async Task<TelnetSession> ConnectAsync(
        string host,
        int port,
        TelnetConfig config,
        ProtocolTerminalOptions options,
        IPluginLogger log,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient
        {
            // Nagle 会把单个按键攒到 40ms 后再发,交互式终端上是肉眼可见的迟滞。
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new ProtocolConnectionException($"{host}:{port} — {ex.Message}", ex);
        }
        NetworkStream stream = client.GetStream();
        var session = new TelnetSession(client, stream, config, log);
        byte[] hello = session._negotiator.BuildInitialRequests(options.Columns, options.Rows);
        try
        {
            await stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new ProtocolConnectionException($"{host}:{port} — {ex.Message}", ex);
        }
        session._pump = Task.Run(() => session.PumpAsync(), CancellationToken.None);
        return session;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        if (_carry.IsEmpty)
        {
            try
            {
                byte[] chunk = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _carry = chunk;
            }
            catch (ChannelClosedException)
            {
                return 0; // 泵已收尾 = 会话结束。
            }
        }
        int copied = Math.Min(buffer.Length, _carry.Length);
        _carry[..copied].CopyTo(buffer);
        _carry = _carry[copied..];
        return copied;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_disposed || data.IsEmpty)
        {
            return;
        }
        byte[] encoded = _negotiator.EncodeOutbound(data.Span);
        await SendAsync(encoded, cancellationToken).ConfigureAwait(false);

        // 本地回显:对端没接管回显时,用户敲的字得由我们显示出来,否则就是"打字看不见"。
        // 回显的是**用户原始字节**,不是编码后的线上字节(那里可能刚补过 LF、双写过 0xFF)。
        if (ShouldEchoLocally())
        {
            _output.Writer.TryWrite(data.ToArray());
        }
    }

    /// <inheritdoc />
    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        if (_negotiator.BuildWindowSize(columns, rows) is { Length: > 0 } frame)
        {
            await SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            _client.Close(); // 唤醒阻塞在读上的泵。
        }
        catch (Exception ex)
        {
            _log.Debug($"Closing telnet socket threw: {ex.Message}");
        }
        if (_pump is { } pump)
        {
            // 泵只做套接字读与通道写,取消后必然很快返回;仍加超时兜底,
            // 绝不让一次关标签卡在这里(关闭路径由宿主在后台调,但也不该无限等)。
            // 用 WaitAsync 而不是 WhenAny(Task.Delay):后者即便泵先返回,那个定时器
            // 也会一直跑到点(CA2027);终端会话可能开开关关很多次,定时器就攒起来了。
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 泵卡在某处收不回来:关闭路径不为它停留,后面的句柄照常释放。
                _log.Debug("Telnet read pump did not finish within the shutdown timeout.");
            }
        }
        _output.Writer.TryComplete();
        _stream.Dispose();
        _client.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>本地回显策略:auto 时只在对端未启用 ECHO 的情况下回显。</summary>
    private bool ShouldEchoLocally() => _config.LocalEcho switch
    {
        TelnetLocalEcho.On => true,
        TelnetLocalEcho.Off => false,
        _ => !_negotiator.RemoteEcho
    };

    private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
        {
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, linked.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // 连接已断:读侧的 EOF 才是权威结论,这里只丢弃这一次写。
            _log.Debug($"Telnet write failed: {ex.Message}");
        }
        finally
        {
            // Dispose 与写并发时信号量可能已释放,吞掉这一次。
            try
            {
                _writeGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// 读泵:套接字 → 协商状态机 → 净数据进通道。任何异常都归一化成"结束"
    /// (完成通道即 EOF),掉线不该以异常形式冒到宿主的读循环里。
    /// </summary>
    private async Task PumpAsync()
    {
        byte[] buffer = new byte[16384];
        List<byte> data = [];
        List<byte> responses = [];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break; // 对端关闭。
                }
                data.Clear();
                responses.Clear();
                _negotiator.Process(buffer.AsSpan(0, read), data, responses);
                if (responses.Count > 0)
                {
                    await SendAsync([.. responses], _lifetime.Token).ConfigureAwait(false);
                }
                if (data.Count > 0)
                {
                    _output.Writer.TryWrite([.. data]);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
                                       or ObjectDisposedException or SocketException)
        {
            // 正常收尾路径:关标签、对端 RST、进程退出。
        }
        catch (Exception ex)
        {
            _log.Warn($"Telnet read pump stopped: {ex.Message}");
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }
}

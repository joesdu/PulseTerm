using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 宿主自建的**计量**端口转发:自己持有监听端、逐连接搬运字节,因而能给出连接数与
/// 累计流量。底层 SSH 库(Tmds.Ssh)把转发的数据面整个做在内部,不暴露任何计数,
/// 隧道面板要显示"3 连接 · 1.4 MB"就只能由这里来数。
/// <para>
/// 三种转发的装配方式不同,但都收敛到同一个"监听 → 建出站流 → 双向搬运"的循环:
/// </para>
/// <list type="bullet">
///   <item>本地转发:本机监听,出站是到远端目标的 <c>direct-tcpip</c> 通道。</item>
///   <item>动态转发:本机监听 + SOCKS5 握手,目标由客户端在握手里给出。</item>
///   <item>
///     远程转发:监听端在服务器上,只能由库来开;于是让库把流量转发到本机的一个
///     临时计量监听,再由这里接力到真正的本地目标 —— 多一次环回拷贝换来同样的统计。
///   </item>
/// </list>
/// </summary>
internal sealed class MeteredPortForwardHandle : IPortForwardHandle
{
    /// <summary>搬运缓冲区大小:32 KiB,与 SSH 通道窗口的量级相称,又不至于让每条连接都占住大块内存。</summary>
    private const int BufferSize = 32 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;

    /// <summary>为一条入站连接建立出站流;动态转发要在入站流上先跑完 SOCKS5 握手才知道目标。</summary>
    private readonly Func<Stream, CancellationToken, Task<Stream>> _openOutbound;

    /// <summary>远程转发时由库持有的那半边(服务器侧监听);本地/动态转发为 null。</summary>
    private readonly IDisposable? _upstream;

    private readonly CancellationTokenRegistration _upstreamStopped;
    private int _activeConnections;
    private long _bytesTransferred;
    private volatile bool _stopped;
    private int _totalConnections;
    private volatile bool _userStopped;

    private MeteredPortForwardHandle(
        TcpListener listener,
        Func<Stream, CancellationToken, Task<Stream>> openOutbound,
        IDisposable? upstream = null,
        Action? throwIfUpstreamStopped = null,
        CancellationToken upstreamStopped = default)
    {
        _listener = listener;
        _openOutbound = openOutbound;
        _upstream = upstream;
        _upstreamStopped = throwIfUpstreamStopped is null
                               ? default
                               : upstreamStopped.Register(() => OnUpstreamStopped(throwIfUpstreamStopped));
        _ = AcceptLoopAsync();
    }

    /// <inheritdoc />
    public bool IsStarted => !_stopped;

    /// <inheritdoc />
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);

    /// <inheritdoc />
    public int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <inheritdoc />
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <inheritdoc />
    public event Action<Exception>? ChannelError;

    /// <inheritdoc />
    public void Stop()
    {
        if (_userStopped)
        {
            return;
        }
        _userStopped = true;
        _stopped = true;
        _upstreamStopped.Dispose();
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _upstream?.Dispose(); } catch { }
        _cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    /// 用给定的监听端与出站工厂装配一条计量转发。这一层("监听 → 建出站流 → 计量搬运")
    /// 与 SSH 无关,单独开出来,让搬运、计数与半关闭这条主路径不必架一台真服务器才验证得了。
    /// </summary>
    internal static MeteredPortForwardHandle CreateRelay(
        TcpListener listener,
        Func<Stream, CancellationToken, Task<Stream>> openOutbound) => new(listener, openOutbound);

    /// <summary>
    /// 建立并启动一条计量转发。监听绑定失败(端口被占用等)时抛出且不留下半挂的监听。
    /// </summary>
    public static async Task<MeteredPortForwardHandle> CreateAsync(
        SshClient client, PortForwardRequest request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case PortForwardKind.Local:
            {
                string targetHost = request.TargetHost!;
                var targetPort = (int)request.TargetPort!;
                TcpListener listener = Listen(request.BoundHost, (int)request.BoundPort);
                return new(listener,
                    async (_, ct) => await client.OpenTcpConnectionAsync(targetHost, targetPort, ct).ConfigureAwait(false));
            }
            case PortForwardKind.Dynamic:
            {
                TcpListener listener = Listen(request.BoundHost, (int)request.BoundPort);
                return new(listener, (inbound, ct) => OpenSocksTargetAsync(client, inbound, ct));
            }
            case PortForwardKind.Remote:
            {
                // 服务器侧的监听只有库能开,所以让它把流量交到本机的一个临时端口上,
                // 由本类接力到真正的目标 —— 这样远程转发也走同一条计量路径。
                IPEndPoint target = ResolveOutboundEndPoint(request.TargetHost!, (int)request.TargetPort!);
                TcpListener meter = Listen("127.0.0.1", 0);
                var meterPort = ((IPEndPoint)meter.LocalEndpoint).Port;
                try
                {
                    RemoteForward forward = await client.StartRemoteForwardAsync(
                        new RemoteIPListenEndPoint(request.BoundHost, (int)request.BoundPort),
                        new IPEndPoint(IPAddress.Loopback, meterPort),
                        cancellationToken).ConfigureAwait(false);
                    return new(meter,
                        async (_, ct) => await ConnectLocalAsync(target, ct).ConfigureAwait(false),
                        forward, forward.ThrowIfStopped, forward.Stopped);
                }
                catch
                {
                    try { meter.Stop(); } catch { }
                    throw;
                }
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, @"Unknown port forward kind.");
        }
    }

    /// <summary>在指定地址上开始监听;绑定失败时不留下半挂的监听。</summary>
    private static TcpListener Listen(string host, int port)
    {
        var listener = new TcpListener(ParseBindAddress(host), port);
        try
        {
            listener.Start();
        }
        catch
        {
            try { listener.Stop(); } catch { }
            throw;
        }
        return listener;
    }

    /// <summary>把配置里的监听主机翻译成绑定地址(<c>0.0.0.0</c> / <c>*</c> 表示所有接口)。</summary>
    private static IPAddress ParseBindAddress(string host) =>
        host is "0.0.0.0" or "*" ? IPAddress.Any :
        host == "::" ? IPAddress.IPv6Any :
        host is "localhost" or "127.0.0.1" ? IPAddress.Loopback :
        IPAddress.Parse(host);

    /// <summary>
    /// 把远程转发的本地目标翻译成可连接的端点。<c>0.0.0.0</c> 作为**目标**没有意义
    /// (它只是"监听所有接口"的写法),按用户的本意落到环回。
    /// </summary>
    private static IPEndPoint ResolveOutboundEndPoint(string host, int port)
    {
        IPAddress address = ParseBindAddress(host);
        if (Equals(address, IPAddress.Any))
        {
            address = IPAddress.Loopback;
        }
        else if (Equals(address, IPAddress.IPv6Any))
        {
            address = IPAddress.IPv6Loopback;
        }
        return new(address, port);
    }

    /// <summary>远程转发的出站侧:从本机连到用户指定的本地目标。</summary>
    private static async Task<Stream> ConnectLocalAsync(IPEndPoint target, CancellationToken cancellationToken)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, true);
    }

    /// <summary>动态转发的出站侧:先跑完 SOCKS5 握手拿到目标,再开 SSH 通道并回应答。</summary>
    private static async Task<Stream> OpenSocksTargetAsync(SshClient client, Stream inbound, CancellationToken cancellationToken)
    {
        (string host, int port) = await Socks5Negotiation.AcceptRequestAsync(inbound, cancellationToken).ConfigureAwait(false);
        Stream outbound;
        try
        {
            outbound = await client.OpenTcpConnectionAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 客户端在等应答,不回它就只能干等到超时;先如实告知失败原因再把异常抛给上报路径。
            try { await Socks5Negotiation.WriteReplyAsync(inbound, Socks5Negotiation.ReplyCodeFor(ex), cancellationToken).ConfigureAwait(false); }
            catch
            {
                // 客户端已走,应答无处可送。
            }
            throw;
        }
        try
        {
            await Socks5Negotiation.WriteReplyAsync(inbound, Socks5Negotiation.ReplySucceeded, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            outbound.Dispose();
            throw;
        }
        return outbound;
    }

    /// <summary>向对端发写方向的 EOF(半关闭),让只读到 EOF 才收尾的协议能正常结束。</summary>
    private static void SignalWriteEof(Stream stream)
    {
        try
        {
            switch (stream)
            {
                case SshDataStream ssh:
                    ssh.WriteEof();
                    break;
                case NetworkStream { Socket: { } socket }:
                    socket.Shutdown(SocketShutdown.Send);
                    break;
            }
        }
        catch
        {
            // 对端已断开时半关闭必然失败,拆链流程随后会收尾。
        }
    }

    /// <summary>转发意外停止(远程转发被服务器撤销/连接断开)时上报;用户主动 Stop 不算通道错误。</summary>
    private void OnUpstreamStopped(Action throwIfStopped)
    {
        if (_userStopped)
        {
            return;
        }
        _stopped = true;
        try { throwIfStopped(); }
        catch (Exception ex) { ChannelError?.Invoke(TmdsSshInterop.Translate(ex) ?? ex); }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || _userStopped)
            {
                return;
            }
            catch (Exception ex)
            {
                // 监听端自己出问题(而非单条连接失败)意味着这条转发废了,如实标记并上报。
                _stopped = true;
                ChannelError?.Invoke(ex);
                return;
            }
            Interlocked.Increment(ref _totalConnections);
            _ = HandleConnectionAsync(inbound);
        }
    }

    private async Task HandleConnectionAsync(Socket inboundSocket)
    {
        Interlocked.Increment(ref _activeConnections);
        inboundSocket.NoDelay = true;
        var inbound = new NetworkStream(inboundSocket, true);
        Stream? outbound = null;
        try
        {
            outbound = await _openOutbound(inbound, _cts.Token).ConfigureAwait(false);
            await PumpBothAsync(inbound, outbound).ConfigureAwait(false);
        }
        catch (Exception) when (_cts.IsCancellationRequested || _userStopped)
        {
            // 主动停止时正在传输的连接被拆掉,不是错误。
        }
        catch (Exception ex)
        {
            // 单条连接失败(目标拒绝、SOCKS 客户端不守协议)不影响监听端口,
            // 上报给界面,否则用户只看到"运行中"却连不上。
            ChannelError?.Invoke(ex);
        }
        finally
        {
            try { outbound?.Dispose(); } catch { }
            try { inbound.Dispose(); } catch { }
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>双向搬运直到两个方向都读到 EOF(或任一方向出错),沿途累加字节数。</summary>
    private async Task PumpBothAsync(Stream inbound, Stream outbound)
    {
        Task up = PumpAsync(inbound, outbound);
        Task down = PumpAsync(outbound, inbound);
        try
        {
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch
        {
            // 一个方向出错就拆掉两端,让另一个方向的搬运立刻退出,而不是挂在读上。
            try { inbound.Dispose(); } catch { }
            try { outbound.Dispose(); } catch { }
            try { await Task.WhenAll(up, down).ConfigureAwait(false); }
            catch
            {
                // 拆链引发的读写异常是正常收尾噪声。
            }
            throw;
        }
    }

    /// <summary>单方向搬运:读到 EOF 后向写端发半关闭,让对端知道这个方向没有更多数据。</summary>
    private async Task PumpAsync(Stream source, Stream destination)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    SignalWriteEof(destination);
                    return;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref _bytesTransferred, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

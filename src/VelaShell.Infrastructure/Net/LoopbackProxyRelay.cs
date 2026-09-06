using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Net;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 环回代理中继:为只认「host:port」直连、又无代理扩展点的客户端库(当前是 Tmds.Ssh)
/// 提供代理通路 —— 在 127.0.0.1 上开一个一次性监听,库照常发起 TCP 连接,
/// 中继在背后经 <see cref="ProxyStreamConnector" /> 打通到真实目标的代理隧道并双向转发。
/// 每次连接尝试各起一个中继,生命周期由发起方持有并随连接释放。
/// </summary>
public sealed class LoopbackProxyRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private volatile TcpClient? _inbound;
    private volatile Stream? _outbound;

    /// <summary>中继监听的环回端口,发起方把连接目标改写为 127.0.0.1:此端口。</summary>
    public int Port { get; }

    /// <summary>代理拨号/握手阶段的失败原因;客户端库只会看到连接被断,持有方据此补全错误信息。</summary>
    public Exception? Error { get; private set; }

    private LoopbackProxyRelay(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>启动中继:立即返回可连接的端口,代理拨号推迟到客户端真正连入时进行。</summary>
    public static LoopbackProxyRelay Start(ProxyRoute route, string targetHost, int targetPort)
    {
        ArgumentNullException.ThrowIfNull(route);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var relay = new LoopbackProxyRelay(listener);
        _ = relay.RunAsync(route, targetHost, targetPort);
        return relay;
    }

    private async Task RunAsync(ProxyRoute route, string targetHost, int targetPort)
    {
        try
        {
            TcpClient inbound = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            _inbound = inbound;
            _listener.Stop(); // 只服务一条连接,立刻停止监听,杜绝其它本机进程搭车。
            inbound.NoDelay = true;
            Stream outbound = await ProxyStreamConnector
                .ConnectAsync(route, targetHost, targetPort, _cts.Token).ConfigureAwait(false);
            _outbound = outbound;

            NetworkStream inStream = inbound.GetStream();
            Task up = inStream.CopyToAsync(outbound, _cts.Token);
            Task down = outbound.CopyToAsync(inStream, _cts.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            // 任一方向断开即拆链:关掉两端让另一方向的拷贝退出,再统一收割异常。
            CloseStreams();
            try { await Task.WhenAll(up, down).ConfigureAwait(false); }
            catch
            {
                // 拆链引发的读写异常是正常收尾噪声。
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            Error = ex;
        }
        catch
        {
            // 主动 Dispose 触发的取消,不记为错误。
        }
        finally
        {
            CloseStreams();
        }
    }

    // 关闭路径上的 catch 一律吞掉:要关的东西本来就在断,连接已断时 socket 的
    // Dispose/Stop 抛的是清理噪声,记下来只会在每次关闭时刷一遍日志。
    // 真出问题的表征是"端口没释放",那由下一次监听失败报出来,而不是这里。
    private void CloseStreams()
    {
        try { _outbound?.Dispose(); } catch { }
        try { _inbound?.Dispose(); } catch { }
    }

    /// <summary>停止中继并关闭两端连接;可重复调用。</summary>
    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        CloseStreams();
        _cts.Dispose();
    }
}

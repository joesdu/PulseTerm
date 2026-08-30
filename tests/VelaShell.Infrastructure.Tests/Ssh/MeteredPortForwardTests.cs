using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 计量端口转发:宿主自己接管转发的数据面,就得自己为搬运的正确性负责 ——
/// 字节要一个不差地送到、两个方向的 EOF 要如实传递、计数要对得上、停止要真的停。
/// 这些都在环回上验证,不需要 SSH 服务器。
/// </summary>
[TestClass]
[TestCategory("Tunnel")]
public class MeteredPortForwardTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    /// <summary>字节数按上行 + 下行累计,连接数按接受次数累计。</summary>
    [TestMethod]
    public async Task Relay_CountsBothDirections()
    {
        using CancellationTokenSource deadline = Deadline();
        await using var echo = EchoServer.Start();
        using MeteredPortForwardHandle relay = StartRelay(echo, out int relayPort);

        byte[] payload = Encoding.ASCII.GetBytes("the quick brown fox");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, relayPort, deadline.Token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(payload, deadline.Token);
            byte[] echoed = new byte[payload.Length];
            await stream.ReadExactlyAsync(echoed, deadline.Token);
            CollectionAssert.AreEqual(payload, echoed);
        }
        await WaitForAsync(() => relay.BytesTransferred >= payload.Length * 2, deadline.Token);

        Assert.AreEqual(payload.Length * 2L, relay.BytesTransferred, "上行与下行都应计入累计流量。");
        Assert.AreEqual(1, relay.TotalConnections);
        await WaitForAsync(() => relay.ActiveConnections == 0, deadline.Token);
    }

    /// <summary>多条连接各自搬运,计数汇总到同一条转发上。</summary>
    [TestMethod]
    public async Task Relay_AggregatesAcrossConnections()
    {
        using CancellationTokenSource deadline = Deadline();
        await using var echo = EchoServer.Start();
        using MeteredPortForwardHandle relay = StartRelay(echo, out int relayPort);

        byte[] payload = "0123456789"u8.ToArray();
        for (int i = 0; i < 3; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, relayPort, deadline.Token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(payload, deadline.Token);
            byte[] echoed = new byte[payload.Length];
            await stream.ReadExactlyAsync(echoed, deadline.Token);
        }
        await WaitForAsync(() => relay.BytesTransferred >= payload.Length * 6, deadline.Token);

        Assert.AreEqual(3, relay.TotalConnections);
        Assert.AreEqual(payload.Length * 6L, relay.BytesTransferred);
    }

    /// <summary>
    /// 客户端半关闭(只关写方向)后,目标端要读到 EOF,而回程数据仍能送达 ——
    /// 一旦把半关闭做成整条拆链,「发完请求就 shutdown 再等响应」的协议全部读不到东西。
    /// </summary>
    [TestMethod]
    public async Task Relay_ForwardsHalfClose()
    {
        using CancellationTokenSource deadline = Deadline();
        // 读到 EOF 才回话的目标:半关闭没传过去的话它会一直等下去。
        await using var target = new StubServer(async (stream, ct) =>
        {
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, ct);
            byte[] reply = Encoding.ASCII.GetBytes($"got {received.Length}");
            await stream.WriteAsync(reply, ct);
        });
        using MeteredPortForwardHandle relay = StartRelay(target, out int relayPort);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort, deadline.Token);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync("hello"u8.ToArray(), deadline.Token);
        client.Client.Shutdown(SocketShutdown.Send);

        using var response = new MemoryStream();
        await stream.CopyToAsync(response, deadline.Token);
        Assert.AreEqual("got 5", Encoding.ASCII.GetString(response.ToArray()));
    }

    /// <summary>停止转发后监听端口应真的释放,而不是留着一个连上就断的僵尸监听。</summary>
    [TestMethod]
    public async Task Stop_ReleasesListeningPort()
    {
        using CancellationTokenSource deadline = Deadline();
        await using var echo = EchoServer.Start();
        MeteredPortForwardHandle relay = StartRelay(echo, out int relayPort);
        Assert.IsTrue(relay.IsStarted);

        relay.Stop();
        Assert.IsFalse(relay.IsStarted);
        relay.Stop(); // 幂等

        // 端口已释放:同一端口可以重新绑定。
        var rebind = new TcpListener(IPAddress.Loopback, relayPort);
        try
        {
            rebind.Start();
        }
        finally
        {
            rebind.Stop();
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 单条连接建不起来(目标拒绝)不该停掉监听端口,但要经 ChannelError 上报 ——
    /// 否则界面一直显示"运行中",用户却怎么也连不上。
    /// </summary>
    [TestMethod]
    public async Task Relay_ReportsChannelError_ButKeepsListening()
    {
        using CancellationTokenSource deadline = Deadline();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int relayPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var relay = MeteredPortForwardHandle.CreateRelay(listener, (_, _) =>
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        });
        relay.ChannelError += ex => reported.TrySetResult(ex);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, relayPort, deadline.Token);
        }
        Exception error = await reported.Task.WaitAsync(deadline.Token);

        Assert.IsInstanceOfType<SocketException>(error);
        Assert.IsTrue(relay.IsStarted, "单条连接失败不该停掉监听端口。");
        Assert.AreEqual(1, relay.TotalConnections, "接受到的连接照数,哪怕它随后失败了。");
    }

    /// <summary>装配一条把入站连接接到给定测试服务器的计量转发。</summary>
    private static MeteredPortForwardHandle StartRelay(StubServer target, out int relayPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        relayPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        int targetPort = target.Port;
        return MeteredPortForwardHandle.CreateRelay(listener, async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, targetPort, ct);
            return new NetworkStream(socket, true);
        });
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>一台按给定处理逻辑服务每条连接的环回测试服务器。</summary>
    private class StubServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<NetworkStream, CancellationToken, Task> _handler;
        private readonly TcpListener _listener;
        private readonly Task _loop;

        public StubServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _handler = handler;
            _listener = new(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptLoopAsync();
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; }
            catch
            {
                // 关停引发的读写异常是正常收尾噪声。
            }
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                _ = ServeAsync(socket);
            }
        }

        private async Task ServeAsync(Socket socket)
        {
            await using var stream = new NetworkStream(socket, true);
            try
            {
                await _handler(stream, _cts.Token);
            }
            catch
            {
                // 客户端提前断开是测试里的常态。
            }
        }
    }

    /// <summary>把收到的字节原样回送的目标服务器。</summary>
    private sealed class EchoServer : StubServer
    {
        private EchoServer() : base(static async (stream, ct) =>
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    return;
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        })
        {
        }

        public static EchoServer Start() => new();
    }
}

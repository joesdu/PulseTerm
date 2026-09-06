using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class RpcConnectionTests
{
    private sealed record Echo(string Text);

    /// <summary>搭一对经真实命名管道互连的 RPC 连接(与生产同传输)。</summary>
    private static async Task<(RpcConnection Server, RpcConnection Client, Func<ValueTask> Cleanup)> ConnectPairAsync(
        Func<string, JsonElement?, CancellationToken, Task<object?>>? serverHandler = null,
        Action<string, JsonElement?>? serverNotifications = null)
    {
        string name = $"velashell-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task wait = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await wait;

        var server = new RpcConnection(serverPipe);
        if (serverHandler is not null)
        {
            server.SetRequestHandler(serverHandler);
        }
        if (serverNotifications is not null)
        {
            server.SetNotificationHandler(serverNotifications);
        }
        server.Start();
        var client = new RpcConnection(clientPipe);
        client.SetRequestHandler((_, _, _) => Task.FromResult<object?>(null));
        client.Start();
        return (server, client, async () =>
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
        );
    }

    [TestMethod]
    public async Task Request_RoundTripsTypedPayloads()
    {
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            (method, payload, _) => Task.FromResult<object?>(method == "echo"
                ? new Echo("re: " + payload!.Value.Deserialize<Echo>()!.Text)
                : throw new InvalidOperationException($"Unknown method '{method}'.")));
        try
        {
            Echo? reply = await client.RequestAsync<Echo>("echo", new Echo("hello"), TimeSpan.FromSeconds(5));
            Assert.AreEqual("re: hello", reply?.Text);
        }
        finally
        {
            await cleanup();
        }
    }

    [TestMethod]
    public async Task Request_RemoteException_MapsToTypedException()
    {
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            (_, _, _) => throw new PluginSessionNotFoundException("s-42"));
        try
        {
            PluginSessionNotFoundException ex = await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
                () => client.RequestAsync<object>("anything", null, TimeSpan.FromSeconds(5)));
            Assert.Contains("s-42", ex.Message);
        }
        finally
        {
            await cleanup();
        }
    }

    [TestMethod]
    public async Task Notification_IsDeliveredWithoutResponse()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            serverNotifications: (method, payload) =>
                received.TrySetResult($"{method}:{payload!.Value.Deserialize<Echo>()!.Text}"));
        try
        {
            await client.NotifyAsync("log", new Echo("ping"));
            Assert.AreEqual("log:ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await cleanup();
        }
    }

    [TestMethod]
    public async Task Request_Timeout_ThrowsTimeoutException()
    {
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return null;
            });
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => client.RequestAsync<object>("slow", null, TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            await cleanup();
        }
    }

    [TestMethod]
    public async Task Disconnect_FailsPendingRequests_AndRaisesDisconnected()
    {
        (RpcConnection server, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return null;
            });
        try
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += () => disconnected.TrySetResult();
            Task<object?> pending = client.RequestAsync<object>("slow", null, TimeSpan.FromSeconds(30));
            await server.DisposeAsync(); // 对端关闭
            await Assert.ThrowsExactlyAsync<RpcDisconnectedException>(() => pending);
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await cleanup();
        }
    }

    [TestMethod]
    public async Task Dispose_WithIdleReadLoop_ThrowsNothing()
    {
        // 停用插件/退出应用走的就是这条路:读循环正等着下一帧,本端主动关闭连接。
        // 这是最常规的分支,不该长成异常 —— 曾经把 _lifetime.Token 传给 ReadAsync,
        // 取消会把待决的读撕成 OperationCanceledException,虽被读循环 catch 吞掉、
        // 退出码照样是 0,却在每次退出时给调试器刷一条首发异常。
        (RpcConnection server, RpcConnection client, Func<ValueTask> _) = await ConnectPairAsync();
        var thrown = new List<Exception>();
        void Record(object? _, FirstChanceExceptionEventArgs e)
        {
            lock (thrown)
            {
                thrown.Add(e.Exception);
            }
        }

        // 读循环此刻确实在等待读取(而不是尚未起步),否则这条测试会空跑通过。
        await Task.Delay(200);
        AppDomain.CurrentDomain.FirstChanceException += Record;
        try
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.Delay(200); // 给读循环收尾的时间,晚到的异常也要算数
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Record;
        }
        lock (thrown)
        {
            List<Exception> ours = [.. thrown.Where(IsNotPipeTeardownNoise)];
            Assert.IsEmpty(ours, $"关闭连接不应抛任何异常,实际抛出:{string.Join(", ", ours.Select(e => e.GetType().Name))}");
        }
    }

    [TestMethod]
    public async Task ConcurrentRequests_DoNotSerialize()
    {
        // 两个并发慢请求总耗时应接近单个,而不是两倍(读循环不被处理器阻塞)。
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            async (_, _, token) =>
            {
                await Task.Delay(300, token);
                return new Echo("done");
            });
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAll(
                client.RequestAsync<Echo>("a", null, TimeSpan.FromSeconds(10)),
                client.RequestAsync<Echo>("b", null, TimeSpan.FromSeconds(10)));
            Assert.IsLessThan(550, stopwatch.ElapsedMilliseconds, "两个 300ms 请求并发执行不应串行成 600ms+");
        }
        finally
        {
            await cleanup();
        }
    }

    /// <summary>
    /// Windows 上关闭管道句柄会把待决的读干净地完成,一条异常都不该有,所以全都算数。
    /// Linux/macOS 的命名管道是 Unix domain socket 实现的:本端 close 时,对端还挂着的那个读
    /// 必然先在 BCL 内部抛 SocketException(ECONNRESET/EPIPE),再被包成 IOException,
    /// 然后才由 SDK 的读循环正常吞掉。那是运行时的收尾方式,不在我们能改的范围内,放它过去 ——
    /// 这条测试真正盯的是**自家**的取消异常(把 lifetime token 传给 ReadAsync 会把待决的读撕成
    /// OperationCanceledException),那种异常在任何平台上都仍然会让断言失败。
    /// </summary>
    private static bool IsNotPipeTeardownNoise(Exception ex) =>
        OperatingSystem.IsWindows() || ex is not (SocketException or IOException);
}

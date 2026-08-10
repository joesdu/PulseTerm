using System.IO.Pipes;
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
            StringAssert.Contains(ex.Message, "s-42");
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
    public async Task ConcurrentRequests_DoNotSerialize()
    {
        // 两个并发慢请求总耗时应接近单个,而不是两倍(读循环不被处理器阻塞)。
        (RpcConnection _, RpcConnection client, Func<ValueTask> cleanup) = await ConnectPairAsync(
            async (_, _, _) =>
            {
                await Task.Delay(300);
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
}

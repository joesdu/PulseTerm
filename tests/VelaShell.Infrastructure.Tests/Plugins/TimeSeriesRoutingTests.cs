using System.IO.Pipes;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 隔离插件的时序 RPC 链路:打开 → 写 → 查 / 计数 / 去重 / 删,
/// 重点是各 DTO(含 <see cref="TimeSeriesValue" /> 联合体与字典)能原样过线。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class TimeSeriesRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSeriesDefinition Definition = new("chat_messages",
    [
        TimeSeriesColumn.Tag("conv"),
        TimeSeriesColumn.Field("role", TimeSeriesValueKind.Text),
        TimeSeriesColumn.Field("seq", TimeSeriesValueKind.Integer),
        TimeSeriesColumn.Field("cost", TimeSeriesValueKind.Number),
        TimeSeriesColumn.Field("done", TimeSeriesValueKind.Flag)
    ]);

    [TestMethod]
    public async Task TimeSeries_RoundTripsOverRpc()
    {
        string name = $"velashell-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task wait = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await wait;

        string dataDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var context = new PluginContext
        {
            PluginId = "acme.ts",
            PluginVersion = "1.0.0",
            DataDirectory = dataDir,
            Host = new TestHostInfo(),
            Log = new CollectingLogger(),
            Storage = new InMemoryStorage(),
            TimeSeries = new InMemoryTimeSeries(),
            Sessions = new FakeSessions(),
            RemoteFs = new FakeRemoteFs(),
            RemoteExec = new FakeRemoteExec(),
            Commands = new RecordingCommands(),
            Events = new PluginEventHub(new CollectingLogger(), null, null, null),
            Ui = new FakeUi(),
            Secrets = new FakeSecrets(),
            Clipboard = new FakeClipboard(),
            Terminal = new FakeTerminal(),
            // 隔离插件本就拿不到协议能力(清单校验会拒 protocols + isolated),这里用"注册即抛"的实现。
            Protocols = new UnavailableProtocols(),
            Workspaces = new UnavailableWorkspaces(),
            Shutdown = CancellationToken.None
        };
        var hostConnection = new RpcConnection(serverPipe);
        var router = new VelaShell.Infrastructure.Plugins.Isolated.PluginCapabilityRouter(context, hostConnection, "token", "1.0.0");
        hostConnection.SetRequestHandler(router.HandleRequestAsync);
        hostConnection.SetNotificationHandler(router.HandleNotification);
        hostConnection.Start();

        var pluginConnection = new RpcConnection(clientPipe);
        pluginConnection.SetRequestHandler((_, _, _) => Task.FromResult<object?>(null));
        pluginConnection.Start();
        try
        {
            await pluginConnection.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
                new HandshakeRequest("token", "acme.ts", "1.0.0", [VelaPluginApi.Level]), Timeout);

            TimeSeriesNameRef? opened = await pluginConnection.RequestAsync<TimeSeriesNameRef>(
                PluginRpc.TimeSeriesOpen, new TimeSeriesOpenRequest(Definition), Timeout);
            Assert.AreEqual("chat_messages", opened!.Name);

            var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            TimeSeriesPoint[] points =
            [
                Point(start, "c1", 0, "user", 0.25, true),
                Point(start.AddMilliseconds(1), "c1", 1, "assistant", 1.5, false),
                Point(start.AddMilliseconds(2), "c2", 0, "user", 0, true)
            ];
            await pluginConnection.RequestAsync<object>(PluginRpc.TimeSeriesWrite,
                new TimeSeriesWriteRequest("chat_messages", points), Timeout);

            TimeSeriesPoint[]? conversation = await pluginConnection.RequestAsync<TimeSeriesPoint[]>(
                PluginRpc.TimeSeriesQuery,
                new TimeSeriesQueryRequest("chat_messages", new()
                {
                    Tags = new Dictionary<string, string> { ["conv"] = "c1" },
                    Descending = false
                }), Timeout);
            Assert.HasCount(2, conversation!);
            Assert.AreEqual("user", conversation[0].Text("role"));
            Assert.AreEqual("c1", conversation[0].Tag("conv"));
            Assert.AreEqual(start, conversation[0].Timestamp, "时间戳过线后应逐毫秒相等");
            Assert.AreEqual(1, conversation[1].Integer("seq"));
            Assert.AreEqual(1.5, conversation[1].Field("cost")!.Value.Number, 0.0001, "浮点字段类型不能在序列化中丢失");
            Assert.IsFalse(conversation[1].Field("done")!.Value.AsFlag());

            long count = await pluginConnection.RequestAsync<long>(PluginRpc.TimeSeriesCount,
                new TimeSeriesCountRequest("chat_messages", "seq", new()), Timeout);
            Assert.AreEqual(3, count);

            string[]? conversations = await pluginConnection.RequestAsync<string[]>(PluginRpc.TimeSeriesDistinct,
                new TimeSeriesDistinctRequest("chat_messages", "conv"), Timeout);
            Assert.AreSequenceEqual(["c1", "c2"], conversations!);

            string[]? listed = await pluginConnection.RequestAsync<string[]>(PluginRpc.TimeSeriesList, null, Timeout);
            Assert.AreSequenceEqual(["chat_messages"], listed!);

            await pluginConnection.RequestAsync<int>(PluginRpc.TimeSeriesDelete,
                new TimeSeriesDeleteRequest("chat_messages", new() { ["conv"] = "c1" }), Timeout);
            TimeSeriesPoint[]? remaining = await pluginConnection.RequestAsync<TimeSeriesPoint[]>(
                PluginRpc.TimeSeriesQuery, new TimeSeriesQueryRequest("chat_messages", new()), Timeout);
            Assert.HasCount(1, remaining!);
            Assert.AreEqual("c2", remaining[0].Tag("conv"));

            // 没打开就用 → 明确报错,而不是悄悄新建一张表
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                pluginConnection.RequestAsync<TimeSeriesPoint[]>(PluginRpc.TimeSeriesQuery,
                    new TimeSeriesQueryRequest("never_opened", new()), Timeout));

            Assert.IsTrue(await pluginConnection.RequestAsync<bool>(PluginRpc.TimeSeriesDrop,
                new TimeSeriesNameRef("chat_messages"), Timeout));
        }
        finally
        {
            await pluginConnection.DisposeAsync();
            await hostConnection.DisposeAsync();
            router.Dispose();
            context.Dispose();
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch
            {
                // 尽力清理。
            }
        }
    }

    private static TimeSeriesPoint Point(DateTimeOffset at, string conversation, long sequence, string role, double cost, bool done)
        => new(at, new Dictionary<string, string> { ["conv"] = conversation }, new Dictionary<string, TimeSeriesValue>
        {
            ["role"] = TimeSeriesValue.FromText(role),
            ["seq"] = TimeSeriesValue.FromInteger(sequence),
            ["cost"] = TimeSeriesValue.FromNumber(cost),
            ["done"] = TimeSeriesValue.FromFlag(done)
        });
}

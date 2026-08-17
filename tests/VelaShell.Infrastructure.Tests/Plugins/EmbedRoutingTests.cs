using System.IO.Pipes;
using System.Text.Json;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>停靠嵌入的 RPC 链路验证(握手宣告 → 嵌入 → 双向关闭),Win32 收养由假宿主替身承担。</summary>
[TestClass]
[TestCategory("Plugins")]
public class EmbedRoutingTests
{
    private sealed class FakeEmbeddedPanel : IPluginPanel
    {
        public string PanelId { get; } = Guid.NewGuid().ToString("N");
        public bool IsOpen { get; private set; } = true;
        public event Action? Closed;

        public static double PlacementRatio => double.NaN;
        public event Action<double>? PlacementRatioChanged { add { } remove { } }
        public Task ActivateAsync() => Task.CompletedTask;

        public Task CloseAsync()
        {
            if (IsOpen)
            {
                IsOpen = false;
                Closed?.Invoke();
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(CloseAsync());

        public void SimulateUserClose() => _ = CloseAsync();
    }

    private sealed class FakeEmbedHost : IPluginEmbedHost
    {
        public List<(string PluginId, string Title, nint Hwnd)> Requests { get; } = [];
        public List<FakeEmbeddedPanel> Panels { get; } = [];
        public bool IsSupported => true;

        public Task<IPluginPanel> EmbedAsync(string pluginId, IPluginLogger log, string title, nint hwnd,
            CancellationToken cancellationToken)
        {
            Requests.Add((pluginId, title, hwnd));
            var panel = new FakeEmbeddedPanel();
            Panels.Add(panel);
            return Task.FromResult<IPluginPanel>(panel);
        }
    }

    private static PluginContext CreateContext(string dataDir) => new()
    {
        PluginId = "acme.embed",
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
        // 本用例测的是停靠嵌入,协议能力被用到即是误用 —— 用"注册即抛"的那个实现。
        Protocols = new UnavailableProtocols(),
        Shutdown = CancellationToken.None
    };

    [TestMethod]
    public async Task EmbedFlow_HandshakeAdvertises_EmbedsAndClosesBothWays()
    {
        string name = $"velashell-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task wait = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await wait;

        string dataDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var embedHost = new FakeEmbedHost();
        PluginContext context = CreateContext(dataDir);
        var hostConnection = new RpcConnection(serverPipe);
        var router = new PluginCapabilityRouter(context, hostConnection, "token", "1.0.0", embedHost: embedHost);
        hostConnection.SetRequestHandler(router.HandleRequestAsync);
        hostConnection.SetNotificationHandler(router.HandleNotification);
        hostConnection.Start();

        var closedNotifications = new List<string>();
        var pluginConnection = new RpcConnection(clientPipe);
        pluginConnection.SetRequestHandler((_, _, _) => Task.FromResult<object?>(null));
        pluginConnection.SetNotificationHandler((method, payload) =>
        {
            if (method == PluginRpc.UiPanelClosed && payload?.Deserialize<UiPanelRef>() is { } closed)
            {
                lock (closedNotifications)
                {
                    closedNotifications.Add(closed.PanelId);
                }
            }
        });
        pluginConnection.Start();
        try
        {
            // 握手宣告嵌入能力。
            HandshakeResponse? hello = await pluginConnection.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
                new HandshakeRequest("token", "acme.embed", "1.0.0", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
            Assert.IsTrue(hello!.SupportsEmbedding);

            // 嵌入:HWND 送达宿主,拿回面板 id。
            UiEmbedResponse? embedded = await pluginConnection.RequestAsync<UiEmbedResponse>(PluginRpc.UiEmbedPanel,
                new UiEmbedRequest("My Tab", 0x1234), TimeSpan.FromSeconds(5));
            Assert.AreEqual(("acme.embed", "My Tab", 0x1234), embedHost.Requests.Single());
            Assert.AreEqual(embedHost.Panels.Single().PanelId, embedded!.PanelId);

            // 方向一:宿主侧关闭(用户关标签)→ 插件收到 ui/closed 通知。
            embedHost.Panels.Single().SimulateUserClose();
            await WaitForAsync(() =>
            {
                lock (closedNotifications)
                {
                    return closedNotifications.Contains(embedded.PanelId);
                }
            }, "宿主关标签应通知插件进程");

            // 方向二:插件程序性关闭 → 宿主面板被关。
            UiEmbedResponse? second = await pluginConnection.RequestAsync<UiEmbedResponse>(PluginRpc.UiEmbedPanel,
                new UiEmbedRequest("Second", 0x5678), TimeSpan.FromSeconds(5));
            await pluginConnection.RequestAsync<object>(PluginRpc.UiClosePanel,
                new UiPanelRef(second!.PanelId), TimeSpan.FromSeconds(5));
            Assert.IsFalse(embedHost.Panels[1].IsOpen);
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

    private static async Task WaitForAsync(Func<bool> condition, string message)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail(message);
    }
}

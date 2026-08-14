using System.IO.Pipes;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>远端文件流式读取的 RPC 链路:打开 → 顺序分块 → EOF 自动释放 / 提前关闭。</summary>
[TestClass]
[TestCategory("Plugins")]
public class StreamingRoutingTests
{
    [TestMethod]
    public async Task OpenRead_StreamsChunksSequentially_UntilEof()
    {
        string name = $"velashell-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task wait = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await wait;

        string dataDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        // 内容 > 单块上限的钳制值不好造(512KB),用小块多次拉取验证顺序性即可。
        byte[] payload = new byte[100_000];
        Random.Shared.NextBytes(payload);
        var remoteFs = new FakeRemoteFs();
        remoteFs.AddFile("s1", "/var/log/big.log", payload);

        var context = new PluginContext
        {
            PluginId = "acme.stream",
            PluginVersion = "1.0.0",
            DataDirectory = dataDir,
            Host = new TestHostInfo(),
            Log = new CollectingLogger(),
            Storage = new InMemoryStorage(),
            Sessions = new FakeSessions(),
            RemoteFs = remoteFs,
            RemoteExec = new FakeRemoteExec(),
            Commands = new RecordingCommands(),
            Events = new PluginEventHub(new CollectingLogger(), null, null, null),
            Ui = new FakeUi(),
            Secrets = new FakeSecrets(),
            Clipboard = new FakeClipboard(),
            Terminal = new FakeTerminal(),
            // 本用例测 RPC 流式链路,协议能力被用到即是误用 —— 用"注册即抛"的那个实现。
            Protocols = new UnavailableProtocols(),
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
                new HandshakeRequest("token", "acme.stream", "1.0.0", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));

            FsOpenReadResponse? opened = await pluginConnection.RequestAsync<FsOpenReadResponse>(PluginRpc.FsOpenRead,
                new FsPathRequest("s1", "/var/log/big.log"), TimeSpan.FromSeconds(5));
            Assert.AreEqual(payload.Length, opened!.Length);

            // 顺序分块拉取(每块 16KB),拼回原文。
            using var assembled = new MemoryStream();
            while (true)
            {
                FsStreamReadResponse? chunk = await pluginConnection.RequestAsync<FsStreamReadResponse>(PluginRpc.FsStreamRead,
                    new FsStreamReadRequest(opened.StreamId, 16 * 1024), TimeSpan.FromSeconds(5));
                byte[] data = Convert.FromBase64String(chunk!.DataBase64);
                assembled.Write(data);
                if (chunk.Eof)
                {
                    break;
                }
            }
            Assert.AreSequenceEqual(payload, assembled.ToArray());

            // EOF 后宿主已自动释放:再拉块报"流未打开"。
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                pluginConnection.RequestAsync<FsStreamReadResponse>(PluginRpc.FsStreamRead,
                    new FsStreamReadRequest(opened.StreamId, 1024), TimeSpan.FromSeconds(5)));

            // 提前关闭路径:打开后立即 close,幂等不报错。
            FsOpenReadResponse? second = await pluginConnection.RequestAsync<FsOpenReadResponse>(PluginRpc.FsOpenRead,
                new FsPathRequest("s1", "/var/log/big.log"), TimeSpan.FromSeconds(5));
            await pluginConnection.RequestAsync<object>(PluginRpc.FsStreamClose,
                new FsStreamRef(second!.StreamId), TimeSpan.FromSeconds(5));
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
}

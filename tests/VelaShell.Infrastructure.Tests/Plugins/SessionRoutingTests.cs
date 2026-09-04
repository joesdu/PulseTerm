using System.IO.Pipes;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 「开会话」在隔离模式下的 RPC 链路:已保存列表 → 请求打开 → 关闭,
/// 以及宿主的拒绝要以 <see cref="PluginPermissionDeniedException" /> 的身份到达插件那一侧。
/// </summary>
/// <remarks>
/// 后一条是这组用例真正的看点。跨进程时异常只剩一个错误码加一句话,
/// 错误码要是丢了,"用户说了不"就会变成一个笼统的调用失败 ——
/// 插件于是换个姿势再试一次,而这正是契约明令禁止的。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public class SessionRoutingTests
{
    private sealed class StubOpener(PluginSessionOpenResult result, List<SshSession> sessions) : IPluginSessionOpener
    {
        public List<Guid> Closed { get; } = [];

        public Task<PluginSessionOpenResult> OpenAsync(string pluginId, SessionProfile profile, string reason,
            CancellationToken cancellationToken)
        {
            if (result.Outcome == PluginSessionOpenOutcome.Opened)
            {
                sessions.Add(new()
                {
                    SessionId = result.SessionId,
                    ConnectionInfo = new()
                    {
                        Host = profile.Host,
                        Port = profile.Port,
                        Username = profile.Username,
                        AuthMethod = AuthMethod.Password
                    },
                    Status = SessionStatus.Connected,
                    ConnectedAt = DateTime.UtcNow
                });
            }
            return Task.FromResult(result);
        }

        public Task CloseAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Closed.Add(sessionId);
            sessions.RemoveAll(s => s.SessionId == sessionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>建起一对管道 + 路由,插件那侧是一个裸 <see cref="RpcConnection" />。</summary>
    private static async Task<(RpcConnection Plugin, IAsyncDisposable Cleanup)> ConnectAsync(ISessionsApi sessions)
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
            PluginId = "acme.sessions",
            PluginVersion = "1.0.0",
            DataDirectory = dataDir,
            Host = new TestHostInfo(),
            Log = new CollectingLogger(),
            Storage = new InMemoryStorage(),
            TimeSeries = new InMemoryTimeSeries(),
            Sessions = sessions,
            RemoteFs = new FakeRemoteFs(),
            RemoteExec = new FakeRemoteExec(),
            RemoteTunnel = new FakeRemoteTunnel(),
            TerminalView = new FakeTerminalViewApi(),
            Commands = new RecordingCommands(),
            Events = new PluginEventHub(new CollectingLogger(), null, null, null),
            Theme = new StaticHostTheme(),
            Ui = new FakeUi(),
            Secrets = new FakeSecrets(),
            Clipboard = new FakeClipboard(),
            Terminal = new FakeTerminal(),
            Protocols = new UnavailableProtocols(),
            Workspaces = new UnavailableWorkspaces(),
            Shutdown = CancellationToken.None
        };
        var hostConnection = new RpcConnection(serverPipe);
        var router = new Infrastructure.Plugins.Isolated.PluginCapabilityRouter(context, hostConnection, "token", "1.0.0");
        hostConnection.SetRequestHandler(router.HandleRequestAsync);
        hostConnection.SetNotificationHandler(router.HandleNotification);
        hostConnection.Start();

        var pluginConnection = new RpcConnection(clientPipe);
        pluginConnection.SetRequestHandler((_, _, _) => Task.FromResult<object?>(null));
        pluginConnection.Start();
        await pluginConnection.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "acme.sessions", "1.0.0", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        return (pluginConnection, new Cleanup(pluginConnection, hostConnection, router, dataDir));
    }

    private sealed class Cleanup(RpcConnection plugin, RpcConnection host,
        Infrastructure.Plugins.Isolated.PluginCapabilityRouter router, string dataDir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await plugin.DisposeAsync();
            await host.DisposeAsync();
            router.Dispose();
            try
            {
                Directory.Delete(dataDir, true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响断言。
            }
        }
    }

    private static (SessionsCapability Capability, SessionProfile Profile, List<SshSession> Sessions, StubOpener Opener)
        NewCapability(PluginSessionOpenResult result)
    {
        SessionProfile profile = new()
        {
            Name = "prod-1",
            Host = "10.0.0.1",
            Port = 22,
            Username = "root",
            ConnectionType = ConnectionType.SSH
        };
        List<SshSession> sessions = [];
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns(_ => sessions);
        connections.GetSession(Arg.Any<Guid>()).Returns(call => sessions.Find(s => s.SessionId == call.Arg<Guid>()));
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));
        repository.GetSessionAsync(Arg.Any<Guid>())
                  .Returns(call => Task.FromResult(call.Arg<Guid>() == profile.Id ? profile : null));
        var opener = new StubOpener(result, sessions);
        return (new("acme.sessions", connections, repository, opener), profile, sessions, opener);
    }

    [TestMethod]
    public async Task ListSaved_Open_Close_RoundTripOverRpc()
    {
        var opened = Guid.NewGuid();
        (SessionsCapability capability, SessionProfile profile, List<SshSession> sessions, StubOpener opener) =
            NewCapability(PluginSessionOpenResult.Opened(opened));
        (RpcConnection plugin, IAsyncDisposable cleanup) = await ConnectAsync(capability);
        await using (cleanup)
        {
            SavedSessionInfo[]? saved = await plugin.RequestAsync<SavedSessionInfo[]>(
                PluginRpc.SessionsListSaved, null, TimeSpan.FromSeconds(5));
            Assert.AreEqual(profile.Id.ToString(), saved!.Single().SavedSessionId);

            SessionInfo? session = await plugin.RequestAsync<SessionInfo>(PluginRpc.SessionsOpen,
                new SessionOpenRequest(profile.Id.ToString(), "查磁盘", true), TimeSpan.FromSeconds(5));
            Assert.AreEqual(opened.ToString(), session!.SessionId);
            Assert.AreEqual(SessionState.Connected, session.State);
            Assert.HasCount(1, sessions);

            await plugin.RequestAsync<object>(PluginRpc.SessionsClose,
                new SessionRef(session.SessionId), TimeSpan.FromSeconds(5));
            Assert.AreSequenceEqual([opened], opener.Closed);
        }
    }

    [TestMethod]
    public async Task Open_UserDenied_ArrivesAsPermissionDeniedOnThePluginSide()
    {
        (SessionsCapability capability, SessionProfile profile, _, _) =
            NewCapability(PluginSessionOpenResult.Denied("用户点了拒绝"));
        (RpcConnection plugin, IAsyncDisposable cleanup) = await ConnectAsync(capability);
        await using (cleanup)
        {
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(
                () => plugin.RequestAsync<SessionInfo>(PluginRpc.SessionsOpen,
                    new SessionOpenRequest(profile.Id.ToString(), "查磁盘", true), TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task Open_ConnectFailure_ArrivesAsSessionOpenFailureNotAsDenial()
    {
        (SessionsCapability capability, SessionProfile profile, _, _) =
            NewCapability(PluginSessionOpenResult.Failed("Connection timed out"));
        (RpcConnection plugin, IAsyncDisposable cleanup) = await ConnectAsync(capability);
        await using (cleanup)
        {
            await Assert.ThrowsExactlyAsync<PluginSessionOpenException>(
                () => plugin.RequestAsync<SessionInfo>(PluginRpc.SessionsOpen,
                    new SessionOpenRequest(profile.Id.ToString(), "查磁盘", true), TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task Close_SessionThisPluginNeverOpened_IsRefusedAcrossTheWire()
    {
        (SessionsCapability capability, _, _, _) = NewCapability(PluginSessionOpenResult.Opened(Guid.NewGuid()));
        (RpcConnection plugin, IAsyncDisposable cleanup) = await ConnectAsync(capability);
        await using (cleanup)
        {
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(
                () => plugin.RequestAsync<object>(PluginRpc.SessionsClose,
                    new SessionRef(Guid.NewGuid().ToString()), TimeSpan.FromSeconds(5)));
        }
    }
}

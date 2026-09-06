using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 指标服务的缓存生命周期。守的是"连了又关"之后不留东西。
/// </summary>
[TestClass]
[TestCategory("Metrics")]
public sealed class SessionMetricsCacheEvictionTests
{
    [TestInitialize]
    public void ResetProbeCache() => RemoteShellProbe.ClearCache();

    /// <summary>
    /// 会话断开就该丢掉它的缓存,而不是等下一次轮询。
    /// </summary>
    /// <remarks>
    /// 旧实现把清理写在 <c>GetMetricsAsync</c> 的"发现连接已断"分支里 —— 可关掉的会话
    /// 恰恰是**不会再被轮询**的会话,那个分支永远走不到。表现是连开关几十次之后,
    /// 字典里躺着几十份再也用不上的主机静态信息(CPU 型号、磁盘型号、网卡属性)
    /// 与采样,一直留到进程退出。
    /// </remarks>
    [TestMethod]
    public async Task DisconnectingASession_DropsItsCache()
    {
        var connections = new FakeConnectionService();
        using var metrics = new SessionMetricsService(connections);

        // 连上、探一次(让缓存真的有东西)、再断开,重复 100 次。
        for (int i = 0; i < 100; i++)
        {
            SshSession session = NewSession($"host-{i}");
            connections.Connect(session);
            Assert.IsNotNull(
                await metrics.GetStaticInfoAsync(session.SessionId),
                "前置:静态信息探测应当成功,否则这条用例什么都没验证。");
            connections.Disconnect(session);
        }

        Assert.AreEqual(
            0,
            metrics.CachedSessionCountForTest,
            "关掉的会话仍留在指标缓存里 —— 开关得越多留得越多。");
    }

    /// <summary>还连着的会话不能被顺手清掉。</summary>
    [TestMethod]
    public async Task DisconnectingOneSession_LeavesTheOthersAlone()
    {
        var connections = new FakeConnectionService();
        using var metrics = new SessionMetricsService(connections);

        SshSession kept = NewSession("kept");
        SshSession closed = NewSession("closed");
        connections.Connect(kept);
        connections.Connect(closed);
        await metrics.GetStaticInfoAsync(kept.SessionId);
        await metrics.GetStaticInfoAsync(closed.SessionId);

        Assert.AreEqual(2, metrics.CachedSessionCountForTest, "前置:两条会话都进了缓存。");

        connections.Disconnect(closed);

        Assert.AreEqual(1, metrics.CachedSessionCountForTest);
    }

    /// <summary>释放服务要退订,免得它继续被连接服务牵着。</summary>
    [TestMethod]
    public void DisposingTheService_UnsubscribesFromTheConnectionService()
    {
        var connections = new FakeConnectionService();
        var metrics = new SessionMetricsService(connections);

        Assert.IsTrue(connections.HasDisconnectedSubscribers);
        metrics.Dispose();
        Assert.IsFalse(connections.HasDisconnectedSubscribers);
    }

    /// <summary>
    /// POSIX 探针的结论按主机缓存,所以每条会话给一个不同的主机名 ——
    /// 否则第二条起会直接借用第一条的结论,探测路径就没被真正走到。
    /// </summary>
    private static SshSession NewSession(string host) =>
        new()
        {
            ConnectionInfo = new()
            {
                Host = host,
                Username = "vela",
                AuthMethod = AuthMethod.Password
            }
        };

    /// <summary>只提供"有哪些连接"和断连事件的连接服务替身。</summary>
    private sealed class FakeConnectionService : ISshConnectionService
    {
        private readonly Dictionary<Guid, ISshClientWrapper> _clients = [];
        private readonly Dictionary<Guid, SshSession> _sessions = [];

        public event Action<SshSession>? SessionConnected;

        public event Action<SshSession>? SessionDisconnected;

        public bool HasDisconnectedSubscribers => SessionDisconnected is not null;

        public IReadOnlyList<SshSession> Sessions => [.. _sessions.Values];

        public void Connect(SshSession session)
        {
            _clients[session.SessionId] = CreatePosixClient();
            _sessions[session.SessionId] = session;
            SessionConnected?.Invoke(session);
        }

        public void Disconnect(SshSession session)
        {
            _clients.Remove(session.SessionId);
            _sessions.Remove(session.SessionId);
            SessionDisconnected?.Invoke(session);
        }

        public SshSession? GetSession(Guid sessionId) =>
            _sessions.TryGetValue(sessionId, out SshSession? session) ? session : null;

        public ISshClientWrapper? GetClient(Guid sessionId) =>
            _clients.TryGetValue(sessionId, out ISshClientWrapper? client) ? client : null;

        public Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>装成一台通过 POSIX 探针、静态信息探测返回空输出的主机。</summary>
        private static ISshClientWrapper CreatePosixClient()
        {
            ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client
                .RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new RemoteCommandResult(RemoteShellProbe.PosixMarker, "", 0)));
            client
                .RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(""));
            return client;
        }
    }
}

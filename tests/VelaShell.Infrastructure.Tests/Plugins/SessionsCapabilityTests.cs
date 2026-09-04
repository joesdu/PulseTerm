using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 会话能力里"开会话"那一半。它是一次实打实的权限扩张,所以这里验的不是
/// "能不能连上"(那是连接服务的事),而是<b>闸门有没有关严</b>:
/// 只能开已保存的配置、宿主能拒、拒绝与连不上是两种结局、只关得掉自己开的那条。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class SessionsCapabilityTests
{
    /// <summary>脚本化的开会话实现:按剧本给结局,并把连上的会话塞进连接服务。</summary>
    private sealed class ScriptedOpener(PluginSessionOpenResult result, List<SshSession> sessions) : IPluginSessionOpener
    {
        public int Calls { get; private set; }

        public string? LastReason { get; private set; }

        public List<Guid> Closed { get; } = [];

        public Task<PluginSessionOpenResult> OpenAsync(string pluginId, SessionProfile profile, string reason,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastReason = reason;
            if (result.Outcome == PluginSessionOpenOutcome.Opened)
            {
                sessions.Add(NewSession(result.SessionId, profile.Host, profile.Port, profile.Username));
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

    private static SshSession NewSession(Guid id, string host, int port, string user) => new()
    {
        SessionId = id,
        ConnectionInfo = new() { Host = host, Port = port, Username = user, AuthMethod = AuthMethod.Password },
        Status = SessionStatus.Connected,
        ConnectedAt = DateTime.UtcNow
    };

    private static SessionProfile NewProfile(string name = "prod-1", string host = "10.0.0.1", string user = "root") =>
        new() { Name = name, Host = host, Port = 22, Username = user, ConnectionType = ConnectionType.SSH };

    /// <summary>连接服务替身:会话表是一个可变列表,连上/断开直接改它。</summary>
    private static ISshConnectionService NewConnections(List<SshSession> sessions)
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.Sessions.Returns(_ => sessions);
        connections.GetSession(Arg.Any<Guid>())
                   .Returns(call => sessions.Find(s => s.SessionId == call.Arg<Guid>()));
        connections.DisconnectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       sessions.RemoveAll(s => s.SessionId == call.Arg<Guid>());
                       return Task.CompletedTask;
                   });
        return connections;
    }

    private static ISessionRepository NewRepository(params SessionProfile[] profiles)
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(profiles.ToList()));
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));
        repository.GetSessionAsync(Arg.Any<Guid>())
                  .Returns(call => Task.FromResult(Array.Find(profiles, p => p.Id == call.Arg<Guid>())));
        return repository;
    }

    [TestMethod]
    public async Task ListSavedAsync_ReportsConfigsThatAreNotConnected_WithoutCredentials()
    {
        SessionProfile profile = NewProfile();
        profile.Password = "hunter2";
        var group = new ServerGroup { Name = "生产" };
        profile.GroupId = group.Id;
        ISessionRepository repository = NewRepository(profile);
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup> { group }));
        var capability = new SessionsCapability("acme.p", NewConnections([]), repository);

        IReadOnlyList<SavedSessionInfo> saved = await capability.ListSavedAsync();

        // 一条都没连着,但它们照样要报出来 —— 这正是 ListAsync 给不了的那部分。
        SavedSessionInfo only = saved.Single();
        Assert.AreEqual(profile.Id.ToString(), only.SavedSessionId);
        Assert.AreEqual("prod-1", only.Name);
        Assert.AreEqual("生产", only.Group);
        Assert.AreEqual(22, only.Port);
    }

    /// <summary>
    /// 非 SSH 的配置(SFTP / FTP / 插件协议)不进列表:<c>OpenAsync</c> 开不出
    /// <see cref="SessionInfo" /> 来,列出去只是发一个注定失败的 id。
    /// </summary>
    [TestMethod]
    public async Task ListSavedAsync_SkipsProfilesThatCannotBeOpenedAsSshSessions()
    {
        SessionProfile ssh = NewProfile();
        SessionProfile ftp = NewProfile("nas", "10.0.0.9", "ftp");
        ftp.ConnectionType = ConnectionType.FTP;
        var capability = new SessionsCapability("acme.p", NewConnections([]), NewRepository(ssh, ftp));

        IReadOnlyList<SavedSessionInfo> saved = await capability.ListSavedAsync();

        Assert.HasCount(1, saved);
        Assert.AreEqual(ssh.Id.ToString(), saved[0].SavedSessionId);
    }

    [TestMethod]
    public async Task OpenAsync_UnknownSavedSession_ThrowsSessionNotFound()
    {
        var capability = new SessionsCapability("acme.p", NewConnections([]), NewRepository());
        await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => capability.OpenAsync(Guid.NewGuid().ToString(), new("查磁盘")));
    }

    /// <summary>没有开会话实现(headless / 无界面宿主)= 没人可问 = 拒绝,绝不静默放行。</summary>
    [TestMethod]
    public async Task OpenAsync_WithoutOpener_IsDeniedRatherThanSilentlyAllowed()
    {
        SessionProfile profile = NewProfile();
        var capability = new SessionsCapability("acme.p", NewConnections([]), NewRepository(profile));
        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(
            () => capability.OpenAsync(profile.Id.ToString(), new("查磁盘")));
    }

    [TestMethod]
    public async Task OpenAsync_UserDenied_ThrowsPermissionDenied_NotOpenFailure()
    {
        SessionProfile profile = NewProfile();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Denied("用户点了拒绝"), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        // "不让你连"与"没连上"处置不同:前者重试没有意义,合成一个异常就只能靠读文本去猜。
        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(
            () => capability.OpenAsync(profile.Id.ToString(), new("查磁盘")));
    }

    [TestMethod]
    public async Task OpenAsync_ConnectFailed_ThrowsSessionOpenException()
    {
        SessionProfile profile = NewProfile();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Failed("Connection timed out"), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        await Assert.ThrowsExactlyAsync<PluginSessionOpenException>(
            () => capability.OpenAsync(profile.Id.ToString(), new("查磁盘")));
    }

    /// <summary>理由是给用户看的:空理由等于把确认框变成一个只能盲点的按钮。</summary>
    [TestMethod]
    public async Task OpenAsync_BlankReason_IsRejectedBeforeAnyoneIsAsked()
    {
        SessionProfile profile = NewProfile();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(Guid.NewGuid()), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => capability.OpenAsync(profile.Id.ToString(), new("   ")));
        Assert.AreEqual(0, opener.Calls);
    }

    [TestMethod]
    public async Task OpenAsync_PassesTheReasonThroughUnchanged()
    {
        SessionProfile profile = NewProfile();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(Guid.NewGuid()), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        const string reason = "AI 助手:飞书群 运维值班 里 张三 要求查看 nginx 日志";
        SessionInfo opened = await capability.OpenAsync(profile.Id.ToString(), new(reason));

        Assert.AreEqual(reason, opener.LastReason, "理由必须原样送到确认框,不许宿主改写");
        Assert.AreEqual(SessionState.Connected, opened.State);
        Assert.AreEqual("10.0.0.1", opened.Host);
    }

    /// <summary>已经连着的同一台机器默认复用,不再开第二条(也就不再弹第二个确认框)。</summary>
    [TestMethod]
    public async Task OpenAsync_ReuseConnected_ReturnsTheExistingSessionWithoutAsking()
    {
        SessionProfile profile = NewProfile();
        var existing = Guid.NewGuid();
        List<SshSession> sessions = [NewSession(existing, profile.Host, profile.Port, profile.Username)];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(Guid.NewGuid()), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        SessionInfo reused = await capability.OpenAsync(profile.Id.ToString(), new("查磁盘"));

        Assert.AreEqual(existing.ToString(), reused.SessionId);
        Assert.AreEqual(0, opener.Calls);
    }

    [TestMethod]
    public async Task OpenAsync_ReuseDisabled_OpensASecondSession()
    {
        SessionProfile profile = NewProfile();
        List<SshSession> sessions = [NewSession(Guid.NewGuid(), profile.Host, profile.Port, profile.Username)];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(Guid.NewGuid()), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        await capability.OpenAsync(profile.Id.ToString(), new("查磁盘", ReuseConnected: false));

        Assert.AreEqual(1, opener.Calls);
        Assert.HasCount(2, sessions);
    }

    [TestMethod]
    public async Task CloseAsync_ClosesWhatThisPluginOpened()
    {
        SessionProfile profile = NewProfile();
        var opened = Guid.NewGuid();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(opened), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        SessionInfo session = await capability.OpenAsync(profile.Id.ToString(), new("查磁盘"));
        await capability.CloseAsync(session.SessionId);

        Assert.AreSequenceEqual([opened], opener.Closed);
    }

    /// <summary>
    /// 用户自己开的会话不归插件管 —— 一个能挂断别人正在用的终端的接口,不该存在。
    /// 复用拿到的那条同样如此:它是用户的,不是插件开的。
    /// </summary>
    [TestMethod]
    public async Task CloseAsync_RefusesSessionsThisPluginDidNotOpen()
    {
        SessionProfile profile = NewProfile();
        var users = Guid.NewGuid();
        List<SshSession> sessions = [NewSession(users, profile.Host, profile.Port, profile.Username)];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(Guid.NewGuid()), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        SessionInfo reused = await capability.OpenAsync(profile.Id.ToString(), new("查磁盘"));
        Assert.AreEqual(users.ToString(), reused.SessionId);

        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => capability.CloseAsync(reused.SessionId));
        Assert.IsEmpty(opener.Closed);
        Assert.HasCount(1, sessions, "用户的会话必须原封不动");
    }

    /// <summary>会话已经不在了(用户先手动关了)不算错:此方法幂等。</summary>
    [TestMethod]
    public async Task CloseAsync_IsIdempotentWhenTheSessionIsAlreadyGone()
    {
        SessionProfile profile = NewProfile();
        var opened = Guid.NewGuid();
        List<SshSession> sessions = [];
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(opened), sessions);
        var capability = new SessionsCapability("acme.p", NewConnections(sessions), NewRepository(profile), opener);

        SessionInfo session = await capability.OpenAsync(profile.Id.ToString(), new("查磁盘"));
        sessions.Clear(); // 用户手动关掉了那个标签页
        await capability.CloseAsync(session.SessionId);

        Assert.IsEmpty(opener.Closed, "会话都没了,不必再劳烦界面层");
    }

    /// <summary>另一个插件开的会话,同样关不掉 —— 归属账本是按插件计的。</summary>
    [TestMethod]
    public async Task CloseAsync_RefusesSessionsOpenedByAnotherPlugin()
    {
        SessionProfile profile = NewProfile();
        var opened = Guid.NewGuid();
        List<SshSession> sessions = [];
        ISshConnectionService connections = NewConnections(sessions);
        ISessionRepository repository = NewRepository(profile);
        var opener = new ScriptedOpener(PluginSessionOpenResult.Opened(opened), sessions);

        var first = new SessionsCapability("acme.first", connections, repository, opener);
        var second = new SessionsCapability("acme.second", connections, repository, opener);

        SessionInfo session = await first.OpenAsync(profile.Id.ToString(), new("查磁盘"));
        await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => second.CloseAsync(session.SessionId));
    }
}

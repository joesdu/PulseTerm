using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

[TestClass]
public sealed class ConnectionWorkflowServiceTests
{
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISshConnectionService _sshConnectionService = Substitute.For<ISshConnectionService>();

    [TestMethod]
    public async Task SaveProfileAsync_PersistsProfile()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        SessionProfile result = await service.SaveProfileAsync(profile);
        Assert.AreSame(profile, result);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    public async Task ConnectProfileAsync_SavesLastConnectedAt()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        var session = new SshSession
        {
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(session);
        SshSession result = await service.ConnectProfileAsync(profile);
        Assert.AreSame(session, result);
        Assert.IsNotNull(profile.LastConnectedAt);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    public async Task TestConnectionAsync_WhenConnectFails_ReturnsFailureResult()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns<Task<SshSession>>(_ => throw new InvalidOperationException("boom"));
        ConnectionTestResult result = await service.TestConnectionAsync(profile);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("boom", result.ErrorMessage);
    }

    [TestMethod]
    public async Task GetSavedProfilesAsync_ReturnsSortedProfiles()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile first = CreateProfile("b", DateTime.UtcNow.AddMinutes(-10));
        SessionProfile second = CreateProfile("a", DateTime.UtcNow);
        _sessionRepository.GetAllSessionsAsync().Returns([first, second]);
        IReadOnlyList<SessionProfile> result = await service.GetSavedProfilesAsync();
        Assert.AreSame(second, result[0]);
        Assert.AreSame(first, result[1]);
    }

    /// <summary>
    /// 插件连接类型的「测试」必须走探针,**绝不能**落到 SSH 那条路上。
    /// <para>
    /// 曾经就是落下去的:一条 Redis 配置按「测试」,宿主拿 SSH 去连 6379,
    /// TCP 连上、版本交换卡死,最后报"连接超时" —— 用户按这个提示去查防火墙,
    /// 而真实原因(口令/库号/TLS)一个都没被测到。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TestConnectionAsync_PluginProfile_UsesProbeInsteadOfSsh()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreatePluginProfile();
        SessionProfile? probed = null;
        service.PluginProbe = (candidate, _) =>
        {
            probed = candidate;
            return Task.CompletedTask;
        };

        ConnectionTestResult result = await service.TestConnectionAsync(profile);

        Assert.IsTrue(result.Success);
        Assert.AreSame(profile, probed);
        await _sshConnectionService.DidNotReceive()
                                  .ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnectionAsync_PluginProbeThrows_ReportsProbeMessage()
    {
        ConnectionWorkflowService service = CreateService();
        service.PluginProbe = (_, _) => throw new InvalidOperationException("wrong password");

        ConnectionTestResult result = await service.TestConnectionAsync(CreatePluginProfile());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("wrong password", result.ErrorMessage);
    }

    /// <summary>没接探针时要明确说"测不了",而不是拿 SSH 去撞插件端口撞出一个假原因。</summary>
    [TestMethod]
    public async Task TestConnectionAsync_PluginProfileWithoutProbe_DoesNotFallBackToSsh()
    {
        ConnectionWorkflowService service = CreateService();

        ConnectionTestResult result = await service.TestConnectionAsync(CreatePluginProfile());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
        await _sshConnectionService.DidNotReceive()
                                  .ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    private ConnectionWorkflowService CreateService() => new(_sessionRepository, _sshConnectionService);

    private static SessionProfile CreatePluginProfile() => new()
    {
        Name = "local-redis",
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = "redis",
        Host = "127.0.0.1",
        Port = 6379,
        AuthMethod = AuthMethod.Password
    };

    private static SessionProfile CreateProfile(string name = "server", DateTime? lastConnectedAt = null)
    {
        return new()
        {
            Name = name,
            Host = "localhost",
            Port = 22,
            Username = "tester",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            LastConnectedAt = lastConnectedAt
        };
    }
}

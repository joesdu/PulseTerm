using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Infrastructure.Tunnels;

namespace VelaShell.Core.Tests.Tunnels;

[TestClass]
[TestCategory("Tunnel")]
public class TunnelServiceTests
{
    private readonly ISshClientWrapper _mockClientWrapper;
    private readonly ISshConnectionService _mockConnectionService;
    private readonly Guid _sessionId;

    public TunnelServiceTests()
    {
        _mockConnectionService = Substitute.For<ISshConnectionService>();
        _mockClientWrapper = Substitute.For<ISshClientWrapper>();
        _sessionId = Guid.NewGuid();
        var mockSession = new SshSession
        {
            SessionId = _sessionId,
            Status = SessionStatus.Connected,
            ConnectionInfo = new()
            {
                Host = "localhost",
                Port = 22,
                Username = "test",
                AuthMethod = AuthMethod.Password
            }
        };
        _mockConnectionService.GetSession(_sessionId).Returns(mockSession);
        _mockClientWrapper.IsConnected.Returns(true);
    }

    /// <summary>
    /// 构造被测服务。端口占用探测默认注入"全都空闲",否则用例里那些 5432 / 3306
    /// 就要看运行机器上恰好有没有数据库在监听 —— 测试的结论必须由代码决定,而不是由机器决定。
    /// </summary>
    private TunnelService CreateService(Func<string, uint, bool>? isLocalPortInUse = null) =>
        new(_mockConnectionService, _ => _mockClientWrapper, null, isLocalPortInUse ?? ((_, _) => false));

    [TestMethod]
    public async Task CreateLocalForwardAsync_CreatesActiveTunnel()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "DB Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db.example.com",
            RemotePort = 5432
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel);
        Assert.AreNotEqual(Guid.Empty, tunnel.Id);
        Assert.AreEqual(config, tunnel.Config);
        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
        Assert.AreEqual(_sessionId, tunnel.SessionId);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(1), (DateTime.UtcNow - tunnel.CreatedAt).Duration());
    }

    [TestMethod]
    public async Task CreateRemoteForwardAsync_CreatesActiveTunnel()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "Web Server",
            LocalHost = "127.0.0.1",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 8080
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel);
        Assert.AreNotEqual(Guid.Empty, tunnel.Id);
        Assert.AreEqual(config, tunnel.Config);
        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
        Assert.AreEqual(_sessionId, tunnel.SessionId);
    }

    [TestMethod]
    public async Task StopTunnelAsync_ChangesTunnelStatusToStopped()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Test Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "mysql.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        await service.StopTunnelAsync(tunnel.Id);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        TunnelInfo? stoppedTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel.Id);
        Assert.AreEqual(TunnelStatus.Stopped, stoppedTunnel?.Status);
    }

    [TestMethod]
    public async Task GetActiveTunnels_ReturnsOnlyActiveTunnels()
    {
        var config1 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 1",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db1.example.com",
            RemotePort = 5432
        };
        var config2 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 2",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "db2.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        TunnelInfo tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        Assert.HasCount(2, activeTunnels);
        Assert.Contains(t => t.Id == tunnel1.Id, activeTunnels);
        Assert.Contains(t => t.Id == tunnel2.Id, activeTunnels);
    }

    [TestMethod]
    public async Task IndividualTunnelFailure_DoesNotAffectOtherTunnels()
    {
        var config1 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 1",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db1.example.com",
            RemotePort = 5432
        };
        var config2 = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Tunnel 2",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "db2.example.com",
            RemotePort = 3306
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        TunnelInfo tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);
        await service.StopTunnelAsync(tunnel1.Id);
        IReadOnlyList<TunnelInfo> activeTunnels = service.GetActiveTunnels(_sessionId);
        TunnelInfo? stoppedTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel1.Id);
        TunnelInfo? activeTunnel = activeTunnels.FirstOrDefault(t => t.Id == tunnel2.Id);
        Assert.AreEqual(TunnelStatus.Stopped, stoppedTunnel?.Status);
        Assert.AreEqual(TunnelStatus.Active, activeTunnel?.Status);
    }

    [TestMethod]
    public async Task TunnelConfig_StoredForReconnectRecreation()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "DB Tunnel",
            LocalHost = "127.0.0.1",
            LocalPort = 5432,
            RemoteHost = "db.example.com",
            RemotePort = 5432
        };
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, config);
        Assert.IsNotNull(tunnel.Config);
        Assert.AreEqual(TunnelType.LocalForward, tunnel.Config.Type);
        Assert.AreEqual("DB Tunnel", tunnel.Config.Name);
        Assert.AreEqual("127.0.0.1", tunnel.Config.LocalHost);
        Assert.AreEqual(5432u, tunnel.Config.LocalPort);
        Assert.AreEqual("db.example.com", tunnel.Config.RemoteHost);
        Assert.AreEqual(5432u, tunnel.Config.RemotePort);
    }

    // ———— 端口冲突预检 ————

    /// <summary>本地端口已被占用时,在建立转发之前就报清楚,而不是让底层套接字抛错误码。</summary>
    [TestMethod]
    public async Task CreateLocalForwardAsync_ThrowsPortInUse_BeforeTouchingTheClient()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = "Occupied",
            LocalHost = "127.0.0.1",
            LocalPort = 27017,
            RemoteHost = "127.0.0.1",
            RemotePort = 27017
        };
        TunnelService service = CreateService((host, port) => host == "127.0.0.1" && port == 27017);

        TunnelPortInUseException error = await Assert.ThrowsExactlyAsync<TunnelPortInUseException>(
            async () => await service.CreateLocalForwardAsync(_sessionId, config));

        Assert.AreEqual(27017u, error.Port);
        await _mockClientWrapper.DidNotReceive().StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>());
        Assert.IsEmpty(service.GetActiveTunnels(_sessionId), "预检拦下的隧道不该在列表里留下残骸。");
    }

    /// <summary>动态转发同样监听在本机,同样要预检。</summary>
    [TestMethod]
    public async Task CreateDynamicForwardAsync_ThrowsPortInUse()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.DynamicForward,
            Name = "SOCKS",
            LocalHost = "127.0.0.1",
            LocalPort = 1080
        };
        TunnelService service = CreateService((_, port) => port == 1080);

        await Assert.ThrowsExactlyAsync<TunnelPortInUseException>(
            async () => await service.CreateDynamicForwardAsync(_sessionId, config));
    }

    /// <summary>远程转发的监听在服务器上,本机端口占用与它无关,不该被预检拦下。</summary>
    [TestMethod]
    public async Task CreateRemoteForwardAsync_IgnoresLocalPortOccupancy()
    {
        var config = new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = "Expose",
            LocalHost = "127.0.0.1",
            LocalPort = 8080,
            RemoteHost = "127.0.0.1",
            RemotePort = 8080
        };
        TunnelService service = CreateService((_, _) => true);

        TunnelInfo tunnel = await service.CreateRemoteForwardAsync(_sessionId, config);

        Assert.AreEqual(TunnelStatus.Active, tunnel.Status);
    }

    // ———— 流量统计 ————

    /// <summary>活动隧道的连接数与流量从底层句柄同步到 TunnelInfo,供界面读取。</summary>
    [TestMethod]
    public async Task RefreshStatistics_CopiesCountersFromHandle()
    {
        var handle = Substitute.For<IPortForwardHandle>();
        handle.BytesTransferred.Returns(4096L);
        handle.TotalConnections.Returns(3);
        handle.ActiveConnections.Returns(1);
        _mockClientWrapper.StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>()).Returns(handle);
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, LocalConfig(5432));

        service.RefreshStatistics();

        Assert.AreEqual(4096L, tunnel.BytesTransferred);
        Assert.AreEqual(3, tunnel.TotalConnections);
        Assert.AreEqual(1, tunnel.ActiveConnections);
    }

    /// <summary>
    /// 停止隧道要先把最后一次读数取下来:句柄一释放就再也问不到,而界面在
    /// "已停止"状态下仍要显示这条隧道跑过的总量。
    /// </summary>
    [TestMethod]
    public async Task StopTunnelAsync_KeepsFinalCounters_AndClearsActive()
    {
        var handle = Substitute.For<IPortForwardHandle>();
        handle.BytesTransferred.Returns(8192L);
        handle.TotalConnections.Returns(5);
        handle.ActiveConnections.Returns(2);
        _mockClientWrapper.StartPortForwardAsync(Arg.Any<PortForwardRequest>(), Arg.Any<CancellationToken>()).Returns(handle);
        TunnelService service = CreateService();
        TunnelInfo tunnel = await service.CreateLocalForwardAsync(_sessionId, LocalConfig(5432));

        await service.StopTunnelAsync(tunnel.Id);

        Assert.AreEqual(8192L, tunnel.BytesTransferred);
        Assert.AreEqual(5, tunnel.TotalConnections);
        Assert.AreEqual(0, tunnel.ActiveConnections, "停掉之后不该还有在传的连接。");
    }

    private static TunnelConfig LocalConfig(uint port) =>
        new()
        {
            Type = TunnelType.LocalForward,
            Name = "Metered",
            LocalHost = "127.0.0.1",
            LocalPort = port,
            RemoteHost = "127.0.0.1",
            RemotePort = port
        };
}

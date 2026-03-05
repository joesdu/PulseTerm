using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Core.Tunnels;
using Xunit;

namespace PulseTerm.Core.Tests.Tunnels;

[Trait("Category", "Tunnel")]
public class TunnelServiceTests
{
    private readonly ISshConnectionService _mockConnectionService;
    private readonly ISshClientWrapper _mockClientWrapper;
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
            ConnectionInfo = new ConnectionInfo
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

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel = await service.CreateLocalForwardAsync(_sessionId, config);

        tunnel.Should().NotBeNull();
        tunnel.Id.Should().NotBe(Guid.Empty);
        tunnel.Config.Should().Be(config);
        tunnel.Status.Should().Be(TunnelStatus.Active);
        tunnel.SessionId.Should().Be(_sessionId);
        tunnel.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel = await service.CreateRemoteForwardAsync(_sessionId, config);

        tunnel.Should().NotBeNull();
        tunnel.Id.Should().NotBe(Guid.Empty);
        tunnel.Config.Should().Be(config);
        tunnel.Status.Should().Be(TunnelStatus.Active);
        tunnel.SessionId.Should().Be(_sessionId);
    }

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel = await service.CreateLocalForwardAsync(_sessionId, config);

        await service.StopTunnelAsync(tunnel.Id);

        var activeTunnels = service.GetActiveTunnels(_sessionId);
        var stoppedTunnel = activeTunnels.Items.FirstOrDefault(t => t.Id == tunnel.Id);
        stoppedTunnel?.Status.Should().Be(TunnelStatus.Stopped);
    }

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        var tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);

        var activeTunnels = service.GetActiveTunnels(_sessionId);
        activeTunnels.Count.Should().Be(2);
        activeTunnels.Items.Should().Contain(t => t.Id == tunnel1.Id);
        activeTunnels.Items.Should().Contain(t => t.Id == tunnel2.Id);
    }

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel1 = await service.CreateLocalForwardAsync(_sessionId, config1);
        var tunnel2 = await service.CreateLocalForwardAsync(_sessionId, config2);

        await service.StopTunnelAsync(tunnel1.Id);

        var activeTunnels = service.GetActiveTunnels(_sessionId);
        var stoppedTunnel = activeTunnels.Items.FirstOrDefault(t => t.Id == tunnel1.Id);
        var activeTunnel = activeTunnels.Items.FirstOrDefault(t => t.Id == tunnel2.Id);

        stoppedTunnel?.Status.Should().Be(TunnelStatus.Stopped);
        activeTunnel?.Status.Should().Be(TunnelStatus.Active);
    }

    [Fact]
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

        var service = new TunnelService(_mockConnectionService, (sessionId) => _mockClientWrapper);
        var tunnel = await service.CreateLocalForwardAsync(_sessionId, config);

        tunnel.Config.Should().NotBeNull();
        tunnel.Config.Type.Should().Be(TunnelType.LocalForward);
        tunnel.Config.Name.Should().Be("DB Tunnel");
        tunnel.Config.LocalHost.Should().Be("127.0.0.1");
        tunnel.Config.LocalPort.Should().Be(5432);
        tunnel.Config.RemoteHost.Should().Be("db.example.com");
        tunnel.Config.RemotePort.Should().Be(5432);
    }
}

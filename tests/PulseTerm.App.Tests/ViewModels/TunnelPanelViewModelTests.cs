using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Tunnels;

namespace PulseTerm.App.Tests.ViewModels;

public class TunnelPanelViewModelTests
{
    private readonly ITunnelService _tunnelService;
    private readonly Guid _sessionId;
    private readonly TunnelPanelViewModel _vm;

    public TunnelPanelViewModelTests()
    {
        _tunnelService = Substitute.For<ITunnelService>();
        _sessionId = Guid.NewGuid();
        _vm = new TunnelPanelViewModel(_tunnelService, _sessionId);
    }

    private static TunnelInfo CreateTunnelInfo(
        TunnelType type = TunnelType.LocalForward,
        TunnelStatus status = TunnelStatus.Active,
        string name = "test-tunnel",
        string localHost = "localhost",
        uint localPort = 3306,
        string remoteHost = "db-server",
        uint remotePort = 3306,
        long bytesTransferred = 0)
    {
        return new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = new TunnelConfig
            {
                Type = type,
                Name = name,
                LocalHost = localHost,
                LocalPort = localPort,
                RemoteHost = remoteHost,
                RemotePort = remotePort
            },
            Status = status,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = bytesTransferred
        };
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public async Task CreateTunnel_WithValidForm_AddsTunnelToList()
    {
        var tunnelInfo = CreateTunnelInfo();
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;
        _vm.NewTunnelType = TunnelType.LocalForward;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        _vm.Tunnels.Should().HaveCount(1);
        _vm.Tunnels[0].Name.Should().Be("test-tunnel");
        _vm.Tunnels[0].LocalPort.Should().Be(3306);
        _vm.Tunnels[0].RemoteHost.Should().Be("db-server");
    }

    [Theory]
    [Trait("Category", "TunnelUI")]
    [InlineData(0, 3306, false)]
    [InlineData(3306, 0, false)]
    [InlineData(-1, 3306, false)]
    [InlineData(3306, -1, false)]
    [InlineData(65536, 3306, false)]
    [InlineData(3306, 65536, false)]
    [InlineData(3306, 3306, true)]
    public void CreateTunnel_ValidatesPortRange(int localPort, int remotePort, bool expectedValid)
    {
        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = "remote";
        _vm.NewRemotePort = remotePort;

        _vm.IsFormValid.Should().Be(expectedValid);
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public async Task StopTunnel_ChangesTunnelStatusToStopped()
    {
        var tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        _vm.Tunnels[0].Status.Should().Be(TunnelStatus.Active);

        await _vm.StopTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        _vm.Tunnels[0].Status.Should().Be(TunnelStatus.Stopped);
        await _tunnelService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public async Task DeleteTunnel_RemovesTunnelFromList()
    {
        var tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.NewRemoteHost = "db-server";
        _vm.NewRemotePort = 3306;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        _vm.Tunnels.Should().HaveCount(1);

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        _vm.Tunnels.Should().BeEmpty();
        await _tunnelService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait("Category", "TunnelUI")]
    [InlineData("", "localhost", 3306, "remote", 3306, false)]
    [InlineData("test", "", 3306, "remote", 3306, false)]
    [InlineData("test", "localhost", 0, "remote", 3306, false)]
    [InlineData("test", "localhost", 3306, "", 3306, false)]
    [InlineData("test", "localhost", 3306, "remote", 0, false)]
    [InlineData("test", "localhost", 3306, "remote", 3306, true)]
    public void PortValidation_RequiredFieldsMustBeNonEmptyNonZero(
        string name, string localHost, int localPort, string remoteHost, int remotePort, bool expectedValid)
    {
        _vm.NewTunnelName = name;
        _vm.NewLocalHost = localHost;
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;

        _vm.IsFormValid.Should().Be(expectedValid);
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public async Task CreateTunnel_RemoteForward_UsesCorrectServiceMethod()
    {
        var tunnelInfo = CreateTunnelInfo(type: TunnelType.RemoteForward);
        _tunnelService.CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "remote-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 8080;
        _vm.NewRemoteHost = "web-server";
        _vm.NewRemotePort = 80;
        _vm.NewTunnelType = TunnelType.RemoteForward;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        await _tunnelService.Received(1).CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        await _tunnelService.DidNotReceive().CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        _vm.Tunnels.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public void TunnelItemViewModel_DisplayFormat_IsCorrect()
    {
        var tunnelInfo = CreateTunnelInfo(
            localHost: "localhost",
            localPort: 3306,
            remoteHost: "db-server",
            remotePort: 3306);

        var itemVm = new TunnelItemViewModel(tunnelInfo);

        itemVm.DisplayRoute.Should().Be("localhost:3306 → db-server:3306");
        itemVm.TypeBadge.Should().Be("L");
    }

    [Theory]
    [Trait("Category", "TunnelUI")]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.0 GB")]
    public void TunnelItemViewModel_BytesTransferred_FormatsCorrectly(long bytes, string expected)
    {
        TunnelItemViewModel.FormatBytes(bytes).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "TunnelUI")]
    public async Task CreateTunnel_ResetsFormAfterSuccess()
    {
        var tunnelInfo = CreateTunnelInfo();
        _tunnelService.CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tunnelInfo));

        _vm.NewTunnelName = "test";
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 8080;
        _vm.NewRemoteHost = "server";
        _vm.NewRemotePort = 80;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        _vm.NewTunnelName.Should().BeEmpty();
        _vm.NewLocalHost.Should().Be("localhost");
        _vm.NewLocalPort.Should().Be(0);
        _vm.NewRemoteHost.Should().BeEmpty();
        _vm.NewRemotePort.Should().Be(0);
        _vm.NewTunnelType.Should().Be(TunnelType.LocalForward);
    }
}

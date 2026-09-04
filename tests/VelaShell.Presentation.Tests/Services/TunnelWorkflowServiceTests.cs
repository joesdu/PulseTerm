using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Tunnels;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

[TestClass]
public sealed class TunnelWorkflowServiceTests
{
    private readonly ITunnelService _tunnelService = Substitute.For<ITunnelService>();
    private readonly Guid _sessionId = Guid.NewGuid();

    private TunnelWorkflowService CreateService() => new(_tunnelService);

    private static TunnelConfig MakeConfig(TunnelType type = TunnelType.LocalForward) =>
        new()
        {
            Type = type,
            Name = "test",
            LocalHost = "127.0.0.1",
            LocalPort = 3306,
            RemoteHost = "db",
            RemotePort = 3306
        };

    private static TunnelInfo MakeInfo(TunnelType type = TunnelType.LocalForward) =>
        new()
        {
            Id = Guid.NewGuid(),
            Config = MakeConfig(type),
            Status = TunnelStatus.Active,
            SessionId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public void Constructor_NullService_Throws() => Assert.ThrowsExactly<ArgumentNullException>(() => new TunnelWorkflowService(null!));

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public void GetActiveTunnels_ReturnsSnapshot()
    {
        TunnelInfo info = MakeInfo();
        var list = new List<TunnelInfo>
        {
            info
        };
        _tunnelService.GetActiveTunnels(_sessionId).Returns(list);

        IReadOnlyList<TunnelInfo> result = CreateService().GetActiveTunnels(_sessionId);
        list.Add(MakeInfo());

        Assert.HasCount(1, result);
        Assert.AreSame(info, result[0]);
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task CreateTunnelAsync_LocalForward_RoutesCorrectly()
    {
        TunnelInfo expected = MakeInfo(TunnelType.LocalForward);
        using var cancellation = new CancellationTokenSource();
        _tunnelService
            .CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), cancellation.Token)
            .Returns(Task.FromResult(expected));

        TunnelInfo result = await CreateService().CreateTunnelAsync(
            _sessionId,
            MakeConfig(TunnelType.LocalForward),
            cancellation.Token);

        Assert.AreSame(expected, result);
        await _tunnelService.Received(1).CreateLocalForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), cancellation.Token);
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task CreateTunnelAsync_RemoteForward_RoutesCorrectly()
    {
        TunnelInfo expected = MakeInfo(TunnelType.RemoteForward);
        _tunnelService
            .CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        TunnelInfo result = await CreateService().CreateTunnelAsync(_sessionId, MakeConfig(TunnelType.RemoteForward));

        Assert.AreSame(expected, result);
        await _tunnelService.Received(1).CreateRemoteForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task CreateTunnelAsync_DynamicForward_RoutesCorrectly()
    {
        TunnelInfo expected = MakeInfo(TunnelType.DynamicForward);
        _tunnelService
            .CreateDynamicForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        TunnelInfo result = await CreateService().CreateTunnelAsync(_sessionId, MakeConfig(TunnelType.DynamicForward));

        Assert.AreSame(expected, result);
        await _tunnelService.Received(1).CreateDynamicForwardAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task CreateTunnelAsync_NullConfig_Throws()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => CreateService().CreateTunnelAsync(_sessionId, null!));
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task CreateTunnelAsync_UnsupportedEnum_ThrowsArgumentOutOfRange()
    {
        TunnelConfig config = new()
        {
            Type = (TunnelType)99,
            Name = "bad",
            LocalHost = "127.0.0.1",
            LocalPort = 8080
        };

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => CreateService().CreateTunnelAsync(_sessionId, config));
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task StopTunnelAsync_ForwardsToService()
    {
        var tunnelId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await CreateService().StopTunnelAsync(tunnelId, cancellation.Token);
        await _tunnelService.Received(1).StopTunnelAsync(tunnelId, cancellation.Token);
    }

    [TestMethod]
    [TestCategory("TunnelWorkflow")]
    public async Task RemoveTunnelAsync_ForwardsToService()
    {
        var tunnelId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await CreateService().RemoveTunnelAsync(tunnelId, cancellation.Token);
        await _tunnelService.Received(1).RemoveTunnelAsync(tunnelId, cancellation.Token);
    }
}

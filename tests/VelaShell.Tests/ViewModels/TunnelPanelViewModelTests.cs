using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class TunnelPanelViewModelTests
{
    private readonly SessionProfile _server;
    private readonly Guid _sessionId;
    private readonly ITunnelWorkflowService _workflowService;
    private readonly TunnelPanelViewModel _vm;

    public TunnelPanelViewModelTests()
    {
        _workflowService = Substitute.For<ITunnelWorkflowService>();
        _sessionId = Guid.NewGuid();
        _server = new() { Name = "srv", Host = "10.0.0.1", Username = "root" };

        // 面板以服务器为中心:后台连接器直接返回固定会话,存活检查恒真。
        _vm = new(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask);
        _vm.Servers.Add(_server);
        _vm.SelectedServer = _server;
    }

    // ———— 断线自动恢复 ————

    /// <summary>
    /// 承载会话掉线后,勾了「自动重连」的隧道会被自动重拨并重建 ——
    /// 这正是这个开关存在的意义,不然用户还是得手动点启动。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_SessionDropped_RebuildsAutoReconnectTunnel()
    {
        bool alive = true;
        var reconnectedSession = Guid.NewGuid();
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(reconnectedSession));
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        TunnelInfo rebuilt = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(reconnectedSession, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(rebuilt));

        vm.RefreshLiveState();

        await WaitForAsync(() => vm.Tunnels.Count == 1 && vm.Tunnels[0].IsActive);
        Assert.AreEqual(rebuilt.Id, vm.Tunnels[0].Id, "条目应换成重建后的那条隧道。");
        // 重建前先清掉服务侧停止状态的旧记录,否则列表里会越积越多。
        await _workflowService.Received().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>没勾自动重连的隧道掉线后只标记为已停止,等用户自己决定 —— 默认不替用户重拨。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_SessionDropped_LeavesManualTunnelStopped()
    {
        bool alive = true;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(Guid.NewGuid()));
        await SeedTunnelAsync(vm, autoReconnect: false);
        _workflowService.ClearReceivedCalls();

        alive = false;
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.HasCount(1, vm.Tunnels);
        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 重拨失败后要退避,不能每一拍时钟都重来一次 —— 服务器真的下线时,
    /// 每 5 秒一次的重连会把凭据提示刷满屏幕。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_ReconnectFailure_BacksOffBeforeRetrying()
    {
        bool alive = true;
        bool serverIsDown = false;
        int attempts = 0;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ =>
        {
            if (!serverIsDown)
            {
                return Task.FromResult(_sessionId);
            }
            Interlocked.Increment(ref attempts);
            return Task.FromException<Guid>(new InvalidOperationException("host down"));
        });
        await SeedTunnelAsync(vm, autoReconnect: true);

        alive = false;
        serverIsDown = true;
        vm.RefreshLiveState();
        await WaitForAsync(() => Volatile.Read(ref attempts) == 1);

        // 紧接着的几拍都落在退避窗口内,不该再拨。
        vm.RefreshLiveState();
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.AreEqual(1, Volatile.Read(ref attempts), "退避窗口内不该重复重拨。");
        Assert.IsNotNull(vm.ErrorMessage, "失败原因要让用户看得到。");
    }

    /// <summary>
    /// 用户按停过的隧道,后来会话掉线也不该被自动恢复拉起来 ——
    /// 「掉线后自动重连」扛的是网络抖动,不是把用户刚按下的停止键撤销掉。
    /// </summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task RefreshLiveState_DoesNotRebuild_TunnelTheUserStopped()
    {
        bool alive = true;
        TunnelPanelViewModel vm = CreateVm(() => alive, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: true);
        await vm.StopTunnelCommand.Execute(vm.Tunnels[0].Id).FirstAsync();
        _workflowService.ClearReceivedCalls();

        alive = false;
        vm.RefreshLiveState();
        await Task.Delay(150);

        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>自动重连开关随隧道配置往返:编辑既有隧道时表单要把它填回来。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_RestoresAutoReconnectIntoForm()
    {
        TunnelPanelViewModel vm = CreateVm(() => true, _ => Task.FromResult(_sessionId));
        await SeedTunnelAsync(vm, autoReconnect: true, status: TunnelStatus.Stopped);

        await vm.EditTunnelCommand.Execute(vm.Tunnels[0].Id).FirstAsync();

        Assert.IsTrue(vm.NewAutoReconnect);
    }

    /// <summary>新建表单里勾上自动重连,要原样落到隧道配置上。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_CarriesAutoReconnectIntoConfig()
    {
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(CreateTunnelInfo()));
        FillValidLocalForm();
        _vm.NewAutoReconnect = true;

        await _vm.CreateTunnelCommand.Execute().FirstAsync();

        Assert.IsTrue(captured?.AutoReconnect);
    }

    /// <summary>取消/重置表单后自动重连开关回到默认的关闭。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task ResetForm_ClearsAutoReconnect()
    {
        _vm.NewAutoReconnect = true;

        await _vm.ResetFormCommand.Execute().FirstAsync();

        Assert.IsFalse(_vm.NewAutoReconnect);
    }

    // ———— 流量统计 ————

    /// <summary>统计行:没连接过就直说,有连接则给出连接数与可读的流量。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_StatsText_ReflectsCounters()
    {
        TunnelInfo info = CreateTunnelInfo();
        var item = new TunnelItemViewModel(info);
        Assert.AreEqual(Strings.Get("Tunnel_StatsNone"), item.StatsText);

        info.TotalConnections = 3;
        info.BytesTransferred = 1536;
        item.RefreshLive();
        Assert.AreEqual(Strings.Format("Tunnel_Stats", 3, "1.5 KB"), item.StatsText);

        info.ActiveConnections = 2;
        item.RefreshLive();
        Assert.AreEqual(Strings.Format("Tunnel_StatsLive", 3, 2, "1.5 KB"), item.StatsText);
    }

    /// <summary>时钟每拍都要让服务把底层读数同步到界面持有的 TunnelInfo 上。</summary>
    [TestMethod]
    [TestCategory("TunnelUI")]
    public void RefreshLiveState_PullsStatisticsFromService()
    {
        _vm.RefreshLiveState();

        _workflowService.Received().RefreshStatistics();
    }

    /// <summary>装一个独立的面板,便于用例自己控制会话存活与重拨结果。</summary>
    private TunnelPanelViewModel CreateVm(Func<bool> isAlive, Func<SessionProfile, Task<Guid>> connector)
    {
        var vm = new TunnelPanelViewModel(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (profile, _) => connector(profile),
            _ => isAlive(),
            _ => Task.CompletedTask);
        vm.Servers.Add(_server);
        vm.SelectedServer = _server;
        return vm;
    }

    /// <summary>通过表单建出一条隧道,让面板处于"这台服务器上有一条隧道"的状态。</summary>
    private async Task SeedTunnelAsync(TunnelPanelViewModel vm, bool autoReconnect, TunnelStatus status = TunnelStatus.Active)
    {
        _workflowService.CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new TunnelInfo
                        {
                            Id = Guid.NewGuid(),
                            Config = callInfo.Arg<TunnelConfig>(),
                            Status = status,
                            SessionId = callInfo.Arg<Guid>(),
                            CreatedAt = DateTime.UtcNow
                        }));
        vm.NewTunnelName = "seeded";
        vm.NewLocalHost = "127.0.0.1";
        vm.NewLocalPort = 3306;
        vm.NewRemotePort = 3306;
        vm.NewTunnelType = TunnelType.LocalForward;
        vm.NewAutoReconnect = autoReconnect;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, vm.Tunnels);
    }

    /// <summary>等待某个条件成立(自动恢复是后台任务,不能建完就断言)。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.IsLessThanOrEqualTo(deadline, DateTime.UtcNow, "等待条件成立超时。");
            await Task.Delay(20);
        }
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
        return new()
        {
            Id = Guid.NewGuid(),
            Config = new()
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

    /// <summary>填一份合法的本地转发表单(目标默认锁定服务器本机,这里显式解锁自填)。</summary>
    private void FillValidLocalForm(string remoteHost = "db-server", int remotePort = 3306)
    {
        _vm.NewTunnelName = "test-tunnel";
        _vm.NewLocalHost = "localhost";
        _vm.NewLocalPort = 3306;
        _vm.ForwardToServerLoopback = false;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;
        _vm.NewTunnelType = TunnelType.LocalForward;
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_WithValidForm_AddsTunnelToList()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Tunnels);
        Assert.AreEqual("test-tunnel", _vm.Tunnels[0].Name);
        Assert.AreEqual(3306u, _vm.Tunnels[0].LocalPort);
        Assert.AreEqual("db-server", _vm.Tunnels[0].RemoteHost);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_WithoutSelectedServer_IsDisabled()
    {
        FillValidLocalForm();
        _vm.SelectedServer = null;
        Assert.IsFalse(await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_LoopbackDefault_TargetsServerItself()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(remoteHost: "127.0.0.1");
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 5432;
        _vm.NewRemotePort = 5432;
        // 默认 ForwardToServerLoopback = true:目标主机锁定 127.0.0.1(服务器视角)。
        Assert.IsTrue(_vm.ForwardToServerLoopback);
        Assert.IsFalse(_vm.IsRemoteHostEditable);
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.IsNotNull(captured);
        Assert.AreEqual("127.0.0.1", captured!.RemoteHost);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, 3306, false)]
    [DataRow(3306, 0, false)]
    [DataRow(-1, 3306, false)]
    [DataRow(3306, -1, false)]
    [DataRow(65536, 3306, false)]
    [DataRow(3306, 65536, false)]
    [DataRow(3306, 3306, true)]
    public async Task CreateTunnel_ValidatesPortRange(int localPort, int remotePort, bool expectedValid)
    {
        FillValidLocalForm(remotePort: remotePort);
        _vm.NewLocalPort = localPort;
        Assert.AreEqual(expectedValid, await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task StopTunnel_ChangesTunnelStatusToStopped()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(TunnelStatus.Active, _vm.Tunnels[0].Status);
        await _vm.StopTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        Assert.AreEqual(TunnelStatus.Stopped, _vm.Tunnels[0].Status);
        await _workflowService.Received(1).StopTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_RemovesTunnelFromList()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm();
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Tunnels);
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        Assert.IsEmpty(_vm.Tunnels);
        // 删除统一走 RemoveTunnelAsync(活动中的由服务先停再移除)。
        await _workflowService.Received(1).RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_ActiveTunnel_DoesNotEnterEditMode()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.EditTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.IsFalse(_vm.IsEditing);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task EditTunnel_StoppedTunnel_EntersEditMode()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Stopped);
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.EditTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.IsTrue(_vm.IsEditing);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_EditToolTip_TracksActiveStatus()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        List<string> changedProperties = [];
        itemVm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        Assert.AreEqual(Strings.Get("Tunnel_EditDisabledTip"), itemVm.EditToolTip);
        tunnelInfo.Status = TunnelStatus.Stopped;
        itemVm.RefreshLive();

        Assert.AreEqual(Strings.Get("Tunnel_EditTip"), itemVm.EditToolTip);
        Assert.Contains(nameof(TunnelItemViewModel.EditToolTip), changedProperties);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_WithoutConfirmationDelegate_IsNoOp()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.HasCount(1, _vm.Tunnels);
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_CancelledConfirmation_IsNoOp()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        _vm.ConfirmDelete = _ => Task.FromResult(false);

        await _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();

        Assert.HasCount(1, _vm.Tunnels);
        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_ConfirmationReleasesBeforeMutation_ReResolvesItem()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        TaskCompletionSource<bool> confirmationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.ConfirmDelete = async _ =>
        {
            confirmationStarted.SetResult(true);
            await releaseConfirmation.Task;
            return true;
        };

        Task deleteTask = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await confirmationStarted.Task;
        _vm.Tunnels.Clear();
        releaseConfirmation.SetResult(true);
        await deleteTask;

        await _workflowService.DidNotReceive().RemoveTunnelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_ConcurrentRequests_RemoveOnlyOnce()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _vm.Tunnels.Add(new TunnelItemViewModel(tunnelInfo));
        TaskCompletionSource<bool> releaseConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int confirmationCount = 0;
        _vm.ConfirmDelete = async _ =>
        {
            Interlocked.Increment(ref confirmationCount);
            await releaseConfirmation.Task;
            return true;
        };
        _workflowService.RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>())
                        .Returns(Task.CompletedTask);

        Task first = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await Task.Delay(20);
        Task second = _vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        releaseConfirmation.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, confirmationCount);
        await _workflowService.Received(1).RemoveTunnelAsync(tunnelInfo.Id, Arg.Any<CancellationToken>());
        Assert.IsEmpty(_vm.Tunnels);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow("", "localhost", 3306, "remote", 3306, true)] // 别名可选(设计 B3Rth)
    [DataRow("test", "", 3306, "remote", 3306, false)]
    [DataRow("test", "localhost", 0, "remote", 3306, false)]
    [DataRow("test", "localhost", 3306, "", 3306, false)]
    [DataRow("test", "localhost", 3306, "remote", 0, false)]
    [DataRow("test", "localhost", 3306, "remote", 3306, true)]
    public async Task PortValidation_RequiredFieldsMustBeNonEmptyNonZero(
        string name,
        string localHost,
        int localPort,
        string remoteHost,
        int remotePort,
        bool expectedValid)
    {
        _vm.ForwardToServerLoopback = false;
        _vm.NewTunnelName = name;
        _vm.NewLocalHost = localHost;
        _vm.NewLocalPort = localPort;
        _vm.NewRemoteHost = remoteHost;
        _vm.NewRemotePort = remotePort;
        Assert.AreEqual(expectedValid, await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DynamicForward_DoesNotRequireRemoteTarget()
    {
        _vm.NewTunnelName = string.Empty;
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 1080;
        _vm.NewRemoteHost = string.Empty;
        _vm.NewRemotePort = 0;
        _vm.NewTunnelTypeIndex = 2; // 动态 SOCKS
        Assert.IsTrue(await _vm.CreateTunnelCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_RemoteForward_RoutesViaWorkflowService()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward);
        TunnelConfig? captured = null;
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Do<TunnelConfig>(c => captured = c), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm("web-server", 80);
        _vm.NewTunnelName = "remote-tunnel";
        _vm.NewLocalPort = 8080;
        _vm.NewTunnelType = TunnelType.RemoteForward;
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        await _workflowService.Received(1).CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
        Assert.IsNotNull(captured);
        Assert.AreEqual(TunnelType.RemoteForward, captured!.Type);
        Assert.HasCount(1, _vm.Tunnels);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_LocalForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(localHost: "localhost",
            localPort: 3306,
            remoteHost: "db-server",
            remotePort: 3306);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteLocal", "localhost", 3306u, "db-server", 3306u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeLocal"), itemVm.TypeBadge);
        Assert.AreEqual("localhost:3306 → db-server:3306", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_RemoteForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward,
            localHost: "127.0.0.1",
            localPort: 3000,
            remoteHost: "0.0.0.0",
            remotePort: 8080);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteRemote", "127.0.0.1", 3000u, 8080u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeRemote"), itemVm.TypeBadge);
        Assert.AreEqual("0.0.0.0:8080 → 127.0.0.1:3000", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_DisplayRoute_DynamicForward()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.DynamicForward,
            localHost: "127.0.0.1",
            localPort: 1080);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        string expectedRoute = Strings.Format("Tunnel_RouteDynamic", "127.0.0.1", 1080u);
        Assert.AreEqual(expectedRoute, itemVm.DisplayRoute);
        Assert.AreEqual(Strings.Get("Tunnel_BadgeDynamic"), itemVm.TypeBadge);
        Assert.AreEqual("127.0.0.1:1080 (SOCKS5)", itemVm.EndpointSummary);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_UsesLocalizedTypeName()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.LocalForward, name: "");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackLocal"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_Remote()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.RemoteForward, name: null!);
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackRemote"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_BlankAlias_Dynamic()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(TunnelType.DynamicForward, name: "   ");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual(Strings.Get("Tunnel_FallbackDynamic"), itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_FallbackName_NonBlankAlias_KeepsOriginal()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(name: "my-db-tunnel");
        var itemVm = new TunnelItemViewModel(tunnelInfo);
        Assert.AreEqual("my-db-tunnel", itemVm.Name);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public void TunnelItemViewModel_ReflectsServiceSideStatusAndError()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo(status: TunnelStatus.Active);
        var itemVm = new TunnelItemViewModel(tunnelInfo);

        // 服务侧(会话断开/通道错误)直接改共享 TunnelInfo,条目应透传。
        tunnelInfo.Status = TunnelStatus.Stopped;
        tunnelInfo.LastError = "目标拒绝连接";
        itemVm.RefreshLive();
        Assert.AreEqual(TunnelStatus.Stopped, itemVm.Status);
        Assert.IsFalse(itemVm.IsActive);
        Assert.IsTrue(itemVm.HasError);
        Assert.AreEqual("目标拒绝连接", itemVm.LastError);
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    [DataRow(0, "0 B")]
    [DataRow(1024, "1.0 KB")]
    [DataRow(1048576, "1.0 MB")]
    [DataRow(1073741824, "1.0 GB")]
    public void TunnelItemViewModel_BytesTransferred_FormatsCorrectly(long bytes, string expected) => Assert.AreEqual(expected, TunnelItemViewModel.FormatBytes(bytes));

    /// <summary>带持久化存储的面板(隧道配置持久化,重启后手动启动)。</summary>
    private TunnelPanelViewModel CreateVmWithStore(IAppDataStore store)
    {
        var vm = new TunnelPanelViewModel(_workflowService,
            () => Task.FromResult<IReadOnlyList<SessionProfile>>([_server]),
            (_, _) => Task.FromResult(_sessionId),
            _ => true,
            _ => Task.CompletedTask,
            store);
        vm.Servers.Add(_server);
        return vm;
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_PersistsConfigsToStore()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        vm.NewTunnelName = "test-tunnel";
        vm.NewLocalHost = "localhost";
        vm.NewLocalPort = 3306;
        vm.ForwardToServerLoopback = false;
        vm.NewRemoteHost = "db-server";
        vm.NewRemotePort = 3306;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        await store.Received(1).UpsertAsync(
            "tunnels",
            _server.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 1 && list[0].Name == "test-tunnel"),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task DeleteTunnel_PersistsEmptyList()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        vm.NewTunnelName = "t";
        vm.NewLocalHost = "localhost";
        vm.NewLocalPort = 3306;
        vm.ForwardToServerLoopback = false;
        vm.NewRemoteHost = "db-server";
        vm.NewRemotePort = 3306;
        await vm.CreateTunnelCommand.Execute().FirstAsync();
        vm.ConfirmDelete = _ => Task.FromResult(true);
        await vm.DeleteTunnelCommand.Execute(tunnelInfo.Id).FirstAsync();
        await store.Received(1).UpsertAsync(
            "tunnels",
            _server.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task SelectServer_RestoresPersistedTunnels_StoppedAndNotAutoStarted()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        List<TunnelConfig> saved =
        [
            new()
            {
                Type = TunnelType.LocalForward,
                Name = "restored",
                LocalHost = "127.0.0.1",
                LocalPort = 8080,
                RemoteHost = "127.0.0.1",
                RemotePort = 80
            }
        ];
        store.GetAsync<List<TunnelConfig>>("tunnels", _server.Id.ToString("D"), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<List<TunnelConfig>?>(saved));
        TunnelPanelViewModel vm = CreateVmWithStore(store);
        vm.SelectedServer = _server;

        // 恢复为"已停止",等待用户手动启动;绝不自动建立转发。
        Assert.HasCount(1, vm.Tunnels);
        Assert.AreEqual("restored", vm.Tunnels[0].Name);
        Assert.AreEqual(TunnelStatus.Stopped, vm.Tunnels[0].Status);
        Assert.IsFalse(vm.Tunnels[0].IsActive);
        await _workflowService.DidNotReceive().CreateTunnelAsync(Arg.Any<Guid>(), Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("TunnelUI")]
    public async Task CreateTunnel_ResetsFormAfterSuccess()
    {
        TunnelInfo tunnelInfo = CreateTunnelInfo();
        _workflowService.CreateTunnelAsync(_sessionId, Arg.Any<TunnelConfig>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(tunnelInfo));
        FillValidLocalForm("server", 80);
        _vm.NewLocalHost = "127.0.0.1";
        _vm.NewLocalPort = 8080;
        await _vm.CreateTunnelCommand.Execute().FirstAsync();
        Assert.AreEqual(string.Empty, _vm.NewTunnelName);
        Assert.AreEqual("127.0.0.1", _vm.NewLocalHost);
        Assert.AreEqual(27017, _vm.NewLocalPort);
        Assert.AreEqual(27017, _vm.NewRemotePort);
        Assert.AreEqual(TunnelType.LocalForward, _vm.NewTunnelType);
        // 复位后目标重新锁定服务器本机。
        Assert.IsTrue(_vm.ForwardToServerLoopback);
        Assert.AreEqual("127.0.0.1", _vm.NewRemoteHost);
    }
}

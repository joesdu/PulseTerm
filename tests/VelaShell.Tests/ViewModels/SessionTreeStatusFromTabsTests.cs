using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器树上一条配置的状态标签,是它名下**所有**终端标签的合并结果
/// (Connected &gt; Connecting &gt; Error &gt; Disconnected),不是最后一次变更的那个标签的状态。
/// <para>
/// #321:在一条已经连上的 SSH 会话上再开一个标签、趁它还在握手时立刻关掉,
/// 旧实现让节点永远停在「连接中」—— 第二个标签把节点写成 Connecting,关闭时
/// 「还有别的标签连着」这条分支又只是跳过、不回写,状态就再也走不出去了。
/// </para>
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public sealed class SessionTreeStatusFromTabsTests
{
    [TestMethod]
    public async Task SecondTabClosedWhileConnecting_LeavesNodeActive()
    {
        (MainWindowViewModel vm, SessionProfile profile, SessionTreeNodeViewModel node) =
            await CreateLoadedAsync();

        TerminalTabViewModel first = CreateTab(profile, SessionStatus.Connected);
        vm.TabBar.AddTab(first);
        Assert.AreEqual(SessionStatus.Connected, node.Status);

        // 同一条配置再开一个标签:握手期间节点仍应显示「活跃」——那条已连上的会话还在。
        TerminalTabViewModel second = CreateTab(profile, SessionStatus.Connecting);
        vm.TabBar.AddTab(second);
        Assert.AreEqual(
            SessionStatus.Connected,
            node.Status,
            "已连上的会话不该因为旁边多了个正在握手的标签而退回「连接中」。"
        );

        vm.TabBar.CloseTabCommand.Execute(second).Subscribe();

        Assert.AreEqual(
            SessionStatus.Connected,
            node.Status,
            "关掉那个还在握手的标签后,节点必须回到「活跃」而不是停在「连接中」(#321)。"
        );
    }

    [TestMethod]
    public async Task SoleTabClosedWhileConnecting_ResetsNodeToDisconnected()
    {
        (MainWindowViewModel vm, SessionProfile profile, SessionTreeNodeViewModel node) =
            await CreateLoadedAsync();

        // 连接失败/取消会静默把标签从标签栏摘掉(RemoveTerminalTab),不走 DocumentClosed。
        TerminalTabViewModel only = CreateTab(profile, SessionStatus.Connecting);
        vm.TabBar.AddTab(only);
        Assert.AreEqual(SessionStatus.Connecting, node.Status);

        vm.TabBar.CloseTabCommand.Execute(only).Subscribe();

        Assert.AreEqual(
            SessionStatus.Disconnected,
            node.Status,
            "最后一个标签走了就该回到未连接,不能留下一个走不出去的「连接中」。"
        );
    }

    [TestMethod]
    public async Task FailedTabAlongsideConnectedTab_KeepsNodeActive()
    {
        (MainWindowViewModel vm, SessionProfile profile, SessionTreeNodeViewModel node) =
            await CreateLoadedAsync();

        TerminalTabViewModel connected = CreateTab(profile, SessionStatus.Connected);
        vm.TabBar.AddTab(connected);
        TerminalTabViewModel failing = CreateTab(profile, SessionStatus.Connecting);
        vm.TabBar.AddTab(failing);

        failing.ConnectionStatus = SessionStatus.Error;

        Assert.AreEqual(
            SessionStatus.Connected,
            node.Status,
            "一个标签握手失败不代表这条配置离线 —— 另一个标签还连着。"
        );

        connected.ConnectionStatus = SessionStatus.Disconnected;

        Assert.AreEqual(
            SessionStatus.Error,
            node.Status,
            "唯一连着的标签断开后,节点跟随剩下那个标签的失败状态显示「离线」。"
        );
    }

    [TestMethod]
    public async Task OtherProfileTabs_DoNotAffectNode()
    {
        (MainWindowViewModel vm, SessionProfile profile, SessionTreeNodeViewModel node) =
            await CreateLoadedAsync();

        vm.TabBar.AddTab(CreateTab(new SessionProfile { Id = Guid.NewGuid(), Name = "other" }, SessionStatus.Connected));

        Assert.AreEqual(SessionStatus.Disconnected, node.Status);
    }

    private static TerminalTabViewModel CreateTab(SessionProfile profile, SessionStatus status) =>
        new(Substitute.For<ITerminalEmulator>())
        {
            Title = profile.Name,
            Profile = profile,
            SessionId = Guid.NewGuid(),
            ConnectionStatus = status,
        };

    private static async Task<(
        MainWindowViewModel Vm,
        SessionProfile Profile,
        SessionTreeNodeViewModel Node
    )> CreateLoadedAsync()
    {
        var profile = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "WebServer",
            Host = "web.example.com",
            Username = "admin",
        };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository
            .GetAllSessionsAsync()
            .Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));

        var vm = new MainWindowViewModel(sessionRepository: repository);
        SessionTreeViewModel tree = vm.Sidebar.SessionTree!;
        await tree.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel node = tree.Nodes.Single(item => item.Id == profile.Id);
        Assert.AreEqual(SessionStatus.Disconnected, node.Status);
        return (vm, profile, node);
    }
}

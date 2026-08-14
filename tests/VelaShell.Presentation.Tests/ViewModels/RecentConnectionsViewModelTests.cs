using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

/// <summary>侧边栏“最近连接”的刷新与清除命令。</summary>
[TestClass]
public sealed class RecentConnectionsViewModelTests
{
    [TestMethod]
    public async Task ClearCommand_ClearsStoreAndList()
    {
        IRecentConnectionService service = Substitute.For<IRecentConnectionService>();
        service.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([Entry("web-01"), Entry("db-01")]);
        var vm = new RecentConnectionsViewModel(service);
        await vm.RefreshAsync();
        Assert.HasCount(2, vm.Connections);

        await vm.ClearCommand.Execute().FirstAsync();

        await service.Received(1).ClearAsync(Arg.Any<CancellationToken>());
        Assert.IsEmpty(vm.Connections);
    }

    /// <summary>清空历史失败时不能把列表也抹掉 —— 界面须继续如实反映存储里还在的记录。</summary>
    [TestMethod]
    public async Task ClearCommand_StoreFailure_KeepsList()
    {
        IRecentConnectionService service = Substitute.For<IRecentConnectionService>();
        service.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([Entry("web-01")]);
        service.ClearAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("db locked")));
        var vm = new RecentConnectionsViewModel(service);
        await vm.RefreshAsync();

        await vm.ClearCommand.Execute().FirstAsync();

        Assert.HasCount(1, vm.Connections);
    }

    /// <summary>无历史服务(如设计时/精简装配)时清除是空操作,不能抛。</summary>
    [TestMethod]
    public async Task ClearCommand_WithoutService_DoesNotThrow()
    {
        var vm = new RecentConnectionsViewModel();

        await vm.ClearCommand.Execute().FirstAsync();

        Assert.IsEmpty(vm.Connections);
    }

    private static RecentConnectionEntry Entry(string name) =>
        new()
        {
            Name = name,
            Host = $"{name}.example",
            Username = "root",
        };
}

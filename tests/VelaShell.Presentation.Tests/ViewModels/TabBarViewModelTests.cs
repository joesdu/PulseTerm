using ReactiveUI.Primitives;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

[TestClass]
public sealed class TabBarViewModelTests
{
    [TestMethod]
    public void AddTab_AddsToCollection()
    {
        var vm = new TabBarViewModel();

        vm.AddTab(new TabViewModel());

        Assert.HasCount(1, vm.Tabs);
    }

    [TestMethod]
    public void AddTab_SetsAsActive()
    {
        var vm = new TabBarViewModel();

        vm.AddTab(new TabViewModel());

        Assert.IsNotNull(vm.ActiveTab);
        Assert.AreSame(vm.Tabs[0], vm.ActiveTab);
    }

    [TestMethod]
    public void CloseTab_RemovesFromCollection()
    {
        var vm = new TabBarViewModel();
        vm.AddTab(new TabViewModel());
        TabViewModel tab = vm.Tabs[0];

        vm.CloseTabCommand.Execute(tab).Subscribe();

        Assert.IsEmpty(vm.Tabs);
    }
}

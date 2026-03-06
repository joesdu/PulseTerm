using FluentAssertions;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Tests.ViewModels;

public class TabBarViewModelTests
{

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_AddTab_AddsToCollection()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        vm.Tabs.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_AddTab_SetsAsActive()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab.Should().BeSameAs(vm.Tabs[0]);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_CloseTab_RemovesFromCollection()
    {
        var vm = new TabBarViewModel();
        vm.AddTabCommand.Execute().Subscribe();
        var tab = vm.Tabs[0];

        vm.CloseTabCommand.Execute(tab).Subscribe();

        vm.Tabs.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_CloseTab_UpdatesActiveTab()
    {
        var vm = new TabBarViewModel();
        vm.AddTabCommand.Execute().Subscribe();
        vm.AddTabCommand.Execute().Subscribe();
        vm.AddTabCommand.Execute().Subscribe();

        var secondTab = vm.Tabs[1];
        vm.ActiveTab = secondTab;
        vm.CloseTabCommand.Execute(secondTab).Subscribe();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab.Should().NotBeSameAs(secondTab);
        vm.Tabs.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_CloseLastTab_SetsActiveTabNull()
    {
        var vm = new TabBarViewModel();
        vm.AddTabCommand.Execute().Subscribe();
        var tab = vm.Tabs[0];

        vm.CloseTabCommand.Execute(tab).Subscribe();

        vm.ActiveTab.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "UI")]
    public void TabBarViewModel_MultipleAddTab_AllPresent()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();
        vm.AddTabCommand.Execute().Subscribe();
        vm.AddTabCommand.Execute().Subscribe();

        vm.Tabs.Should().HaveCount(3);
        vm.ActiveTab.Should().BeSameAs(vm.Tabs[2]);
    }
}

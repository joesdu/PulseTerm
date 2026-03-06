using System.Reactive.Concurrency;
using FluentAssertions;
using PulseTerm.App.ViewModels;
using ReactiveUI.Builder;

namespace PulseTerm.App.Tests.ViewModels;

public class MainWindowViewModelTests
{
    static MainWindowViewModelTests()
    {
        try
        {
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadScheduler.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }
    [Fact]
    [Trait("Category", "UI")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var vm = new MainWindowViewModel();

        vm.Sidebar.Should().NotBeNull();
        vm.TabBar.Should().NotBeNull();
        vm.StatusBar.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "UI")]
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        vm.QuickConnectCommand.Should().NotBeNull();
        vm.SettingsCommand.Should().NotBeNull();
        vm.NotificationsCommand.Should().NotBeNull();
        vm.QuickConnectText.Should().BeEmpty();
    }
}

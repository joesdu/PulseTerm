using System.Reactive;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    private SidebarViewModel _sidebar;
    private TabBarViewModel _tabBar;
    private StatusBarViewModel _statusBar;

    public MainWindowViewModel()
    {
        _sidebar = new SidebarViewModel();
        _tabBar = new TabBarViewModel();
        _statusBar = new StatusBarViewModel();

        OpenSettingsCommand = ReactiveCommand.Create(() => { });
    }

    public SidebarViewModel Sidebar
    {
        get => _sidebar;
        set => this.RaiseAndSetIfChanged(ref _sidebar, value);
    }

    public TabBarViewModel TabBar
    {
        get => _tabBar;
        set => this.RaiseAndSetIfChanged(ref _tabBar, value);
    }

    public StatusBarViewModel StatusBar
    {
        get => _statusBar;
        set => this.RaiseAndSetIfChanged(ref _statusBar, value);
    }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
}

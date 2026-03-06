using System.Reactive;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class SidebarViewModel : ReactiveObject
{
    private string _quickConnectText;
    private SessionTreeViewModel? _sessionTree;

    public SidebarViewModel()
    {
        _quickConnectText = string.Empty;

        QuickConnect = new QuickConnectViewModel();
        RecentConnections = new RecentConnectionsViewModel();

        QuickConnectCommand = ReactiveCommand.Create(() => { });
        SettingsCommand = ReactiveCommand.Create(() => { });
        NotificationsCommand = ReactiveCommand.Create(() => { });
    }

    public string QuickConnectText
    {
        get => _quickConnectText;
        set => this.RaiseAndSetIfChanged(ref _quickConnectText, value);
    }

    public QuickConnectViewModel QuickConnect { get; }

    public RecentConnectionsViewModel RecentConnections { get; }

    public SessionTreeViewModel? SessionTree
    {
        get => _sessionTree;
        set => this.RaiseAndSetIfChanged(ref _sessionTree, value);
    }

    public ReactiveCommand<Unit, Unit> QuickConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> SettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> NotificationsCommand { get; }
}

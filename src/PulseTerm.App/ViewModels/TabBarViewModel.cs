using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TabBarViewModel : ReactiveObject
{
    private TabViewModel? _activeTab;

    public TabBarViewModel()
    {
        Tabs = new ObservableCollection<TabViewModel>();

        AddTabCommand = ReactiveCommand.Create(AddTab);
        CloseTabCommand = ReactiveCommand.Create<TabViewModel>(CloseTab);
        CloseActiveTabCommand = ReactiveCommand.Create(CloseActiveTab);
        NextTabCommand = ReactiveCommand.Create(NextTab);
        PreviousTabCommand = ReactiveCommand.Create(PreviousTab);
    }

    public ObservableCollection<TabViewModel> Tabs { get; }

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    public ReactiveCommand<Unit, Unit> AddTabCommand { get; }
    public ReactiveCommand<TabViewModel, Unit> CloseTabCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseActiveTabCommand { get; }
    public ReactiveCommand<Unit, Unit> NextTabCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousTabCommand { get; }

    private void AddTab()
    {
        var tab = new TabViewModel();
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    private void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Tabs.RemoveAt(index);

        if (ActiveTab == tab)
        {
            if (Tabs.Count == 0)
            {
                ActiveTab = null;
            }
            else
            {
                ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            }
        }
    }

    private void CloseActiveTab()
    {
        if (ActiveTab != null)
            CloseTab(ActiveTab);
    }

    private void NextTab()
    {
        if (Tabs.Count == 0 || ActiveTab == null)
            return;

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1) % Tabs.Count];
    }

    private void PreviousTab()
    {
        if (Tabs.Count == 0 || ActiveTab == null)
            return;

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }
}

using System;
using System.Collections.ObjectModel;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class SessionTreeNodeViewModel : ReactiveObject
{
    private string _name;
    private bool _isExpanded;
    private SessionStatus _status;

    public SessionTreeNodeViewModel(Guid id, string name, bool isGroup)
    {
        Id = id;
        _name = name;
        IsGroup = isGroup;
        _isExpanded = isGroup;
        _status = SessionStatus.Disconnected;
        Children = new ObservableCollection<SessionTreeNodeViewModel>();
    }

    public Guid Id { get; }

    public bool IsGroup { get; }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public SessionStatus Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ObservableCollection<SessionTreeNodeViewModel> Children { get; }
}

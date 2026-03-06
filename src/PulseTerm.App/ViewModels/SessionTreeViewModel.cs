using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class SessionTreeViewModel : ReactiveObject
{
    private readonly ISessionRepository _repository;
    private SessionTreeNodeViewModel? _selectedNode;
    private readonly Dictionary<Guid, SessionProfile> _sessionCache = new();
    private bool _hasNoSessions;

    public SessionTreeViewModel(ISessionRepository repository)
    {
        _repository = repository;
        Nodes = new ObservableCollection<SessionTreeNodeViewModel>();
        _hasNoSessions = true;

        LoadCommand = ReactiveCommand.CreateFromTask(LoadTreeAsync);

        var hasSelectedSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(n => n is { IsGroup: false });

        ConnectCommand = ReactiveCommand.Create<Unit>(_ => { }, hasSelectedSession);
        EditSessionCommand = ReactiveCommand.Create<Unit>(_ => { }, hasSelectedSession);
        DeleteSessionCommand = ReactiveCommand.CreateFromTask(DeleteSelectedSessionAsync, hasSelectedSession);
    }

    public ObservableCollection<SessionTreeNodeViewModel> Nodes { get; }

    public bool HasNoSessions
    {
        get => _hasNoSessions;
        private set => this.RaiseAndSetIfChanged(ref _hasNoSessions, value);
    }

    public string EmptyStateMessage => "Add your first connection";

    public SessionTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> EditSessionCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSessionCommand { get; }

    public void AddSession(SessionProfile session)
    {
        _sessionCache[session.Id] = session;

        var groupNode = Nodes.FirstOrDefault(n => n.IsGroup && n.Id == session.GroupId);
        if (groupNode != null)
        {
            var childNode = new SessionTreeNodeViewModel(session.Id, session.Name, isGroup: false);
            groupNode.Children.Add(childNode);
        }

        HasNoSessions = !Nodes.Any(g => g.Children.Count > 0);
    }

    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId)
    {
        SessionTreeNodeViewModel? sourceNode = null;
        SessionTreeNodeViewModel? sourceGroup = null;

        foreach (var group in Nodes)
        {
            var child = group.Children.FirstOrDefault(c => c.Id == sessionId);
            if (child != null)
            {
                sourceNode = child;
                sourceGroup = group;
                break;
            }
        }

        if (sourceNode == null || sourceGroup == null) return;

        sourceGroup.Children.Remove(sourceNode);

        var targetGroup = Nodes.FirstOrDefault(n => n.IsGroup && n.Id == targetGroupId);
        targetGroup?.Children.Add(sourceNode);

        if (_sessionCache.TryGetValue(sessionId, out var session))
        {
            session.GroupId = targetGroupId;
            _repository.SaveSessionAsync(session);
        }
    }

    private async Task LoadTreeAsync()
    {
        Nodes.Clear();
        _sessionCache.Clear();

        var groups = await _repository.GetAllGroupsAsync();

        foreach (var group in groups.OrderBy(g => g.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, isGroup: true);

            foreach (var sessionId in group.Sessions)
            {
                var session = await _repository.GetSessionAsync(sessionId);
                if (session != null)
                {
                    _sessionCache[session.Id] = session;
                    var sessionNode = new SessionTreeNodeViewModel(session.Id, session.Name, isGroup: false);
                    groupNode.Children.Add(sessionNode);
                }
            }

            Nodes.Add(groupNode);
        }

        HasNoSessions = !Nodes.Any(g => g.Children.Count > 0);
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (SelectedNode == null || SelectedNode.IsGroup) return;

        var sessionId = SelectedNode.Id;
        await _repository.DeleteSessionAsync(sessionId);
        _sessionCache.Remove(sessionId);

        foreach (var group in Nodes)
        {
            var child = group.Children.FirstOrDefault(c => c.Id == sessionId);
            if (child != null)
            {
                group.Children.Remove(child);
                break;
            }
        }

        SelectedNode = null;
        HasNoSessions = !Nodes.Any(g => g.Children.Count > 0);
    }
}

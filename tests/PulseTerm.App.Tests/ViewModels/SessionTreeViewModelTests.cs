using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.App.Tests.ViewModels;

public class SessionTreeViewModelTests
{
    private readonly ISessionRepository _repository;
    private readonly SessionTreeViewModel _vm;

    public SessionTreeViewModelTests()
    {
        _repository = Substitute.For<ISessionRepository>();
        _vm = new SessionTreeViewModel(_repository);
    }

    private static ServerGroup CreateGroup(string name, int sortOrder, params Guid[] sessionIds)
    {
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = sortOrder
        };
        group.Sessions.AddRange(sessionIds);
        return group;
    }

    private static SessionProfile CreateSession(string name, Guid? groupId = null)
    {
        return new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = $"{name.ToLower()}.example.com",
            Username = "admin",
            GroupId = groupId
        };
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void Constructor_InitializesWithEmptyNodes()
    {
        _vm.Nodes.Should().BeEmpty();
        _vm.SelectedNode.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public async Task LoadCommand_PopulatesTreeFromRepository()
    {
        var session1 = CreateSession("WebServer");
        var session2 = CreateSession("DbServer");
        var group = CreateGroup("Production", 0, session1.Id, session2.Id);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetSessionAsync(session1.Id).Returns(Task.FromResult<SessionProfile?>(session1));
        _repository.GetSessionAsync(session2.Id).Returns(Task.FromResult<SessionProfile?>(session2));

        await _vm.LoadCommand.Execute().FirstAsync();

        _vm.Nodes.Should().HaveCount(1);
        _vm.Nodes[0].Name.Should().Be("Production");
        _vm.Nodes[0].IsGroup.Should().BeTrue();
        _vm.Nodes[0].Children.Should().HaveCount(2);
        _vm.Nodes[0].Children[0].Name.Should().Be("WebServer");
        _vm.Nodes[0].Children[1].Name.Should().Be("DbServer");
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public async Task LoadCommand_OrdersGroupsBySortOrder()
    {
        var group1 = CreateGroup("Staging", 1);
        var group2 = CreateGroup("Production", 0);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group1, group2 }));

        await _vm.LoadCommand.Execute().FirstAsync();

        _vm.Nodes.Should().HaveCount(2);
        _vm.Nodes[0].Name.Should().Be("Production");
        _vm.Nodes[1].Name.Should().Be("Staging");
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void AddSession_AddsToCorrectGroup()
    {
        var groupId = Guid.NewGuid();
        var groupNode = new SessionTreeNodeViewModel(groupId, "Production", isGroup: true);
        _vm.Nodes.Add(groupNode);

        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "NewServer",
            GroupId = groupId
        };

        _vm.AddSession(session);

        groupNode.Children.Should().HaveCount(1);
        groupNode.Children[0].Name.Should().Be("NewServer");
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void MoveSessionToGroup_MovesNodeBetweenGroups()
    {
        var sourceGroupId = Guid.NewGuid();
        var targetGroupId = Guid.NewGuid();

        var sourceGroup = new SessionTreeNodeViewModel(sourceGroupId, "Source", isGroup: true);
        var targetGroup = new SessionTreeNodeViewModel(targetGroupId, "Target", isGroup: true);

        _vm.Nodes.Add(sourceGroup);
        _vm.Nodes.Add(targetGroup);

        var session = new SessionProfile { Id = Guid.NewGuid(), Name = "MoveMe", GroupId = sourceGroupId };
        _vm.AddSession(session);

        sourceGroup.Children.Should().HaveCount(1);

        _vm.MoveSessionToGroup(session.Id, targetGroupId);

        sourceGroup.Children.Should().BeEmpty();
        targetGroup.Children.Should().HaveCount(1);
        targetGroup.Children[0].Name.Should().Be("MoveMe");
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public async Task DeleteSessionCommand_RemovesSelectedSession()
    {
        var session = CreateSession("ToDelete");
        var group = CreateGroup("Group", 0, session.Id);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetSessionAsync(session.Id).Returns(Task.FromResult<SessionProfile?>(session));

        await _vm.LoadCommand.Execute().FirstAsync();

        _vm.SelectedNode = _vm.Nodes[0].Children[0];
        await _vm.DeleteSessionCommand.Execute().FirstAsync();

        _vm.Nodes[0].Children.Should().BeEmpty();
        _vm.SelectedNode.Should().BeNull();
        await _repository.Received(1).DeleteSessionAsync(session.Id);
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void SelectedNode_RaisesPropertyChanged()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "Test", isGroup: false);
        _vm.Nodes.Add(node);

        var changed = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionTreeViewModel.SelectedNode))
                changed = true;
        };

        _vm.SelectedNode = node;

        changed.Should().BeTrue();
        _vm.SelectedNode.Should().BeSameAs(node);
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void SessionTreeNodeViewModel_DefaultStatus_IsDisconnected()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "Server1", isGroup: false);

        node.Status.Should().Be(SessionStatus.Disconnected);
        node.IsGroup.Should().BeFalse();
        node.IsExpanded.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "SessionTree")]
    public void SessionTreeNodeViewModel_GroupNode_DefaultsExpanded()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "MyGroup", isGroup: true);

        node.IsGroup.Should().BeTrue();
        node.IsExpanded.Should().BeTrue();
        node.Children.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public void HasNoSessions_DefaultsToTrue_WhenNoSessionsLoaded()
    {
        _vm.HasNoSessions.Should().BeTrue();
        _vm.EmptyStateMessage.Should().Be("Add your first connection");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task HasNoSessions_FalseAfterLoadingSessionsFromRepository()
    {
        var session = CreateSession("WebServer");
        var group = CreateGroup("Production", 0, session.Id);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetSessionAsync(session.Id).Returns(Task.FromResult<SessionProfile?>(session));

        await _vm.LoadCommand.Execute().FirstAsync();

        _vm.HasNoSessions.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task HasNoSessions_TrueWhenAllSessionsDeleted()
    {
        var session = CreateSession("OnlyServer");
        var group = CreateGroup("Production", 0, session.Id);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetSessionAsync(session.Id).Returns(Task.FromResult<SessionProfile?>(session));

        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.HasNoSessions.Should().BeFalse();

        _vm.SelectedNode = _vm.Nodes[0].Children[0];
        await _vm.DeleteSessionCommand.Execute().FirstAsync();

        _vm.HasNoSessions.Should().BeTrue();
    }
}

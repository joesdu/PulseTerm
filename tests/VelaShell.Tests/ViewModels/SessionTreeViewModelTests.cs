using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class SessionTreeViewModelTests
{
    private readonly ISessionRepository _repository;
    private readonly SessionTreeViewModel _vm;

    public SessionTreeViewModelTests()
    {
        _repository = Substitute.For<ISessionRepository>();
        _vm = new(_repository);
    }

    private static ServerGroup CreateGroup(string name, int sortOrder, params Guid[] sessionIds)
    {
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = sortOrder,
        };
        group.Sessions.AddRange(sessionIds);
        return group;
    }

    private static SessionProfile CreateSession(string name, Guid? groupId = null)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = $"{name.ToLower()}.example.com",
            Username = "admin",
            GroupId = groupId,
        };
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void Constructor_InitializesWithEmptyNodes()
    {
        Assert.IsEmpty(_vm.Nodes);
        Assert.IsNull(_vm.SelectedNode);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task LoadCommand_PopulatesTreeFromRepository()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session1 = CreateSession("WebServer", group.Id);
        SessionProfile session2 = CreateSession("DbServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session1, session2 }));
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Nodes);
        Assert.AreEqual("Production", _vm.Nodes[0].Name);
        Assert.IsTrue(_vm.Nodes[0].IsGroup);
        Assert.HasCount(2, _vm.Nodes[0].Children);
        // 组内按名称排序。
        Assert.AreEqual("DbServer", _vm.Nodes[0].Children[0].Name);
        Assert.AreEqual("WebServer", _vm.Nodes[0].Children[1].Name);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task LoadCommand_OrdersGroupsBySortOrder()
    {
        ServerGroup group1 = CreateGroup("Staging", 1);
        ServerGroup group2 = CreateGroup("Production", 0);
        _repository
            .GetAllGroupsAsync()
            .Returns(Task.FromResult(new List<ServerGroup> { group1, group2 }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.HasCount(2, _vm.Nodes);
        Assert.AreEqual("Production", _vm.Nodes[0].Name);
        Assert.AreEqual("Staging", _vm.Nodes[1].Name);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task LoadCommand_PutsUngroupedSessions_AtTreeRoot()
    {
        // 设计 FrJPu:未分组会话直接挂树根,不再收进“未分组”目录。
        SessionProfile orphan = CreateSession("Orphan");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.HasCount(1, _vm.Nodes);
        Assert.IsFalse(_vm.Nodes[0].IsGroup);
        Assert.AreEqual("Orphan", _vm.Nodes[0].Name);
        Assert.IsTrue(_vm.Nodes[0].IsRootLevel);
        Assert.IsFalse(_vm.HasNoSessions);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroup_ToUngrouped_MovesNodeToRoot()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();

        // Guid.Empty 是“未分组”落点:节点应移到树根,落库 GroupId 为 null。
        _vm.MoveSessionToGroup(session.Id, Guid.Empty);
        Assert.IsEmpty(_vm.Nodes[0].Children);
        SessionTreeNodeViewModel? rootNode = _vm.Nodes.FirstOrDefault(node =>
            !node.IsGroup && node.Id == session.Id
        );
        Assert.IsNotNull(rootNode);
        Assert.IsTrue(rootNode.IsRootLevel);
        Assert.IsNull(session.GroupId);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroup_FromRootIntoGroup_UpdatesGroupId()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile orphan = CreateSession("Orphan");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.MoveSessionToGroup(orphan.Id, group.Id);
        Assert.DoesNotContain(node => !node.IsGroup && node.Id == orphan.Id, _vm.Nodes);
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node =>
            node.IsGroup && node.Id == group.Id
        );
        Assert.HasCount(1, groupNode.Children);
        Assert.IsFalse(groupNode.Children[0].IsRootLevel);
        Assert.AreEqual(group.Id, orphan.GroupId);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SetSessionStatus_SurvivesTreeReload()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.SetSessionStatus(session.Id, SessionStatus.Connected);
        Assert.AreEqual(SessionStatus.Connected, _vm.Nodes[0].Children[0].Status);

        // 重建树后状态应从缓存重放,而不是回到断开态。
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.AreEqual(SessionStatus.Connected, _vm.Nodes[0].Children[0].Status);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SetSessionSyncChannel_SetClearAndSurvivesTreeReload()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();

        _vm.SetSessionSyncChannel(session.Id, "A");
        Assert.AreEqual("A", _vm.Nodes[0].Children[0].SyncChannelLetter);
        Assert.IsTrue(_vm.Nodes[0].Children[0].HasSyncChannel);

        // 重建树后频道标识应从缓存重放。
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.AreEqual("A", _vm.Nodes[0].Children[0].SyncChannelLetter);

        // 退出频道:上报空串清除标识。
        _vm.SetSessionSyncChannel(session.Id, string.Empty);
        Assert.IsFalse(_vm.Nodes[0].Children[0].HasSyncChannel);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SelectSession_ExpandsParentAndSelectsMatchingNode()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile first = CreateSession("First", group.Id);
        SessionProfile second = CreateSession("Second", group.Id);
        _repository.GetAllGroupsAsync().Returns([group]);
        _repository.GetAllSessionsAsync().Returns([first, second]);
        await _vm.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel groupNode = _vm.Nodes.Single();
        groupNode.IsExpanded = false;

        bool selected = _vm.SelectSession(second.Id);

        Assert.IsTrue(selected);
        Assert.IsTrue(groupNode.IsExpanded);
        Assert.AreEqual(second.Id, _vm.SelectedNode?.Id);
        Assert.ContainsSingle(node => node.IsSelected, groupNode.Children);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SelectSession_WhenMissing_PreservesCurrentSelection()
    {
        SessionProfile session = CreateSession("Existing");
        _repository.GetAllGroupsAsync().Returns([]);
        _repository.GetAllSessionsAsync().Returns([session]);
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.SelectSession(session.Id));
        SessionTreeNodeViewModel selected = _vm.SelectedNode!;

        Assert.IsFalse(_vm.SelectSession(Guid.NewGuid()));

        Assert.AreSame(selected, _vm.SelectedNode);
        Assert.IsTrue(selected.IsSelected);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void AddSession_AddsToCorrectGroup()
    {
        var groupId = Guid.NewGuid();
        var groupNode = new SessionTreeNodeViewModel(groupId, "Production", true);
        _vm.Nodes.Add(groupNode);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "NewServer",
            GroupId = groupId,
        };
        _vm.AddSession(session);
        Assert.HasCount(1, groupNode.Children);
        Assert.AreEqual("NewServer", groupNode.Children[0].Name);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void MoveSessionToGroup_MovesNodeBetweenGroups()
    {
        var sourceGroupId = Guid.NewGuid();
        var targetGroupId = Guid.NewGuid();
        var sourceGroup = new SessionTreeNodeViewModel(sourceGroupId, "Source", true);
        var targetGroup = new SessionTreeNodeViewModel(targetGroupId, "Target", true);
        _vm.Nodes.Add(sourceGroup);
        _vm.Nodes.Add(targetGroup);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "MoveMe",
            GroupId = sourceGroupId,
        };
        _vm.AddSession(session);
        Assert.HasCount(1, sourceGroup.Children);
        _vm.MoveSessionToGroup(session.Id, targetGroupId);
        Assert.IsEmpty(sourceGroup.Children);
        Assert.HasCount(1, targetGroup.Children);
        Assert.AreEqual("MoveMe", targetGroup.Children[0].Name);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteSessionCommand_RemovesSelectedSession()
    {
        ServerGroup group = CreateGroup("Group", 0);
        SessionProfile session = CreateSession("ToDelete", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.SelectedNode = _vm.Nodes[0].Children[0];
        await _vm.DeleteSessionCommand.Execute().FirstAsync();
        Assert.IsEmpty(_vm.Nodes[0].Children);
        Assert.IsNull(_vm.SelectedNode);
        await _repository.Received(1).DeleteSessionAsync(session.Id);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DuplicateSessionCommand_PreservesConnectionType()
    {
        SessionProfile source = CreateSession("Files");
        source.ConnectionType = ConnectionType.SFTP;
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { source }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.SelectedNode = _vm.Nodes.Single();

        await _vm.DuplicateSessionCommand.Execute().FirstAsync();

        await _repository.Received(1).SaveSessionAsync(
            Arg.Is<SessionProfile>(copy => copy.ConnectionType == ConnectionType.SFTP)
        );
    }

    [TestMethod]
    public async Task SftpSession_AllowsStandaloneSftp_AndKeepsSshOnlyCommandsHidden()
    {
        SessionProfile source = CreateSession("Files");
        source.ConnectionType = ConnectionType.SFTP;
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { source }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.SelectedNode = _vm.Nodes.Single();

        SessionProfile? requested = null;
        _vm.OpenSftpRequested += profile => requested = profile;
        SessionProfile? portForwardRequested = null;
        _vm.PortForwardRequested += profile => portForwardRequested = profile;

        Assert.IsFalse(_vm.SelectedNode.IsSshProfile);
        Assert.IsTrue(await _vm.OpenSftpCommand.CanExecute.FirstAsync());
        Assert.IsFalse(await _vm.PortForwardCommand.CanExecute.FirstAsync());
        await _vm.OpenSftpCommand.Execute().FirstAsync();
        await _vm.PortForwardCommand.Execute().FirstAsync();
        Assert.AreSame(source, requested);
        Assert.IsNull(portForwardRequested);
    }

    [TestMethod]
    public async Task SshSession_OpenSftpCommandRaisesExistingExplorerRequest()
    {
        SessionProfile source = CreateSession("Server");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { source }));
        await _vm.LoadCommand.Execute().FirstAsync();
        _vm.SelectedNode = _vm.Nodes.Single();

        SessionProfile? requested = null;
        _vm.OpenSftpRequested += profile => requested = profile;

        await _vm.OpenSftpCommand.Execute().FirstAsync();

        Assert.AreSame(source, requested);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void SelectedNode_RaisesPropertyChanged()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "Test", false);
        _vm.Nodes.Add(node);
        bool changed = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionTreeViewModel.SelectedNode))
            {
                changed = true;
            }
        };
        _vm.SelectedNode = node;
        Assert.IsTrue(changed);
        Assert.AreSame(node, _vm.SelectedNode);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void SessionTreeNodeViewModel_DefaultStatus_IsDisconnected()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "Server1", false);
        Assert.AreEqual(SessionStatus.Disconnected, node.Status);
        Assert.IsFalse(node.IsGroup);
        Assert.IsFalse(node.IsExpanded);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public void SessionTreeNodeViewModel_GroupNode_DefaultsExpanded()
    {
        var node = new SessionTreeNodeViewModel(Guid.NewGuid(), "MyGroup", true);
        Assert.IsTrue(node.IsGroup);
        Assert.IsTrue(node.IsExpanded);
        Assert.IsEmpty(node.Children);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public void HasNoSessions_DefaultsToTrue_WhenNoSessionsLoaded()
    {
        Assert.IsTrue(_vm.HasNoSessions);
        // 文案已本地化:断言资源值而非硬编码英文(测试机 UI culture 不定)。
        Assert.AreEqual(
            Core.Resources.Strings.Get("Svc_AddFirstConnection"),
            SessionTreeViewModel.EmptyStateMessage
        );
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task HasNoSessions_FalseAfterLoadingSessionsFromRepository()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.IsFalse(_vm.HasNoSessions);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task HasNoSessions_TrueWhenAllSessionsDeleted()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("OnlyServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();
        Assert.IsFalse(_vm.HasNoSessions);
        _vm.SelectedNode = _vm.Nodes[0].Children[0];
        await _vm.DeleteSessionCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.HasNoSessions);
    }
}

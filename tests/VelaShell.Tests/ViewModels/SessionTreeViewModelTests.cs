using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
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
        // 组内最后一条被移走 → 空分组随之自动删除,树里只剩这条根级会话。
        await _vm.MoveSessionToGroupAsync(session.Id, Guid.Empty);
        SessionTreeNodeViewModel? rootNode = _vm.Nodes.FirstOrDefault(node =>
            !node.IsGroup && node.Id == session.Id
        );
        Assert.IsNotNull(rootNode);
        Assert.IsTrue(rootNode.IsRootLevel);
        Assert.IsNull(session.GroupId);
        Assert.DoesNotContain(node => node.IsGroup, _vm.Nodes);
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

    // ── 拖动分组:移动 + 空分组自动删除 ────────────────────────────

    /// <summary>组内最后一条被拖走 → 空分组连同落库一并删除(“移动到分组”菜单同一条路径)。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_LastMemberLeaves_DeletesEmptiedGroup()
    {
        ServerGroup source = CreateGroup("Source", 0);
        ServerGroup target = CreateGroup("Target", 1);
        SessionProfile session = CreateSession("WebServer", source.Id);
        _repository
            .GetAllGroupsAsync()
            .Returns(Task.FromResult(new List<ServerGroup> { source, target }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(session.Id, target.Id);

        await _repository.Received(1).DeleteGroupAsync(source.Id);
        Assert.DoesNotContain(node => node.Id == source.Id, _vm.Nodes);
        // “移动到分组”子菜单绑定 GroupNodes,不同步移除会留下指向已删分组的落点。
        Assert.DoesNotContain(node => node.Id == source.Id, _vm.GroupNodes);
        Assert.HasCount(1, _vm.Nodes.First(node => node.Id == target.Id).Children);
        Assert.AreEqual(target.Id, session.GroupId);
    }

    /// <summary>组里还剩人时不能删组 —— 自动删除只针对被搬空的分组。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_GroupKeepsMembers_IsNotDeleted()
    {
        ServerGroup source = CreateGroup("Source", 0);
        SessionProfile leaving = CreateSession("Leaving", source.Id);
        SessionProfile staying = CreateSession("Staying", source.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { source }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { leaving, staying }));
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(leaving.Id, Guid.Empty);

        await _repository.DidNotReceive().DeleteGroupAsync(Arg.Any<Guid>());
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.Id == source.Id);
        Assert.HasCount(1, groupNode.Children);
        Assert.AreEqual("Staying", groupNode.Children[0].Name);
    }

    /// <summary>
    /// 拖回自己所在的分组是空操作。少了这道判断会先摘节点、把分组判空删掉,
    /// 再往已删除的分组里挂回去 —— 会话凭空消失。
    /// </summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_DropOnOwnGroup_IsNoOp()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(session.Id, group.Id);

        await _repository.DidNotReceive().DeleteGroupAsync(Arg.Any<Guid>());
        await _repository.DidNotReceive().SaveSessionAsync(Arg.Any<SessionProfile>());
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.Id == group.Id);
        Assert.HasCount(1, groupNode.Children);
        Assert.AreEqual(group.Id, session.GroupId);
    }

    /// <summary>根级会话拖到树根(还是未分组)同样是空操作。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_RootSessionToRoot_IsNoOp()
    {
        SessionProfile orphan = CreateSession("Orphan");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(orphan.Id, Guid.Empty);

        await _repository.DidNotReceive().SaveSessionAsync(Arg.Any<SessionProfile>());
        Assert.HasCount(1, _vm.Nodes);
    }

    /// <summary>目标分组不存在时整个移动放弃,不能把节点摘下来又挂不回去。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_UnknownTargetGroup_KeepsSessionInPlace()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile session = CreateSession("WebServer", group.Id);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(session.Id, Guid.NewGuid());

        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.Id == group.Id);
        Assert.HasCount(1, groupNode.Children);
        Assert.AreEqual(group.Id, session.GroupId);
        await _repository.DidNotReceive().DeleteGroupAsync(Arg.Any<Guid>());
    }

    /// <summary>落点按名称插位,与重新加载后的排序一致(直接 Add 会固定落在末尾)。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_InsertsByName_InTargetAndAtRoot()
    {
        ServerGroup target = CreateGroup("Target", 0);
        SessionProfile alpha = CreateSession("Alpha", target.Id);
        SessionProfile zulu = CreateSession("Zulu", target.Id);
        SessionProfile middle = CreateSession("Middle");
        SessionProfile rootAaa = CreateSession("Aaa");
        SessionProfile rootZzz = CreateSession("Zzz");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { target }));
        _repository
            .GetAllSessionsAsync()
            .Returns(
                Task.FromResult(new List<SessionProfile> { alpha, zulu, middle, rootAaa, rootZzz })
            );
        await _vm.LoadCommand.Execute().FirstAsync();

        await _vm.MoveSessionToGroupAsync(middle.Id, target.Id);
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.Id == target.Id);
        Assert.AreEqual("Alpha", groupNode.Children[0].Name);
        Assert.AreEqual("Middle", groupNode.Children[1].Name);
        Assert.AreEqual("Zulu", groupNode.Children[2].Name);

        // 再拖回树根:分组节点始终在前,未分组会话在后并按名称排位。
        await _vm.MoveSessionToGroupAsync(middle.Id, Guid.Empty);
        Assert.IsTrue(_vm.Nodes[0].IsGroup);
        Assert.AreEqual("Aaa", _vm.Nodes[1].Name);
        Assert.AreEqual("Middle", _vm.Nodes[2].Name);
        Assert.AreEqual("Zzz", _vm.Nodes[3].Name);
    }

    /// <summary>拖进折叠着的分组会把它展开,否则会话看起来像是"没了"。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task MoveSessionToGroupAsync_ExpandsCollapsedTargetGroup()
    {
        ServerGroup target = CreateGroup("Target", 0);
        SessionProfile member = CreateSession("Member", target.Id);
        SessionProfile orphan = CreateSession("Orphan");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { target }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { member, orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.Id == target.Id);
        groupNode.IsExpanded = false;

        await _vm.MoveSessionToGroupAsync(orphan.Id, target.Id);

        Assert.IsTrue(groupNode.IsExpanded);
    }

    /// <summary>落点解析(视图只负责找出鼠标下的节点,规则在这里)。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ResolveDropTargetGroupId_MapsRowUnderCursorToTargetGroup()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile member = CreateSession("Member", group.Id);
        SessionProfile orphan = CreateSession("Orphan");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { member, orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel groupNode = _vm.Nodes.First(node => node.IsGroup);
        SessionTreeNodeViewModel memberNode = groupNode.Children[0];
        SessionTreeNodeViewModel orphanNode = _vm.Nodes.First(node =>
            !node.IsGroup && node.Id == orphan.Id
        );

        // 空白处 = 未分组;分组行 = 该分组;组内会话行 = 它所在的分组;根级会话行 = 未分组。
        Assert.AreEqual(Guid.Empty, _vm.ResolveDropTargetGroupId(null));
        Assert.AreEqual(group.Id, _vm.ResolveDropTargetGroupId(groupNode));
        Assert.AreEqual(group.Id, _vm.ResolveDropTargetGroupId(memberNode));
        Assert.AreEqual(Guid.Empty, _vm.ResolveDropTargetGroupId(orphanNode));
    }

    /// <summary>落点名(拖拽时跟随光标的标签用):分组取组名,树根取“未分组”,未知分组不显示 Guid。</summary>
    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DescribeDropTarget_NamesGroupOrUngrouped()
    {
        ServerGroup group = CreateGroup("Production", 0);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        await _vm.LoadCommand.Execute().FirstAsync();

        Assert.AreEqual("Production", _vm.DescribeDropTarget(group.Id));
        Assert.AreEqual(Strings.Get("Svc_Ungrouped"), _vm.DescribeDropTarget(Guid.Empty));
        Assert.AreEqual(Strings.Get("Svc_Ungrouped"), _vm.DescribeDropTarget(Guid.NewGuid()));
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
        // 源分组被搬空 → 自动删除,树里不再留着它。
        Assert.DoesNotContain(node => node.Id == sourceGroupId, _vm.Nodes);
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

    // ---- 删除分组(连带删除组内连接) ----

    /// <summary>建一棵「一个分组 + 组内两条连接 + 树根一条未分组连接」的树。</summary>
    private async Task<(ServerGroup Group, SessionProfile First, SessionProfile Second, SessionProfile Orphan)> LoadGroupWithTwoSessionsAsync()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile first = CreateSession("WebServer", group.Id);
        SessionProfile second = CreateSession("DbServer", group.Id);
        SessionProfile orphan = CreateSession("Standalone");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { first, second, orphan }));
        await _vm.LoadCommand.Execute().FirstAsync();
        return (group, first, second, orphan);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteGroupCommand_DeletesGroupAndEveryConnectionInside()
    {
        (ServerGroup group, SessionProfile first, SessionProfile second, SessionProfile orphan) =
            await LoadGroupWithTwoSessionsAsync();
        _vm.ConfirmDeleteGroup = _ => Task.FromResult(true);
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup);

        await _vm.DeleteGroupCommand.Execute().FirstAsync();

        // 组内连接逐条落库删除,分组本身随后删除。
        await _repository.Received(1).DeleteSessionAsync(first.Id);
        await _repository.Received(1).DeleteSessionAsync(second.Id);
        await _repository.Received(1).DeleteGroupAsync(group.Id);
        // 组外的连接不受影响。
        await _repository.DidNotReceive().DeleteSessionAsync(orphan.Id);
        Assert.DoesNotContain(node => node.IsGroup, _vm.Nodes);
        Assert.ContainsSingle(node => node.Id == orphan.Id, _vm.Nodes);
        Assert.IsNull(_vm.SelectedNode);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteGroupCommand_WithoutConfirmation_DeletesNothing()
    {
        (ServerGroup group, SessionProfile first, _, _) = await LoadGroupWithTwoSessionsAsync();
        _vm.ConfirmDeleteGroup = _ => Task.FromResult(false);
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup);

        await _vm.DeleteGroupCommand.Execute().FirstAsync();

        await _repository.DidNotReceive().DeleteGroupAsync(Arg.Any<Guid>());
        await _repository.DidNotReceive().DeleteSessionAsync(Arg.Any<Guid>());
        Assert.ContainsSingle(node => node.IsGroup && node.Id == group.Id, _vm.Nodes);
        Assert.HasCount(2, _vm.Nodes.Single(node => node.IsGroup).Children);
        Assert.IsTrue(_vm.SelectedNode?.IsGroup);
        Assert.IsNotNull(first);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteGroupCommand_ConfirmationMessage_NamesGroupAndCountsConnections()
    {
        (ServerGroup group, _, _, _) = await LoadGroupWithTwoSessionsAsync();
        string? shown = null;
        _vm.ConfirmDeleteGroup = message =>
        {
            shown = message;
            return Task.FromResult(false);
        };
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup);

        await _vm.DeleteGroupCommand.Execute().FirstAsync();

        // 用户必须在点确认前就知道「删的是哪个组」「要连带删掉几条连接」。
        Assert.IsNotNull(shown);
        Assert.Contains(group.Name, shown);
        Assert.Contains("2", shown);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteGroupCommand_RemovesGroupFromMoveToGroupMenu()
    {
        (ServerGroup group, _, _, _) = await LoadGroupWithTwoSessionsAsync();
        _vm.ConfirmDeleteGroup = _ => Task.FromResult(true);
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup);

        await _vm.DeleteGroupCommand.Execute().FirstAsync();

        // 不同步移除的话,「移动到分组」子菜单会留下一个指向已删分组的落点。
        Assert.DoesNotContain(node => node.Id == group.Id, _vm.GroupNodes);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task DeleteGroupCommand_IsDisabled_WhenSelectionIsNotAGroup()
    {
        (_, SessionProfile first, _, _) = await LoadGroupWithTwoSessionsAsync();
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup)
                              .Children.Single(child => child.Id == first.Id);

        Assert.IsFalse(await _vm.DeleteGroupCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task DeleteGroupCommand_EmptyGroup_UsesEmptyGroupPrompt_AndStillDeletes()
    {
        ServerGroup group = CreateGroup("Empty", 0);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        await _vm.LoadCommand.Execute().FirstAsync();
        string? shown = null;
        _vm.ConfirmDeleteGroup = message =>
        {
            shown = message;
            return Task.FromResult(true);
        };
        _vm.SelectedNode = _vm.Nodes.Single(node => node.IsGroup);

        await _vm.DeleteGroupCommand.Execute().FirstAsync();

        // 空分组不该提「组内 0 个连接会被删除」这种话。
        Assert.IsNotNull(shown);
        Assert.DoesNotContain("0", shown);
        await _repository.Received(1).DeleteGroupAsync(group.Id);
        Assert.IsEmpty(_vm.Nodes);
        Assert.IsTrue(_vm.HasNoSessions);
    }

    // ---- 摊平后的行(界面绑的是 Rows,不是 Nodes) ----

    /// <summary>造一棵"一个分组带两台 + 一台未分组"的树。</summary>
    private async Task LoadOneGroupAndARootSessionAsync()
    {
        ServerGroup group = CreateGroup("Production", 0);
        SessionProfile inside = CreateSession("WebServer", group.Id);
        SessionProfile another = CreateSession("DbServer", group.Id);
        SessionProfile loose = CreateSession("Laptop");
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { inside, another, loose }));
        await _vm.LoadCommand.Execute().FirstAsync();
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task Rows_FlattensGroupsWithTheirSessionsInOrder()
    {
        await LoadOneGroupAndARootSessionAsync();

        // 分组行 + 它的两台(分组默认展开)+ 根级那台
        Assert.HasCount(4, _vm.Rows);
        Assert.AreEqual("Production", _vm.Rows[0].Name);
        Assert.IsTrue(_vm.Rows[0].IsGroup);
        Assert.AreEqual("DbServer", _vm.Rows[1].Name);
        Assert.AreEqual("WebServer", _vm.Rows[2].Name);
        Assert.AreEqual("Laptop", _vm.Rows[3].Name);
        Assert.IsTrue(_vm.Rows[3].IsRootLevel);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task Rows_CollapsingAGroupTakesItsSessionsOut_AndPuttingItBackRestoresThem()
    {
        await LoadOneGroupAndARootSessionAsync();
        SessionTreeNodeViewModel group = _vm.Nodes.Single(node => node.IsGroup);

        group.IsExpanded = false;

        Assert.HasCount(2, _vm.Rows, "只剩分组行和根级那台");
        Assert.AreEqual("Production", _vm.Rows[0].Name);
        Assert.AreEqual("Laptop", _vm.Rows[1].Name);

        group.IsExpanded = true;

        Assert.HasCount(4, _vm.Rows);
        Assert.AreEqual("DbServer", _vm.Rows[1].Name);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task Rows_CollapsingTheGroupOfTheSelectedSession_MovesTheSelectionUpToTheGroup()
    {
        await LoadOneGroupAndARootSessionAsync();
        SessionTreeNodeViewModel group = _vm.Nodes.Single(node => node.IsGroup);
        _vm.SelectedNode = group.Children[0];

        group.IsExpanded = false;

        // 选中项不能藏起来:右键菜单里的命令都作用于 SelectedNode,
        // 停在一个看不见的会话上等于对着谁执行都说不准
        Assert.AreSame(group, _vm.SelectedNode);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task Rows_KeepTheSelectionWhenAnUnrelatedGroupFolds()
    {
        ServerGroup first = CreateGroup("Production", 0);
        ServerGroup second = CreateGroup("Staging", 1);
        SessionProfile inFirst = CreateSession("WebServer", first.Id);
        SessionProfile inSecond = CreateSession("Beta", second.Id);
        _repository
            .GetAllGroupsAsync()
            .Returns(Task.FromResult(new List<ServerGroup> { first, second }));
        _repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { inFirst, inSecond }));
        await _vm.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel selected = _vm.Nodes[1].Children[0];
        _vm.SelectedNode = selected;

        _vm.Nodes[0].IsExpanded = false;

        Assert.AreSame(selected, _vm.SelectedNode, "折的是隔壁那组,不该动我的选择");
        Assert.HasCount(3, _vm.Rows);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task Rows_FollowSessionsMovedBetweenGroups()
    {
        await LoadOneGroupAndARootSessionAsync();
        SessionTreeNodeViewModel group = _vm.Nodes.Single(node => node.IsGroup);
        SessionTreeNodeViewModel moving = _vm.Rows.Single(row => row.Name == "Laptop");

        await _vm.MoveSessionToGroupAsync(moving.Id, group.Id);

        // 搬进分组之后它该出现在分组行下面,而不是继续留在根级那一段
        Assert.HasCount(4, _vm.Rows);
        Assert.AreEqual("Production", _vm.Rows[0].Name);
        Assert.Contains(moving, _vm.Nodes[0].Children);
        Assert.IsGreaterThan(_vm.Rows.IndexOf(_vm.Rows[0]), _vm.Rows.IndexOf(moving));
    }
}

using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 会话树过滤框:按 名称 / 主机 / 用户名 / 标签 过滤,命中的分组自动展开,清空后还原折叠状态。
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public sealed class SessionTreeFilterTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();

    private static SessionProfile Session(string name, string host, string user, Guid? groupId = null, params string[] tags)
    {
        var profile = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = host,
            Username = user,
            GroupId = groupId
        };
        profile.Tags.AddRange(tags);
        return profile;
    }

    /// <summary>建一棵「生产组(web / db)+ 根级一台 bastion」的树。</summary>
    private async Task<SessionTreeViewModel> BuildTreeAsync()
    {
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "Production", SortOrder = 0 };
        SessionProfile web = Session("WebServer", "web.example.com", "deploy", group.Id, "prod", "nginx");
        SessionProfile db = Session("DbServer", "db.internal", "postgres", group.Id, "prod");
        SessionProfile bastion = Session("Bastion", "gate.example.com", "root");
        group.Sessions.AddRange([web.Id, db.Id]);

        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { web, db, bastion }));

        var vm = new SessionTreeViewModel(_repository);
        await vm.LoadCommand.Execute().FirstAsync();
        return vm;
    }

    private static IEnumerable<string> RowNames(SessionTreeViewModel vm) => vm.Rows.Select(row => row.Name);

    [TestMethod]
    public async Task NoFilter_ShowsTheWholeTree()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        Assert.IsFalse(vm.HasFilter);
        CollectionAssert.Contains(RowNames(vm).ToList(), "Bastion");
        CollectionAssert.Contains(RowNames(vm).ToList(), "Production");
    }

    [TestMethod]
    public async Task FilteringByName_KeepsOnlyMatchesAndTheirGroup()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "web";

        Assert.IsTrue(vm.HasFilter);
        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "WebServer");
        CollectionAssert.Contains(names, "Production", "命中的会话所在的分组行要留着,否则看不出它属于谁。");
        CollectionAssert.DoesNotContain(names, "DbServer");
        CollectionAssert.DoesNotContain(names, "Bastion");
    }

    [TestMethod]
    public async Task FilteringByHost_Matches()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "db.internal";

        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "DbServer");
        CollectionAssert.DoesNotContain(names, "WebServer");
    }

    [TestMethod]
    public async Task FilteringByUsername_Matches()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "postgres";

        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "DbServer");
        CollectionAssert.DoesNotContain(names, "WebServer");
    }

    [TestMethod]
    public async Task FilteringByTag_Matches()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "nginx";

        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "WebServer");
        CollectionAssert.DoesNotContain(names, "DbServer");
    }

    [TestMethod]
    public async Task FilteringIsCaseInsensitive()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "WEBSERVER";

        CollectionAssert.Contains(RowNames(vm).ToList(), "WebServer");
    }

    [TestMethod]
    public async Task MatchingAGroupName_ShowsTheWholeGroup()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "production";

        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "Production");
        CollectionAssert.Contains(names, "WebServer");
        CollectionAssert.Contains(names, "DbServer", "分组名命中时整组展示。");
        CollectionAssert.DoesNotContain(names, "Bastion");
    }

    [TestMethod]
    public async Task RootLevelSessions_AreMatchedToo()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "gate";

        List<string> names = [.. RowNames(vm)];
        CollectionAssert.Contains(names, "Bastion");
        CollectionAssert.DoesNotContain(names, "Production");
    }

    [TestMethod]
    public async Task NoMatches_LeavesAnEmptyList()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "zzz-nothing-matches";

        Assert.IsEmpty(vm.Rows);
    }

    [TestMethod]
    public async Task FilteringExpandsGroups_AndClearingRestoresTheOriginalCollapsedState()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();
        SessionTreeNodeViewModel group = vm.Nodes.Single(node => node.IsGroup);
        group.IsExpanded = false;

        vm.FilterText = "web";
        Assert.IsTrue(group.IsExpanded, "过滤时命中的分组必须展开,否则箭头方向与展示内容对不上。");

        vm.FilterText = "";
        Assert.IsFalse(group.IsExpanded, "清空过滤后应当还原用户原来的折叠状态。");
    }

    [TestMethod]
    public async Task ClearFilterCommand_ResetsTheText()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();
        vm.FilterText = "web";

        await vm.ClearFilterCommand.Execute().FirstAsync();

        Assert.AreEqual(string.Empty, vm.FilterText);
        Assert.IsFalse(vm.HasFilter);
        CollectionAssert.Contains(RowNames(vm).ToList(), "Bastion");
    }

    [TestMethod]
    public async Task WhitespaceOnlyFilter_IsTreatedAsNoFilter()
    {
        SessionTreeViewModel vm = await BuildTreeAsync();

        vm.FilterText = "   ";

        Assert.IsFalse(vm.HasFilter);
        CollectionAssert.Contains(RowNames(vm).ToList(), "Bastion");
    }
}

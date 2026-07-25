using System.Reactive.Linq;
using VelaShell.Core.Processes;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>任务管理器视图模型的列表逻辑:合并、过滤、排序与结束进程树的目标集合。</summary>
[TestClass]
[TestCategory("Processes")]
public class ProcessManagerViewModelTests
{
    private readonly FakeProcessService _service = new();
    private readonly ProcessManagerViewModel _vm;

    public ProcessManagerViewModelTests()
    {
        _vm = new(_service, Guid.NewGuid(), "生产数据库");
    }

    [TestMethod]
    public async Task Refresh_WithoutData_KeepsThePlaceholderVisible()
    {
        _service.Snapshot = null;
        await _vm.RefreshAsync();
        Assert.IsTrue(_vm.IsUnavailable);
        Assert.IsEmpty(_vm.Processes);
    }

    [TestMethod]
    public async Task Refresh_HidesKernelThreadsByDefault()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(2048, 2, "kworker", "[kworker/0:1H]")
        );
        await _vm.RefreshAsync();

        Assert.IsFalse(_vm.IsUnavailable);
        Assert.HasCount(1, _vm.Processes);
        Assert.AreEqual(2, _vm.TotalCount);

        _vm.ShowKernelThreads = true;
        Assert.HasCount(2, _vm.Processes);
    }

    [TestMethod]
    public async Task Search_MatchesNameUserCommandLineAndPid()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init", user: "root"),
            Process(1337, 1, "java", "/usr/bin/java -jar app.jar", user: "www-data")
        );
        // 平铺模式下只留命中行;树形模式还会补回祖先,那条语义由
        // TreeView_KeepsAncestorsOfSearchHits 单独覆盖。
        _vm.ShowTree = false;
        await _vm.RefreshAsync();

        _vm.SearchText = "java";
        Assert.HasCount(1, _vm.Processes);

        _vm.SearchText = "www-data";
        Assert.HasCount(1, _vm.Processes);

        _vm.SearchText = "app.jar";
        Assert.HasCount(1, _vm.Processes);

        _vm.SearchText = "1337";
        Assert.HasCount(1, _vm.Processes);

        _vm.SearchText = "";
        Assert.HasCount(2, _vm.Processes);
    }

    [TestMethod]
    public async Task Sort_DefaultsToCpuDescending_AndTogglesOnRepeatedClicks()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "idle", "/usr/bin/idle", cpu: 0.5),
            Process(2, 0, "busy", "/usr/bin/busy", cpu: 42.0)
        );
        await _vm.RefreshAsync();
        Assert.AreEqual("busy", _vm.Processes[0].Name);

        _vm.SortCommand.Execute("cpu").Subscribe();
        Assert.AreEqual("idle", _vm.Processes[0].Name);

        // 切到文本列应默认升序。
        _vm.SortCommand.Execute("name").Subscribe();
        Assert.AreEqual("busy", _vm.Processes[0].Name);
        Assert.IsFalse(_vm.SortDescending);
        Assert.AreEqual(" ▲", _vm.NameSortGlyph);
    }

    [TestMethod]
    public async Task Refresh_ReusesRowObjects_SoSelectionSurvives()
    {
        _service.Snapshot = Snapshot(Process(1, 0, "init", "/sbin/init", cpu: 1));
        await _vm.RefreshAsync();
        ProcessRowViewModel first = _vm.Processes[0];
        _vm.SelectedProcess = first;

        _service.Snapshot = Snapshot(Process(1, 0, "init", "/sbin/init", cpu: 90));
        await _vm.RefreshAsync();

        Assert.AreSame(first, _vm.Processes[0]);
        Assert.AreSame(first, _vm.SelectedProcess);
        Assert.AreEqual(90, first.CpuPercent);
    }

    [TestMethod]
    public async Task Refresh_ClearsSelection_WhenTheSelectedProcessExits()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(99, 1, "doomed", "/usr/bin/doomed")
        );
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes.Single(row => row.Pid == 99);

        _service.Snapshot = Snapshot(Process(1, 0, "init", "/sbin/init"));
        await _vm.RefreshAsync();

        Assert.IsNull(_vm.SelectedProcess);
    }

    [TestMethod]
    public async Task EndTask_SendsTermToTheSelectedProcessOnly()
    {
        _service.Snapshot = Snapshot(
            Process(100, 1, "parent", "/usr/bin/parent"),
            Process(200, 100, "child", "/usr/bin/child")
        );
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes.Single(row => row.Pid == 100);

        await _vm.EndTaskCommand.Execute().FirstAsync();

        (IReadOnlyList<int> pids, ProcessSignal signal) = _service.Signals.Single();
        CollectionAssert.AreEqual(new[] { 100 }, pids.ToArray());
        Assert.AreEqual(ProcessSignal.Terminate, signal);
    }

    [TestMethod]
    public async Task ForceEndTask_SendsKill()
    {
        _service.Snapshot = Snapshot(Process(100, 1, "parent", "/usr/bin/parent"));
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes[0];

        await _vm.ForceEndTaskCommand.Execute().FirstAsync();

        Assert.AreEqual(ProcessSignal.Kill, _service.Signals.Single().Signal);
    }

    [TestMethod]
    public async Task EndTaskTree_KillsDescendantsBeforeTheParent()
    {
        // 先杀父的话子进程会被 init 收养,ppid 变成 1,后面就再也找不到它们了。
        _service.Snapshot = Snapshot(
            Process(100, 1, "parent", "/usr/bin/parent"),
            Process(200, 100, "child", "/usr/bin/child"),
            Process(300, 200, "grandchild", "/usr/bin/grandchild"),
            Process(400, 1, "unrelated", "/usr/bin/unrelated")
        );
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes.Single(row => row.Pid == 100);

        await _vm.EndTaskTreeCommand.Execute().FirstAsync();

        CollectionAssert.AreEqual(new[] { 300, 200, 100 }, _service.Signals.Single().Pids.ToArray());
    }

    [TestMethod]
    public async Task EndTask_ReportsTheRemoteFailureReason()
    {
        _service.Snapshot = Snapshot(Process(1, 0, "init", "/sbin/init"));
        _service.Outcome = new(false, "kill: (1): Operation not permitted");
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes[0];

        await _vm.EndTaskCommand.Execute().FirstAsync();

        Assert.IsNotNull(_vm.StatusMessage);
        Assert.Contains("Operation not permitted", _vm.StatusMessage);
    }

    [TestMethod]
    public async Task EndTask_DoesNothing_WhenTheUserCancelsTheConfirmation()
    {
        _service.Snapshot = Snapshot(Process(1, 0, "init", "/sbin/init"));
        await _vm.RefreshAsync();
        _vm.SelectedProcess = _vm.Processes[0];
        _vm.ConfirmAction = (_, _) => Task.FromResult(false);

        await _vm.EndTaskCommand.Execute().FirstAsync();

        Assert.IsEmpty(_service.Signals);
    }

    [TestMethod]
    public async Task Refresh_KeepsSelection_EvenWhenSortingMovesTheRow()
    {
        // 按 CPU 降序时行序每轮都在变。选中项被冲掉正是"一刷新就选不中"的症状。
        _service.Snapshot = Snapshot(
            Process(1, 0, "idle", "/usr/bin/idle", cpu: 0.1),
            Process(2, 0, "target", "/usr/bin/target", cpu: 0.2),
            Process(3, 0, "busy", "/usr/bin/busy", cpu: 90)
        );
        await _vm.RefreshAsync();
        ProcessRowViewModel target = _vm.Processes.Single(row => row.Pid == 2);
        _vm.SelectedProcess = target;

        // 下一轮采样把它顶到了第一位。
        _service.Snapshot = Snapshot(
            Process(1, 0, "idle", "/usr/bin/idle", cpu: 0.1),
            Process(2, 0, "target", "/usr/bin/target", cpu: 99),
            Process(3, 0, "busy", "/usr/bin/busy", cpu: 1)
        );
        await _vm.RefreshAsync();

        Assert.AreSame(target, _vm.SelectedProcess);
        Assert.AreEqual(0, _vm.Processes.IndexOf(target));
    }

    [TestMethod]
    public void TreeView_IsOnByDefault() => Assert.IsTrue(_vm.ShowTree);

    [TestMethod]
    public async Task TreeView_NestsChildrenUnderTheirParent()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(100, 1, "parent", "/usr/bin/parent"),
            Process(200, 100, "child", "/usr/bin/child"),
            Process(300, 200, "grandchild", "/usr/bin/grandchild")
        );
        await _vm.RefreshAsync();
        _vm.ShowTree = true;

        CollectionAssert.AreEqual(
            new[] { 1, 100, 200, 300 },
            _vm.Processes.Select(row => row.Pid).ToArray()
        );
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3 },
            _vm.Processes.Select(row => row.Depth).ToArray()
        );
        Assert.IsTrue(_vm.Processes.Single(row => row.Pid == 200).HasChildren);
        Assert.IsFalse(_vm.Processes.Single(row => row.Pid == 300).HasChildren);
    }

    [TestMethod]
    public async Task TreeView_CollapsingHidesTheWholeSubtree()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(100, 1, "parent", "/usr/bin/parent"),
            Process(200, 100, "child", "/usr/bin/child")
        );
        await _vm.RefreshAsync();
        _vm.ShowTree = true;

        ProcessRowViewModel parent = _vm.Processes.Single(row => row.Pid == 100);
        _vm.ToggleExpandCommand.Execute(parent).Subscribe();

        CollectionAssert.AreEqual(new[] { 1, 100 }, _vm.Processes.Select(row => row.Pid).ToArray());

        // 折叠状态挂在复用的行对象上,刷新之后仍然保持。
        await _vm.RefreshAsync();
        CollectionAssert.AreEqual(new[] { 1, 100 }, _vm.Processes.Select(row => row.Pid).ToArray());
    }

    [TestMethod]
    public async Task TreeView_KeepsAncestorsOfSearchHits()
    {
        // 只留命中行会把它们变成孤儿根,层级就没了。
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(100, 1, "parent", "/usr/bin/parent"),
            Process(200, 100, "needle", "/usr/bin/needle")
        );
        await _vm.RefreshAsync();
        _vm.ShowTree = true;
        _vm.SearchText = "needle";

        CollectionAssert.AreEqual(new[] { 1, 100, 200 }, _vm.Processes.Select(row => row.Pid).ToArray());
        Assert.AreEqual(2, _vm.Processes.Single(row => row.Pid == 200).Depth);
    }

    [TestMethod]
    public async Task TreeView_PromotesRowsWhoseParentIsFilteredOut()
    {
        // 父是内核线程且内核线程被隐藏时,子进程必须升为根,否则整棵子树会消失。
        _service.Snapshot = Snapshot(
            Process(2, 0, "kthreadd", "[kthreadd]"),
            Process(500, 2, "orphan", "/usr/bin/orphan")
        );
        await _vm.RefreshAsync();
        _vm.ShowTree = true;

        CollectionAssert.AreEqual(new[] { 500 }, _vm.Processes.Select(row => row.Pid).ToArray());
        Assert.AreEqual(0, _vm.Processes[0].Depth);
    }

    [TestMethod]
    public async Task FlatView_ClearsTreeDecorations()
    {
        _service.Snapshot = Snapshot(
            Process(1, 0, "init", "/sbin/init"),
            Process(100, 1, "child", "/usr/bin/child")
        );
        await _vm.RefreshAsync();
        _vm.ShowTree = true;
        Assert.AreEqual(1, _vm.Processes.Single(row => row.Pid == 100).Depth);

        _vm.ShowTree = false;
        Assert.IsTrue(_vm.Processes.All(row => row.Depth == 0));
        Assert.IsTrue(_vm.Processes.All(row => !row.HasChildren));
    }

    [TestMethod]
    public void Dispose_DropsTheDeltaBaseline()
    {
        _vm.Dispose();
        Assert.AreEqual(1, _service.BaselineResets);
    }

    // ---- helpers ----

    private static RemoteProcessSnapshot Snapshot(params RemoteProcessInfo[] processes) =>
        new()
        {
            Processes = processes,
            CpuCores = 4,
            MemTotalBytes = 16L * 1024 * 1024 * 1024,
            MemUsedBytes = 6L * 1024 * 1024 * 1024,
            UptimeSeconds = 1000
        };

    private static RemoteProcessInfo Process(
        int pid,
        int ppid,
        string name,
        string commandLine,
        string user = "root",
        double cpu = 0
    ) =>
        new()
        {
            Pid = pid,
            ParentPid = ppid,
            Name = name,
            CommandLine = commandLine,
            User = user,
            State = "S",
            Threads = 1,
            MemoryBytes = 1024 * 1024,
            CpuPercent = cpu
        };

    private sealed class FakeProcessService : IRemoteProcessService
    {
        public RemoteProcessSnapshot? Snapshot { get; set; }

        public RemoteCommandOutcome Outcome { get; set; } = new(true, string.Empty);

        public List<(IReadOnlyList<int> Pids, ProcessSignal Signal)> Signals { get; } = [];

        public int BaselineResets { get; private set; }

        public Task<RemoteProcessSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<RemoteCommandOutcome> SignalAsync(
            Guid sessionId,
            IReadOnlyList<int> pids,
            ProcessSignal signal,
            CancellationToken cancellationToken = default
        )
        {
            Signals.Add((pids, signal));
            return Task.FromResult(Outcome);
        }

        public Task<RemoteCommandOutcome> ReniceAsync(
            Guid sessionId,
            int pid,
            int niceness,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Outcome);

        public void ResetBaseline(Guid sessionId) => BaselineResets++;
    }
}

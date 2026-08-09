using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class QuickCommandsViewModelTests : IDisposable
{
    /// <summary>
    /// 内置命令条数。一律由目录推导,不写死数字:内置目录是产品内容、会随需求增删
    /// (如 5c22090 换掉了整批命令),这里要盯的是“视图模型如何对待内置命令”,
    /// 而不是目录当下装了哪几条 —— 写死就会在每次改目录时集体失效。
    /// </summary>
    private static int BuiltInCount => QuickCommandCatalog.BuiltIns.Count;

    /// <summary>拿来当操作对象的样本内置命令(同样不写死具体是哪条)。</summary>
    private static QuickCommand SampleBuiltIn => QuickCommandCatalog.BuiltIns[0];

    private readonly IAppDataStore _dataStore;
    private readonly SonnetDbEngine _engine;
    private readonly string _legacyDataPath;
    private readonly string _testDirectory;
    private readonly IQuickCommandRepository _repository;
    private readonly QuickCommandsViewModel _vm;

    public QuickCommandsViewModelTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_qctest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _legacyDataPath = Path.Combine(_testDirectory, "quick-commands.json");
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _dataStore = new SonnetDbAppDataStore(_engine);
        _repository = new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath);
        _vm = new(_repository);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInDefaults_LoadTheEntireBuiltInCatalog()
    {
        var names = _vm.AllCommands.Select(c => c.Name).ToList();
        Assert.AreSequenceEqual([.. QuickCommandCatalog.BuiltIns.Select(c => c.Name)], names, SequenceOrder.InAnyOrder);
        Assert.HasCount(BuiltInCount, _vm.AllCommands);
        Assert.IsTrue(_vm.AllCommands.All(c => c.IsBuiltIn));
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByName_CaseInsensitive()
    {
        // 大写查询命中小写名称 —— 这里验的是忽略大小写,期望条数按目录实际情况推导。
        int expected = QuickCommandCatalog.BuiltIns.Count(c =>
            c.Name.Contains("docker", StringComparison.OrdinalIgnoreCase)
        );
        Assert.IsGreaterThan(0, expected, "样本前提:内置目录里应有 docker 相关命令");

        _vm.SearchQuery = "DOCKER";

        Assert.HasCount(expected, _vm.FilteredCommands);
        Assert.IsTrue(
            _vm.FilteredCommands.All(c =>
                c.Name.Contains("docker", StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_FiltersByDescriptionAndCommandText()
    {
        // 只出现在命令正文、不出现在名称里的词:命中它就说明筛选确实看了 CommandText。
        QuickCommand byText = QuickCommandCatalog.BuiltIns.First(c =>
            c.CommandText.Contains("dpkg")
        );
        _vm.SearchQuery = "dpkg";
        Assert.ContainsSingle(c => c.Name == byText.Name, _vm.FilteredCommands);
        Assert.DoesNotContain(
            "dpkg",
            byText.Name,
            "样本前提:该词不该出现在名称里,否则测不到 CommandText 匹配"
        );

        // 描述是本地化的,拿目录里的原值当查询,才不会绑死在某种语言上。
        QuickCommand byDescription = QuickCommandCatalog.BuiltIns.First(c =>
            !string.IsNullOrWhiteSpace(c.Description)
        );
        _vm.SearchQuery = byDescription.Description;
        Assert.Contains(c => c.Name == byDescription.Name, _vm.FilteredCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_NoExplicitSelection_UsesCurrentTerminal()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(currentId, "current")]);
        runner.SetCurrentTarget(currentId);
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;
        QuickCommandViewModel command = _vm.AllCommands.First(c => c.Name == SampleBuiltIn.Name);
        runner.SendCommand.Execute(command).Subscribe();

        Assert.IsNotNull(request);
        Assert.AreEqual(SampleBuiltIn.CommandText, request.CommandText);
        Assert.AreSequenceEqual([currentId], [.. request.TargetIds]);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_ExplicitSelection_BroadcastsOnlySelectedTerminals()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(firstId, "first"), (secondId, "second"), (currentId, "current")]);
        runner.SetCurrentTarget(currentId);
        runner.Targets[0].IsSelected = true;
        runner.Targets[1].IsSelected = true;
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;

        runner.SendCommand.Execute(_vm.AllCommands[0]).Subscribe();

        Assert.IsNotNull(request);
        Assert.AreSequenceEqual([firstId, secondId], [.. request.TargetIds], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Runner_RemovedSelectedTarget_FallsBackToCurrentTerminal()
    {
        var runner = new QuickCommandRunnerViewModel(_vm);
        var removedId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        runner.UpdateTargets([(removedId, "removed"), (currentId, "current")]);
        runner.Targets[0].IsSelected = true;
        runner.SetCurrentTarget(currentId);

        runner.UpdateTargets([(currentId, "current")]);

        Assert.AreEqual(0, runner.SelectedTargetCount);
        Assert.IsTrue(runner.CanRun);
        QuickCommandExecutionRequest? request = null;
        runner.ExecutionRequested += (_, e) => request = e;
        runner.SendCommand.Execute(_vm.AllCommands[0]).Subscribe();
        Assert.IsNotNull(request);
        Assert.AreSequenceEqual([currentId], [.. request.TargetIds]);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task AddCommand_AddsCustomCommandToListAndPersists()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsAddingCommand);
        string ungrouped = Core.Resources.Strings.Get("QuickCmd_Ungrouped");
        Assert.AreEqual(ungrouped, _vm.NewCategory);
        _vm.NewName = "my-cmd";
        _vm.NewCommandText = "echo hello";
        _vm.NewDescription = "Says hello";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        Assert.HasCount(BuiltInCount + 1, _vm.AllCommands);
        Assert.IsFalse(_vm.IsAddingCommand);
        QuickCommandViewModel added = _vm.AllCommands.Last();
        Assert.AreEqual("my-cmd", added.Name);
        Assert.AreEqual("echo hello", added.CommandText);
        Assert.AreEqual("Says hello", added.Description);
        Assert.AreEqual(ungrouped, added.Category);
        Assert.IsFalse(added.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task DeleteCommand_RemovesCustomCommand_ButNotBuiltIn()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "temp-cmd";
        _vm.NewCommandText = "ls -la";
        _vm.NewDescription = "List files";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel customCmd = _vm.AllCommands.First(c => c.Name == "temp-cmd");
        await _vm.DeleteCommandCommand.Execute(customCmd).FirstAsync();
        Assert.DoesNotContain(c => c.Name == "temp-cmd", _vm.AllCommands);
        Assert.HasCount(BuiltInCount, _vm.AllCommands);

        // 内置命令删不掉。
        QuickCommandViewModel builtIn = _vm.AllCommands.First(c => c.Name == SampleBuiltIn.Name);
        await _vm.DeleteCommandCommand.Execute(builtIn).FirstAsync();
        Assert.Contains(c => c.Name == SampleBuiltIn.Name, _vm.AllCommands);
        Assert.HasCount(BuiltInCount, _vm.AllCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SearchQuery_EmptyString_ShowsAllCommands()
    {
        _vm.SearchQuery = "docker";
        Assert.IsLessThan(
            BuiltInCount,
            _vm.FilteredCommands.Count,
            "样本前提:该查询应筛掉一部分命令"
        );

        _vm.SearchQuery = "";

        Assert.HasCount(BuiltInCount, _vm.FilteredCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Categories_ContainAllDistinctCategories()
    {
        var expected = QuickCommandGroupCatalog
            .BuiltIns.Select(group => group.Name)
            .Append(Core.Resources.Strings.Get("QuickCmd_Ungrouped"))
            .ToList();
        Assert.AreSequenceEqual(
            expected, [.. _vm.Categories], "分类应为内置目录去重排序后的结果"
        );
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void BuiltInCommand_CannotBeModified()
    {
        QuickCommandViewModel builtIn = _vm.AllCommands.First(c => c.Name == SampleBuiltIn.Name);

        builtIn.Name = "modified";
        builtIn.CommandText = "modified";
        builtIn.Description = "modified";

        // 三个字段都该纹丝不动(描述是本地化文案,拿目录里的原值比,不绑死语言)。
        Assert.AreEqual(SampleBuiltIn.Name, builtIn.Name);
        Assert.AreEqual(SampleBuiltIn.CommandText, builtIn.CommandText);
        Assert.AreEqual(SampleBuiltIn.Description, builtIn.Description);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task LoadCustomCommands_RestoresPersistedCommands()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "persisted-cmd";
        _vm.NewCommandText = "uptime";
        _vm.NewDescription = "Show uptime";
        _vm.NewCategory = "Custom";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );
        await vm2.LoadAsync();
        Assert.HasCount(BuiltInCount + 1, vm2.AllCommands);
        QuickCommandViewModel restored = vm2.AllCommands.First(c => c.Name == "persisted-cmd");
        Assert.AreEqual("uptime", restored.CommandText);
        Assert.IsFalse(restored.IsBuiltIn);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task LoadAsync_RepeatedCall_DoesNotDuplicateCustomCommands()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "single-load";
        _vm.NewCommandText = "whoami";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );

        await vm2.LoadAsync();
        await vm2.LoadAsync();

        Assert.ContainsSingle(command => command.Name == "single-load", vm2.AllCommands);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void Search_ExpandsMatchingGroup_ThenRestoresCollapsedState()
    {
        QuickCommandGroupViewModel group = _vm.Groups.First(item => item.Commands.Count > 0);
        QuickCommandViewModel command = group.Commands[0];
        group.IsExpanded = false;

        _vm.SearchQuery = command.Name;

        Assert.IsTrue(group.IsExpanded);
        Assert.Contains(group, _vm.FilteredGroups);

        _vm.SearchQuery = string.Empty;

        Assert.IsFalse(group.IsExpanded);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public async Task EditCommand_MovesItToNewPersistedGroup()
    {
        _vm.NewName = "deploy";
        _vm.NewCommandText = "./deploy.sh";
        _vm.NewCategory = "Ops";
        await _vm.SaveNewCommandCommand.Execute().FirstAsync();
        QuickCommandViewModel command = _vm.AllCommands.Single(item => item.Name == "deploy");

        _vm.BeginEditCommand.Execute(command).Subscribe();
        _vm.NewCategory = "Release";
        await _vm.SaveEditCommand.Execute().FirstAsync();

        Assert.AreEqual("Release", command.Category);
        var vm2 = new QuickCommandsViewModel(
            new SonnetDbQuickCommandRepository(_dataStore, _legacyDataPath)
        );
        await vm2.LoadAsync();
        Assert.AreEqual("Release", vm2.AllCommands.Single(item => item.Name == "deploy").Category);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void CancelAdd_HidesAddForm()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsAddingCommand);
        _vm.CancelAddCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsAddingCommand);
    }
}

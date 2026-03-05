using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.App.Tests.ViewModels;

public class QuickCommandsViewModelTests : IDisposable
{
    private readonly JsonDataStore _dataStore;
    private readonly string _testDirectory;
    private readonly string _testDataPath;
    private readonly QuickCommandsViewModel _vm;
    private string? _executedCommand;

    public QuickCommandsViewModelTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_qctest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testDataPath = Path.Combine(_testDirectory, "quick-commands.json");

        _dataStore = new JsonDataStore();
        _executedCommand = null;
        _vm = new QuickCommandsViewModel(
            _dataStore,
            cmd => _executedCommand = cmd,
            _testDataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void BuiltInDefaults_ContainExpectedCommands()
    {
        var names = _vm.AllCommands.Select(c => c.Name).ToList();

        names.Should().Contain("htop");
        names.Should().Contain("top");
        names.Should().Contain("df -h");
        names.Should().Contain("free -m");
        names.Should().Contain("docker ps");
        names.Should().Contain("docker stats");
        names.Should().Contain("netstat -tlnp");
        names.Should().Contain("ss -tlnp");
        names.Should().Contain("systemctl status");
        names.Should().Contain("journalctl -f");

        _vm.AllCommands.Should().HaveCount(10);
        _vm.AllCommands.Should().OnlyContain(c => c.IsBuiltIn);
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void SearchQuery_FiltersByName_CaseInsensitive()
    {
        _vm.SearchQuery = "DOCKER";

        _vm.FilteredCommands.Should().HaveCount(2);
        _vm.FilteredCommands.Should().OnlyContain(c => c.Name.Contains("docker"));
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void SearchQuery_FiltersByDescriptionAndCommandText()
    {
        _vm.SearchQuery = "process";

        _vm.FilteredCommands.Should().Contain(c => c.Name == "htop");
        _vm.FilteredCommands.Should().Contain(c => c.Name == "top");

        _vm.SearchQuery = "systemctl";

        _vm.FilteredCommands.Should().HaveCount(1);
        _vm.FilteredCommands[0].Name.Should().Be("systemctl status");
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void ExecuteCommand_InvokesCallbackWithCommandText()
    {
        var htopVm = _vm.AllCommands.First(c => c.Name == "htop");

        _vm.ExecuteCommandCommand.Execute(htopVm).Subscribe();

        _executedCommand.Should().Be("htop");
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void AddCommand_AddsCustomCommandToListAndPersists()
    {
        _vm.AddCommandCommand.Execute().Subscribe();

        _vm.IsAddingCommand.Should().BeTrue();
        _vm.NewCategory.Should().Be("Custom");

        _vm.NewName = "my-cmd";
        _vm.NewCommandText = "echo hello";
        _vm.NewDescription = "Says hello";

        _vm.SaveNewCommandCommand.Execute().Subscribe();

        _vm.AllCommands.Should().HaveCount(11);
        _vm.IsAddingCommand.Should().BeFalse();

        var added = _vm.AllCommands.Last();
        added.Name.Should().Be("my-cmd");
        added.CommandText.Should().Be("echo hello");
        added.Description.Should().Be("Says hello");
        added.Category.Should().Be("Custom");
        added.IsBuiltIn.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void DeleteCommand_RemovesCustomCommand_ButNotBuiltIn()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "temp-cmd";
        _vm.NewCommandText = "ls -la";
        _vm.NewDescription = "List files";
        _vm.SaveNewCommandCommand.Execute().Subscribe();

        var customCmd = _vm.AllCommands.First(c => c.Name == "temp-cmd");
        _vm.DeleteCommandCommand.Execute(customCmd).Subscribe();

        _vm.AllCommands.Should().NotContain(c => c.Name == "temp-cmd");
        _vm.AllCommands.Should().HaveCount(10);

        var builtIn = _vm.AllCommands.First(c => c.Name == "htop");
        _vm.DeleteCommandCommand.Execute(builtIn).Subscribe();

        _vm.AllCommands.Should().Contain(c => c.Name == "htop");
        _vm.AllCommands.Should().HaveCount(10);
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void SearchQuery_EmptyString_ShowsAllCommands()
    {
        _vm.SearchQuery = "docker";
        _vm.FilteredCommands.Should().HaveCount(2);

        _vm.SearchQuery = "";
        _vm.FilteredCommands.Should().HaveCount(10);
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void Categories_ContainAllDistinctCategories()
    {
        _vm.Categories.Should().Contain("System Monitor");
        _vm.Categories.Should().Contain("Network");
        _vm.Categories.Should().Contain("Docker");
        _vm.Categories.Should().Contain("System");
        _vm.Categories.Should().HaveCount(4);
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void BuiltInCommand_CannotBeModified()
    {
        var htop = _vm.AllCommands.First(c => c.Name == "htop");

        htop.Name = "modified";
        htop.Name.Should().Be("htop");

        htop.CommandText = "modified";
        htop.CommandText.Should().Be("htop");

        htop.Description = "modified";
        htop.Description.Should().Be("Interactive process viewer");
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public async Task LoadCustomCommands_RestoresPersistedCommands()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.NewName = "persisted-cmd";
        _vm.NewCommandText = "uptime";
        _vm.NewDescription = "Show uptime";
        _vm.NewCategory = "Custom";
        _vm.SaveNewCommandCommand.Execute().Subscribe();

        await Task.Delay(200);

        var vm2 = new QuickCommandsViewModel(
            _dataStore,
            null,
            _testDataPath);
        await vm2.LoadCustomCommandsAsync();

        vm2.AllCommands.Should().HaveCount(11);
        var restored = vm2.AllCommands.First(c => c.Name == "persisted-cmd");
        restored.CommandText.Should().Be("uptime");
        restored.IsBuiltIn.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "QuickCommands")]
    public void CancelAdd_HidesAddForm()
    {
        _vm.AddCommandCommand.Execute().Subscribe();
        _vm.IsAddingCommand.Should().BeTrue();

        _vm.CancelAddCommand.Execute().Subscribe();
        _vm.IsAddingCommand.Should().BeFalse();
    }
}

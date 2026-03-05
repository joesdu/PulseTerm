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

public class QuickCommandsViewModel : ReactiveObject
{
    private readonly JsonDataStore _dataStore;
    private readonly string _dataPath;
    private readonly Action<string>? _executeCallback;

    private string _searchQuery = string.Empty;
    private bool _isAddingCommand;
    private string _newName = string.Empty;
    private string _newCategory = string.Empty;
    private string _newCommandText = string.Empty;
    private string _newDescription = string.Empty;
    private QuickCommandViewModel? _editingCommand;

    public QuickCommandsViewModel(
        JsonDataStore dataStore,
        Action<string>? executeCallback = null,
        string? dataPath = null)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _executeCallback = executeCallback;

        if (string.IsNullOrEmpty(dataPath))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _dataPath = System.IO.Path.Combine(userProfile, ".pulseterm", "quick-commands.json");
        }
        else
        {
            _dataPath = dataPath;
        }

        AllCommands = new ObservableCollection<QuickCommandViewModel>();
        FilteredCommands = new ObservableCollection<QuickCommandViewModel>();
        Categories = new ObservableCollection<string>();

        ExecuteCommandCommand = ReactiveCommand.Create<QuickCommandViewModel>(ExecuteCommand);
        AddCommandCommand = ReactiveCommand.Create(AddCommand);
        DeleteCommandCommand = ReactiveCommand.Create<QuickCommandViewModel>(DeleteCommand);
        SaveNewCommandCommand = ReactiveCommand.Create(SaveNewCommand);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        BeginEditCommand = ReactiveCommand.Create<QuickCommandViewModel>(BeginEdit);
        SaveEditCommand = ReactiveCommand.Create(SaveEdit);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);

        this.WhenAnyValue(vm => vm.SearchQuery)
            .Subscribe(_ => ApplyFilter());

        LoadBuiltInCommands();
    }

    public ObservableCollection<QuickCommandViewModel> AllCommands { get; }
    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; }
    public ObservableCollection<string> Categories { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    public bool IsAddingCommand
    {
        get => _isAddingCommand;
        set => this.RaiseAndSetIfChanged(ref _isAddingCommand, value);
    }

    public string NewName
    {
        get => _newName;
        set => this.RaiseAndSetIfChanged(ref _newName, value);
    }

    public string NewCategory
    {
        get => _newCategory;
        set => this.RaiseAndSetIfChanged(ref _newCategory, value);
    }

    public string NewCommandText
    {
        get => _newCommandText;
        set => this.RaiseAndSetIfChanged(ref _newCommandText, value);
    }

    public string NewDescription
    {
        get => _newDescription;
        set => this.RaiseAndSetIfChanged(ref _newDescription, value);
    }

    public QuickCommandViewModel? EditingCommand
    {
        get => _editingCommand;
        set => this.RaiseAndSetIfChanged(ref _editingCommand, value);
    }

    public ReactiveCommand<QuickCommandViewModel, Unit> ExecuteCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommandCommand { get; }
    public ReactiveCommand<QuickCommandViewModel, Unit> DeleteCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveNewCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddCommand { get; }
    public ReactiveCommand<QuickCommandViewModel, Unit> BeginEditCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }

    private void LoadBuiltInCommands()
    {
        var builtIns = new List<QuickCommand>
        {
            new() { Name = "htop", Category = "System Monitor", CommandText = "htop", Description = "Interactive process viewer", IsBuiltIn = true },
            new() { Name = "top", Category = "System Monitor", CommandText = "top", Description = "Display running processes", IsBuiltIn = true },
            new() { Name = "df -h", Category = "System Monitor", CommandText = "df -h", Description = "Disk space usage (human-readable)", IsBuiltIn = true },
            new() { Name = "free -m", Category = "System Monitor", CommandText = "free -m", Description = "Memory usage in MB", IsBuiltIn = true },

            new() { Name = "netstat -tlnp", Category = "Network", CommandText = "netstat -tlnp", Description = "Show listening ports", IsBuiltIn = true },
            new() { Name = "ss -tlnp", Category = "Network", CommandText = "ss -tlnp", Description = "Socket statistics", IsBuiltIn = true },

            new() { Name = "docker ps", Category = "Docker", CommandText = "docker ps", Description = "List running containers", IsBuiltIn = true },
            new() { Name = "docker stats", Category = "Docker", CommandText = "docker stats", Description = "Container resource usage", IsBuiltIn = true },

            new() { Name = "systemctl status", Category = "System", CommandText = "systemctl status", Description = "Show systemd service status", IsBuiltIn = true },
            new() { Name = "journalctl -f", Category = "System", CommandText = "journalctl -f", Description = "Follow system journal", IsBuiltIn = true },
        };

        foreach (var cmd in builtIns)
        {
            AllCommands.Add(new QuickCommandViewModel(cmd));
        }

        RefreshCategories();
        ApplyFilter();
    }

    public async Task LoadCustomCommandsAsync()
    {
        var data = await _dataStore.LoadAsync<QuickCommandData>(_dataPath);
        if (data?.Commands != null)
        {
            foreach (var cmd in data.Commands)
            {
                cmd.IsBuiltIn = false;
                AllCommands.Add(new QuickCommandViewModel(cmd));
            }
        }

        RefreshCategories();
        ApplyFilter();
    }

    private async Task SaveCustomCommandsAsync()
    {
        var customCommands = AllCommands
            .Where(c => !c.IsBuiltIn)
            .Select(c => c.ToModel())
            .ToList();

        var data = new QuickCommandData { Commands = customCommands };
        await _dataStore.SaveAsync(_dataPath, data);
    }

    private void ExecuteCommand(QuickCommandViewModel command)
    {
        _executeCallback?.Invoke(command.CommandText);
    }

    private void AddCommand()
    {
        IsAddingCommand = true;
        NewName = string.Empty;
        NewCategory = "Custom";
        NewCommandText = string.Empty;
        NewDescription = string.Empty;
    }

    private void SaveNewCommand()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewCommandText))
            return;

        var model = new QuickCommand
        {
            Name = NewName.Trim(),
            Category = string.IsNullOrWhiteSpace(NewCategory) ? "Custom" : NewCategory.Trim(),
            CommandText = NewCommandText.Trim(),
            Description = NewDescription.Trim(),
            IsBuiltIn = false
        };

        AllCommands.Add(new QuickCommandViewModel(model));
        IsAddingCommand = false;

        RefreshCategories();
        ApplyFilter();

        _ = SaveCustomCommandsAsync();
    }

    private void DeleteCommand(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn)
            return;

        AllCommands.Remove(command);
        FilteredCommands.Remove(command);

        RefreshCategories();

        _ = SaveCustomCommandsAsync();
    }

    private void BeginEdit(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn)
            return;

        EditingCommand = command;
        NewName = command.Name;
        NewCategory = command.Category;
        NewCommandText = command.CommandText;
        NewDescription = command.Description;
    }

    private void SaveEdit()
    {
        if (EditingCommand == null || EditingCommand.IsBuiltIn)
            return;

        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewCommandText))
            return;

        EditingCommand.Name = NewName.Trim();
        EditingCommand.Category = string.IsNullOrWhiteSpace(NewCategory) ? "Custom" : NewCategory.Trim();
        EditingCommand.CommandText = NewCommandText.Trim();
        EditingCommand.Description = NewDescription.Trim();

        EditingCommand = null;

        RefreshCategories();
        ApplyFilter();

        _ = SaveCustomCommandsAsync();
    }

    private void CancelEdit()
    {
        EditingCommand = null;
    }

    private void CancelAdd()
    {
        IsAddingCommand = false;
    }

    private void ApplyFilter()
    {
        FilteredCommands.Clear();

        var query = SearchQuery?.Trim() ?? string.Empty;

        foreach (var cmd in AllCommands)
        {
            if (string.IsNullOrEmpty(query) ||
                cmd.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                cmd.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                cmd.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(cmd);
            }
        }
    }

    private void RefreshCategories()
    {
        Categories.Clear();
        var cats = AllCommands.Select(c => c.Category).Distinct().OrderBy(c => c);
        foreach (var cat in cats)
        {
            Categories.Add(cat);
        }
    }

    internal class QuickCommandData
    {
        public List<QuickCommand> Commands { get; set; } = new();
    }
}

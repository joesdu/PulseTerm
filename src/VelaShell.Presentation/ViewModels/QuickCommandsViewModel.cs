using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令目录视图模型:管理分组、内置命令与自定义命令。</summary>
public class QuickCommandsViewModel : ReactiveObject
{
    private readonly IQuickCommandRepository _repository;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<Guid, bool> _expansionBeforeSearch = [];
    private bool _loaded;
    private bool _searchActive;

    /// <summary>创建快捷命令视图模型。</summary>
    public QuickCommandsViewModel(IQuickCommandRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        AllCommands = [];
        Groups = [];
        FilteredGroups = [];
        FilteredCommands = [];
        Categories = [];
        AddCommandCommand = ReactiveCommand.Create(AddCommand);
        DeleteCommandCommand = ReactiveCommand.CreateFromTask<QuickCommandViewModel>(
            DeleteCommandAsync
        );
        SaveNewCommandCommand = ReactiveCommand.CreateFromTask(SaveNewCommandAsync);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        BeginEditCommand = ReactiveCommand.Create<QuickCommandViewModel>(BeginEdit);
        SaveEditCommand = ReactiveCommand.CreateFromTask(SaveEditAsync);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        this.WhenAnyValue(viewModel => viewModel.SearchQuery).Subscribe(_ => ApplyFilter());
        BuildFromData(new() { Groups = QuickCommandGroupCatalog.CreateSystemGroups() });
    }

    /// <summary>全部命令(内置 + 自定义)。</summary>
    public ObservableCollection<QuickCommandViewModel> AllCommands { get; }

    /// <summary>全部分组。</summary>
    public ObservableCollection<QuickCommandGroupViewModel> Groups { get; }

    /// <summary>当前搜索条件下可见的分组。</summary>
    public ObservableCollection<QuickCommandGroupViewModel> FilteredGroups { get; }

    /// <summary>兼容旧绑定与命令补全测试的扁平筛选结果。</summary>
    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; }

    /// <summary>新增/编辑时可选择的分组名称。</summary>
    public ObservableCollection<string> Categories { get; }

    /// <summary>搜索关键字。</summary>
    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>迁移或加载错误;非空时只展示内置命令并禁止覆盖未知版本。</summary>
    public string ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>设置页当前是否显示新增片段表单。</summary>
    public bool IsAddingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新增或编辑片段的名称。</summary>
    public string NewName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段的分组名称。</summary>
    public string NewCategory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段的命令正文。</summary>
    public string NewCommandText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段的说明。</summary>
    public string NewDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>当前正在编辑的自定义片段。</summary>
    public QuickCommandViewModel? EditingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开新增片段表单。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddCommandCommand { get; }

    /// <summary>删除指定自定义片段。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> DeleteCommandCommand { get; }

    /// <summary>保存新增片段。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveNewCommandCommand { get; }

    /// <summary>取消新增片段。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelAddCommand { get; }

    /// <summary>开始编辑指定自定义片段。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> BeginEditCommand { get; }

    /// <summary>保存当前片段编辑。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveEditCommand { get; }

    /// <summary>取消当前片段编辑。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelEditCommand { get; }

    /// <summary>加载并迁移自定义快捷命令。</summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }
        await _loadGate.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }
            QuickCommandLoadResult result = await _repository.LoadAsync();
            ErrorMessage = result.Error ?? string.Empty;
            BuildFromData(result.Data);
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void BuildFromData(QuickCommandData data)
    {
        var expansion = Groups.ToDictionary(
            group => group.Id,
            group => group.IsExpanded
        );
        Groups.Clear();
        AllCommands.Clear();

        foreach (
            QuickCommandGroup group in data
                .Groups.OrderBy(group => group.Kind == QuickCommandGroupKind.Default)
                .ThenBy(group => group.SortOrder)
                .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            string displayName =
                group.Kind == QuickCommandGroupKind.Default
                    ? Strings.Get("QuickCmd_Ungrouped")
                    : group.Name;
            var groupViewModel = new QuickCommandGroupViewModel(
                QuickCommandGroupCatalog.Clone(group),
                displayName
            )
            {
                IsExpanded = expansion.GetValueOrDefault(group.Id, true),
            };
            Groups.Add(groupViewModel);
        }

        foreach (QuickCommand command in QuickCommandCatalog.BuiltIns)
        {
            AddToGroup(new(command));
        }
        foreach (
            QuickCommand command in data
                .Commands.OrderBy(command => command.SortOrder)
                .ThenBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            command.IsBuiltIn = false;
            AddToGroup(new(command));
        }
        RefreshCategories();
        ApplyFilter();
    }

    private void AddToGroup(QuickCommandViewModel command)
    {
        QuickCommandGroupViewModel group =
            Groups.FirstOrDefault(item => item.Id == command.GroupId)
            ?? Groups.First(item => item.Id == QuickCommandGroupCatalog.DefaultGroupId);
        if (group.Id != command.GroupId && !command.IsBuiltIn)
        {
            command.GroupId = group.Id;
        }
        command.Category = group.Name;
        group.Commands.Add(command);
        AllCommands.Add(command);
    }

    private async Task SaveCustomCommandsAsync()
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        var data = new QuickCommandData
        {
            Groups = [.. Groups.Select(group => QuickCommandGroupCatalog.Clone(group.Model))],
            Commands =
            [
                .. AllCommands
                    .Where(command => !command.IsBuiltIn)
                    .Select(command => command.ToModel()),
            ],
        };
        await _repository.SaveAsync(data);
    }

    private void AddCommand()
    {
        IsAddingCommand = true;
        NewName = string.Empty;
        NewCategory = Strings.Get("QuickCmd_Ungrouped");
        NewCommandText = string.Empty;
        NewDescription = string.Empty;
    }

    private async Task SaveNewCommandAsync()
    {
        if (
            !string.IsNullOrEmpty(ErrorMessage)
            || string.IsNullOrWhiteSpace(NewName)
            || string.IsNullOrWhiteSpace(NewCommandText)
        )
        {
            return;
        }
        QuickCommandGroupViewModel group = ResolveOrCreateGroup(NewCategory);
        int order =
            group
                .Commands.Where(command => !command.IsBuiltIn)
                .Select(command => command.SortOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;
        var command = new QuickCommandViewModel(
            new()
            {
                GroupId = group.Id,
                Name = NewName.Trim(),
                CommandText = NewCommandText.Trim(),
                Description = NewDescription.Trim(),
                SortOrder = order,
            }
        )
        {
            Category = group.Name,
        };
        group.Commands.Add(command);
        AllCommands.Add(command);
        IsAddingCommand = false;
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private async Task DeleteCommandAsync(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn || !string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        AllCommands.Remove(command);
        Groups.FirstOrDefault(group => group.Id == command.GroupId)?.Commands.Remove(command);
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private void BeginEdit(QuickCommandViewModel command)
    {
        if (command.IsBuiltIn || !string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        EditingCommand = command;
        NewName = command.Name;
        NewCategory = command.Category;
        NewCommandText = command.CommandText;
        NewDescription = command.Description;
    }

    private async Task SaveEditAsync()
    {
        if (
            EditingCommand is null
            || EditingCommand.IsBuiltIn
            || !string.IsNullOrEmpty(ErrorMessage)
            || string.IsNullOrWhiteSpace(NewName)
            || string.IsNullOrWhiteSpace(NewCommandText)
        )
        {
            return;
        }

        QuickCommandViewModel command = EditingCommand;
        QuickCommandGroupViewModel previous = Groups.First(group => group.Id == command.GroupId);
        QuickCommandGroupViewModel next = ResolveOrCreateGroup(NewCategory);
        command.Name = NewName.Trim();
        command.CommandText = NewCommandText.Trim();
        command.Description = NewDescription.Trim();
        if (previous.Id != next.Id)
        {
            previous.Commands.Remove(command);
            command.GroupId = next.Id;
            command.SortOrder =
                next.Commands.Where(item => !item.IsBuiltIn)
                    .Select(item => item.SortOrder)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
            next.Commands.Add(command);
        }
        command.Category = next.Name;
        EditingCommand = null;
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private void CancelEdit() => EditingCommand = null;

    private void CancelAdd() => IsAddingCommand = false;

    private QuickCommandGroupViewModel ResolveOrCreateGroup(string name)
    {
        string normalized = name.Trim();
        if (
            string.IsNullOrEmpty(normalized)
            || string.Equals(
                normalized,
                Strings.Get("QuickCmd_Ungrouped"),
                StringComparison.CurrentCultureIgnoreCase
            )
        )
        {
            return Groups.First(group => group.Id == QuickCommandGroupCatalog.DefaultGroupId);
        }
        QuickCommandGroupViewModel? existing = Groups.FirstOrDefault(group =>
            string.Equals(group.Name, normalized, StringComparison.CurrentCultureIgnoreCase)
        );
        if (existing is not null)
        {
            return existing;
        }

        int sortOrder =
            Groups
                .Where(group => group.Kind != QuickCommandGroupKind.Default)
                .Select(group => group.Model.SortOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;
        var created = new QuickCommandGroupViewModel(
            new()
            {
                Id = QuickCommandGroupCatalog.IdForName(normalized),
                Name = normalized,
                SortOrder = sortOrder,
                Kind = QuickCommandGroupKind.User,
            },
            normalized
        );
        int defaultIndex = Groups
            .ToList()
            .FindIndex(group => group.Kind == QuickCommandGroupKind.Default);
        Groups.Insert(defaultIndex < 0 ? Groups.Count : defaultIndex, created);
        return created;
    }

    private void ApplyFilter()
    {
        string query = SearchQuery.Trim();
        bool active = query.Length > 0;
        if (active && !_searchActive)
        {
            _expansionBeforeSearch.Clear();
            foreach (QuickCommandGroupViewModel group in Groups)
            {
                _expansionBeforeSearch[group.Id] = group.IsExpanded;
            }
        }
        else if (!active && _searchActive)
        {
            foreach (QuickCommandGroupViewModel group in Groups)
            {
                group.IsExpanded = _expansionBeforeSearch.GetValueOrDefault(group.Id, true);
            }
            _expansionBeforeSearch.Clear();
        }
        _searchActive = active;

        FilteredGroups.Clear();
        FilteredCommands.Clear();
        foreach (QuickCommandGroupViewModel group in Groups)
        {
            group.FilteredCommands.Clear();
            foreach (QuickCommandViewModel command in group.Commands)
            {
                if (!active || Matches(command, query))
                {
                    group.FilteredCommands.Add(command);
                    FilteredCommands.Add(command);
                }
            }
            if (group.FilteredCommands.Count > 0)
            {
                if (active)
                {
                    group.IsExpanded = true;
                }
                FilteredGroups.Add(group);
            }
        }
    }

    private static bool Matches(QuickCommandViewModel command, string query) =>
        command.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void RefreshCategories()
    {
        Categories.Clear();
        foreach (QuickCommandGroupViewModel group in Groups)
        {
            Categories.Add(group.Name);
        }
    }
}

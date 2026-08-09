using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 会话导入对话框的视图模型:扫描某个来源(Xshell / WinSCP …)生成勾选预览,并把选中的会话
/// 经 <see cref="ISessionImportService" /> 一键写入 VelaShell。同一 VM 适配所有来源。
/// </summary>
public sealed class SessionImportViewModel : ReactiveObject
{
    private readonly ISessionImportService _service;
    private readonly List<IDisposable> _itemSubscriptions = [];

    /// <summary>用某个来源的导入服务构造视图模型,并初始化扫描/勾选/导入命令。</summary>
    public SessionImportViewModel(ISessionImportService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Items = [];
        Title = Strings.Format("XImport_TitleFmt", _service.SourceKey);
        GroupName = Strings.Format("XImport_GroupFmt", _service.SourceKey);
        SourceLabel = _service.BrowseKind == ImportBrowseKind.File
            ? Strings.Get("XImport_SourceFile")
            : Strings.Get("XImport_SourceFolder");

        IObservable<bool> notBusy = this.WhenAnyValue(x => x.IsScanning).Select(static busy => !busy);
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync, notBusy);

        IObservable<bool> canImport = this.WhenAnyValue(x => x.SelectedCount, x => x.IsScanning)
            .Select(static t => t.Item1 > 0 && !t.Item2);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync, canImport);

        SelectAllCommand = ReactiveCommand.Create(() => SetAllSelected(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAllSelected(false));
    }

    /// <summary>对话框标题(含来源名)。</summary>
    public string Title { get; }

    /// <summary>来源输入框的标签(会话目录 / 配置文件)。</summary>
    public string SourceLabel { get; }

    /// <summary>是否允许手动浏览来源。</summary>
    public bool CanBrowse => _service.BrowseKind != ImportBrowseKind.None;

    /// <summary>浏览来源时应弹出的选择器类型。</summary>
    public ImportBrowseKind BrowseKind => _service.BrowseKind;

    /// <summary>当前来源(目录/文件路径或来源描述)。</summary>
    public string SourceText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新建承载分组的名称。</summary>
    public string GroupName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>导入预览行集合。</summary>
    public ObservableCollection<SessionImportItemViewModel> Items { get; }

    /// <summary>是否正在扫描来源。</summary>
    public bool IsScanning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已勾选(且受支持)的会话数量。</summary>
    public int SelectedCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>其中可自动还原密码的数量。</summary>
    public int RecoveredCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>底部选择汇总文案(已选数量 · 含密码数量)。</summary>
    public string SelectionSummary => Strings.Format("XImport_SelectedCount", SelectedCount, RecoveredCount);

    /// <summary>扫描到的会话总数是否为 0(驱动空状态提示)。</summary>
    public bool HasNoItems
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>来源是否启用主密码(启用时无法还原任何密码,给出提示)。</summary>
    public bool MasterPasswordEnabled
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>面向用户的状态/结果文案。</summary>
    public string StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>扫描当前来源并重建预览列表。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ScanCommand { get; }

    /// <summary>执行导入,返回结果统计(取消或无选中时为 <c>null</c>)。</summary>
    public ReactiveCommand<RxVoid, SessionImportOutcome?> ImportCommand { get; }

    /// <summary>勾选全部受支持的会话。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; }

    /// <summary>取消勾选全部会话。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectNoneCommand { get; }

    /// <summary>对话框打开时调用:自动探测来源并立即扫描;探测不到则提示手动选择。</summary>
    public async Task InitializeAsync()
    {
        string? detected = _service.DetectDefaultSource();
        SourceText = detected ?? string.Empty;
        if (detected is { Length: > 0 } || !CanBrowse)
        {
            await ScanAsync().ConfigureAwait(true);
        }
        else
        {
            StatusMessage = Strings.Get("XImport_NotDetected");
        }
    }

    private async Task ScanAsync()
    {
        IsScanning = true;
        StatusMessage = string.Empty;
        try
        {
            SessionImportScan result = await _service
                .ScanAsync(string.IsNullOrWhiteSpace(SourceText) ? null : SourceText)
                .ConfigureAwait(true);
            ClearItems();
            foreach (ImportedSession item in result.Items)
            {
                var vm = new SessionImportItemViewModel(item);
                _itemSubscriptions.Add(vm.WhenAnyValue(static x => x.IsSelected).Subscribe(_ => RecomputeCounts()));
                Items.Add(vm);
            }
            MasterPasswordEnabled = result.MasterPasswordEnabled;
            HasNoItems = Items.Count == 0;
            if (result.Source is { Length: > 0 })
            {
                SourceText = result.Source;
            }
            RecomputeCounts();
            StatusMessage = Items.Count == 0
                ? Strings.Get("XImport_NoSessions")
                : Strings.Format("XImport_Found", Items.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearItems();
            HasNoItems = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task<SessionImportOutcome?> ImportAsync()
    {
        var selected = Items
            .Where(static i => i.IsSelected && i.CanSelect)
            .Select(static i => i.Source)
            .ToList();
        if (selected.Count == 0)
        {
            return null;
        }
        IsScanning = true;
        try
        {
            return await _service.ImportAsync(selected, GroupName).ConfigureAwait(true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void SetAllSelected(bool value)
    {
        foreach (SessionImportItemViewModel item in Items)
        {
            if (item.CanSelect)
            {
                item.IsSelected = value;
            }
        }
    }

    private void RecomputeCounts()
    {
        int selected = 0;
        int recovered = 0;
        foreach (SessionImportItemViewModel item in Items)
        {
            if (!item.IsSelected || !item.CanSelect)
            {
                continue;
            }
            selected++;
            if (item.Source.PasswordRecovered)
            {
                recovered++;
            }
        }
        SelectedCount = selected;
        RecoveredCount = recovered;
        this.RaisePropertyChanged(nameof(SelectionSummary));
    }

    private void ClearItems()
    {
        foreach (IDisposable subscription in _itemSubscriptions)
        {
            subscription.Dispose();
        }
        _itemSubscriptions.Clear();
        Items.Clear();
    }
}

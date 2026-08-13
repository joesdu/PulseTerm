using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 导入对话框中的一个来源(Xshell / WinSCP …):自动探测来源位置、扫描出可导入会话,
/// 并持有该来源的预览行。用户可单独为该来源手动指定路径并重新扫描。
/// </summary>
public sealed class SessionImportSourceViewModel : ReactiveObject
{
    private readonly ISessionImportService _service;
    private readonly List<IDisposable> _itemSubscriptions = [];

    /// <summary>以某个来源的导入服务构造来源视图模型。</summary>
    public SessionImportSourceViewModel(ISessionImportService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Items = [];
        GroupName = Strings.Format("XImport_GroupFmt", service.SourceKey);
        SourceLabel = service.BrowseKind == ImportBrowseKind.File
            ? Strings.Get("XImport_SourceFile")
            : Strings.Get("XImport_SourceFolder");

        IObservable<bool> notBusy = this.WhenAnyValue(x => x.IsScanning).Select(static busy => !busy);
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync, notBusy);
    }

    /// <summary>来源名称(如 <c>Xshell</c>、<c>WinSCP</c>)。</summary>
    public string SourceKey => _service.SourceKey;

    /// <summary>手动指定来源时应弹出的选择器类型。</summary>
    public ImportBrowseKind BrowseKind => _service.BrowseKind;

    /// <summary>是否允许手动指定来源。</summary>
    public bool CanBrowse => _service.BrowseKind != ImportBrowseKind.None;

    /// <summary>手动指定按钮的提示文案(会话目录 / 配置文件)。</summary>
    public string SourceLabel { get; }

    /// <summary>导入该来源时新建的分组名。</summary>
    public string GroupName { get; }

    /// <summary>该来源的预览行。</summary>
    public ObservableCollection<SessionImportItemViewModel> Items { get; }

    /// <summary>某一行的勾选状态发生变化。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>一次扫描结束(无论是否扫到会话),供聚合视图模型重新去重与统计。</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>当前来源(目录/文件路径或注册表键);探测不到时为空串。</summary>
    public string SourceText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否已探测到该来源(能给出一个可读的来源位置)。</summary>
    public bool Detected => SourceText.Length > 0;

    /// <summary>是否正在扫描。</summary>
    public bool IsScanning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该来源是否启用了主密码(启用时其密码一律无法还原)。</summary>
    public bool MasterPasswordEnabled
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否展开显示逐条会话(自定义选择模式下为 <c>true</c>)。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowItems));
            this.RaisePropertyChanged(nameof(ShowSourceActions));
        }
    }

    /// <summary>是否展示逐条会话列表(展开且确实扫到了会话)。</summary>
    public bool ShowItems => IsExpanded && HasItems;

    /// <summary>
    /// 是否展示「手动指定 / 重新扫描」按钮:自定义选择模式下始终展示;
    /// 自动模式下仅在没探测到该来源时展示,便于用户立即指向便携版配置。
    /// </summary>
    public bool ShowSourceActions => IsExpanded || !Detected;

    /// <summary>该来源的一行状态文案(扫描中 / 发现 N 个会话 / 未检测到 / 读取失败)。</summary>
    public string StatusLine
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Get("XImport_SourceScanning");

    /// <summary>是否扫描到了会话。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>重新扫描该来源。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ScanCommand { get; }

    /// <summary>探测默认来源位置并扫描一次(对话框打开时调用)。</summary>
    public async Task DetectAndScanAsync()
    {
        SourceText = _service.DetectDefaultSource() ?? string.Empty;
        await ScanAsync().ConfigureAwait(true);
    }

    /// <summary>按当前 <see cref="SourceText" /> 扫描来源并重建预览行。</summary>
    public async Task ScanAsync()
    {
        IsScanning = true;
        StatusLine = Strings.Get("XImport_SourceScanning");
        RaiseStateChanged();
        try
        {
            SessionImportScan result = await _service
                .ScanAsync(string.IsNullOrWhiteSpace(SourceText) ? null : SourceText)
                .ConfigureAwait(true);
            ClearItems();
            foreach (ImportedSession item in result.Items)
            {
                var vm = new SessionImportItemViewModel(item);
                _itemSubscriptions.Add(vm.WhenAnyValue(static x => x.IsSelected)
                    .Subscribe(_ => SelectionChanged?.Invoke(this, EventArgs.Empty)));
                Items.Add(vm);
            }
            MasterPasswordEnabled = result.MasterPasswordEnabled;
            if (result.Source is { Length: > 0 })
            {
                SourceText = result.Source;
            }
            StatusLine = Items.Count > 0
                ? Strings.Format("XImport_SourceFoundFmt", Items.Count, Items.Count(static i => i.Source.PasswordRecovered))
                : Strings.Get("XImport_SourceNotFound");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearItems();
            StatusLine = ex.Message;
        }
        finally
        {
            IsScanning = false;
            RaiseStateChanged();
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>把该来源已勾选的会话写入 VelaShell;没有勾选项时返回 <c>null</c>。</summary>
    public async Task<SessionImportOutcome?> ImportSelectedAsync(CancellationToken cancellationToken = default)
    {
        List<ImportedSession> selected = [.. Items.Where(static i => i.IsSelected && i.CanSelect).Select(static i => i.Source)];
        return selected.Count == 0
            ? null
            : await _service.ImportAsync(selected, GroupName, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>扫描前后来源位置与会话数都可能变化,统一刷新派生的可见性属性。</summary>
    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(Detected));
        this.RaisePropertyChanged(nameof(HasItems));
        this.RaisePropertyChanged(nameof(ShowItems));
        this.RaisePropertyChanged(nameof(ShowSourceActions));
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

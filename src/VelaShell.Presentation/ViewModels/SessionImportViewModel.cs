using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 会话导入对话框的视图模型:打开即自动扫描所有已知来源(Xshell / WinSCP …),
/// 智能勾选可直接导入的会话(跳过重复与不支持的协议),用户点一下即可完成迁移;
/// 需要时可切到「自定义选择」逐条勾选、或手动指定某个来源的路径。
/// </summary>
public sealed class SessionImportViewModel : ReactiveObject
{
    /// <summary>用全部已注册的来源导入服务构造视图模型。</summary>
    public SessionImportViewModel(IEnumerable<ISessionImportService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Sources = [];
        foreach (ISessionImportService service in services)
        {
            var source = new SessionImportSourceViewModel(service);
            source.SelectionChanged += (_, _) => RecomputeCounts();
            source.ScanCompleted += (_, _) => ApplySmartSelection();
            Sources.Add(source);
        }

        IObservable<bool> notBusy = this.WhenAnyValue(x => x.IsBusy).Select(static busy => !busy);
        RescanAllCommand = ReactiveCommand.CreateFromTask(RescanAllAsync, notBusy);

        IObservable<bool> canImport = this.WhenAnyValue(x => x.SelectedCount, x => x.IsBusy)
            .Select(static t => t.Value1 > 0 && !t.Value2);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportSelectedAsync, canImport);

        SelectAllCommand = ReactiveCommand.Create(() => SetAllSelected(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAllSelected(false));
        ToggleAdvancedCommand = ReactiveCommand.Create(() => { IsAdvanced = !IsAdvanced; });
    }

    /// <summary>对话框标题。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("XImport_TitleAll");

    /// <summary>所有来源(每个来源一张卡片)。</summary>
    public ObservableCollection<SessionImportSourceViewModel> Sources { get; }

    /// <summary>是否处于「自定义选择」模式(展开逐条会话与手动指定来源)。</summary>
    public bool IsAdvanced
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            foreach (SessionImportSourceViewModel source in Sources)
            {
                source.IsExpanded = value;
            }
            this.RaisePropertyChanged(nameof(ModeToggleText));
        }
    }

    /// <summary>模式切换链接的文案(自定义选择 / 返回自动)。</summary>
    public string ModeToggleText => Strings.Get(IsAdvanced ? "XImport_BackToAuto" : "XImport_Advanced");

    /// <summary>是否跳过与已有会话重复的目标(默认开启)。</summary>
    public bool SkipExisting
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ApplySelectionRule();
        }
    } = true;

    /// <summary>是否正在扫描或导入(期间禁用主要按钮)。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>扫描到的会话总数(含重复与不支持的)。</summary>
    public int TotalCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前将要导入的会话数量。</summary>
    public int SelectedCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>其中已成功还原密码的数量。</summary>
    public int RecoveredCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>被跳过(未勾选)的数量:重复项、不支持的协议或用户手动取消。</summary>
    public int SkippedCount => TotalCount - SelectedCount;

    /// <summary>摘要主标题:扫描中 / 将导入 N 个会话 / 没有可导入的会话。</summary>
    public string Headline =>
        IsBusy ? Strings.Get("XImport_Scanning") :
        TotalCount == 0 ? Strings.Get("XImport_NoSourceDetected") :
        SelectedCount == 0 ? Strings.Get("XImport_NothingSelected") :
        Strings.Format("XImport_ReadyHeadlineFmt", SelectedCount);

    /// <summary>摘要副标题:密码还原与跳过数量。</summary>
    public string Detail =>
        IsBusy || TotalCount == 0
            ? Strings.Get("XImport_AutoHint")
            : SkippedCount > 0
                ? Strings.Format("XImport_ReadySubFmt", RecoveredCount, SkippedCount)
                : Strings.Format("XImport_ReadySubNoSkipFmt", RecoveredCount);

    /// <summary>导入按钮文案(带数量,让用户清楚点下去会发生什么)。</summary>
    public string ImportButtonText =>
        SelectedCount > 0
            ? Strings.Format("XImport_ImportCountFmt", SelectedCount)
            : Strings.Get("XImport_ImportButton");

    /// <summary>启用了主密码的来源提示;没有这类来源时为空串。</summary>
    public string MasterPasswordWarning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否需要显示主密码提示。</summary>
    public bool HasMasterPasswordWarning => MasterPasswordWarning.Length > 0;

    /// <summary>重新扫描全部来源。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RescanAllCommand { get; }

    /// <summary>执行导入,返回汇总结果(没有勾选项时为 <c>null</c>)。</summary>
    public ReactiveCommand<RxVoid, SessionImportOutcome?> ImportCommand { get; }

    /// <summary>勾选全部受支持的会话(含重复项)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; }

    /// <summary>取消勾选全部会话。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectNoneCommand { get; }

    /// <summary>在「自动」与「自定义选择」之间切换。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleAdvancedCommand { get; }

    /// <summary>对话框打开时调用:自动探测并扫描全部来源,然后按智能规则完成勾选。</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        RaiseSummaryChanged();
        try
        {
            foreach (SessionImportSourceViewModel source in Sources)
            {
                await source.DetectAndScanAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            ApplySmartSelection();
        }
    }

    /// <summary>把所有来源中已勾选的会话写入 VelaShell,并汇总结果;无勾选项时返回 <c>null</c>。</summary>
    public async Task<SessionImportOutcome?> ImportSelectedAsync()
    {
        IsBusy = true;
        RaiseSummaryChanged();
        try
        {
            int imported = 0;
            int recovered = 0;
            Guid? firstGroup = null;
            foreach (SessionImportSourceViewModel source in Sources)
            {
                SessionImportOutcome? outcome = await source.ImportSelectedAsync().ConfigureAwait(true);
                if (outcome is null)
                {
                    continue;
                }
                imported += outcome.Imported;
                recovered += outcome.PasswordsRecovered;
                firstGroup ??= outcome.GroupId;
            }
            return imported == 0
                ? null
                : new SessionImportOutcome
                {
                    Imported = imported,
                    PasswordsRecovered = recovered,
                    GroupId = firstGroup
                };
        }
        finally
        {
            IsBusy = false;
            RaiseSummaryChanged();
        }
    }

    /// <summary>
    /// 智能勾选:跨来源按 <c>主机|端口|用户</c> 去重(同一目标只保留第一条),
    /// 标出与 VelaShell 已有会话重复的项,然后按当前规则完成勾选并刷新统计。
    /// </summary>
    private void ApplySmartSelection()
    {
        HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (SessionImportSourceViewModel source in Sources)
        {
            foreach (SessionImportItemViewModel item in source.Items)
            {
                if (!item.CanSelect)
                {
                    continue;
                }
                string key = $"{item.Source.Host.Trim()}|{item.Source.Port}|{item.Source.Username.Trim()}";
                item.IsDuplicate = !seen.Add(key) || item.Source.AlreadyExists;
            }
        }
        ApplySelectionRule();

        List<string> withMaster = [.. Sources.Where(static s => s.MasterPasswordEnabled).Select(static s => s.SourceKey)];
        MasterPasswordWarning = withMaster.Count == 0
            ? string.Empty
            : Strings.Format("XImport_MasterPwWarnFmt", string.Join(" / ", withMaster));
        this.RaisePropertyChanged(nameof(HasMasterPasswordWarning));
    }

    /// <summary>按「是否跳过重复」重设勾选:受支持且(允许重复或非重复)的会话被勾上。</summary>
    private void ApplySelectionRule()
    {
        foreach (SessionImportSourceViewModel source in Sources)
        {
            foreach (SessionImportItemViewModel item in source.Items)
            {
                item.IsSelected = item.CanSelect && (!SkipExisting || !item.IsDuplicate);
            }
        }
        RecomputeCounts();
    }

    private void SetAllSelected(bool value)
    {
        foreach (SessionImportSourceViewModel source in Sources)
        {
            foreach (SessionImportItemViewModel item in source.Items)
            {
                if (item.CanSelect)
                {
                    item.IsSelected = value;
                }
            }
        }
        RecomputeCounts();
    }

    private void RecomputeCounts()
    {
        int total = 0;
        int selected = 0;
        int recovered = 0;
        foreach (SessionImportSourceViewModel source in Sources)
        {
            foreach (SessionImportItemViewModel item in source.Items)
            {
                total++;
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
        }
        TotalCount = total;
        SelectedCount = selected;
        RecoveredCount = recovered;
        RaiseSummaryChanged();
    }

    private void RaiseSummaryChanged()
    {
        this.RaisePropertyChanged(nameof(SkippedCount));
        this.RaisePropertyChanged(nameof(Headline));
        this.RaisePropertyChanged(nameof(Detail));
        this.RaisePropertyChanged(nameof(ImportButtonText));
    }

    private async Task RescanAllAsync()
    {
        IsBusy = true;
        RaiseSummaryChanged();
        try
        {
            foreach (SessionImportSourceViewModel source in Sources)
            {
                await source.ScanAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            ApplySmartSelection();
        }
    }
}

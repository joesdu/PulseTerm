using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Processes;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>任务管理器的刷新速度,对应 Windows 任务管理器"查看 → 更新速度"。</summary>
public enum ProcessRefreshSpeed
{
    /// <summary>高:每秒一次。</summary>
    High,

    /// <summary>普通:每 2 秒一次(默认)。</summary>
    Normal,

    /// <summary>低:每 4 秒一次。</summary>
    Low,

    /// <summary>已暂停:只在手动刷新时采样。</summary>
    Paused
}

/// <summary>
/// 远端进程管理器(设计对标 Windows 任务管理器"详细信息"页):对当前聚焦的 SSH 会话
/// 周期性采集进程列表,支持排序、搜索、结束进程/进程树与调整优先级。
/// </summary>
public sealed class ProcessManagerViewModel : ReactiveObject, IDisposable
{
    /// <summary>占用条转黄/转红的阈值(与资源监视面板同口径)。</summary>
    private const double WarnThreshold = 70;

    private const double CriticalThreshold = 90;

    private readonly IRemoteProcessService _service;
    private readonly Guid _sessionId;
    private readonly Dictionary<int, ProcessRowViewModel> _rows = [];
    private readonly DispatcherTimer? _timer;
    private readonly List<ProcessRowViewModel> _all = [];
    private bool _refreshing;
    private bool _disposed;

    /// <summary>创建进程管理器视图模型并立即开始按"普通"速度轮询。</summary>
    /// <param name="service">远端进程采集/操作服务。</param>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    /// <param name="hostLabel">窗口标题中显示的会话名称。</param>
    public ProcessManagerViewModel(IRemoteProcessService service, Guid sessionId, string hostLabel)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _sessionId = sessionId;
        HostLabel = hostLabel;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
        SetSpeedCommand = ReactiveCommand.Create<string>(SetSpeed);
        SetPriorityCommand = ReactiveCommand.CreateFromTask<string>(SetPriorityAsync);
        ToggleExpandCommand = ReactiveCommand.Create<ProcessRowViewModel>(ToggleExpand);

        // 只有选中了进程才允许动作,与任务管理器按钮的启用规则一致。
        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedProcess)
            .Select(row => row is not null);
        EndTaskCommand = ReactiveCommand.CreateFromTask(() => EndTaskAsync(tree: false, force: false), hasSelection);
        EndTaskTreeCommand = ReactiveCommand.CreateFromTask(() => EndTaskAsync(tree: true, force: false), hasSelection);
        ForceEndTaskCommand = ReactiveCommand.CreateFromTask(() => EndTaskAsync(tree: false, force: true), hasSelection);
        CopyCommandLineCommand = ReactiveCommand.CreateFromTask(CopyCommandLineAsync, hasSelection);

        // 搜索、内核线程与树形开关只影响可见集合,不需要重新采样。
        this.WhenAnyValue(x => x.SearchText, x => x.ShowKernelThreads, x => x.ShowTree)
            .Skip(1)
            .Subscribe(_ => ApplyView());

        // 无 Application 说明跑在无头测试里,不建定时器(与隧道面板同样的守卫)。
        if (Application.Current is not null)
        {
            _timer = new(DispatcherPriority.Background) { Interval = IntervalFor(ProcessRefreshSpeed.Normal) };
            _timer.Tick += (_, _) => _ = RefreshAsync();
            _timer.Start();
        }
    }

    /// <summary>窗口标题中显示的会话名称。</summary>
    public string HostLabel { get; }

    /// <summary>当前可见(已过滤 + 已排序)的进程行。</summary>
    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];

    /// <summary>列表中选中的进程行;为空时所有动作命令不可用。</summary>
    public ProcessRowViewModel? SelectedProcess
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>搜索词,匹配名称/命令行/用户/PID。</summary>
    public string SearchText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否显示内核线程([kthreadd] 之类);默认关闭。</summary>
    public bool ShowKernelThreads
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 是否按父子关系树形展示(htop 的树模式)。默认开启:远端进程的父子关系是排查问题的主要
    /// 线索(哪个服务拉起了哪些子进程),平铺看不出来。
    /// </summary>
    public bool ShowTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>当前排序列的键(name/pid/user/cpu/mem/threads/state/time)。</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "cpu";

    /// <summary>当前排序是否为降序。</summary>
    public bool SortDescending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>当前刷新速度。</summary>
    public ProcessRefreshSpeed Speed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = ProcessRefreshSpeed.Normal;

    /// <summary>当前刷新速度的本地化名称,显示在工具栏按钮上。</summary>
    public string SpeedLabel =>
        Strings.Get(
            Speed switch
            {
                ProcessRefreshSpeed.High => "Proc_SpeedHigh",
                ProcessRefreshSpeed.Low => "Proc_SpeedLow",
                ProcessRefreshSpeed.Paused => "Proc_SpeedPaused",
                _ => "Proc_SpeedNormal"
            }
        );

    /// <summary>全机 CPU 占用率(0-100)。</summary>
    public double CpuPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>全机内存占用率(0-100)。</summary>
    public double MemoryPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>概览条的内存汇总文本(已用 / 总量)。</summary>
    public string MemorySummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    /// <summary>CPU 占用是否进入告警区(&gt;70%),占用条转黄。阈值与资源面板一致。</summary>
    public bool IsCpuWarn => CpuPercent is > WarnThreshold and <= CriticalThreshold;

    /// <summary>CPU 占用是否进入危险区(&gt;90%),占用条转红。</summary>
    public bool IsCpuCritical => CpuPercent > CriticalThreshold;

    /// <summary>内存占用是否进入告警区(&gt;70%)。</summary>
    public bool IsMemoryWarn => MemoryPercent is > WarnThreshold and <= CriticalThreshold;

    /// <summary>内存占用是否进入危险区(&gt;90%)。</summary>
    public bool IsMemoryCritical => MemoryPercent > CriticalThreshold;

    /// <summary>概览条上 CPU 进度条旁的百分比文本。</summary>
    public string CpuSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    /// <summary>概览条上内存进度条旁的百分比文本。</summary>
    public string MemoryPercentSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    /// <summary>逻辑核心数,用于说明 CPU 列的归一口径。</summary>
    public int CpuCores
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    /// <summary>当前可见的进程数。</summary>
    public int VisibleCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>本次采样到的进程总数(未过滤)。</summary>
    public int TotalCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态栏左侧的进程计数文本(可见 / 总数)。</summary>
    public string CountSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    /// <summary>状态栏右侧的核心数与 CPU 口径说明。</summary>
    public string CoresSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// 是否尚未取到任何数据。远端不是 Linux、缺少 ps/awk 或会话已断开时保持为 true,
    /// 界面显示占位说明而不是一张空表。
    /// </summary>
    public bool IsUnavailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>最近一次动作的结果提示(权限不足等),为空时不显示。</summary>
    public string? StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>手动刷新一次(F5 与刷新按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>按列排序;同列再次点击切换升/降序。</summary>
    public ReactiveCommand<string, RxVoid> SortCommand { get; }

    /// <summary>切换刷新速度;参数为 <see cref="ProcessRefreshSpeed" /> 的枚举名。</summary>
    public ReactiveCommand<string, RxVoid> SetSpeedCommand { get; }

    /// <summary>结束选中进程(SIGTERM)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> EndTaskCommand { get; }

    /// <summary>结束选中进程及其全部子孙(SIGTERM)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> EndTaskTreeCommand { get; }

    /// <summary>强制结束选中进程(SIGKILL)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ForceEndTaskCommand { get; }

    /// <summary>把选中进程的完整命令行复制到剪贴板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyCommandLineCommand { get; }

    /// <summary>设置选中进程的优先级;参数为 nice 值的字符串形式。</summary>
    public ReactiveCommand<string, RxVoid> SetPriorityCommand { get; }

    /// <summary>展开/折叠树形视图中的一个节点。</summary>
    public ReactiveCommand<ProcessRowViewModel, RxVoid> ToggleExpandCommand { get; }

    private void ToggleExpand(ProcessRowViewModel row)
    {
        if (row is null)
        {
            return;
        }
        row.IsExpanded = !row.IsExpanded;
        ApplyView();
    }

    /// <summary>结束进程前的确认回调,由视图注入;未注入时视为已确认。</summary>
    public Func<string, string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>复制到剪贴板的回调,由视图注入(视图层才拿得到剪贴板)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>停止轮询并丢弃差分基准。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer?.Stop();
        _service.ResetBaseline(_sessionId);
    }

    /// <summary>采集一轮进程列表并刷新视图。重入时直接返回,慢主机不会堆积请求。</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing || _disposed)
        {
            return;
        }
        _refreshing = true;
        try
        {
            RemoteProcessSnapshot? snapshot = await _service.GetSnapshotAsync(_sessionId).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }
            if (snapshot is null)
            {
                IsUnavailable = true;
                return;
            }
            Merge(snapshot);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>把快照并入行集合:同 PID 复用行对象,消失的进程移除,新进程新建。</summary>
    private void Merge(RemoteProcessSnapshot snapshot)
    {
        HashSet<int> seen = [];
        foreach (RemoteProcessInfo process in snapshot.Processes)
        {
            seen.Add(process.Pid);
            if (_rows.TryGetValue(process.Pid, out ProcessRowViewModel? row))
            {
                row.Update(process);
            }
            else
            {
                _rows[process.Pid] = new(process);
            }
        }
        foreach (int gone in _rows.Keys.Where(pid => !seen.Contains(pid)).ToArray())
        {
            if (_rows.Remove(gone, out ProcessRowViewModel? row) && ReferenceEquals(SelectedProcess, row))
            {
                SelectedProcess = null;
            }
        }
        _all.Clear();
        _all.AddRange(_rows.Values);

        CpuPercent = snapshot.CpuPercent;
        MemoryPercent = snapshot.MemPercent;
        CpuCores = snapshot.CpuCores;
        TotalCount = snapshot.Processes.Count;
        CoresSummary = Strings.Format("Proc_CoresFormat", snapshot.CpuCores);
        MemorySummary = string.Create(
            CultureInfo.CurrentCulture,
            $"{snapshot.MemUsedBytes / (1024.0 * 1024 * 1024):F1} / {snapshot.MemTotalBytes / (1024.0 * 1024 * 1024):F1} GB"
        );
        CpuSummary = string.Create(CultureInfo.CurrentCulture, $"{snapshot.CpuPercent:F1}%");
        MemoryPercentSummary = string.Create(CultureInfo.CurrentCulture, $"{snapshot.MemPercent:F1}%");
        this.RaisePropertyChanged(nameof(IsCpuWarn));
        this.RaisePropertyChanged(nameof(IsCpuCritical));
        this.RaisePropertyChanged(nameof(IsMemoryWarn));
        this.RaisePropertyChanged(nameof(IsMemoryCritical));
        IsUnavailable = false;
        ApplyView();
    }

    /// <summary>
    /// 按当前搜索词与排序重排可见集合。用原地 Move 而不是清空重填:清空会丢掉选中项,
    /// 而按 CPU 排序时行序每轮都在变,选中项每秒被清一次就没法右键了。
    /// </summary>
    private void ApplyView()
    {
        // ListBox 在集合发生 Move/Insert 时按索引重算选中项,行对象虽然是复用的,
        // 选中项仍会被冲掉 —— 表现就是"每刷新一次选中就没了,根本点不准"。
        // 先记住选中的行对象,重排完再按回去。
        ProcessRowViewModel? previouslySelected = SelectedProcess;

        List<ProcessRowViewModel> visible = [.. Filter()];
        List<ProcessRowViewModel> ordered = ShowTree ? BuildTree(visible) : Flatten(visible);

        for (int i = 0; i < ordered.Count; i++)
        {
            ProcessRowViewModel row = ordered[i];
            if (i < Processes.Count && ReferenceEquals(Processes[i], row))
            {
                continue;
            }
            int existing = IndexOfFrom(row, i);
            if (existing >= 0)
            {
                Processes.Move(existing, i);
            }
            else
            {
                Processes.Insert(i, row);
            }
        }
        while (Processes.Count > ordered.Count)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }

        if (previouslySelected is not null
            && !ReferenceEquals(SelectedProcess, previouslySelected)
            && Processes.Contains(previouslySelected))
        {
            SelectedProcess = previouslySelected;
        }

        VisibleCount = Processes.Count;
        CountSummary = Strings.Format("Proc_CountFormat", VisibleCount, TotalCount);
    }

    /// <summary>按内核线程开关与搜索词筛出可见行。树形视图下额外补回命中行的全部祖先。</summary>
    private IEnumerable<ProcessRowViewModel> Filter()
    {
        IEnumerable<ProcessRowViewModel> query = _all;
        if (!ShowKernelThreads)
        {
            query = query.Where(row => !row.IsKernelThread);
        }
        string term = SearchText.Trim();
        if (term.Length == 0)
        {
            return query;
        }
        List<ProcessRowViewModel> candidates = [.. query];
        HashSet<ProcessRowViewModel> matched = [.. candidates.Where(row => row.Matches(term))];
        if (!ShowTree)
        {
            return matched;
        }

        // 树形视图里只留命中行会把它们全变成孤儿根,层级信息就没了;把祖先链一并保留。
        Dictionary<int, ProcessRowViewModel> byPid = [];
        foreach (ProcessRowViewModel row in candidates)
        {
            byPid[row.Pid] = row;
        }
        foreach (ProcessRowViewModel row in matched.ToArray())
        {
            ProcessRowViewModel cursor = row;
            while (byPid.TryGetValue(cursor.ParentPid, out ProcessRowViewModel? parent)
                   && parent.Pid != cursor.Pid
                   && matched.Add(parent))
            {
                cursor = parent;
            }
        }
        return matched;
    }

    /// <summary>平铺视图:整表按当前列排序,层级信息清零。</summary>
    private List<ProcessRowViewModel> Flatten(List<ProcessRowViewModel> visible)
    {
        foreach (ProcessRowViewModel row in visible)
        {
            row.Depth = 0;
            row.HasChildren = false;
        }
        return [.. Sort(visible)];
    }

    /// <summary>
    /// 树形视图:父进程在上、子进程缩进其下(htop 的树模式)。排序作用于同级之间,
    /// 折叠的节点不展开其子树。
    /// </summary>
    private List<ProcessRowViewModel> BuildTree(List<ProcessRowViewModel> visible)
    {
        HashSet<int> present = [.. visible.Select(row => row.Pid)];
        Dictionary<int, List<ProcessRowViewModel>> children = [];
        List<ProcessRowViewModel> roots = [];
        foreach (ProcessRowViewModel row in visible)
        {
            // 父不在可见集合里(被过滤掉,或就是 init 的父 0)的行升为根,否则整棵子树会消失。
            if (row.ParentPid == row.Pid || !present.Contains(row.ParentPid))
            {
                roots.Add(row);
                continue;
            }
            if (!children.TryGetValue(row.ParentPid, out List<ProcessRowViewModel>? bucket))
            {
                bucket = [];
                children[row.ParentPid] = bucket;
            }
            bucket.Add(row);
        }

        List<ProcessRowViewModel> ordered = [with(visible.Count)];
        HashSet<int> walked = [];
        foreach (ProcessRowViewModel root in Sort(roots))
        {
            Walk(root, 0);
        }
        return ordered;

        void Walk(ProcessRowViewModel row, int depth)
        {
            // ppid 成环(畸形采样)会让递归停不下来,走过的 pid 不再进。
            if (!walked.Add(row.Pid))
            {
                return;
            }
            row.Depth = depth;
            children.TryGetValue(row.Pid, out List<ProcessRowViewModel>? kids);
            row.HasChildren = kids is { Count: > 0 };
            ordered.Add(row);
            if (kids is null || !row.IsExpanded)
            {
                return;
            }
            foreach (ProcessRowViewModel kid in Sort(kids))
            {
                Walk(kid, depth + 1);
            }
        }
    }

    private int IndexOfFrom(ProcessRowViewModel row, int start)
    {
        for (int i = start; i < Processes.Count; i++)
        {
            if (ReferenceEquals(Processes[i], row))
            {
                return i;
            }
        }
        return -1;
    }

    private IEnumerable<ProcessRowViewModel> Sort(IEnumerable<ProcessRowViewModel> rows) =>
        SortColumn switch
        {
            "pid" => Order(rows, row => row.Pid),
            "user" => Order(rows, row => row.User),
            "mem" => Order(rows, row => row.MemoryBytes),
            "threads" => Order(rows, row => row.Threads),
            "state" => Order(rows, row => row.State),
            "time" => Order(rows, row => row.ElapsedSeconds),
            "cpu" => Order(rows, row => row.CpuPercent),
            _ => Order(rows, row => row.Name)
        };

    // 次级排序固定为 PID:CPU 相同(空闲进程一律 0.0)的行若不定序,每轮都会互相跳动。
    private IOrderedEnumerable<ProcessRowViewModel> Order<TKey>(
        IEnumerable<ProcessRowViewModel> rows,
        Func<ProcessRowViewModel, TKey> key
    ) =>
        SortDescending
            ? rows.OrderByDescending(key).ThenBy(row => row.Pid)
            : rows.OrderBy(key).ThenBy(row => row.Pid);

    private void ToggleSort(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.Ordinal))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            // 数值列默认降序(先看占用最高的),文本列默认升序。
            SortDescending = column is "cpu" or "mem" or "threads" or "time";
        }
        RaiseSortGlyphs();
        ApplyView();
    }

    private void RaiseSortGlyphs()
    {
        this.RaisePropertyChanged(nameof(NameSortGlyph));
        this.RaisePropertyChanged(nameof(PidSortGlyph));
        this.RaisePropertyChanged(nameof(UserSortGlyph));
        this.RaisePropertyChanged(nameof(CpuSortGlyph));
        this.RaisePropertyChanged(nameof(MemSortGlyph));
        this.RaisePropertyChanged(nameof(ThreadsSortGlyph));
        this.RaisePropertyChanged(nameof(StateSortGlyph));
        this.RaisePropertyChanged(nameof(TimeSortGlyph));
    }

    /// <summary>"名称"列的排序箭头。</summary>
    public string NameSortGlyph => GlyphFor("name");

    /// <summary>"PID"列的排序箭头。</summary>
    public string PidSortGlyph => GlyphFor("pid");

    /// <summary>"用户"列的排序箭头。</summary>
    public string UserSortGlyph => GlyphFor("user");

    /// <summary>"CPU"列的排序箭头。</summary>
    public string CpuSortGlyph => GlyphFor("cpu");

    /// <summary>"内存"列的排序箭头。</summary>
    public string MemSortGlyph => GlyphFor("mem");

    /// <summary>"线程"列的排序箭头。</summary>
    public string ThreadsSortGlyph => GlyphFor("threads");

    /// <summary>"状态"列的排序箭头。</summary>
    public string StateSortGlyph => GlyphFor("state");

    /// <summary>"运行时长"列的排序箭头。</summary>
    public string TimeSortGlyph => GlyphFor("time");

    private string GlyphFor(string column) =>
        string.Equals(SortColumn, column, StringComparison.Ordinal)
            ? SortDescending ? " ▼" : " ▲"
            : string.Empty;

    private void SetSpeed(string speedName)
    {
        if (!Enum.TryParse(speedName, out ProcessRefreshSpeed speed))
        {
            return;
        }
        Speed = speed;
        this.RaisePropertyChanged(nameof(SpeedLabel));
        if (_timer is null)
        {
            return;
        }
        if (speed == ProcessRefreshSpeed.Paused)
        {
            _timer.Stop();
            return;
        }
        _timer.Interval = IntervalFor(speed);
        _timer.Start();
    }

    private static TimeSpan IntervalFor(ProcessRefreshSpeed speed) =>
        speed switch
        {
            ProcessRefreshSpeed.High => TimeSpan.FromSeconds(1),
            ProcessRefreshSpeed.Low => TimeSpan.FromSeconds(4),
            _ => TimeSpan.FromSeconds(2)
        };

    /// <summary>结束选中进程;tree 为真时连同其全部子孙,force 为真时用 SIGKILL。</summary>
    private async Task EndTaskAsync(bool tree, bool force)
    {
        if (SelectedProcess is not { } target)
        {
            return;
        }
        List<int> pids = tree ? [.. CollectTree(target.Pid)] : [target.Pid];
        string title = Strings.Get(
            force ? "Proc_ConfirmForceEndTitle" : tree ? "Proc_ConfirmEndTreeTitle" : "Proc_ConfirmEndTitle"
        );
        string body = Strings.Format(
            tree ? "Proc_ConfirmEndTreeBody" : "Proc_ConfirmEndBody",
            target.Name,
            target.Pid,
            pids.Count
        );
        if (ConfirmAction is { } confirm && !await confirm(title, body).ConfigureAwait(true))
        {
            return;
        }

        RemoteCommandOutcome outcome = await _service
            .SignalAsync(_sessionId, pids, force ? ProcessSignal.Kill : ProcessSignal.Terminate)
            .ConfigureAwait(true);
        StatusMessage = outcome.Success
                            ? Strings.Format("Proc_EndSucceeded", target.Name, pids.Count)
                            : Strings.Format(
                                "Proc_EndFailed",
                                target.Name,
                                string.IsNullOrWhiteSpace(outcome.Output)
                                    ? Strings.Get("Proc_ReasonUnknown")
                                    : outcome.Output
                            );
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 收集一棵进程树的全部 PID,子在前父在后 —— 先杀父的话子进程会被 init 收养,
    /// 后续按 ppid 就找不到它们了。
    /// </summary>
    private List<int> CollectTree(int rootPid)
    {
        Dictionary<int, List<int>> children = [];
        foreach (ProcessRowViewModel row in _all)
        {
            if (!children.TryGetValue(row.ParentPid, out List<int>? bucket))
            {
                bucket = [];
                children[row.ParentPid] = bucket;
            }
            bucket.Add(row.Pid);
        }
        List<int> ordered = [];
        Walk(rootPid);
        return ordered;

        void Walk(int pid)
        {
            if (children.TryGetValue(pid, out List<int>? kids))
            {
                foreach (int kid in kids)
                {
                    // 防御自引用/环:PID 必须严格向下走,否则畸形数据会导致无限递归。
                    if (kid != pid && !ordered.Contains(kid))
                    {
                        Walk(kid);
                    }
                }
            }
            if (!ordered.Contains(pid))
            {
                ordered.Add(pid);
            }
        }
    }

    private async Task SetPriorityAsync(string niceness)
    {
        if (SelectedProcess is not { } target
            || !int.TryParse(niceness, CultureInfo.InvariantCulture, out int nice))
        {
            return;
        }
        RemoteCommandOutcome outcome = await _service
            .ReniceAsync(_sessionId, target.Pid, nice)
            .ConfigureAwait(true);
        StatusMessage = outcome.Success
                            ? Strings.Format("Proc_PriorityChanged", target.Name, nice)
                            : Strings.Format(
                                "Proc_PriorityFailed",
                                target.Name,
                                string.IsNullOrWhiteSpace(outcome.Output)
                                    ? Strings.Get("Proc_ReasonUnknown")
                                    : outcome.Output
                            );
    }

    private async Task CopyCommandLineAsync()
    {
        if (SelectedProcess is { } target && CopyToClipboard is { } copy)
        {
            await copy(target.CommandLine).ConfigureAwait(true);
            StatusMessage = Strings.Get("Proc_CommandCopied");
        }
    }
}

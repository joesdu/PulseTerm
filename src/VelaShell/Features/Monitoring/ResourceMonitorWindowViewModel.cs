using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;

namespace VelaShell.Features.Monitoring;

/// <summary>逻辑处理器区块的三种视图(设计稿 CPU 页的分段控件)。</summary>
public enum CoreView
{
    /// <summary>热力图:一格一色,核心多时唯一读得过来的形态(&gt;32 核默认)。</summary>
    Heat,

    /// <summary>迷你折线:每格一条该核心的 60 秒趋势(≤32 核默认)。</summary>
    Spark,

    /// <summary>列表:逐核心一行,带占用条,可滚动。</summary>
    List
}

/// <summary>资源监视窗口的六个页面(左侧导航项)。</summary>
public enum MonitorPage
{
    /// <summary>总览:六张卡片的 2 × 3 网格。</summary>
    Overview,

    /// <summary>CPU:总/用户/内核曲线 + 逻辑处理器热力网格。</summary>
    Cpu,

    /// <summary>GPU:多卡卡片条 + 选中卡的利用率与显存。</summary>
    Gpu,

    /// <summary>内存:使用曲线 + 内存组合 + 占用最高进程。</summary>
    Memory,

    /// <summary>磁盘:物理盘列表 + 吞吐曲线 + 分区表。</summary>
    Disk,

    /// <summary>网络:网卡卡片条 + 上下行镜像曲线 + 详情。</summary>
    Network
}

/// <summary>
/// 资源监视窗口(设计帧 XQTSJ / l6zvbI / j0NcGX / St1My / gwmD7 / OHvJS)的视图模型:
/// 按可调间隔轮询会话指标,把每次采样推入定长历史缓冲驱动曲线,并维护六个页面的展示数据。
/// 未探测到 GPU 时自动隐藏 GPU 页与总览里的两张 GPU 卡(规范"空态与降级")。
/// </summary>
public sealed class ResourceMonitorWindowViewModel : ReactiveObject, IDisposable
{
    // 选中项在首个采样到达前(以及无 GPU 时)指向这些占位行,而不是 null。
    // 编译绑定遍历 SelectedGpu.UtilHistory.Values 这类路径时,中间节点为 null 会每帧刷一条
    // "Value is null" 绑定错误;占位行同时正好呈现"—"的空态。它们只读不写,可安全共享。
    private static readonly DiskDeviceRow PlaceholderDisk = new(NotAvailable)
    {
        CapacityText = NotAvailable,
        UsedPercentText = NotAvailable,
        ActivityText = NotAvailable
    };

    private static readonly NicRow PlaceholderNic = new(NotAvailable)
    {
        RxText = NotAvailable,
        TxText = NotAvailable,
        LinkText = NotAvailable
    };

    private static readonly GpuCardRow PlaceholderGpu = new(0, NotAvailable)
    {
        Label = NotAvailable,
        UtilText = NotAvailable,
        MemText = NotAvailable,
        MemPercentText = NotAvailable,
        TempText = NotAvailable,
        PowerText = NotAvailable
    };

    private readonly ISessionMetricsService _metrics;
    private readonly DispatcherTimer? _timer;
    private readonly Dictionary<int, GpuCardRow> _gpuRows = [];
    private readonly Dictionary<string, DiskDeviceRow> _diskRows = [];
    private readonly Dictionary<string, NicRow> _nicRows = [];
    private readonly List<MetricHistory> _coreHistories = [];
    private double[] _corePercents = [];
    private string[] _coreLabels = [];
    private IReadOnlyList<double>[] _coreHistoryViews = [];
    private int[] _coreOrder = [];
    private bool _coreViewChosen;
    private bool _refreshing;
    private bool _disposed;
    private SessionStaticInfo? _static;
    private int _consecutiveFailures;

    /// <summary>创建资源监视窗口的视图模型并立即开始轮询。</summary>
    /// <param name="metricsService">会话指标采集服务。</param>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    /// <param name="hostName">窗口标题中显示的主机名。</param>
    public ResourceMonitorWindowViewModel(ISessionMetricsService metricsService, Guid sessionId, string hostName)
    {
        _metrics = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
        SessionId = sessionId;
        HostName = hostName;

        SelectPageCommand = ReactiveCommand.Create<string>(SelectPage);
        SetIntervalCommand = ReactiveCommand.Create<string>(SetInterval);
        TogglePauseCommand = ReactiveCommand.Create(() => { IsPaused = !IsPaused; });
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        SetCoreViewCommand = ReactiveCommand.Create<string>(SetCoreView);
        SetCoreSortCommand = ReactiveCommand.Create<string>(SetCoreSort);
        SelectGpuCommand = ReactiveCommand.Create<GpuCardRow>(SelectGpu);
        SelectDiskCommand = ReactiveCommand.Create<DiskDeviceRow>(SelectDisk);
        SelectNicCommand = ReactiveCommand.Create<NicRow>(SelectNic);

        // 无 Application 说明跑在无头测试里,不建定时器(与任务管理器同样的守卫)。
        if (Application.Current is not null)
        {
            _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(IntervalSeconds) };
            _timer.Tick += (_, _) => _ = RefreshAsync();
            _timer.Start();
        }
    }

    /// <summary>被监视的会话标识。</summary>
    public Guid SessionId { get; }

    /// <summary>会话主机名。</summary>
    public string HostName { get; }

    /// <summary>标题栏第二行:发行版 / 内核 / 核心数 / GPU 数 / 采样间隔。</summary>
    public string HeaderSubtitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>false 时整页显示“数据不可用”占位(会话断开或非 Linux 主机)。</summary>
    public bool IsAvailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>连续采样失败达到阈值后置位,用于提示曲线已停更。</summary>
    public bool IsStale
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否已暂停轮询(曲线冻结)。</summary>
    public bool IsPaused
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前采样间隔(秒):1 / 2 / 5 / 10。</summary>
    public int IntervalSeconds
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    /// <summary>曲线版本号:每次采样后自增,驱动图表控件重绘。</summary>
    public int Revision
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 图表左下角的时间刻度:滚动窗口保留 60 个采样点,所以跨度随采样间隔变化 ——
    /// 写死"60 秒前"在 2s/5s/10s 档上就是错的。
    /// </summary>
    public string HistoryWindowText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Format("Monitor_WindowSeconds", HistoryCapacity);

    /// <summary>历史缓冲保留的采样点数(与 <see cref="MetricHistory" /> 的默认容量一致)。</summary>
    private const int HistoryCapacity = 60;

    /// <summary>当前页面。</summary>
    public MonitorPage Page
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsOverview));
            this.RaisePropertyChanged(nameof(IsCpuPage));
            this.RaisePropertyChanged(nameof(IsGpuPage));
            this.RaisePropertyChanged(nameof(IsMemoryPage));
            this.RaisePropertyChanged(nameof(IsDiskPage));
            this.RaisePropertyChanged(nameof(IsNetworkPage));
        }
    } = MonitorPage.Overview;

    /// <summary>当前是否停在总览页(供页面容器切换可见性)。</summary>
    public bool IsOverview => Page == MonitorPage.Overview;

    /// <inheritdoc cref="IsOverview" />
    public bool IsCpuPage => Page == MonitorPage.Cpu;

    /// <inheritdoc cref="IsOverview" />
    public bool IsGpuPage => Page == MonitorPage.Gpu;

    /// <inheritdoc cref="IsOverview" />
    public bool IsMemoryPage => Page == MonitorPage.Memory;

    /// <inheritdoc cref="IsOverview" />
    public bool IsDiskPage => Page == MonitorPage.Disk;

    /// <inheritdoc cref="IsOverview" />
    public bool IsNetworkPage => Page == MonitorPage.Network;

    /// <summary>是否探测到 GPU;false 时隐藏 GPU 页与总览的两张 GPU 卡。</summary>
    public bool HasGpu
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(OverviewColumns));
        }
    }

    /// <summary>
    /// 总览网格的列数:有 GPU 是 3 × 2(六张卡),没有就回落 2 × 2(四张卡)。
    /// 隐藏的 GPU 卡不能在网格里留空位 —— 那样右边会空出两块。
    /// </summary>
    public int OverviewColumns => HasGpu ? 3 : 2;

    /// <summary>选中 1s 采样间隔(分段控件高亮用)。</summary>
    public bool Is1S => IntervalSeconds == 1;

    /// <inheritdoc cref="Is1S" />
    public bool Is2S => IntervalSeconds == 2;

    /// <inheritdoc cref="Is1S" />
    public bool Is5S => IntervalSeconds == 5;

    /// <inheritdoc cref="Is1S" />
    public bool Is10S => IntervalSeconds == 10;

    // ---- 历史曲线 ----

    /// <summary>CPU 总利用率历史(0-100)。</summary>
    public MetricHistory CpuHistory { get; } = new();

    /// <summary>CPU 用户态占比历史。</summary>
    public MetricHistory CpuUserHistory { get; } = new();

    /// <summary>CPU 内核态占比历史。</summary>
    public MetricHistory CpuSystemHistory { get; } = new();

    /// <summary>内存已用历史(字节)。</summary>
    public MetricHistory MemoryHistory { get; } = new();

    /// <summary>内存缓存合计历史(字节)。</summary>
    public MetricHistory CacheHistory { get; } = new();

    /// <summary>磁盘读取速率历史(字节/秒,全盘合计)。</summary>
    public MetricHistory DiskReadHistory { get; } = new();

    /// <summary>磁盘写入速率历史(字节/秒,全盘合计)。</summary>
    public MetricHistory DiskWriteHistory { get; } = new();

    /// <summary>网络下行速率历史(字节/秒,全网卡合计)。</summary>
    public MetricHistory NetRxHistory { get; } = new();

    /// <summary>网络上行速率历史(字节/秒,全网卡合计)。</summary>
    public MetricHistory NetTxHistory { get; } = new();

    // ---- 顶部读数 ----

    /// <summary>CPU 总利用率文本。</summary>
    public string CpuText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>CPU 用户/内核/IO 等待的细分文本。</summary>
    public string CpuBreakdownText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>内存“已用 / 总量”文本。</summary>
    public string MemoryText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>内存总量(字节),用作内存曲线的 Y 轴上限。</summary>
    public double MemoryTotalBytes
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    /// <summary>磁盘合计吞吐文本。</summary>
    public string DiskThroughputText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>本次采样是否拿到了逐盘 IO 速率(拿不到时读写显示占位符而不是 0)。</summary>
    public bool HasDiskIo
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>网络合计吞吐文本(↓ 下行 ↑ 上行)。</summary>
    public string NetThroughputText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>导航项副标题:CPU。</summary>
    public string NavCpuSub
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="NavCpuSub" />
    public string NavGpuSub
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="NavCpuSub" />
    public string NavMemorySub
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="NavCpuSub" />
    public string NavDiskSub
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="NavCpuSub" />
    public string NavNetworkSub
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    // ---- CPU 页 ----

    /// <summary>各逻辑核心的当前占用率,驱动热力网格。</summary>
    public IReadOnlyList<double> CorePercents
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>逻辑处理器区块的副标题(核心数与选中核心)。</summary>
    public string CoreSubtitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>热力网格中选中的核心下标;-1 = 未选中。</summary>
    public int SelectedCoreIndex
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            UpdateCoreSubtitle();
        }
    } = -1;

    /// <summary>各核心的显示标签(受排序影响,如按负载排序后第 0 格可能是 CPU37)。</summary>
    public IReadOnlyList<string> CoreLabels
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>各核心的 60 秒历史,与 <see cref="CorePercents" /> 同序(迷你折线模式用)。</summary>
    public IReadOnlyList<IReadOnlyList<double>> CoreHistories
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>列表模式下的逐核心行。</summary>
    public ObservableCollection<CoreRow> CoreRows { get; } = [];

    /// <summary>逻辑处理器区块的呈现方式。</summary>
    public CoreView CoreViewMode
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = CoreView.Heat;

    /// <summary>当前是热力图视图(分段控件高亮与内容切换用)。</summary>
    public bool IsHeatView => CoreViewMode == CoreView.Heat;

    /// <inheritdoc cref="IsHeatView" />
    public bool IsSparkView => CoreViewMode == CoreView.Spark;

    /// <inheritdoc cref="IsHeatView" />
    public bool IsListView => CoreViewMode == CoreView.List;

    /// <summary>true = 按当前负载降序排列核心,false = 按核心号。</summary>
    public bool SortCoresByLoad
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>按核心号排序(分段控件高亮用)。</summary>
    public bool IsSortByIndex => !SortCoresByLoad;

    /// <inheritdoc cref="IsSortByIndex" />
    public bool IsSortByLoad => SortCoresByLoad;

    /// <summary>CPU 型号名。整行独占并可换行 —— 型号名普遍长过键值行能容下的宽度。</summary>
    public string CpuModelText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>探到型号名才显示那一行,否则留给下面的明细。</summary>
    public bool HasCpuModel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>CPU 页右侧的机器信息明细。</summary>
    public IReadOnlyList<KeyValueRow> CpuDetails
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    // ---- 内存页 ----

    /// <summary>内存页的明细行。</summary>
    public IReadOnlyList<KeyValueRow> MemoryDetails
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>“内存组合”条中已用段的比例(0-1)。</summary>
    public double MemoryUsedRatio
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“内存组合”条中缓存段的比例(0-1)。</summary>
    public double MemoryCacheRatio
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“内存组合”条中空闲段的比例(0-1)。</summary>
    public double MemoryFreeRatio
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    /// <summary>“内存组合”三段的数值文本。</summary>
    public string MemoryUsedText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="MemoryUsedText" />
    public string MemoryCacheText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <inheritdoc cref="MemoryUsedText" />
    public string MemoryFreeText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>交换分区使用率(0-100)。</summary>
    public double SwapPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>交换分区“已用 / 总量”文本。</summary>
    public string SwapText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>按常驻内存排序的进程 Top。</summary>
    public ObservableCollection<ProcessRow> TopMemoryProcesses { get; } = [];

    // ---- 磁盘页 ----

    /// <summary>物理磁盘列表。</summary>
    public ObservableCollection<DiskDeviceRow> Disks { get; } = [];

    /// <summary>
    /// 当前选中的物理磁盘(右侧吞吐曲线的数据源)。首个采样到达前指向占位行而不是 null ——
    /// 编译绑定遍历 <c>SelectedDisk.ReadHistory.Values</c> 这类路径时,中间节点为 null 会每帧刷一条绑定错误。
    /// </summary>
    public DiskDeviceRow SelectedDisk
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = PlaceholderDisk;

    /// <summary>选中磁盘的 IOPS / 延迟等明细。</summary>
    public IReadOnlyList<KeyValueRow> DiskDetails
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>分区(挂载点)表。</summary>
    public ObservableCollection<PartitionRow> Partitions { get; } = [];

    /// <summary>“物理磁盘”卡片标题右侧的盘数副标题。</summary>
    public string DiskCountText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ---- 网络页 ----

    /// <summary>物理网卡列表。</summary>
    public ObservableCollection<NicRow> Nics { get; } = [];

    /// <summary>当前选中的网卡(镜像曲线的数据源);首个采样前指向占位行,见 <see cref="SelectedDisk" />。</summary>
    public NicRow SelectedNic
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = PlaceholderNic;

    /// <summary>按瞬时收发合计排序的连接占用 Top。</summary>
    public ObservableCollection<ConnectionRow> TopConnections { get; } = [];

    /// <summary>连接列表为空(ss 不可用、无权限或确实没有连接)时置位,界面给出文字提示。</summary>
    public bool ConnectionsUnavailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // ---- GPU 页 ----

    /// <summary>GPU 卡片列表。</summary>
    public ObservableCollection<GpuCardRow> Gpus { get; } = [];

    /// <summary>当前选中的 GPU;无 GPU 或首个采样前指向占位行,见 <see cref="SelectedDisk" />。</summary>
    public GpuCardRow SelectedGpu
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = PlaceholderGpu;

    /// <summary>GPU 计算进程表。</summary>
    public ObservableCollection<GpuProcessRow> GpuProcesses { get; } = [];

    // ---- 命令 ----

    /// <summary>切换页面(参数为 <see cref="MonitorPage" /> 的名称)。</summary>
    public ReactiveCommand<string, RxVoid> SelectPageCommand { get; }

    /// <summary>设置采样间隔(参数为秒数字符串)。</summary>
    public ReactiveCommand<string, RxVoid> SetIntervalCommand { get; }

    /// <summary>暂停 / 继续轮询。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePauseCommand { get; }

    /// <summary>立即采样一次。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>切换逻辑处理器视图(参数为 <see cref="CoreView" /> 的名称)。</summary>
    public ReactiveCommand<string, RxVoid> SetCoreViewCommand { get; }

    /// <summary>切换核心排序(参数 Index / Load)。</summary>
    public ReactiveCommand<string, RxVoid> SetCoreSortCommand { get; }

    /// <summary>选中一张 GPU。</summary>
    public ReactiveCommand<GpuCardRow, RxVoid> SelectGpuCommand { get; }

    /// <summary>选中一块物理磁盘。</summary>
    public ReactiveCommand<DiskDeviceRow, RxVoid> SelectDiskCommand { get; }

    /// <summary>选中一张网卡。</summary>
    public ReactiveCommand<NicRow, RxVoid> SelectNicCommand { get; }

    /// <summary>停表并释放定时器(窗口关闭时必须调用,否则调度器会一直强引用本对象)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer?.Stop();
    }

    /// <summary>
    /// 采集一次指标并刷新全部页面。暂停时直接返回;上一次采集尚未完成时跳过本轮
    /// (慢主机上 1s 间隔容易叠加)。
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed || _refreshing || IsPaused)
        {
            return;
        }
        _refreshing = true;
        try
        {
            _static ??= await _metrics.GetStaticInfoAsync(SessionId).ConfigureAwait(true);
            SessionMetrics? metrics = await _metrics.GetMetricsAsync(SessionId, MetricsScope.Full).ConfigureAwait(true);
            if (metrics is null)
            {
                _consecutiveFailures++;
                IsStale = _consecutiveFailures >= 3;
                IsAvailable = false;
                return;
            }
            _consecutiveFailures = 0;
            IsStale = false;
            IsAvailable = true;
            Apply(metrics);
            Revision++;
        }
        catch
        {
            // 采集失败按"数据不可用"处理,曲线保留历史。
            _consecutiveFailures++;
            IsStale = _consecutiveFailures >= 3;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SelectPage(string page)
    {
        if (Enum.TryParse(page, out MonitorPage parsed))
        {
            Page = parsed;
        }
    }

    private void SetInterval(string seconds)
    {
        if (!int.TryParse(seconds, out int value) || value <= 0)
        {
            return;
        }
        IntervalSeconds = value;
        this.RaisePropertyChanged(nameof(Is1S));
        this.RaisePropertyChanged(nameof(Is2S));
        this.RaisePropertyChanged(nameof(Is5S));
        this.RaisePropertyChanged(nameof(Is10S));
        _timer?.Interval = TimeSpan.FromSeconds(value);
        // 60 个采样点的跨度随间隔变化,图表左下角的时间刻度要跟着改。
        int span = HistoryCapacity * value;
        HistoryWindowText = span >= 120
                                ? Strings.Format("Monitor_WindowMinutes", span / 60)
                                : Strings.Format("Monitor_WindowSeconds", span);
        UpdateHeader();
    }

    private void SelectGpu(GpuCardRow row)
    {
        foreach (GpuCardRow gpu in Gpus)
        {
            gpu.IsSelected = ReferenceEquals(gpu, row);
        }
        SelectedGpu = row;
    }

    private void SelectDisk(DiskDeviceRow row)
    {
        foreach (DiskDeviceRow disk in Disks)
        {
            disk.IsSelected = ReferenceEquals(disk, row);
        }
        SelectedDisk = row;
    }

    private void SelectNic(NicRow row)
    {
        foreach (NicRow nic in Nics)
        {
            nic.IsSelected = ReferenceEquals(nic, row);
        }
        SelectedNic = row;
    }

    /// <summary>把一次采样铺开到所有页面。</summary>
    private void Apply(SessionMetrics m)
    {
        ApplyCpu(m);
        ApplyMemory(m);
        ApplyDisk(m);
        ApplyNetwork(m);
        ApplyGpu(m);
        UpdateHeader(m);
    }

    private void ApplyCpu(SessionMetrics m)
    {
        CpuHistory.Push(m.CpuPercent);
        CpuText = MetricFormat.Percent(m.CpuPercent);
        if (m.Cpu is { } cpu)
        {
            CpuUserHistory.Push(cpu.User);
            CpuSystemHistory.Push(cpu.System);
            CpuBreakdownText = Strings.Format("Monitor_CpuBreakdown", cpu.User, cpu.System, cpu.IoWait);
        }
        NavCpuSub = Strings.Format("Monitor_NavCpu", m.CpuPercent, m.CpuCores);

        if (m.CorePercents is { Count: > 0 } cores)
        {
            ApplyCores(cores);
        }
        UpdateCoreSubtitle();

        CpuModelText = _static?.CpuModel ?? "";
        HasCpuModel = CpuModelText.Length > 0;

        var details = new List<KeyValueRow>();
        if (_static is { } info)
        {
            if (info.PhysicalCores > 0)
            {
                details.Add(new(Strings.Get("Monitor_CpuCoresThreads"), $"{info.PhysicalCores} / {m.CpuCores}"));
            }
            if (info.MaxMhz > 0 || m.CurrentMhz > 0)
            {
                details.Add(new(Strings.Get("Monitor_CpuClock"),
                    $"{FormatGhz(m.CurrentMhz)} / {FormatGhz(info.MaxMhz)} GHz"));
            }
        }
        details.Add(new(Strings.Get("Monitor_LoadAverage"),
            $"{m.Load1:F2} {m.Load5:F2} {m.Load15:F2}"));
        if (m.ProcessCount > 0 || m.ThreadCount > 0)
        {
            details.Add(new(Strings.Get("Monitor_ProcessesThreads"), $"{m.ProcessCount} / {m.ThreadCount}"));
        }
        if (m.ContextSwitchesPerSec > 0)
        {
            details.Add(new(Strings.Get("Monitor_ContextSwitches"), $"{m.ContextSwitchesPerSec / 1000:F1} K/s"));
        }
        if (m.UptimeSeconds > 0)
        {
            details.Add(new(Strings.Get("Monitor_UptimeLabel"), MetricFormat.Uptime(m.UptimeSeconds)));
        }
        details.Add(new(Strings.Get("Monitor_Kernel"), m.Kernel));
        CpuDetails = details;
    }

    private void ApplyMemory(SessionMetrics m)
    {
        MemoryTotalBytes = Math.Max(1, m.MemTotalBytes);
        MemoryHistory.Push(m.MemUsedBytes);
        MemoryText = $"{MetricFormat.Bytes(m.MemUsedBytes)} / {MetricFormat.Bytes(m.MemTotalBytes)}";
        NavMemorySub = MemoryText;

        long cache = m.Memory?.CacheTotal ?? 0;
        long buffers = m.Memory?.Buffers ?? 0;
        CacheHistory.Push(m.MemUsedBytes + cache + buffers);

        long free = Math.Max(0, m.MemTotalBytes - m.MemUsedBytes - cache - buffers);
        double total = Math.Max(1, m.MemTotalBytes);
        MemoryUsedRatio = m.MemUsedBytes / total;
        MemoryCacheRatio = (cache + buffers) / total;
        MemoryFreeRatio = free / total;
        MemoryUsedText = MetricFormat.Bytes(m.MemUsedBytes);
        MemoryCacheText = MetricFormat.Bytes(cache + buffers);
        MemoryFreeText = MetricFormat.Bytes(free);

        SwapPercent = m.SwapPercent;
        SwapText = m.SwapTotalBytes > 0
            ? $"{MetricFormat.Bytes(m.SwapUsedBytes)} / {MetricFormat.Bytes(m.SwapTotalBytes)}"
            : "--";

        var details = new List<KeyValueRow>
        {
            new(Strings.Get("Monitor_MemTotal"), MetricFormat.Bytes(m.MemTotalBytes)),
            new(Strings.Get("Monitor_MemUsed"), $"{MetricFormat.Bytes(m.MemUsedBytes)} ({m.MemPercent:F0}%)")
        };
        if (m.Memory is { } detail)
        {
            details.Add(new(Strings.Get("Monitor_MemAvailable"), MetricFormat.Bytes(detail.Available)));
            details.Add(new(Strings.Get("Monitor_MemCacheBuffers"),
                $"{MetricFormat.Bytes(detail.CacheTotal)} / {MetricFormat.Bytes(detail.Buffers)}"));
            details.Add(new(Strings.Get("Monitor_MemShared"), MetricFormat.Bytes(detail.Shmem)));
            details.Add(new(Strings.Get("Monitor_MemSlab"), MetricFormat.Bytes(detail.SReclaimable)));
            details.Add(new(Strings.Get("Monitor_MemDirty"), MetricFormat.Bytes(detail.Dirty)));
        }
        // 交换分区不进明细列表 —— 卡片底部已经有一条带进度条的固定区,重复一行是噪音。
        MemoryDetails = details;

        Fill(TopMemoryProcesses, m.TopByMemory.Select(p => new ProcessRow(
            p.Pid, p.Command, p.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
            MetricFormat.Bytes(p.RssBytes),
            m.MemTotalBytes > 0 ? p.RssBytes * 100.0 / m.MemTotalBytes : 0,
            p.SharedBytes is { } shared ? MetricFormat.Bytes(shared) : NotAvailable,
            p.SwapBytes is { } swap ? MetricFormat.Bytes(swap) : NotAvailable)));
    }

    private void ApplyDisk(SessionMetrics m)
    {
        double totalRead = 0, totalWrite = 0;
        HasDiskIo = m.DiskIoRates is { Count: > 0 };
        if (m.DiskIoRates is { Count: > 0 } rates)
        {
            foreach (DiskIoRate rate in rates)
            {
                totalRead += rate.ReadBytesPerSec;
                totalWrite += rate.WriteBytesPerSec;
                DiskDeviceRow row = EnsureDisk(rate.Name);
                row.ReadHistory.Push(rate.ReadBytesPerSec);
                row.WriteHistory.Push(rate.WriteBytesPerSec);
                row.BusyHistory.Push(rate.BusyPercent);
                // 带上"活动"二字(设计稿口径):卡片右上角光一个百分数,会被当成容量占用率。
                row.ActivityText = Strings.Format("Monitor_DiskActivity", MetricFormat.Percent(rate.BusyPercent));
                row.Revision++;
            }
        }
        DiskReadHistory.Push(totalRead);
        DiskWriteHistory.Push(totalWrite);
        // 采不到逐盘 IO(探针缺 /sys/block 或首个采样)时显示占位符,不拿 0 冒充"空闲"。
        DiskThroughputText = HasDiskIo
                                 ? Strings.Format("Monitor_DiskThroughput",
                                     MetricFormat.Rate(totalRead), MetricFormat.Rate(totalWrite))
                                 : NotAvailable;
        NavDiskSub = DiskThroughputText;

        // 逐盘容量:把各挂载点按所属块设备归并(/dev/nvme0n1p2 → nvme0n1)。
        var used = new Dictionary<string, (long Used, long Total)>(StringComparer.Ordinal);
        foreach (DiskUsage disk in m.Disks)
        {
            string device = DeviceOf(disk.Source);
            if (device.Length == 0)
            {
                continue;
            }
            (long u, long t) = used.GetValueOrDefault(device);
            used[device] = (u + disk.UsedBytes, t + disk.TotalBytes);
        }
        foreach (BlockDevice device in _static?.Disks ?? [])
        {
            DiskDeviceRow row = EnsureDisk(device.Name);
            // virtio / Xen / nbd 这类虚拟盘的 rotational 恒为 1,标成 "HDD" 是误导 —— 干脆不标。
            bool virtualDisk = device.Name.StartsWith("vd", StringComparison.Ordinal)
                               || device.Name.StartsWith("xvd", StringComparison.Ordinal)
                               || device.Name.StartsWith("nbd", StringComparison.Ordinal);
            // 副标题按设计稿是"型号 · 接口类型";lsblk 给不出 TRAN(虚拟盘 / 老 lsblk)时
            // 退回 SSD / HDD,总比只剩型号强。
            string kind = TransportLabel(device.Transport);
            if (kind.Length == 0)
            {
                kind = virtualDisk ? "" : device.Rotational ? "HDD" : "SSD";
            }
            row.Model = string.Join(" · ", new[] { device.Model, kind }.Where(part => part.Length > 0));
            // 该盘没有已挂载分区时,退回 lsblk 的整盘容量,免得显示成 "--"。
            if (row.UsedPercent <= 0 && device.SizeBytes > 0)
            {
                row.CapacityText = MetricFormat.Bytes(device.SizeBytes);
            }
        }
        DiskCountText = Strings.Format("Monitor_DiskCount", Disks.Count);
        foreach (DiskDeviceRow row in Disks)
        {
            if (!used.TryGetValue(row.Name, out (long Used, long Total) capacity) || capacity.Total <= 0)
            {
                continue;
            }
            row.CapacityText = $"{MetricFormat.Bytes(capacity.Used)} / {MetricFormat.Bytes(capacity.Total)}";
            row.UsedPercent = capacity.Used * 100.0 / capacity.Total;
            row.UsedPercentText = MetricFormat.Percent(row.UsedPercent);
        }
        if (ReferenceEquals(SelectedDisk, PlaceholderDisk) && Disks.Count > 0)
        {
            SelectDisk(Disks[0]);
        }
        if (!ReferenceEquals(SelectedDisk, PlaceholderDisk))
        {
            DiskDeviceRow selected = SelectedDisk;
            DiskDetails =
            [
                new(Strings.Get("Monitor_DiskRead"), MetricFormat.Rate(selected.ReadHistory.Last)),
                new(Strings.Get("Monitor_DiskWrite"), MetricFormat.Rate(selected.WriteHistory.Last)),
                new(Strings.Get("Monitor_DiskActive"), MetricFormat.Percent(selected.BusyHistory.Last)),
                new(Strings.Get("Monitor_DiskCapacity"), selected.CapacityText)
            ];
        }

        Fill(Partitions, m.Disks.Select(d => new PartitionRow(
            d.MountPoint, d.Source,
            $"{MetricFormat.Bytes(d.UsedBytes)} / {MetricFormat.Bytes(d.TotalBytes)}",
            d.Percent, d.FsType is { Length: > 0 } fs ? fs : NotAvailable)));
    }

    private void ApplyNetwork(SessionMetrics m)
    {
        NetRxHistory.Push(m.NetRxBytesPerSec);
        NetTxHistory.Push(m.NetTxBytesPerSec);
        NetThroughputText = $"↓{MetricFormat.Rate(m.NetRxBytesPerSec)}  ↑{MetricFormat.Rate(m.NetTxBytesPerSec)}";
        NavNetworkSub = NetThroughputText;

        foreach (NetInterfaceRate rate in m.NicRates ?? [])
        {
            NicRow row = EnsureNic(rate.Name);
            row.RxHistory.Push(rate.RxBytesPerSec);
            row.TxHistory.Push(rate.TxBytesPerSec);
            row.RxText = MetricFormat.Rate(rate.RxBytesPerSec);
            row.TxText = MetricFormat.Rate(rate.TxBytesPerSec);
            row.Revision++;
        }
        foreach (NicInfo nic in m.NicInfos)
        {
            NicRow row = EnsureNic(nic.Name);
            row.IpAddress = nic.IpAddress;
            row.IsUp = nic.LinkUp;
            row.StateText = row.IsUp ? Strings.Get("Connected") : Strings.Get("Disconnected");
            row.LinkText = nic.SpeedMbps > 0
                ? nic.SpeedMbps >= 1000
                    ? $"{nic.SpeedMbps / 1000} Gbps"
                    : $"{nic.SpeedMbps} Mbps"
                : NotAvailable;
            // 明细按设计稿排成左右两列各四行:左列是身份与链路,右列是流量与差错。
            // 双工挂在链路速率后面 —— 单独占一行不值,合起来正好读成 "10 Gbps · 全双工"。
            string link = nic.Duplex switch
            {
                "full" => $"{row.LinkText} · {Strings.Get("Monitor_NicFullDuplex")}",
                "half" => $"{row.LinkText} · {Strings.Get("Monitor_NicHalfDuplex")}",
                _ => row.LinkText
            };
            row.Details =
            [
                new(Strings.Get("Monitor_NicMac"), nic.Mac.Length > 0 ? nic.Mac : NotAvailable),
                new(Strings.Get("Monitor_NicMtu"), nic.Mtu.ToString(CultureInfo.InvariantCulture)),
                new(Strings.Get("Monitor_NicLink"), link),
                new(Strings.Get("Monitor_NicIp"), nic.IpAddress.Length > 0 ? nic.IpAddress : NotAvailable),
                new(Strings.Get("Monitor_NicRxTotal"), NotAvailable),
                new(Strings.Get("Monitor_NicTxTotal"), NotAvailable),
                new(Strings.Get("Monitor_NicDropped"), Pair(nic.RxDropped, nic.TxDropped)),
                new(Strings.Get("Monitor_NicErrors"), Pair(nic.RxErrors, nic.TxErrors))
            ];
        }
        // 累计收发按网卡计数器直接展示(与设计的"累计接收/发送"一致)。
        // 计数器与属性来自两个分段,这里按位替换而不是追加,免得刷新一次多两行。
        foreach (NetInterfaceCounter counter in m.NicCounters)
        {
            NicRow row = EnsureNic(counter.Name);
            List<KeyValueRow> details = [.. row.Details];
            Replace(details, "Monitor_NicRxTotal", MetricFormat.Bytes(counter.RxBytes));
            Replace(details, "Monitor_NicTxTotal", MetricFormat.Bytes(counter.TxBytes));
            row.Details = details;
        }
        if (ReferenceEquals(SelectedNic, PlaceholderNic) && Nics.Count > 0)
        {
            SelectNic(Nics[0]);
        }

        // 连接占用 Top:按瞬时收发合计排序取前 8。ss 不可用或首个采样时给空态提示,
        // 而不是留一块没有任何解释的空白。
        if (m.ConnectionRates is { Count: > 0 } connections)
        {
            Fill(TopConnections, connections
                .OrderByDescending(c => c.RxBytesPerSec + c.TxBytesPerSec)
                .Take(8)
                .Select(c => new ConnectionRow(
                    c.Peer,
                    c.Process.Length > 0 ? c.Process : NotAvailable,
                    MetricFormat.Rate(c.RxBytesPerSec),
                    MetricFormat.Rate(c.TxBytesPerSec))));
        }
        else if (m.HasConnectionProbe && m.Connections.Count == 0)
        {
            TopConnections.Clear();
        }
        ConnectionsUnavailable = TopConnections.Count == 0;
    }

    private void ApplyGpu(SessionMetrics m)
    {
        // 虚拟化场景里"有卡但读不到指标"是常态:直通卡宿主没装驱动、KVM 的 virtio-gpu、
        // ESXi 的 SVGA、WSL 的 /dev/dxg —— 静态探针能在 PCI 上看见它们,实时探针一个数也给不出。
        // 只按实时列表判断有没有 GPU,这些机器的 GPU 页会整页消失。
        IReadOnlyList<GpuCardInfo> staticCards = _static?.GpuCards ?? [];
        HasGpu = m.Gpus.Count > 0 || staticCards.Count > 0;
        if (!HasGpu)
        {
            NavGpuSub = "--";
            return;
        }
        foreach (GpuDevice gpu in m.Gpus)
        {
            // 名字优先用探针给的(NVIDIA / amdgpu 的 product_name),否则退回 lspci 的型号名。
            string name = gpu.Name.Length > 0
                              ? gpu.Name
                              : _static?.GpuCards.FirstOrDefault(c => c.Card == gpu.Card)?.Name ?? "";
            if (name.Length == 0)
            {
                name = gpu.Vendor.ToString().ToUpperInvariant();
            }
            GpuCardRow row = EnsureGpu(gpu.Index, name);
            row.Name = name;
            row.Vendor = gpu.Vendor;
            row.HasUtil = gpu.UtilPercent is not null;
            row.UtilPercent = gpu.UtilPercent ?? 0;
            row.UtilText = Format(gpu.UtilPercent, MetricFormat.Percent);
            row.MemPercent = gpu.MemPercent ?? 0;
            row.MemTotalBytes = gpu.MemTotalBytes ?? 0;
            row.MemText = gpu.MemUsedBytes is { } used && gpu.MemTotalBytes is { } total
                              ? $"{MetricFormat.Bytes(used)} / {MetricFormat.Bytes(total)}"
                              : NotAvailable;
            row.MemPercentText = Format(gpu.MemPercent, MetricFormat.Percent);
            row.TempText = Format(gpu.TemperatureC, t => $"{t:F0} °C");
            row.TempWarn = gpu.TemperatureC is > 70 and <= 80;
            row.TempCrit = gpu.TemperatureC is > 80;
            row.PowerText = (gpu.PowerWatts, gpu.PowerLimitWatts) switch
            {
                ({ } watts, { } limit) when limit > 0 => $"{watts:F0} / {limit:F0} W",
                ({ } watts, _) => $"{watts:F0} W",
                _ => NotAvailable
            };
            // 拿不到的指标不入曲线 —— 推 0 进去会画出一条"占用恒为 0"的假线。
            if (gpu.UtilPercent is { } util)
            {
                row.UtilHistory.Push(util);
            }
            if (gpu.MemUtilPercent is { } bandwidth)
            {
                row.MemBandwidthHistory.Push(bandwidth);
            }
            if (gpu.MemUsedBytes is { } vram)
            {
                row.MemHistory.Push(vram);
            }
            row.Details =
            [
                new(Strings.Get("Monitor_GpuVendor"), VendorLabel(gpu.Vendor)),
                new(Strings.Get("Monitor_GpuDriver"),
                    gpu.Vendor == GpuVendor.Nvidia && _static?.GpuDriver is { Length: > 0 } d ? d : NotAvailable),
                new(Strings.Get("Monitor_GpuClock"), (gpu.ClockMhz, gpu.MemClockMhz) switch
                {
                    ({ } core, { } mem) => $"{core} / {mem} MHz",
                    ({ } core, _) => $"{core} MHz",
                    _ => NotAvailable
                }),
                new(Strings.Get("Monitor_GpuPower"), row.PowerText),
                new(Strings.Get("Monitor_GpuTemp"), row.TempText),
                new(Strings.Get("Monitor_GpuFan"), Format(gpu.FanPercent, f => $"{f}%")),
                new(Strings.Get("Monitor_GpuMemory"), row.MemText)
            ];
            row.Revision++;
        }

        // 只在 PCI / WSL 上看得见的卡:排在实时卡后面,指标全是占位符 ——
        // 显示"这张卡在,但这套虚拟化不给读数",比整张卡不显示有用得多。
        bool hasLiveNvidia = m.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);
        int nextIndex = m.Gpus.Count > 0 ? m.Gpus.Max(g => g.Index) + 1 : 0;
        foreach (GpuCardInfo card in staticCards)
        {
            // 同一张卡已由 DRM sysfs 采到,或已由 nvidia-smi 报过(闭源驱动没有 DRM 名字可比)。
            if (m.Gpus.Any(g => g.Card.Length > 0 && g.Card == card.Card) ||
                (card.Vendor == GpuVendor.Nvidia && hasLiveNvidia))
            {
                continue;
            }
            string label = card.Name.Length > 0 ? card.Name : VendorLabel(card.Vendor);
            GpuCardRow row = EnsureGpu(nextIndex++, label);
            row.Name = label;
            row.Vendor = card.Vendor;
            row.HasUtil = false;
            row.UtilPercent = 0;
            row.UtilText = NotAvailable;
            row.MemPercent = 0;
            row.MemTotalBytes = 0;
            row.MemText = NotAvailable;
            row.MemPercentText = NotAvailable;
            row.TempText = NotAvailable;
            row.TempWarn = false;
            row.TempCrit = false;
            row.PowerText = NotAvailable;
            row.Details =
            [
                new(Strings.Get("Monitor_GpuVendor"), VendorLabel(card.Vendor)),
                new(Strings.Get("Monitor_GpuDriver"), card.Driver.Length > 0 ? card.Driver : NotAvailable),
                new(Strings.Get("Monitor_GpuSlot"), card.Slot.Length > 0 ? card.Slot : NotAvailable),
                new(Strings.Get("Monitor_GpuClock"), NotAvailable),
                new(Strings.Get("Monitor_GpuPower"), NotAvailable),
                new(Strings.Get("Monitor_GpuTemp"), NotAvailable),
                new(Strings.Get("Monitor_GpuFan"), NotAvailable),
                new(Strings.Get("Monitor_GpuMemory"), NotAvailable)
            ];
            row.Revision++;
        }

        if (ReferenceEquals(SelectedGpu, PlaceholderGpu) && Gpus.Count > 0)
        {
            SelectGpu(Gpus[0]);
        }
        NavGpuSub = Strings.Format("Monitor_NavGpu", SelectedGpu.UtilPercent, Gpus.Count);

        Fill(GpuProcesses, m.GpuProcesses.Select(p => new GpuProcessRow(
            p.GpuIndex >= 0 ? p.GpuIndex.ToString(CultureInfo.InvariantCulture) : "--",
            p.Pid, p.Name, MetricFormat.Bytes(p.MemBytes))));
    }

    private void UpdateHeader(SessionMetrics? m = null)
    {
        string os = m?.OsVersion is { Length: > 0 } value ? value : "";
        string cpu = _static?.CpuModel is { Length: > 0 } model ? model : "";
        var parts = new List<string>();
        if (os.Length > 0)
        {
            parts.Add(os);
        }
        if (cpu.Length > 0)
        {
            parts.Add(cpu);
        }
        if (m is not null)
        {
            parts.Add(Strings.Format("Monitor_LogicalCores", m.CpuCores));
        }
        if (HasGpu)
        {
            parts.Add(Strings.Format("Monitor_GpuCount", Gpus.Count));
        }
        parts.Add(Strings.Format("Monitor_Interval", IntervalSeconds));
        HeaderSubtitle = "// " + string.Join(" · ", parts);
    }

    /// <summary>
    /// 逐核心数据铺开:先按核心号存历史,再按当前排序生成"显示序"的三份并行数组
    /// (占用率 / 标签 / 历史)。排序只影响显示序,历史始终按物理核心号累积。
    /// </summary>
    private void ApplyCores(IReadOnlyList<double> cores)
    {
        int n = cores.Count;
        while (_coreHistories.Count < n)
        {
            _coreHistories.Add(new());
        }
        for (int i = 0; i < n; i++)
        {
            _coreHistories[i].Push(cores[i]);
        }

        // 核心数一旦确定就按规范定默认视图:>32 核用热力图,否则用迷你折线(用户手动选过就不再改)。
        if (!_coreViewChosen)
        {
            CoreViewMode = n > 32 ? CoreView.Heat : CoreView.Spark;
            RaiseCoreViewFlags();
        }

        if (_corePercents.Length != n)
        {
            _corePercents = new double[n];
            _coreLabels = new string[n];
            _coreHistoryViews = new IReadOnlyList<double>[n];
            _coreOrder = new int[n];
        }
        for (int i = 0; i < n; i++)
        {
            _coreOrder[i] = i;
        }
        if (SortCoresByLoad)
        {
            Array.Sort(_coreOrder, (a, b) => cores[b].CompareTo(cores[a]));
        }
        for (int i = 0; i < n; i++)
        {
            int core = _coreOrder[i];
            _corePercents[i] = cores[core];
            _coreLabels[i] = "CPU" + core.ToString(CultureInfo.InvariantCulture);
            _coreHistoryViews[i] = _coreHistories[core].Values;
        }
        // 同一组数组原地更新,靠 Revision 触发重绘;每帧新建 192 元素数组是白费的分配。
        if (!ReferenceEquals(CorePercents, _corePercents))
        {
            CorePercents = _corePercents;
            CoreLabels = _coreLabels;
            CoreHistories = _coreHistoryViews;
        }
        if (CoreViewMode == CoreView.List)
        {
            Fill(CoreRows, Enumerable.Range(0, n).Select(i => new CoreRow(
                _coreLabels[i], _corePercents[i], MetricFormat.Percent(_corePercents[i]))));
        }
    }

    private void UpdateCoreSubtitle()
    {
        int count = CorePercents.Count;
        if (count == 0)
        {
            CoreSubtitle = "";
            return;
        }
        CoreSubtitle = SelectedCoreIndex >= 0 && SelectedCoreIndex < count && SelectedCoreIndex < _coreLabels.Length
            ? Strings.Format("Monitor_CoreSelectedLabel", count, _coreLabels[SelectedCoreIndex], CorePercents[SelectedCoreIndex])
            : Strings.Format("Monitor_CoreCount", count);
    }

    private void SetCoreView(string mode)
    {
        if (!Enum.TryParse(mode, out CoreView parsed))
        {
            return;
        }
        _coreViewChosen = true;
        CoreViewMode = parsed;
        RaiseCoreViewFlags();
        if (parsed == CoreView.List)
        {
            Fill(CoreRows, Enumerable.Range(0, CorePercents.Count).Select(i => new CoreRow(
                _coreLabels[i], _corePercents[i], MetricFormat.Percent(_corePercents[i]))));
        }
    }

    private void SetCoreSort(string mode)
    {
        SortCoresByLoad = string.Equals(mode, "Load", StringComparison.OrdinalIgnoreCase);
        this.RaisePropertyChanged(nameof(IsSortByIndex));
        this.RaisePropertyChanged(nameof(IsSortByLoad));
        // 排序立刻生效,不必等下一个采样周期。
        if (CorePercents.Count > 0)
        {
            ApplyCores([.. _coreHistories.Take(CorePercents.Count).Select(h => h.Last)]);
            Revision++;
        }
    }

    private void RaiseCoreViewFlags()
    {
        this.RaisePropertyChanged(nameof(IsHeatView));
        this.RaisePropertyChanged(nameof(IsSparkView));
        this.RaisePropertyChanged(nameof(IsListView));
    }

    private GpuCardRow EnsureGpu(int index, string name)
    {
        if (_gpuRows.TryGetValue(index, out GpuCardRow? row))
        {
            return row;
        }
        row = new(index, name);
        _gpuRows[index] = row;
        Gpus.Add(row);
        return row;
    }

    private DiskDeviceRow EnsureDisk(string name)
    {
        if (_diskRows.TryGetValue(name, out DiskDeviceRow? row))
        {
            return row;
        }
        row = new(name);
        _diskRows[name] = row;
        Disks.Add(row);
        return row;
    }

    private NicRow EnsureNic(string name)
    {
        if (_nicRows.TryGetValue(name, out NicRow? row))
        {
            return row;
        }
        row = new(name);
        _nicRows[name] = row;
        Nics.Add(row);
        return row;
    }

    /// <summary>把只读快照铺进集合:逐项替换而不是清空重建,避免列表每秒闪一下。</summary>
    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        int index = 0;
        foreach (T item in source)
        {
            if (index < target.Count)
            {
                target[index] = item;
            }
            else
            {
                target.Add(item);
            }
            index++;
        }
        while (target.Count > index)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>把 “/dev/nvme0n1p2” 之类的分区设备名归并到所属块设备(nvme0n1 / sda)。</summary>
    private static string DeviceOf(string source)
    {
        const string prefix = "/dev/";
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "";
        }
        string name = source[prefix.Length..];
        // nvme0n1p2 / mmcblk0p1:分区后缀是 "p" + 数字;sda1 / vdb3:直接去掉尾部数字。
        int p = name.LastIndexOf('p');
        if (p > 0 && p < name.Length - 1 && name[(p + 1)..].All(char.IsDigit) && char.IsDigit(name[p - 1]))
        {
            return name[..p];
        }
        return name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }

    /// <summary>“收 / 发”成对读数;任一侧探不到就整格显示占位符。</summary>
    private static string Pair(long? rx, long? tx) =>
        (rx, tx) switch
        {
            ({ } r, { } t) => $"{r} / {t}",
            ({ } r, null) => $"{r} / {NotAvailable}",
            (null, { } t) => $"{NotAvailable} / {t}",
            _ => NotAvailable
        };

    /// <summary>就地替换某个键的值,键不存在时追加。</summary>
    private static void Replace(List<KeyValueRow> rows, string key, string value)
    {
        string label = Strings.Get(key);
        int index = rows.FindIndex(r => r.Key == label);
        if (index >= 0)
        {
            rows[index] = new(label, value);
        }
        else
        {
            rows.Add(new(label, value));
        }
    }

    /// <summary>lsblk 的 TRAN 值 → 展示用的接口名;不认识的传输层原样大写显示。</summary>
    private static string TransportLabel(string transport) => transport.ToLowerInvariant() switch
    {
        "" => "",
        "nvme" => "NVMe",
        "sata" => "SATA",
        "sas" => "SAS",
        "usb" => "USB",
        "mmc" => "eMMC",
        "iscsi" => "iSCSI",
        "fc" => "FC",
        _ => transport.ToUpperInvariant()
    };

    private static string FormatGhz(double mhz) => (mhz / 1000).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>指标不可用时的占位符。Intel 核显没有利用率、数据中心卡没有风扇,一律显示它。</summary>
    private const string NotAvailable = "—";

    /// <summary>把可空指标格式化为文本;为 null 时返回占位符。</summary>
    private static string Format<T>(T? value, Func<T, string> format) where T : struct =>
        value is { } v ? format(v) : NotAvailable;

    private static string VendorLabel(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Virtual => Strings.Get("Monitor_GpuVirtual"),
        _ => NotAvailable
    };
}

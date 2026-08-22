using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Disposables;
using ReactiveUI.Primitives.Signals;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 后台活动清单里的一行(圆环的悬停提示与弹出清单共用)。
/// 可见性判定预先算成 bool 而不是在 axaml 里挂转换器:这份清单每秒会重建好几次,
/// 每行再走两趟转换器纯属白烧。
/// </summary>
/// <param name="Title">活动名称。</param>
/// <param name="Detail">当前处理对象;无则为空串。</param>
/// <param name="HasDetail">是否有 <paramref name="Detail" />。</param>
/// <param name="Progress">百分比文本(如 "42%");进度不可知时为空串。</param>
/// <param name="HasProgress">是否有确定进度。</param>
public sealed record BackgroundActivityItem(
    string Title,
    string Detail,
    bool HasDetail,
    string Progress,
    bool HasProgress);

/// <summary>状态栏视图模型:维护连接状态、终端信息与 CPU/内存/磁盘/网络等实时指标,并驱动运行时长计时。</summary>
public sealed class StatusBarViewModel(ISequencer scheduler) : ReactiveObject, IDisposable
{
    private readonly MultipleDisposable _disposables = [];

    private DateTimeOffset _uptimeStart;

    private IDisposable? _uptimeSubscription;

    /// <summary>使用默认调度器构造状态栏视图模型(供设计期/无注入场景使用)。</summary>
    public StatusBarViewModel()
        : this(ThreadPoolSequencer.Instance) { }

    // 指标分段始终可见;在首个采样前(或无已连接会话时)显示空闲占位符。

    /// <summary>状态栏左侧的当前状态文本,默认显示“就绪”。</summary>
    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Ready;

    /// <summary>连接信息文本(如主机名/用户名),未连接时为空。</summary>
    public string ConnectionInfo
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>连接状态文本;赋值时同步刷新 <see cref="IsConnected"/>。</summary>
    public string Status
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            IsConnected = value == Strings.Connected;
        }
    } = Strings.Disconnected;

    /// <summary>与服务器的往返延迟文本,未测得时为空。</summary>
    public string Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>终端类型标识,默认 xterm-256color。</summary>
    public string TerminalType
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "xterm-256color";

    /// <summary>终端窗口尺寸(列×行)文本。</summary>
    public string WindowSize
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "80×24";

    /// <summary>字符编码文本,默认 UTF-8。</summary>
    public string Encoding
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "UTF-8";

    /// <summary>会话已运行时长文本(hh:mm:ss),由计时器刷新。</summary>
    public string Uptime
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否已连接;随 <see cref="Status"/> 是否等于“已连接”自动更新。</summary>
    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>CPU 总占用率文本,无实时会话时为 "--"。</summary>
    public string CpuUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>内存占用率文本,无实时会话时为 "--"。</summary>
    public string MemUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>悬停提示:CPU 总占用 + 每核心占用(由主 VM 按采样填充)。</summary>
    public string CpuTooltip
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(MetricsTooltip));
        }
    } = "CPU";

    /// <summary>悬停提示:内存/交换分区的已用与总量。</summary>
    public string MemTooltip
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(MetricsTooltip));
        }
    } = Strings.Get("Svc_Memory");

    /// <summary>悬停提示:每个磁盘(挂载点)的用量。</summary>
    public string DiskTooltip
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(MetricsTooltip));
        }
    } = Strings.Get("Svc_Disk");

    /// <summary>悬停提示:每个网卡的上下行速率。</summary>
    public string NetTooltip
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(MetricsTooltip));
        }
    } = Strings.Get("Svc_NetSpeed");

    /// <summary>
    /// 打开资源监视窗口的命令,由主窗口视图模型注入。
    /// 命令挂在这里而不是让状态栏去 <c>$parent[Window].DataContext</c> 找 ——
    /// 视图在窗口 DataContext 赋值之前就完成了加载,跨树查找会在启动时刷一条绑定错误。
    /// </summary>
    public ICommand? OpenResourceMonitorCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 状态栏资源按钮的悬停提示:四段详情拼在一起。读数不再常驻状态栏(已收进资源监视窗口),
    /// 但"不开窗瞄一眼"的路径必须保留 —— 否则每次看占用都要开一扇窗。
    /// </summary>
    public string MetricsTooltip =>
        string.Join(
            "\n\n",
            new[] { CpuTooltip, MemTooltip, DiskTooltip, NetTooltip, Strings.Get("StatusBar_OpenMonitor") }
                .Where(section => !string.IsNullOrWhiteSpace(section)));

    /// <summary>交换分区占用率;无交换分区或无实时会话时显示 "--"。</summary>
    public string SwapUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>根文件系统占用率;无实时会话时显示 "--"。</summary>
    public string DiskUsage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>主导方向的速率,例如 "4.2 MB/s"(安卓风格读数)。</summary>
    public string NetSpeed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "0 B/s";

    /// <summary>
    /// 服务器实际上传时为 True —— 点亮上下箭头字形中的 ↑ 半边(强调色)。
    /// </summary>
    public bool IsNetUpActive
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>服务器实际下载时为 True —— 点亮 ↓ 半边。</summary>
    public bool IsNetDownActive
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ---- 后台活动指示器(状态栏右下角的圆环) ----

    /// <summary>是否有后台活动进行中;为假时整个指示器隐藏,状态栏回到干净状态。</summary>
    public bool HasBackgroundActivity
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>圆环是否走不确定动画(存在进度不可知的活动,或只有一条无进度活动时)。</summary>
    public bool IsBackgroundIndeterminate
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>聚合后的确定进度,取值 0~1(<see cref="IsBackgroundIndeterminate"/> 为真时无意义)。</summary>
    public double BackgroundProgress
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>圆环右侧的一行摘要:单条活动显示其名称,多条时显示"N 项后台任务"。</summary>
    public string BackgroundSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>悬停提示:逐条列出进行中的活动(带百分比)。</summary>
    public string BackgroundTooltip
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Get("BackgroundTasksIdle");

    /// <summary>进行中的活动明细,供点击圆环后弹出的清单绑定。</summary>
    public IReadOnlyList<BackgroundActivityItem> BackgroundActivities
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>
    /// 用一份后台活动快照刷新指示器。
    /// <para>
    /// **必须在 UI 线程调用** —— 本类型不知道调度器是谁(表现层不引 Avalonia),
    /// 由持有它的窗口视图模型负责把账本的变更通知切到 UI 线程再送进来。
    /// </para>
    /// <para>
    /// 聚合规则:全部活动都报了确定进度时取算术平均并显示确定圆环;
    /// 只要有一条说不出进度,整个圆环就走不确定动画 —— 把"不知道"混进平均值里
    /// 算出来的百分比是假的,而进度条一旦骗过人一次就再也不会被相信。
    /// </para>
    /// </summary>
    /// <param name="activities">当前进行中的活动快照。</param>
    public void ApplyBackgroundActivities(IReadOnlyList<BackgroundActivitySnapshot> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        HasBackgroundActivity = activities.Count > 0;
        if (activities.Count == 0)
        {
            IsBackgroundIndeterminate = true;
            BackgroundProgress = 0;
            BackgroundSummary = string.Empty;
            BackgroundTooltip = Strings.Get("BackgroundTasksIdle");
            BackgroundActivities = [];
            return;
        }

        BackgroundActivities = [.. activities.Select(a => new BackgroundActivityItem(
            a.Title,
            a.Detail ?? string.Empty,
            !string.IsNullOrEmpty(a.Detail),
            a.Progress is { } p ? $"{p * 100:F0}%" : string.Empty,
            a.Progress is not null))];

        bool allDeterminate = activities.All(a => a.Progress is not null);
        IsBackgroundIndeterminate = !allDeterminate;
        BackgroundProgress = allDeterminate ? activities.Average(a => a.Progress!.Value) : 0;
        BackgroundSummary = activities.Count == 1
            ? activities[0].Title
            : Strings.Format("BackgroundTasksCount", activities.Count);
        BackgroundTooltip = string.Join('\n', BackgroundActivities.Select(item =>
            string.Join(" ", new[]
            {
                item.Title,
                item.HasDetail ? $"— {item.Detail}" : string.Empty,
                item.HasProgress ? $"({item.Progress})" : string.Empty
            }.Where(part => part.Length > 0))));
    }

    /// <summary>释放内部订阅资源并停止运行时长计时。</summary>
    public void Dispose()
    {
        _disposables.Dispose();
        _uptimeSubscription = null;
    }

    /// <summary>
    /// 喂入一个网络采样(各方向字节/秒)。每个箭头半边点亮对应方向;读数显示主导方向的速率。
    /// </summary>
    public void UpdateNetwork(double rxBytesPerSec, double txBytesPerSec, bool hasRates)
    {
        if (!hasRates)
        {
            NetSpeed = "0 B/s";
            IsNetUpActive = false;
            IsNetDownActive = false;
            return;
        }

        // 保活/回显流量通常低于此阈值,不为此噪声点亮箭头。
        const double activeThreshold = 512;
        IsNetUpActive = txBytesPerSec >= activeThreshold;
        IsNetDownActive = rxBytesPerSec >= activeThreshold;
        NetSpeed = FormatRate(Math.Max(rxBytesPerSec, txBytesPerSec));
    }

    /// <summary>
    /// 将指标分段重置为空闲占位符(无已连接会话)。分段保持可见,状态栏布局不会跳动。
    /// </summary>
    public void ClearSessionMetrics()
    {
        CpuUsage = "--";
        MemUsage = "--";
        SwapUsage = "--";
        DiskUsage = "--";
        NetSpeed = "0 B/s";
        IsNetUpActive = false;
        IsNetDownActive = false;
        CpuTooltip = "CPU";
        MemTooltip = Strings.Get("Svc_Memory");
        DiskTooltip = Strings.Get("Svc_Disk");
        NetTooltip = Strings.Get("Svc_NetSpeed");
    }

    /// <summary>将字节/秒速率格式化为带单位(B/s、KB/s、MB/s、GB/s)的可读文本。</summary>
    /// <param name="bytesPerSec">每秒字节数。</param>
    /// <returns>带单位的速率文本。</returns>
    public static string FormatRate(double bytesPerSec)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytesPerSec switch
        {
            >= gb => $"{bytesPerSec / gb:F1} GB/s",
            >= mb => $"{bytesPerSec / mb:F1} MB/s",
            >= kb => $"{bytesPerSec / kb:F1} KB/s",
            _ => $"{bytesPerSec:F0} B/s"
        };
    }

    /// <summary>启动运行时长计时器,每秒刷新 <see cref="Uptime"/>;重复调用会先重置。</summary>
    public void StartUptimeTimer()
    {
        StopUptimeTimer();
        _uptimeStart = scheduler.Now;
        _uptimeSubscription = Signal
                              .Every(TimeSpan.FromSeconds(1), scheduler)
                              .Subscribe(_ =>
                              {
                                  TimeSpan elapsed = scheduler.Now - _uptimeStart;
                                  Uptime = elapsed.ToString(@"hh\:mm\:ss");
                              });
        _disposables.Add(_uptimeSubscription);
    }

    /// <summary>停止运行时长计时器并释放其订阅。</summary>
    public void StopUptimeTimer()
    {
        _uptimeSubscription?.Dispose();
        _uptimeSubscription = null;
    }

    /// <summary>重置并重新开始运行时长计时(清空 <see cref="Uptime"/> 后重启计时器)。</summary>
    public void ResetUptime()
    {
        StopUptimeTimer();
        Uptime = string.Empty;
        StartUptimeTimer();
    }
}

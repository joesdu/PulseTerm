using Avalonia;
using Avalonia.Threading;
using System.Net.NetworkInformation;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>
/// 状态栏那一排实时指标(CPU / 内存 / 磁盘 / 网络 / 延迟)的采样循环。
/// </summary>
/// <remarks>
/// <para>
/// 从 <see cref="MainWindowViewModel" /> 拆出来的一簇:定时器、两个重入闸、
/// 失焦降频与最小化暂停、四段悬停提示的拼装 —— 这些彼此紧密、与主窗口的其余职责毫不相干,
/// 混在一个五千行的类里既读不出边界,也没法单独测。
/// </para>
/// <para>
/// 采样时序上有两条必须守住的规矩,都藏在下面的代码里:
/// </para>
/// <list type="bullet">
/// <item><b>重入闸</b> —— 一次采样要在远端 fork/exec 一趟,慢于采样间隔是常态。
/// 不挡住重入的话,一台负载高的机器会被越堆越多的探测压垮。</item>
/// <item><b>写回前重认标签</b> —— 探测期间用户可能切了标签,把结果写到别的会话上
/// 是实打实的错误数据。</item>
/// </list>
/// </remarks>
/// <param name="statusBar">要写入的状态栏视图模型。</param>
/// <param name="activeTab">取当前活动标签(每次采样现取,不缓存)。</param>
/// <param name="metricsService">远端指标采集;为 null 时只跑延迟探测。</param>
public sealed class StatusMetricsPoller(
    StatusBarViewModel statusBar,
    Func<TerminalTabViewModel?> activeTab,
    ISessionMetricsService? metricsService) : IDisposable
{
    /// <summary>失焦时的采样间隔。看都没在看,没有理由继续每两秒敲一次远端。</summary>
    private static readonly TimeSpan UnfocusedInterval = TimeSpan.FromSeconds(10);

    /// <summary>延迟探测的分频:每 3 次指标采样才 ping 一次。</summary>
    private const int LatencyEveryNTicks = 3;

    private bool _latencyPolling;
    private int _latencyTick;
    private bool _metricsPolling;
    private bool _reduced;
    private DispatcherTimer? _timer;

    /// <summary>当前配置的采样间隔(秒),由宿主在设置变化时写入。</summary>
    public int ConfiguredIntervalSeconds
    {
        get;
        set
        {
            field = Math.Clamp(value, 1, 60);
            ApplyInterval();
        }
    } = 2;

    /// <summary>
    /// 起采样循环。
    /// </summary>
    /// <remarks>
    /// 没有 Avalonia 应用时直接返回:无头单元测试会构造宿主视图模型,那里没有调度器。
    /// 延迟测量(ICMP)不依赖指标服务,所以只要有 UI 就起表。
    /// </remarks>
    public void Start()
    {
        if (Application.Current is null || _timer is not null)
        {
            return;
        }
        _timer = new(
            CurrentInterval(),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _ = PollMetricsAsync();
                _ = PollLatencyAsync();
            }
        );
        _timer.Start();
    }

    /// <summary>
    /// 窗口失焦时把采样降到 10 秒一次(由视图按 Activated/Deactivated 驱动)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SetSuspended" /> 并列而不是二选一:最小化是"完全看不到,停掉";
    /// 失焦只是"没在看,慢一点" —— 切回来时状态栏不该是一片空白,所以不能直接停。
    /// 每次采样对远端都是一次 fork/exec + 一条 SSH 通道的建立与拆除,降频是实打实的省。
    /// </remarks>
    /// <param name="reduced">true = 窗口失焦。</param>
    public void SetReduced(bool reduced)
    {
        if (_reduced == reduced)
        {
            return;
        }
        _reduced = reduced;
        ApplyInterval();
    }

    /// <summary>
    /// 窗口最小化 / 隐入托盘时暂停采样(由视图按 WindowState 驱动)。
    /// </summary>
    /// <remarks>
    /// 用户看不见状态栏时,每秒一次的 SSH exec 探测 + 周期 ICMP 纯属浪费 CPU/网络,
    /// 还会阻止系统进入低功耗。恢复可见即重启,下一秒就有新数据。
    /// </remarks>
    /// <param name="suspended">true = 暂停。</param>
    public void SetSuspended(bool suspended)
    {
        if (_timer is null)
        {
            return;
        }
        if (suspended)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>当前该用的采样间隔:失焦时取「设置值与 10 秒的较大者」,否则取设置值。</summary>
    internal TimeSpan CurrentInterval()
    {
        TimeSpan interval = TimeSpan.FromSeconds(ConfiguredIntervalSeconds);
        return _reduced && interval < UnfocusedInterval ? UnfocusedInterval : interval;
    }

    private void ApplyInterval()
    {
        if (_timer is { } timer)
        {
            timer.Interval = CurrentInterval();
        }
    }

    /// <summary>
    /// 状态栏延迟指示:每 3 次 tick 对活动标签的主机发一次 ICMP ping,RTT 写入
    /// <c>tab.Latency</c>(经既有 WhenAnyValue 管道刷新状态栏)。
    /// </summary>
    /// <remarks>
    /// 目标禁 ICMP 或解析失败时清空显示,不打扰;不用 TCP 探测以免刷爆 sshd 日志。
    /// </remarks>
    internal async Task PollLatencyAsync()
    {
        if (_latencyPolling || _latencyTick++ % LatencyEveryNTicks != 0)
        {
            return;
        }
        TerminalTabViewModel? tab = activeTab();
        if (tab?.Profile is null || tab.ConnectionStatus != SessionStatus.Connected)
        {
            tab?.Latency = null;
            return;
        }
        _latencyPolling = true;
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(tab.Profile.Host, TimeSpan.FromSeconds(2));

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(activeTab(), tab))
            {
                return;
            }
            tab.Latency = reply.Status == IPStatus.Success
                              ? TimeSpan.FromMilliseconds(reply.RoundtripTime)
                              : null;
        }
        catch
        {
            tab.Latency = null;
        }
        finally
        {
            _latencyPolling = false;
        }
    }

    /// <summary>采一次远端指标写进状态栏。</summary>
    internal async Task PollMetricsAsync()
    {
        if (_metricsPolling || metricsService is null)
        {
            return;
        }
        TerminalTabViewModel? tab = activeTab();
        if (tab is null || tab.SessionId == Guid.Empty || tab.ConnectionStatus != SessionStatus.Connected)
        {
            statusBar.ClearSessionMetrics();
            return;
        }
        _metricsPolling = true;
        try
        {
            SessionMetrics? metrics = await metricsService.GetMetricsAsync(tab.SessionId);

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(activeTab(), tab))
            {
                return;
            }
            if (metrics is null)
            {
                statusBar.ClearSessionMetrics();
                return;
            }
            statusBar.CpuUsage = $"{metrics.CpuPercent:F2}%";
            statusBar.MemUsage = $"{metrics.MemPercent:F1}%";
            statusBar.SwapUsage = metrics.SwapTotalBytes > 0 ? $"{metrics.SwapPercent:F1}%" : "--";
            statusBar.DiskUsage = metrics.DiskTotalBytes > 0 ? $"{metrics.DiskPercent:F1}%" : "--";
            statusBar.UpdateNetwork(
                metrics.NetRxBytesPerSec,
                metrics.NetTxBytesPerSec,
                metrics.HasNetRates
            );

            // CPU 逐核心、磁盘逐挂载点、网速逐网卡的悬停提示详情。
            statusBar.CpuTooltip = BuildCpuTooltip(metrics);
            statusBar.MemTooltip = BuildMemTooltip(metrics);
            statusBar.DiskTooltip = BuildDiskTooltip(metrics);
            statusBar.NetTooltip = BuildNetTooltip(metrics);
        }
        catch
        {
            // 绝不让失败的探测浮现到 UI 循环里;下次 tick 再重试。
        }
        finally
        {
            _metricsPolling = false;
        }
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }


    private static string BuildCpuTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(Strings.Format("Msg_CpuTooltipTotal", m.CpuPercent, m.CpuCores));
        if (m.CorePercents is { Count: > 0 } percents)
        {
            string corePrefix = Strings.Get("Msg_CpuCorePrefix");
            for (int i = 0; i < percents.Count; i++)
            {
                string name =
                    i < m.CoreCounters.Count
                        ? m.CoreCounters[i].Name.Replace("cpu", corePrefix)
                        : $"{corePrefix}{i}";
                sb.Append('\n').Append($"{name}: {percents[i]:F0}%");
            }
        }
        else if (m.CoreCounters.Count > 0)
        {
            sb.Append('\n').Append(Strings.Get("Msg_PerCoreCollecting"));
        }
        return sb.ToString();
    }

    private static string BuildMemTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            Strings.Format(
                "Msg_MemTooltip",
                FormatGb(m.MemUsedBytes),
                FormatGb(m.MemTotalBytes),
                m.MemPercent
            )
        );
        if (m.SwapTotalBytes > 0)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_SwapTooltip",
                        FormatGb(m.SwapUsedBytes),
                        FormatGb(m.SwapTotalBytes),
                        m.SwapPercent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildDiskTooltip(SessionMetrics m)
    {
        if (m.Disks.Count == 0)
        {
            return m.DiskTotalBytes > 0
                ? Strings.Format(
                    "Msg_DiskRootTooltip",
                    FormatGb(m.DiskUsedBytes),
                    FormatGb(m.DiskTotalBytes),
                    m.DiskPercent
                )
                : Strings.Get("Msg_Disk");
        }
        var sb = new StringBuilder(Strings.Get("Msg_DiskUsage"));
        foreach (DiskUsage d in m.Disks)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_DiskMountLine",
                        d.MountPoint,
                        FormatGb(d.UsedBytes),
                        FormatGb(d.TotalBytes),
                        d.Percent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildNetTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            m.HasNetRates
                ? Strings.Format(
                    "Msg_NetTooltipTotal",
                    StatusBarViewModel.FormatRate(m.NetRxBytesPerSec),
                    StatusBarViewModel.FormatRate(m.NetTxBytesPerSec)
                )
                : Strings.Get("Msg_NetCollecting")
        );
        if (m.NicRates is not { Count: > 0 } rates)
        {
            return sb.ToString();
        }
        foreach (NetInterfaceRate r in rates)
        {
            sb.Append('\n')
                .Append(
                    $"{r.Name}: ↓ {StatusBarViewModel.FormatRate(r.RxBytesPerSec)}  ↑ {StatusBarViewModel.FormatRate(r.TxBytesPerSec)}"
                );
        }
        return sb.ToString();
    }
}

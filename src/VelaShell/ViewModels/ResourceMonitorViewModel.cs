using ReactiveUI;
using VelaShell.Core.Services;

namespace VelaShell.ViewModels;

/// <summary>资源面板中的一行磁盘信息(多磁盘主机逐盘显示)。</summary>
public sealed class DiskRowViewModel(string label, string text, double percent)
{
    /// <summary>磁盘挂载点标签(如 “Disk /home”),显示在行首。</summary>
    public string Label { get; } = label;

    /// <summary>该盘用量的展示文本(已用 / 总量 GB 及百分比)。</summary>
    public string Text { get; } = text;

    /// <summary>该盘使用率百分比,用于进度条与阈值着色。</summary>
    public double Percent { get; } = percent;

    /// <summary>使用率处于警告区间(>70% 且 ≤90%)时为 true。</summary>
    public bool Warn => Percent is > 70 and <= 90;

    /// <summary>使用率处于危险区间(>90%)时为 true。</summary>
    public bool Crit => Percent > 90;
}

/// <summary>
/// 支撑标签页悬停资源面板(设计 EP3Gd,规范 §11):在面板可见期间
/// 轮询得到的会话主机 CPU / RAM / 磁盘 / 系统信息的实时快照。
/// </summary>
public class ResourceMonitorViewModel(ISessionMetricsService metricsService, Guid sessionId, string hostName) : ReactiveObject
{
    private readonly ISessionMetricsService _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
    private SessionMetrics? _metrics;

    /// <summary>该面板所监控会话的唯一标识。</summary>
    public Guid SessionId { get; } = sessionId;

    /// <summary>会话主机名,显示在面板标题处。</summary>
    public string HostName { get; } = hostName;

    /// <summary>为 false 时显示 "数据不可用" 占位符(已断开连接 / 非 Linux 主机)。</summary>
    public bool IsAvailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>CPU 区块标签,含核心数(如 “CPU (8 cores)”)。</summary>
    public string CpuLabel => _metrics is { } m ? $"CPU ({m.CpuCores} cores)" : "CPU";

    /// <summary>CPU 使用率展示文本(百分比),无数据时为 “--”。</summary>
    public string CpuText => _metrics is { } m ? $"{m.CpuPercent:F0}%" : "--";

    /// <summary>CPU 使用率百分比,用于进度条与阈值着色。</summary>
    public double CpuPercent => _metrics?.CpuPercent ?? 0;

    /// <summary>内存使用展示文本(已用 / 总量 GB),无数据时为 “--”。</summary>
    public string RamText => _metrics is { } m
                                 ? $"{FormatGb(m.MemUsedBytes)} / {FormatGb(m.MemTotalBytes)} GB"
                                 : "--";

    /// <summary>内存使用率百分比,用于进度条与阈值着色。</summary>
    public double RamPercent => _metrics?.MemPercent ?? 0;

    /// <summary>根分区磁盘使用展示文本(已用 / 总量 GB 及百分比),无数据时为 “--”。</summary>
    public string DiskText => _metrics is { } m
                                  ? $"{FormatGb(m.DiskUsedBytes)} / {FormatGb(m.DiskTotalBytes)} GB ({m.DiskPercent:F0}%)"
                                  : "--";

    /// <summary>根分区磁盘使用率百分比,用于进度条与阈值着色。</summary>
    public double DiskPercent => _metrics?.DiskPercent ?? 0;

    /// <summary>
    /// 逐盘行:探针拿到完整磁盘列表时一盘一行(按挂载点标注);
    /// 老主机/BusyBox 拿不到列表时退回原来的根分区单行。
    /// </summary>
    public IReadOnlyList<DiskRowViewModel> Disks
    {
        get
        {
            if (_metrics is not { } m)
            {
                return [];
            }
            if (m.Disks.Count > 0)
            {
                return [.. m.Disks.Select(d => new DiskRowViewModel($"Disk {d.MountPoint}", $"{FormatGb(d.UsedBytes)} / {FormatGb(d.TotalBytes)} GB ({d.Percent:F0}%)", d.Percent))];
            }
            return [new("Disk", DiskText, DiskPercent)];
        }
    }

    /// <summary>操作系统发行版信息,无数据时为 “--”。</summary>
    public string OsVersion => _metrics?.OsVersion is { Length: > 0 } os ? os : "--";

    /// <summary>内核版本(以 “Linux ” 前缀展示),无数据时为 “--”。</summary>
    public string Kernel => _metrics?.Kernel is { Length: > 0 } k ? $"Linux {k}" : "--";

    // 阈值着色按 §11:正常 / >70% 警告 / >90% 危险。
    /// <summary>CPU 使用率处于警告区间(>70% 且 ≤90%)时为 true。</summary>
    public bool CpuWarn => CpuPercent is > 70 and <= 90;

    /// <summary>CPU 使用率处于危险区间(>90%)时为 true。</summary>
    public bool CpuCrit => CpuPercent > 90;

    /// <summary>内存使用率处于警告区间(>70% 且 ≤90%)时为 true。</summary>
    public bool RamWarn => RamPercent is > 70 and <= 90;

    /// <summary>内存使用率处于危险区间(>90%)时为 true。</summary>
    public bool RamCrit => RamPercent > 90;

    /// <summary>磁盘使用率处于警告区间(>70% 且 ≤90%)时为 true。</summary>
    public bool DiskWarn => DiskPercent is > 70 and <= 90;

    /// <summary>磁盘使用率处于危险区间(>90%)时为 true。</summary>
    public bool DiskCrit => DiskPercent > 90;

    /// <summary>拉取会话最新指标快照并刷新所有计算属性;主机不可达时置为不可用。</summary>
    public async Task RefreshAsync()
    {
        SessionMetrics? metrics = await _metricsService.GetMetricsAsync(SessionId);
        _metrics = metrics;
        IsAvailable = metrics is not null;
        this.RaisePropertyChanged(string.Empty); // 刷新所有计算属性
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");
}

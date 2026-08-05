using System.Globalization;
using ReactiveUI;
using VelaShell.Core.Services;

namespace VelaShell.Features.Monitoring;

/// <summary>
/// 定长滚动窗口:资源监视窗口的每条曲线保留最近 N 个采样点(默认 60)。
/// 用 List 而不是环形缓冲 —— 图表要的是"按时间先后"的序列,环形缓冲每帧都得重排一遍。
/// 60 个 double 的头部移除代价可以忽略,换来的是绑定一次、原地更新。
/// </summary>
public sealed class MetricHistory(int capacity = 60)
{
    private readonly List<double> _values = [with(capacity)];

    /// <summary>按时间先后排列的采样值(最后一个是最新)。绑定到图表控件的 Values。</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>最新一个采样值;尚无数据时为 0。</summary>
    public double Last => _values.Count > 0 ? _values[^1] : 0;

    /// <summary>窗口内的最大值;尚无数据时为 0。</summary>
    public double Peak
    {
        get
        {
            double max = 0;
            foreach (double v in _values)
            {
                max = Math.Max(max, v);
            }
            return max;
        }
    }

    /// <summary>追加一个采样点,超出容量时丢弃最旧的一个。</summary>
    /// <param name="value">采样值。</param>
    public void Push(double value)
    {
        if (_values.Count >= capacity)
        {
            _values.RemoveAt(0);
        }
        _values.Add(value);
    }

    /// <summary>清空历史(会话断开或切换主机时)。</summary>
    public void Clear() => _values.Clear();
}

/// <summary>资源监视窗口内共用的数值格式化。</summary>
public static class MetricFormat
{
    private const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;

    /// <summary>把字节数格式化为最合适的单位(KB / MB / GB / TB)。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>形如 “1.7 TB” 的文本。</returns>
    public static string Bytes(double bytes) => bytes switch
    {
        >= Tb => (bytes / Tb).ToString("F1", CultureInfo.InvariantCulture) + " TB",
        >= Gb => (bytes / Gb).ToString("F1", CultureInfo.InvariantCulture) + " GB",
        >= Mb => (bytes / Mb).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= Kb => (bytes / Kb).ToString("F0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString("F0", CultureInfo.InvariantCulture) + " B"
    };

    /// <summary>把字节/秒格式化为速率文本。</summary>
    /// <param name="bytesPerSec">每秒字节数。</param>
    /// <returns>形如 “18.4 MB/s” 的文本。</returns>
    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    /// <summary>把百分比格式化为整数百分号文本。</summary>
    /// <param name="percent">0-100 的百分比。</param>
    /// <returns>形如 “36%” 的文本。</returns>
    public static string Percent(double percent) => percent.ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>把秒数格式化为 “36 天 04:12:57”。</summary>
    /// <param name="seconds">秒数。</param>
    /// <returns>运行时长文本;秒数无效时为 “--”。</returns>
    public static string Uptime(double seconds)
    {
        if (seconds <= 0)
        {
            return "--";
        }
        var span = TimeSpan.FromSeconds(seconds);
        return span.Days > 0
            ? $"{span.Days} d {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }
}

/// <summary>进程 Top 列表中的一行。</summary>
/// <param name="Pid">进程号。</param>
/// <param name="Command">命令行。</param>
/// <param name="CpuText">CPU 占用文本。</param>
/// <param name="MemoryText">常驻内存文本。</param>
/// <param name="Percent">用于排序/着色的主指标百分比。</param>
/// <param name="SharedText">共享驻留内存文本;探不到为占位符。</param>
/// <param name="SwapText">已换出内存文本;探不到为占位符。</param>
public sealed record ProcessRow(
    int Pid, string Command, string CpuText, string MemoryText, double Percent,
    string SharedText = "--", string SwapText = "--")
{
    /// <summary>该进程占物理内存总量的比例文本。</summary>
    public string PercentText => MetricFormat.Percent(Percent);
}

/// <summary>分区(挂载点)表中的一行。</summary>
/// <param name="MountPoint">挂载点。</param>
/// <param name="Source">设备来源。</param>
/// <param name="UsedText">已用 / 总量文本。</param>
/// <param name="Percent">使用率(0-100)。</param>
/// <param name="FsType">文件系统类型;df 不给这一列时为空串。</param>
public sealed record PartitionRow(string MountPoint, string Source, string UsedText, double Percent, string FsType = "")
{
    /// <summary>使用率文本。</summary>
    public string PercentText => MetricFormat.Percent(Percent);

    /// <summary>使用率是否进入警告区(&gt;70%),与占用条的配色口径一致。</summary>
    public bool IsWarn => Percent is > MeterThresholds.Warn and <= MeterThresholds.Crit;

    /// <inheritdoc cref="IsWarn" />
    public bool IsCrit => Percent > MeterThresholds.Crit;
}

/// <summary>占用条转色的阈值(规范 §11),与 <c>MeterBar</c> 的默认值保持同一口径。</summary>
internal static class MeterThresholds
{
    /// <summary>转警告色的百分比。</summary>
    public const double Warn = 70;

    /// <summary>转危险色的百分比。</summary>
    public const double Crit = 90;
}

/// <summary>GPU 进程表中的一行。</summary>
/// <param name="GpuText">所属 GPU 文本。</param>
/// <param name="Pid">进程号。</param>
/// <param name="Name">进程名。</param>
/// <param name="MemoryText">显存占用文本。</param>
public sealed record GpuProcessRow(string GpuText, int Pid, string Name, string MemoryText);

/// <summary>一张 GPU 的卡片行:每张卡自带利用率与显存的历史曲线。</summary>
public sealed class GpuCardRow(int index, string name) : ReactiveObject
{
    /// <summary>GPU 序号。</summary>
    public int Index { get; } = index;

    /// <summary>显示名(如 “GPU 0”);占位行会改写成 “—”。</summary>
    public string Label
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = $"GPU {index}";

    /// <summary>型号名。</summary>
    public string Name
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = name;

    /// <summary>厂商(决定哪些指标注定拿不到)。</summary>
    public GpuVendor Vendor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该卡是否提供计算利用率;Intel 核显没有,界面据此隐藏利用率曲线。</summary>
    public bool HasUtil
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>SM 利用率(0-100)。</summary>
    public double UtilPercent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>SM 利用率文本。</summary>
    public string UtilText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>显存使用率(0-100)。</summary>
    public double MemPercent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>显存 “已用 / 总量” 文本。</summary>
    public string MemText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>显存使用率文本。</summary>
    public string MemPercentText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>温度文本。</summary>
    public string TempText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>温度是否进入警告区间(&gt;70 °C)。</summary>
    public bool TempWarn
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>温度是否进入危险区间(&gt;80 °C)。</summary>
    public bool TempCrit
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“当前功耗 / 上限” 文本。</summary>
    public string PowerText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>是否为当前选中的卡(右侧大图展示它)。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>SM 利用率历史。</summary>
    public MetricHistory UtilHistory { get; } = new();

    /// <summary>显存占用历史(单位:字节)。</summary>
    public MetricHistory MemHistory { get; } = new();

    /// <summary>显存带宽利用率历史。</summary>
    public MetricHistory MemBandwidthHistory { get; } = new();

    /// <summary>显存总量(字节),用作显存曲线的 Y 轴上限。</summary>
    public double MemTotalBytes
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>驱动/时钟/功耗等明细行(“键 = 值” 直接展示)。</summary>
    public IReadOnlyList<KeyValueRow> Details
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>曲线版本号,每次采样后自增以触发图表重绘。</summary>
    public int Revision
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>一块物理磁盘的列表行:自带读写速率历史。</summary>
public sealed class DiskDeviceRow(string name) : ReactiveObject
{
    /// <summary>块设备名(如 nvme0n1)。</summary>
    public string Name { get; } = name;

    /// <summary>厂商型号与接口类型。</summary>
    public string Model
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>容量 “已用 / 总量” 文本(取该盘上各分区的合计)。</summary>
    public string CapacityText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>容量使用率(0-100)。</summary>
    public double UsedPercent
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            // 派生的告警标记必须跟着一起通知,否则占用条变了色、读数还留在原来的颜色。
            this.RaisePropertyChanged(nameof(IsWarn));
            this.RaisePropertyChanged(nameof(IsCrit));
        }
    }

    /// <summary>容量使用率文本。</summary>
    public string UsedPercentText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>使用率是否进入警告区(&gt;70%),与占用条的配色口径一致。</summary>
    public bool IsWarn => UsedPercent is > MeterThresholds.Warn and <= MeterThresholds.Crit;

    /// <inheritdoc cref="IsWarn" />
    public bool IsCrit => UsedPercent > MeterThresholds.Crit;

    /// <summary>活动时间占比文本。</summary>
    public string ActivityText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>是否为当前选中的盘。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>读取速率历史(字节/秒)。</summary>
    public MetricHistory ReadHistory { get; } = new();

    /// <summary>写入速率历史(字节/秒)。</summary>
    public MetricHistory WriteHistory { get; } = new();

    /// <summary>活动时间历史(0-100)。</summary>
    public MetricHistory BusyHistory { get; } = new();

    /// <summary>曲线版本号。</summary>
    public int Revision
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>一张网卡的卡片行:自带上下行速率历史。</summary>
public sealed class NicRow(string name) : ReactiveObject
{
    /// <summary>接口名。</summary>
    public string Name { get; } = name;

    /// <summary>IPv4 地址(含掩码位)。</summary>
    public string IpAddress
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>链路速率文本。</summary>
    public string LinkText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>链路状态文本。</summary>
    public string StateText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>链路是否已连通(决定状态点颜色)。</summary>
    public bool IsUp
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>下行速率文本。</summary>
    public string RxText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>上行速率文本。</summary>
    public string TxText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "--";

    /// <summary>是否为当前选中的网卡。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>下行速率历史(字节/秒)。</summary>
    public MetricHistory RxHistory { get; } = new();

    /// <summary>上行速率历史(字节/秒)。</summary>
    public MetricHistory TxHistory { get; } = new();

    /// <summary>MAC / MTU / 累计收发等明细行。</summary>
    public IReadOnlyList<KeyValueRow> Details
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>曲线版本号。</summary>
    public int Revision
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>连接占用 Top 中的一行。</summary>
/// <param name="Peer">对端地址:端口。</param>
/// <param name="Process">进程名;非 root 时多数连接取不到,显示占位符。</param>
/// <param name="RxText">下行速率文本。</param>
/// <param name="TxText">上行速率文本。</param>
public sealed record ConnectionRow(string Peer, string Process, string RxText, string TxText);

/// <summary>逻辑处理器列表视图中的一行。</summary>
/// <param name="Label">核心标签(如 CPU12)。</param>
/// <param name="Percent">当前占用率(0-100),驱动占用条与阈值着色。</param>
/// <param name="PercentText">占用率文本。</param>
public sealed record CoreRow(string Label, double Percent, string PercentText);

/// <summary>“键 — 值” 明细行(CPU / GPU / 网卡详情等处复用)。</summary>
/// <param name="Key">左侧标签。</param>
/// <param name="Value">右侧数值。</param>
public sealed record KeyValueRow(string Key, string Value);

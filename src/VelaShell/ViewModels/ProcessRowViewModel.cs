using System.Globalization;
using ReactiveUI;
using VelaShell.Core.Processes;

namespace VelaShell.ViewModels;

/// <summary>任务管理器列表中的一行,对应远端的一个进程。</summary>
/// <remarks>
/// 行对象按 PID 复用而不是每轮重建:刷新时只更新字段,列表选中项、右键菜单目标
/// 与滚动位置才不会每秒被打断一次。
/// </remarks>
public sealed class ProcessRowViewModel(RemoteProcessInfo process) : ReactiveObject
{
    /// <summary>进程号。</summary>
    public int Pid { get; } = process.Pid;

    /// <summary>父进程号(结束进程树时用来找子孙)。</summary>
    public int ParentPid { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.ParentPid;

    /// <summary>可执行名(任务管理器的"名称"列)。</summary>
    public string Name { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.Name;

    /// <summary>属主用户名。</summary>
    public string User { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.User;

    /// <summary>完整命令行。</summary>
    public string CommandLine { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.CommandLine;

    /// <summary>ps 的 STAT 原文。</summary>
    public string State { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.State;

    /// <summary>线程数。</summary>
    public int Threads { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.Threads;

    /// <summary>瞬时 CPU 占用率(0-100,已按核心数归一)。</summary>
    public double CpuPercent { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.CpuPercent;

    /// <summary>常驻内存(字节)。</summary>
    public long MemoryBytes { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.MemoryBytes;

    /// <summary>常驻内存占比(0-100)。</summary>
    public double MemoryPercent { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.MemoryPercent;

    /// <summary>已运行的墙钟秒数。</summary>
    public long ElapsedSeconds { get; private set => this.RaiseAndSetIfChanged(ref field, value); } =
        process.ElapsedSeconds;

    /// <summary>是否为内核线程(默认不显示,与任务管理器不列内核对象一致)。</summary>
    public bool IsKernelThread { get; } = process.IsKernelThread;

    /// <summary>树形视图中的层级,0 为根。平铺视图下恒为 0。</summary>
    public int Depth
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IndentWidth));
        }
    }

    /// <summary>树形视图中本行是否有子进程(决定展开箭头是否出现)。</summary>
    public bool HasChildren
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 树形视图中本行是否展开。默认展开,与 htop 的树模式一致;状态挂在行对象上,
    /// 而行对象按 PID 复用,所以折叠状态能跨刷新保留。
    /// </summary>
    public bool IsExpanded
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ExpanderGlyph));
        }
    } = true;

    /// <summary>名称列左侧的缩进宽度,由层级换算。</summary>
    public double IndentWidth => Depth * 14.0;

    /// <summary>展开箭头的字形。</summary>
    public string ExpanderGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>CPU 列显示文本,一位小数。</summary>
    public string CpuText => CpuPercent.ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>内存列显示文本。</summary>
    public string MemoryText => FormatMemory(MemoryBytes);

    /// <summary>运行时长列显示文本(D天 HH:MM:SS)。</summary>
    public string ElapsedText => FormatElapsed(ElapsedSeconds);

    /// <summary>进程号列显示文本。</summary>
    public string PidText => Pid.ToString(CultureInfo.CurrentCulture);

    /// <summary>用新一轮采样更新本行的可变字段。</summary>
    /// <param name="sample">同一 PID 的最新样本。</param>
    public void Update(RemoteProcessInfo sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ParentPid = sample.ParentPid;
        Name = sample.Name;
        User = sample.User;
        CommandLine = sample.CommandLine;
        State = sample.State;
        Threads = sample.Threads;
        CpuPercent = sample.CpuPercent;
        MemoryBytes = sample.MemoryBytes;
        MemoryPercent = sample.MemoryPercent;
        ElapsedSeconds = sample.ElapsedSeconds;
        this.RaisePropertyChanged(nameof(CpuText));
        this.RaisePropertyChanged(nameof(MemoryText));
        this.RaisePropertyChanged(nameof(ElapsedText));
    }

    /// <summary>是否命中搜索词(名称/命令行/用户/PID 任一包含即可)。</summary>
    /// <param name="term">已转小写的搜索词。</param>
    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || User.Contains(term, StringComparison.OrdinalIgnoreCase)
        || CommandLine.Contains(term, StringComparison.OrdinalIgnoreCase)
        || PidText.Contains(term, StringComparison.Ordinal);

    /// <summary>内存按任务管理器口径显示:1 GB 以下用 MB,以上用 GB。</summary>
    private static string FormatMemory(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F1} MB");

    private static string FormatElapsed(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1
                   ? string.Create(
                       CultureInfo.CurrentCulture,
                       $"{(int)span.TotalDays}d {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
                   )
                   : string.Create(
                       CultureInfo.CurrentCulture,
                       $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
                   );
    }
}

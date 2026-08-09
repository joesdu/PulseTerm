namespace VelaShell.Core.Processes;

/// <summary>远端主机上的一个进程在某一时刻的样本(任务管理器一行)。</summary>
public sealed record RemoteProcessInfo
{
    /// <summary>进程号。</summary>
    public int Pid { get; init; }

    /// <summary>父进程号;init 进程与内核线程的父为 0 或 1。</summary>
    public int ParentPid { get; init; }

    /// <summary>进程属主的用户名(取不到时为 uid 的字符串形式)。</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>可执行文件名(不含路径与参数),即任务管理器的"名称"列。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>完整命令行(含参数);内核线程为方括号包裹的名字。</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>ps 的 STAT 字段原文(R/S/D/Z/T 加修饰符)。</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>线程数(nlwp)。</summary>
    public int Threads { get; init; }

    /// <summary>常驻内存 RSS(字节)。</summary>
    public long MemoryBytes { get; init; }

    /// <summary>常驻内存占物理内存总量的百分比(0-100),由远端 ps 直接给出。</summary>
    public double MemoryPercent { get; init; }

    /// <summary>进程已运行的墙钟秒数。</summary>
    public long ElapsedSeconds { get; init; }

    /// <summary>
    /// 进程累计占用的 CPU 时钟滴答(utime + stime)。这是原始累计量,
    /// 采集器用相邻两次快照的差分算出瞬时 <see cref="CpuPercent" />。
    /// </summary>
    public long CpuTicks { get; init; }

    /// <summary>
    /// 瞬时 CPU 占用率(0-100,已按核心数归一,与 Windows 任务管理器同口径:
    /// 满载全部核心才是 100%,而非 top 的每核 100%)。首次快照无差分基准时为 0。
    /// </summary>
    public double CpuPercent { get; set; }

    /// <summary>是否为内核线程(命令行被方括号包裹,无用户态可执行映像)。</summary>
    public bool IsKernelThread =>
        CommandLine.StartsWith('[') && CommandLine.EndsWith(']');
}

/// <summary>远端主机上一次进程列表采样的完整结果。</summary>
public sealed class RemoteProcessSnapshot
{
    /// <summary>本次采样到的全部进程。</summary>
    public IReadOnlyList<RemoteProcessInfo> Processes { get; init; } = [];

    /// <summary>逻辑 CPU 核心数;至少为 1。</summary>
    public int CpuCores { get; init; } = 1;

    /// <summary>全机 CPU 占用率(0-100);无差分基准时为 0。</summary>
    public double CpuPercent { get; set; }

    /// <summary>物理内存总量(字节)。</summary>
    public long MemTotalBytes { get; init; }

    /// <summary>已用物理内存(字节,total − available 口径)。</summary>
    public long MemUsedBytes { get; init; }

    /// <summary>主机已开机的秒数,用作两次采样之间的服务端时基(不受网络抖动影响)。</summary>
    public double UptimeSeconds { get; init; }

    /// <summary>每秒的时钟滴答数(getconf CLK_TCK,通常为 100)。</summary>
    public long ClockTicksPerSecond { get; init; } = 100;

    /// <summary>/proc/stat 聚合行的累计总 jiffies,供采集器差分出全机 CPU%。</summary>
    public long CpuTotalJiffies { get; init; }

    /// <summary>/proc/stat 聚合行的累计空闲 jiffies(含 iowait)。</summary>
    public long CpuIdleJiffies { get; init; }

    /// <summary>已用内存占总量的百分比(0-100);总量为 0 时返回 0。</summary>
    public double MemPercent => MemTotalBytes > 0 ? (MemUsedBytes * 100.0) / MemTotalBytes : 0;
}

/// <summary>可向远端进程投递的信号(任务管理器的"结束/强制结束"等动作)。</summary>
public enum ProcessSignal
{
    /// <summary>SIGTERM(15):请求进程自行退出,对应"结束任务"。</summary>
    Terminate,

    /// <summary>SIGKILL(9):内核强制终止,不可捕获,对应"强制结束"。</summary>
    Kill,

    /// <summary>SIGINT(2):等同于在终端按 Ctrl+C。</summary>
    Interrupt,

    /// <summary>SIGHUP(1):挂起信号,多数守护进程用它触发重载配置。</summary>
    Hangup,

    /// <summary>SIGSTOP(19):暂停进程调度。</summary>
    Stop,

    /// <summary>SIGCONT(18):恢复被暂停的进程。</summary>
    Continue
}

/// <summary>一次远端管理动作(kill / renice)的执行结果。</summary>
/// <param name="Success">远端命令退出码是否为 0。</param>
/// <param name="Output">远端命令的合并输出(stdout + stderr),失败时即为原因,如权限不足。</param>
public sealed record RemoteCommandOutcome(bool Success, string Output)
{
    /// <summary>会话不可用(未连接 / 已拆除)时的固定结果。</summary>
    public static RemoteCommandOutcome NoSession { get; } = new(false, string.Empty);
}

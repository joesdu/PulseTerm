using System.Globalization;

namespace VelaShell.Core.Processes;

/// <summary>
/// 远端进程列表探针:一条命令取回任务管理器需要的全部数据,以及把输出解析成快照。
/// 面向 Linux(/proc + procps 的 ps),与 <see cref="Core.Services.SessionMetrics" /> 同样
/// 采用分段标记,任一段探测失败只丢该段,不拖垮整次采样。
/// </summary>
public static class RemoteProcessProbe
{
    /// <summary>信号名到编号的映射(用 kill -N 而非 -NAME,避开 BusyBox 对信号名的支持差异)。</summary>
    private static readonly Dictionary<ProcessSignal, int> SignalNumbers = new()
    {
        [ProcessSignal.Hangup] = 1,
        [ProcessSignal.Interrupt] = 2,
        [ProcessSignal.Kill] = 9,
        [ProcessSignal.Terminate] = 15,
        [ProcessSignal.Continue] = 18,
        [ProcessSignal.Stop] = 19
    };

    /// <summary>动作命令用来回传退出码的标记;远端命令的 stderr 已合并进 stdout。</summary>
    public const string ExitCodeMarker = "__RC__";

    /// <summary>
    /// 一次性进程探测。分段说明:
    /// __N__ 核心数、__K__ 每秒时钟滴答、__B__ 开机秒数(采样时基)、__M__ 内存总量与已用、
    /// __S__ /proc/stat 聚合行、__J__ 每进程累计 CPU 滴答、__P__ ps 的人类可读字段。
    /// CPU 占用率不取 ps 的 pcpu —— 那是进程整个生命周期的平均值,任务管理器要的是瞬时值,
    /// 只能由 __J__ 的相邻两次差分得到。
    /// </summary>
    public const string ProbeCommand =
        "echo __N__; nproc 2>/dev/null; " +
        "echo __K__; getconf CLK_TCK 2>/dev/null; " +
        "echo __B__; cut -d' ' -f1 /proc/uptime 2>/dev/null; " +
        """echo __M__; awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} END{if(t>0) print t*1024" "(t-a)*1024}' /proc/meminfo 2>/dev/null; """ +
        "echo __S__; grep -m1 '^cpu ' /proc/stat 2>/dev/null; " +
        // 只回传 "pid 累计滴答" 两列:整份 /proc/*/stat 在千进程主机上是几百 KB,
        // 每秒一次会明显吃带宽。正则的 .* 是贪婪的,匹配到最后一个 ") ",
        // 因此进程名里含括号或空格也不会错位。
        """echo __J__; awk '{p=$1; l=$0; sub(/^[0-9]+ \(.*\) /,"",l); split(l,f," "); print p" "f[12]+f[13]}' /proc/[0-9]*/stat 2>/dev/null; """ +
        // args 放最后且不取 comm:comm 允许含空格,夹在定长字段中间会让整行错位;
        // 显示名由 args 的首个 token 取基名得到。
        "echo __P__; ps -eo pid=,ppid=,user:24=,stat=,nlwp=,rss=,pmem=,etimes=,args= 2>/dev/null";

    /// <summary>构造向一组进程投递信号的命令;输出合并 stderr 并在末行回传退出码。</summary>
    /// <param name="pids">目标进程号,必须非空。</param>
    /// <param name="signal">要投递的信号。</param>
    public static string BuildSignalCommand(IReadOnlyList<int> pids, ProcessSignal signal)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Count == 0)
        {
            throw new ArgumentException("至少需要一个进程号。", nameof(pids));
        }
        string targets = string.Join(' ', pids.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
        return $"kill -{SignalNumbers[signal]} {targets} 2>&1; echo {ExitCodeMarker}$?";
    }

    /// <summary>构造调整进程 nice 值的命令(任务管理器的"设置优先级")。</summary>
    /// <param name="pid">目标进程号。</param>
    /// <param name="niceness">目标 nice 值,-20(最高优先级)到 19(最低)。</param>
    public static string BuildReniceCommand(int pid, int niceness)
    {
        int clamped = Math.Clamp(niceness, -20, 19);
        return $"renice -n {clamped.ToString(CultureInfo.InvariantCulture)} -p {pid.ToString(CultureInfo.InvariantCulture)} 2>&1; echo {ExitCodeMarker}$?";
    }

    /// <summary>解析动作命令的输出,拆出退出码与用户可读的消息。</summary>
    public static RemoteCommandOutcome ParseOutcome(string? output)
    {
        if (output is null)
        {
            return new(false, string.Empty);
        }
        int marker = output.LastIndexOf(ExitCodeMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            // 没有标记说明命令根本没跑完(通道中断等),按失败处理并原样回显。
            return new(false, output.Trim());
        }
        string code = output[(marker + ExitCodeMarker.Length)..].Trim();
        string message = output[..marker].Trim();
        bool success = int.TryParse(code, CultureInfo.InvariantCulture, out int rc) && rc == 0;
        return new(success, message);
    }

    /// <summary>
    /// 解析 <see cref="ProbeCommand" /> 的分段输出。输出为空或不含进程段(如远端不是 Linux)时返回 null。
    /// </summary>
    public static RemoteProcessSnapshot? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        string text = output;
        int cores = int.TryParse(Section("__N__"), CultureInfo.InvariantCulture, out int n)
                        ? Math.Max(1, n)
                        : 1;
        long clockTicks = long.TryParse(Section("__K__"), CultureInfo.InvariantCulture, out long k) && k > 0
                              ? k
                              : 100;
        _ = double.TryParse(Section("__B__"), CultureInfo.InvariantCulture, out double uptime);

        long memTotal = 0, memUsed = 0;
        string[] memParts = Section("__M__").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (memParts.Length >= 2)
        {
            _ = long.TryParse(memParts[0], CultureInfo.InvariantCulture, out memTotal);
            _ = long.TryParse(memParts[1], CultureInfo.InvariantCulture, out memUsed);
        }

        (long cpuTotal, long cpuIdle) = ParseCpuLine(Section("__S__"));
        Dictionary<int, long> ticksByPid = ParseTicks(Section("__J__"));
        List<RemoteProcessInfo> processes = ParseProcesses(Section("__P__"), ticksByPid);
        if (processes.Count == 0)
        {
            return null;
        }
        return new RemoteProcessSnapshot
        {
            Processes = processes,
            CpuCores = cores,
            ClockTicksPerSecond = clockTicks,
            UptimeSeconds = uptime,
            MemTotalBytes = memTotal,
            MemUsedBytes = memUsed,
            CpuTotalJiffies = cpuTotal,
            CpuIdleJiffies = cpuIdle
        };

        // 取某个标记与下一个标记之间的内容;标记缺失时返回空串(该段探测失败)。
        string Section(string marker)
        {
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }
            start += marker.Length;
            int end = text.IndexOf("\n__", start, StringComparison.Ordinal);
            return (end < 0 ? text[start..] : text[start..end]).Trim();
        }
    }

    /// <summary>解析 /proc/stat 的 "cpu ..." 聚合行,返回累计总量与空闲量(空闲含 iowait)。</summary>
    private static (long Total, long Idle) ParseCpuLine(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not ["cpu", _, _, _, _, ..])
        {
            return (0, 0);
        }
        long total = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i], CultureInfo.InvariantCulture, out long value))
            {
                total += value;
            }
        }
        _ = long.TryParse(parts[4], CultureInfo.InvariantCulture, out long idle);
        long iowait = 0;
        if (parts.Length > 5)
        {
            _ = long.TryParse(parts[5], CultureInfo.InvariantCulture, out iowait);
        }
        return (total, idle + iowait);
    }

    private static Dictionary<int, long> ParseTicks(string section)
    {
        Dictionary<int, long> ticks = [];
        foreach (string line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long value))
            {
                ticks[pid] = value;
            }
        }
        return ticks;
    }

    /// <summary>
    /// 解析 ps 段。前 8 列是无空格的定长字段,第 9 列起全部是命令行原文(允许含空格),
    /// 因此手工切词而不是 Split —— Split 的 count 语义会先按分隔符切再去空,拿不到干净的余部。
    /// </summary>
    private static List<RemoteProcessInfo> ParseProcesses(string section, Dictionary<int, long> ticksByPid)
    {
        List<RemoteProcessInfo> processes = [];
        foreach (string raw in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd('\r');
            string[] fields = new string[8];
            int cursor = 0;
            bool malformed = false;
            for (int i = 0; i < 8; i++)
            {
                while (cursor < line.Length && line[cursor] == ' ')
                {
                    cursor++;
                }
                int start = cursor;
                while (cursor < line.Length && line[cursor] != ' ')
                {
                    cursor++;
                }
                if (cursor == start)
                {
                    malformed = true;
                    break;
                }
                fields[i] = line[start..cursor];
            }
            if (malformed || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }
            string commandLine = line[Math.Min(cursor, line.Length)..].Trim();
            _ = int.TryParse(fields[1], CultureInfo.InvariantCulture, out int ppid);
            _ = int.TryParse(fields[4], CultureInfo.InvariantCulture, out int threads);
            _ = long.TryParse(fields[5], CultureInfo.InvariantCulture, out long rssKb);
            _ = double.TryParse(fields[6], CultureInfo.InvariantCulture, out double memPercent);
            _ = long.TryParse(fields[7], CultureInfo.InvariantCulture, out long elapsed);
            processes.Add(new()
            {
                Pid = pid,
                ParentPid = ppid,
                User = fields[2],
                State = fields[3],
                Threads = threads,
                MemoryBytes = rssKb * 1024,
                MemoryPercent = memPercent,
                ElapsedSeconds = elapsed,
                CommandLine = commandLine,
                Name = DisplayName(commandLine),
                CpuTicks = ticksByPid.TryGetValue(pid, out long t) ? t : 0
            });
        }
        return processes;
    }

    /// <summary>从命令行取显示名:首个 token 的基名。内核线程的 [xxx] 原样保留。</summary>
    private static string DisplayName(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return string.Empty;
        }
        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        string first = space < 0 ? commandLine : commandLine[..space];
        int slash = first.LastIndexOf('/');
        return slash >= 0 && slash < first.Length - 1 ? first[(slash + 1)..] : first;
    }
}

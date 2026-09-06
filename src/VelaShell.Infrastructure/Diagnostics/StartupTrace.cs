using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 冷启动打点:记录从进程创建到首帧之间每个关键节点的耗时。
/// </summary>
/// <remarks>
/// <para>
/// 启动优化此前一直是凭感觉在猜 —— 「数据库打开慢」「更新收尾在扫目录」都说得头头是道,
/// 但没有一个数字。先量再改:这里在启动链路上打几个点,把每段耗时写进诊断日志
/// (<see cref="DiagnosticLog" />),于是每次崩溃报告和用户日志里都自带一份启动画像。
/// </para>
/// <para>
/// <b>基准取的是进程创建时刻</b>而不是 <c>Main</c> 的第一行:运行时初始化、程序集加载与 JIT
/// 本身就占冷启动的一大块,把它们排除在外量出来的数会好看,但没用。
/// <c>Process.StartTime</c> 拿不到时(受限环境)退回本类型第一次被触碰的时刻,
/// 那时算出来的是相对值,首个打点会显示为 0 —— 日志里据此就能认出来。
/// </para>
/// <para>
/// <c>VELASHELL_STARTUP_TRACE=1</c> 时额外打到控制台,方便反复启动做 A/B 对比,
/// 不必每次去翻日志文件。全部方法不抛异常。
/// </para>
/// </remarks>
public static class StartupTrace
{
    /// <summary>设为 <c>1</c> 时把打点同时打到控制台。</summary>
    public const string VerboseEnvironmentVariable = "VELASHELL_STARTUP_TRACE";

    private static readonly long _originTimestamp = ResolveOrigin();
    private static readonly ConcurrentQueue<(string Name, TimeSpan At)> _marks = new();
    private static int _summaryWritten;

    /// <summary>是否把打点同时打到控制台。</summary>
    public static bool IsVerbose { get; } =
        Environment.GetEnvironmentVariable(VerboseEnvironmentVariable) == "1";

    /// <summary>是否拿到了真实的进程创建时刻(否则基准是本类型首次被触碰的时刻)。</summary>
    public static bool HasProcessOrigin { get; private set; }

    /// <summary>已记录的打点,按记录顺序。</summary>
    public static IReadOnlyList<(string Name, TimeSpan At)> Marks => [.. _marks];

    /// <summary>自基准时刻起已过去的时间。</summary>
    public static TimeSpan Elapsed => Stopwatch.GetElapsedTime(_originTimestamp);

    /// <summary>
    /// 记一个打点。
    /// </summary>
    /// <param name="name">节点名(如 <c>Main</c>、<c>DI</c>、<c>FirstFrame</c>)。</param>
    public static void Mark(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        try
        {
            TimeSpan at = Elapsed;
            _marks.Enqueue((name, at));
            if (IsVerbose)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[startup] {at.TotalMilliseconds,8:F1} ms  {name}"));
            }
        }
        catch
        {
            // 打点自己绝不能影响启动。
        }
    }

    /// <summary>
    /// 把整份打点写进诊断日志,只写一次(首帧到达时调用)。
    /// </summary>
    /// <remarks>
    /// 只写一次是因为「首帧」这个信号可能来自多处(窗口 Opened、第一次渲染),
    /// 重复写会让日志里出现几份几乎一样的表,反而难读。
    /// </remarks>
    public static void WriteSummaryOnce()
    {
        if (Interlocked.Exchange(ref _summaryWritten, 1) != 0)
        {
            return;
        }
        try
        {
            Trace.WriteLine(Format());
        }
        catch
        {
            // 同上:诊断设施不该成为故障源。
        }
    }

    /// <summary>把打点排成一张「节点 / 累计 / 本段」的表。</summary>
    /// <returns>多行文本;没有打点时是一行说明。</returns>
    public static string Format()
    {
        (string Name, TimeSpan At)[] marks = [.. _marks];
        if (marks.Length == 0)
        {
            return "[Startup] no marks recorded";
        }
        // 「本段」比「累计」有用得多:一眼看出时间花在哪一步,而不是花到哪一刻。
        System.Text.StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"[Startup] timeline (origin={(HasProcessOrigin ? "process start" : "first touch")}):");
        TimeSpan previous = TimeSpan.Zero;
        foreach ((string name, TimeSpan at) in marks)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"  {at.TotalMilliseconds,8:F1} ms  (+{(at - previous).TotalMilliseconds,7:F1})  {name}");
            previous = at;
        }
        return text.ToString();
    }

    /// <summary>基准时间戳:进程创建时刻换算成 <c>Stopwatch</c> 刻度。</summary>
    private static long ResolveOrigin()
    {
        long now = Stopwatch.GetTimestamp();
        try
        {
            using Process self = Process.GetCurrentProcess();
            TimeSpan since = DateTime.Now - self.StartTime;
            // 负值或荒诞的大值说明时钟被调过(或者容器里 StartTime 不可信),不如不用。
            if (since > TimeSpan.Zero && since < TimeSpan.FromMinutes(10))
            {
                HasProcessOrigin = true;
                return now - (long)(since.TotalSeconds * Stopwatch.Frequency);
            }
        }
        catch
        {
            // 受限环境下拿不到进程信息:退回相对基准。
        }
        return now;
    }
}

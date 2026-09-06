using System.Diagnostics;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Infrastructure.Tests.Diagnostics;

/// <summary>
/// 冷启动打点。
/// </summary>
/// <remarks>
/// 打点是启动优化的量尺,所以它自己得先立得住:基准要合理(含运行时初始化,不是从
/// <c>Main</c> 第一行算起)、顺序不能乱、表要读得懂,而且任何一步出岔子都不能把启动带崩。
/// </remarks>
[TestClass]
public sealed class StartupTraceTests
{
    /// <summary>
    /// 打点按记录顺序排,时刻单调不减。
    /// </summary>
    /// <remarks>
    /// <see cref="StartupTrace" /> 是进程级静态的(它量的就是这一个进程的启动),所以这里不能
    /// 假设自己是第一个打点的人 —— 只断言"我加进去的这几个,彼此的相对关系是对的"。
    /// </remarks>
    [TestMethod]
    public void MarksKeepTheirOrderAndAdvance()
    {
        string tag = $"test-{Guid.NewGuid():N}";
        StartupTrace.Mark($"{tag}-a");
        StartupTrace.Mark($"{tag}-b");
        StartupTrace.Mark($"{tag}-c");

        (string Name, TimeSpan At)[] mine =
            [.. StartupTrace.Marks.Where(m => m.Name.StartsWith(tag, StringComparison.Ordinal))];

        Assert.AreSequenceEqual(new[] { $"{tag}-a", $"{tag}-b", $"{tag}-c" }, mine.Select(m => m.Name).ToArray());
        Assert.IsTrue(mine[0].At <= mine[1].At && mine[1].At <= mine[2].At,
            $"打点时刻应单调不减,实际:{string.Join(", ", mine.Select(m => m.At.TotalMilliseconds))}");
    }

    [TestMethod]
    public void TheBaselineCountsRuntimeStartupNotJustMain()
    {
        // 基准若取 Main 的第一行,量出来的数会好看,但会把运行时初始化、程序集加载与 JIT
        // 这一大块排除在外 —— 那正是冷启动里最该看见的部分。
        StartupTrace.Mark($"baseline-{Guid.NewGuid():N}");

        Assert.IsTrue(StartupTrace.HasProcessOrigin,
            "拿不到进程创建时刻。受限环境下这是允许的退化,但在测试机上应当能拿到 —— "
            + "拿不到说明 ResolveOrigin 的判定过严。");
        Assert.IsGreaterThan(TimeSpan.Zero, StartupTrace.Elapsed);
    }

    [TestMethod]
    public void EmptyOrOddNamesAreIgnoredRatherThanThrowing()
    {
        // 诊断设施把应用带崩是最糟糕的结局:宁可少一个点。
        int before = StartupTrace.Marks.Count;

        StartupTrace.Mark(null!);
        StartupTrace.Mark("");
        StartupTrace.Mark("   ");

        Assert.AreEqual(before, StartupTrace.Marks.Count);
    }

    [TestMethod]
    public void TheTableShowsBothTheRunningTotalAndEachLeg()
    {
        // 「本段」比「累计」有用得多:一眼看出时间花在哪一步,而不是花到哪一刻。
        string tag = $"fmt-{Guid.NewGuid():N}";
        StartupTrace.Mark(tag);

        string table = StartupTrace.Format();

        StringAssert.Contains(table, tag);
        StringAssert.Contains(table, "ms");
        StringAssert.Contains(table, "(+");
    }

    [TestMethod]
    public void TheSummaryIsWrittenOnlyOnce()
    {
        // 「首帧」这个信号可能来自多处(窗口 Opened、第一次渲染),重复写会让日志里
        // 出现几份几乎一样的表,反而难读。
        CountingTraceListener listener = new();
        Trace.Listeners.Add(listener);
        try
        {
            StartupTrace.WriteSummaryOnce();
            int afterFirst = listener.Count;
            StartupTrace.WriteSummaryOnce();
            StartupTrace.WriteSummaryOnce();

            // 不假设本测试是进程里第一个调用者(静态闸是进程级的):只断言"第一次之后再叫也不写"。
            Assert.IsTrue(afterFirst <= 1, $"第一次调用最多写一份,实际写了 {afterFirst} 份。");
            Assert.AreEqual(afterFirst, listener.Count, "第二次起应当是纯空操作。");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    /// <summary>数出有多少行是启动时间线表头。</summary>
    private sealed class CountingTraceListener : TraceListener
    {
        public int Count { get; private set; }

        public override void Write(string? message) => Tally(message);

        public override void WriteLine(string? message) => Tally(message);

        private void Tally(string? message)
        {
            if (message?.Contains("[Startup] timeline", StringComparison.Ordinal) is true)
            {
                Count++;
            }
        }
    }
}

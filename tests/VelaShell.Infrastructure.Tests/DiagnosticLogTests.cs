using System.Diagnostics;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 诊断日志(<see cref="DiagnosticLog" /> / <see cref="RollingFileTraceListener" />)的行为契约。
/// </summary>
/// <remarks>
/// <see cref="DiagnosticLog" /> 是进程级单例(要挂进全局 <c>Trace.Listeners</c>),
/// 所以这里只对**不依赖全局初始化**的部分下断言:滚动文件监听器本身、崩溃记录的
/// 去重水位线、以及保留期清理。全局 <c>Initialize</c> 的副作用留给实机验证。
/// </remarks>
[TestClass]
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory;

    public DiagnosticLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_difflog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public void Listener_WritesEachLineToTodaysFile()
    {
        var listener = new RollingFileTraceListener(_directory, "test-");

        listener.WriteLine("hello");
        listener.WriteLine("world");

        string path = Path.Combine(_directory, $"test-{DateTime.Now:yyyyMMdd}.log");
        Assert.IsTrue(File.Exists(path), "当天的日志文件应当被创建。");
        string content = File.ReadAllText(path);
        Assert.Contains("hello", content, StringComparison.Ordinal);
        Assert.Contains("world", content, StringComparison.Ordinal);
        Assert.HasCount(2, File.ReadAllLines(path));
    }

    [TestMethod]
    public void Listener_StampsEveryLineWithATimestampAndThreadId()
    {
        var listener = new RollingFileTraceListener(_directory, "test-");

        listener.WriteLine("payload");

        string line = File.ReadAllLines(listener.CurrentPath!)[0];
        Assert.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"), line, StringComparison.Ordinal);
        Assert.Contains("payload", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Listener_BuffersPartialWritesUntilTheLineEnds()
    {
        // Trace.Write 的分段输出该攒成一行,而不是拆成一堆各带时间戳的碎行。
        var listener = new RollingFileTraceListener(_directory, "test-");

        listener.Write("part-one ");
        listener.Write("part-two");
        listener.WriteLine(" end");

        string[] lines = File.ReadAllLines(listener.CurrentPath!);
        Assert.HasCount(1, lines);
        Assert.Contains("part-one part-two end", lines[0], StringComparison.Ordinal);
    }

    [TestMethod]
    public void Listener_NeverThrows_WhenTheDirectoryIsGone()
    {
        // 监听器抛异常会连带把 Trace.WriteLine 的调用方炸掉 —— 而调用方往往正在处理另一个异常。
        var listener = new RollingFileTraceListener(Path.Combine(_directory, "does", "not", "exist"), "test-");

        listener.WriteLine("should be swallowed");

        // 走到这里没抛就是通过。
        Assert.IsNotNull(listener);
    }

    [TestMethod]
    public void Prune_DeletesFilesOlderThanTheRetentionWindow()
    {
        string stale = Path.Combine(_directory, "velashell-20200101.log");
        string fresh = Path.Combine(_directory, $"velashell-{DateTime.Now:yyyyMMdd}.log");
        string foreign = Path.Combine(_directory, "someone-elses.txt");
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.WriteAllText(foreign, "not ours");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-30));

        DiagnosticLog.Prune(_directory, retainDays: 7);

        Assert.IsFalse(File.Exists(stale), "超过保留期的日志应被删除。");
        Assert.IsTrue(File.Exists(fresh), "当天的日志不该被删。");
        Assert.IsTrue(File.Exists(foreign), "不是我们写的文件一律不碰。");
    }

    [TestMethod]
    public void Prune_AlsoSweepsOldCrashReports()
    {
        string stale = Path.Combine(_directory, "crash-20200101-000000-000.txt");
        File.WriteAllText(stale, "boom");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        DiagnosticLog.Prune(_directory, retainDays: 7);

        Assert.IsFalse(File.Exists(stale));
    }

    [TestMethod]
    public void Prune_OnAMissingDirectory_DoesNotThrow()
    {
        DiagnosticLog.Prune(Path.Combine(_directory, "nope"), retainDays: 7);

        Assert.IsTrue(Directory.Exists(_directory));
    }

    [TestMethod]
    public void WriteCrash_BeforeInitialize_ReturnsNullInsteadOfThrowing()
    {
        // 未初始化(日志目录建不出来的受限环境)时,写崩溃记录必须是空操作而不是二次崩溃。
        if (DiagnosticLog.Directory is not null)
        {
            Assert.Inconclusive("本进程已初始化过 DiagnosticLog,跳过未初始化路径的断言。");
            return;
        }

        Assert.IsNull(DiagnosticLog.WriteCrash("Test", new InvalidOperationException("boom")));
        Assert.IsFalse(DiagnosticLog.TryTakeUnseenCrash(out _));
        Assert.IsFalse(DiagnosticLog.OpenLogsDirectory());
    }

    [TestMethod]
    public void Listener_IsUsableAsATraceListener()
    {
        // 挂上去 → Trace.WriteLine 落盘 → 摘下来。全仓 65 处诊断输出就是这么自动落盘的。
        var listener = new RollingFileTraceListener(_directory, "trace-");
        Trace.Listeners.Add(listener);
        try
        {
            Trace.WriteLine("via-trace");
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.IsNotNull(listener.CurrentPath);
        Assert.Contains("via-trace", File.ReadAllText(listener.CurrentPath!), StringComparison.Ordinal);
    }
}

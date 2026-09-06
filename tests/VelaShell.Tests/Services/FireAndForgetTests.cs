using System.Diagnostics;
using System.Text.RegularExpressions;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="FireAndForget" /> 的行为契约,以及"不许再冒出裸 <c>async void</c>"的守门。
/// </summary>
/// <remarks>
/// <c>async void</c> 的问题只有一个但很致命:方法体里抛出的异常没有任何东西承接,
/// 直接成为进程级未处理异常 —— 一个点错的文件对话框能把整个应用带走,
/// 而用户看到的只是"点了一下就闪退"。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class FireAndForgetTests
{
    /// <summary>把一段 <c>Trace</c> 输出收集起来。</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text => _text.ToString();

        public override void Write(string? message) => _text.Append(message);

        public override void WriteLine(string? message) => _text.AppendLine(message);
    }

    private static async Task<string> RunAndCapture(Func<Task> action)
    {
        var capture = new TraceCapture();
        Trace.Listeners.Add(capture);
        try
        {
            FireAndForget.Run(action, "TestSite");
            // async void 的续体在同步上下文上跑;给它几拍把 catch 走完。
            for (int i = 0; i < 50 && capture.Text.Length == 0; i++)
            {
                await Task.Delay(10);
            }
            return capture.Text;
        }
        finally
        {
            Trace.Listeners.Remove(capture);
        }
    }

    [TestMethod]
    public async Task AThrowingActionIsLogged_NotRethrown()
    {
        string log = await RunAndCapture(() => throw new InvalidOperationException("boom"));

        Assert.Contains("FireAndForget", log, StringComparison.Ordinal);
        Assert.Contains("TestSite", log, StringComparison.Ordinal, "日志里要带调用点,否则查不出是哪个按钮。");
        Assert.Contains("boom", log, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AsynchronousFailuresAreLoggedToo()
    {
        string log = await RunAndCapture(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("late-boom");
        });

        Assert.Contains("late-boom", log, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CancellationIsNotLogged()
    {
        // 用户取消是正常事件;记它只会把日志淹掉。
        var capture = new TraceCapture();
        Trace.Listeners.Add(capture);
        try
        {
            FireAndForget.Run(() => throw new OperationCanceledException(), "TestSite");
            await Task.Delay(80);
            Assert.DoesNotContain("TestSite", capture.Text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(capture);
        }
    }

    [TestMethod]
    public async Task ANullActionIsLogged_NotThrown()
    {
        // 这个 helper **自己**绝不能成为异常源。参数校验若放在 try 外面,
        // 在 async void 里同样是进程级未处理异常 —— 写这条用例时它就是这么
        // 把整个测试宿主崩掉的("测试主机进程崩溃")。
        string log = await RunAndCapture(null!);

        Assert.Contains("FireAndForget", log, StringComparison.Ordinal);
        Assert.Contains("TestSite", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 全仓不允许再出现裸的 <c>async void</c> 方法。
    /// </summary>
    /// <remarks>
    /// 白名单只有三处,每一处都在方法体里自带 try/catch:
    /// <list type="bullet">
    /// <item><c>FireAndForget.Run</c> 本身 —— 它就是那个兜底。</item>
    /// <item><c>VelaTerminalControl.OpenLink</c> —— 终端项目在宿主之下,用不了 FireAndForget。</item>
    /// <item><c>OnRemoteClipboardWrite</c> 里投递给 UI 线程的 <c>async void</c> lambda。</item>
    /// </list>
    /// </remarks>
    [TestMethod]
    public void NoBareAsyncVoidRemainsInProductionCode()
    {
        Regex pattern = new(@"\basync\s+void\b", RegexOptions.Compiled);
        string[] allowed =
        [
            Path.Combine("VelaShell", "Services", "FireAndForget.cs"),
            Path.Combine("VelaShell.Terminal", "Rendering", "VelaTerminalControl.cs")
        ];

        string sourceRoot = Path.Combine(RepoRoot(), "src");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || allowed.Any(a => relative.EndsWith(a, StringComparison.Ordinal)))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // 跳过文档注释里提到 async void 的地方。
                if (!lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal) && pattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.IsEmpty(offenders,
            "生产代码里不允许出现裸的 async void —— 它抛出的异常没有任何东西承接,会直接掀翻进程。"
            + $"请改为 FireAndForget.Run(async () => …):{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}

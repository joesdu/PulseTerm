namespace VelaShell.Tests;

/// <summary>
/// <b>守住一个会让整条 headless 用例静默失效的写法。</b>
/// <para>
/// <c>HeadlessUnitTestSession</c> 只有 <c>Dispatch(Action, ct)</c> 与
/// <c>Dispatch&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt;, ct)</c> 两族重载,<b>没有 <c>Func&lt;Task&gt;</c> 那一支</b>。
/// 于是这样写:
/// </para>
/// <code>
/// public Task 某某() =&gt; _session.Dispatch(async () =&gt;
/// {
///     Assert.AreEqual(3, grid.Columns.Count);   // ← 这条断言永远不会让用例变红
/// }, CancellationToken.None);
/// </code>
/// <para>
/// 不返回值的 async lambda 被绑到 <c>Action</c> 上,变成 <b>async void</b>:
/// 断言异常落在调度线程上没人接,而 <c>Dispatch</c> 返回的 <c>Task</c> 早就完成了。
/// <b>编译通过、测试恒绿。</b>
/// </para>
/// <para>
/// 这不是假想的风险 —— 实测发现仓库里 30 处这种调用有 <b>20 处是哑的</b>,
/// 横跨 4 个测试项目(数据库插件 8 条、宿主自己的界面用例 12 条)。
/// 验法很直接:把 <c>Assert.Fail</c> 放在用例第一行,<c>dotnet test</c> 照样报全过。
/// </para>
/// <para>
/// 修法是让 lambda 有返回值(末尾 <c>return true;</c>),这样才会绑到
/// <c>Func&lt;Task&lt;T&gt;&gt;</c>,异常随 <c>Task</c> 传回来。这条用例就是那个修法的看门狗。
/// </para>
/// </summary>
[TestClass]
public sealed class HeadlessDispatchGuardTests
{
    private const string Needle = "Dispatch(async () =>";

    /// <summary>
    /// 第二种写法,失效方式与 <see cref="Needle" /> 一模一样:
    /// <code>
    /// private static void OnUi(Func&lt;Task&gt; body) =&gt;
    ///     _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
    /// </code>
    /// <c>body</c> 是 <c>Func&lt;Task&gt;</c>,而 <c>HeadlessUnitTestSession</c> 没有那一支重载,
    /// 于是绑到 <c>Dispatch&lt;T&gt;(Func&lt;T&gt;, …)</c> 上、T 推成 <c>Task</c> ——
    /// <c>GetResult()</c> 拿回的是**还没跑完的内层 Task**,第一个 <c>await</c> 之后的断言全部被吞。
    /// <para>
    /// <b>判据必须精确到 <c>Func&lt;Task&gt;</c> 这一种类型。</b> 第一版写成"实参位置上是个标识符"
    /// 就报了 6 处假阳性 —— 那些是 <c>OnUi(Action action)</c>,而 <c>Dispatch(Action, ct)</c>
    /// 是**真实存在且工作正常**的重载:同步体、没有 await,什么都不会被吞。
    /// 危险的只有"交出去的委托返回 awaitable、而那个形状没有对应重载"这一种,
    /// 也就是 <c>Func&lt;Task&gt;</c>。<c>Func&lt;Task&lt;T&gt;&gt;</c> 不在其列(它正好有重载),
    /// 正则里 <c>Task&gt;</c> 后面紧跟空白的写法把它天然排除了。
    /// </para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TaskDelegateParam = new(
        @"Func<Task>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// <b>已知欠账,只许变少不许变多。</b>
    /// <para>
    /// 这个文件里的 headless 用例目前仍是哑的。把它们激活过一次,结果不是断言变红,
    /// 而是 <b>6 条直接挂死超时</b> —— 它们在**占着 UI 线程**的同时
    /// <c>await</c> 一个**需要 UI 线程才能完成**的任务(把 <c>CloseSftpDocumentAsync</c>
    /// 扔进 <c>Task.Run</c> 再等它,而那条路要回 UI 线程续跑)。
    /// 即发即忘的旧绑定下,第一个 <c>await</c> 就让出了控制权、用例立刻报通过,
    /// 所以这个死锁从来没暴露过。
    /// </para>
    /// <para>
    /// 修它要重构这几条用例的等待方式,属于宿主侧的活,不该顺手带过 ——
    /// 改错了会掩盖真实的线程行为,而那正是它们要验的东西。
    /// 于是这里把它记成**棘轮**:名单里的可以先欠着,<b>名单之外一处都不许新增</b>。
    /// </para>
    /// </summary>
    private static readonly string[] KnownInert =
    [
        Path.Combine("tests", "VelaShell.Tests", "ViewModels", "StandaloneSftpDocumentBehaviorTests.cs")
    ];

    /// <summary>
    /// 每一处 <c>Dispatch(async () =&gt; {...})</c> 的 lambda 都必须以 <c>return</c> 收尾。
    /// <para>
    /// 扫的是**源码**而不是反射:这个错的本质是"重载决议选错了",
    /// 而选错的结果在运行期与选对完全一样(都返回一个已完成的 Task) —— 运行期看不出来。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Headless用例_Dispatch的lambda必须有返回值否则断言会被吞掉()
    {
        string root = FindSolutionRoot();
        string testsRoot = Path.Combine(root, "tests");
        Assert.IsTrue(Directory.Exists(testsRoot), $"找不到测试目录:{testsRoot}");

        List<string> inert = [];
        int scanned = 0;
        int sites = 0;

        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            // 跳过本文件:上面那个 Needle 常量里就写着这串字面量,不排除的话这条用例会举报自己。
            if (Path.GetFileName(file) == "HeadlessDispatchGuardTests.cs")
            {
                continue;
            }
            scanned++;
            string text = File.ReadAllText(file);

            // 第二种写法:把 Func<Task> 形参直传给 Dispatch。它没有 lambda 体可查,
            // 只能两步认:先找出 Func<Task> 形参名,再看那个名字有没有被直传出去。
            foreach (System.Text.RegularExpressions.Match declaration in TaskDelegateParam.Matches(text))
            {
                string parameter = declaration.Groups["name"].Value;
                if (!text.Contains($".Dispatch({parameter}", StringComparison.Ordinal))
                {
                    continue;
                }
                string relative = Path.GetRelativePath(root, file);
                if (KnownInert.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                int line = text[..declaration.Index].Count(c => c == '\n') + 1;
                inert.Add(
                    $"{relative}:{line}(Func<Task> 直传给 Dispatch,"
                    + $"要包成 async () => {{ await {parameter}(); return true; }})");
            }

            int from = 0;
            while (true)
            {
                int idx = text.IndexOf(Needle, from, StringComparison.Ordinal);
                if (idx < 0)
                {
                    break;
                }
                sites++;
                int end = MatchingBrace(text, text.IndexOf('{', idx + Needle.Length));
                if (end < 0)
                {
                    break;
                }
                if (!TailHasReturn(text[..end]))
                {
                    string relative = Path.GetRelativePath(root, file);
                    int line = text[..idx].Count(c => c == '\n') + 1;
                    if (!KnownInert.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    {
                        inert.Add($"{relative}:{line}");
                    }
                }
                from = end;
            }
        }

        Assert.IsTrue(scanned > 50, $"只扫到 {scanned} 个源文件,路径多半找错了。");
        Assert.IsTrue(sites > 10, $"只找到 {sites} 处 Dispatch 调用,扫描逻辑多半失效了。");
        Assert.AreEqual(
            0, inert.Count,
            "这些 headless 用例的 lambda 没有返回值,会被绑到 Action 上变成 async void —— "
            + $"里面的断言**永远不会让用例变红**。在 lambda 末尾加一句 return true; 即可:{string.Join(", ", inert)}");

        // 棘轮的另一半:欠账清掉之后要把名单也删掉,否则它会变成一张永远没人看的白名单。
        foreach (string known in KnownInert)
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(root, known)),
                $"{known} 已经不在了,请把它从 KnownInert 里删掉。");
        }
    }

    /// <summary>从某个 <c>{</c> 出发找配对的 <c>}</c>;找不到返回 -1。</summary>
    private static int MatchingBrace(string text, int open)
    {
        if (open < 0)
        {
            return -1;
        }
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>lambda 体的末尾几行里有没有 <c>return</c>。</summary>
    /// <remarks>
    /// 只看末尾而不是全文:体中间的 <c>return</c>(提前退出)同样能让重载决议选中
    /// <c>Func&lt;Task&lt;T&gt;&gt;</c>,但那种写法这里也接受 —— 判据是"编译器能不能推出返回类型",
    /// 而不是"最后一行长什么样"。取末尾若干行是为了避开体内嵌套 lambda 的干扰。
    /// </remarks>
    private static bool TailHasReturn(string body)
    {
        string[] lines = [.. body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
        return lines.TakeLast(8).Any(l => l.StartsWith("return ", StringComparison.Ordinal));
    }

    private static string FindSolutionRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("找不到解决方案根目录(祖先目录里没有 VelaShell.slnx)。");
    }
}

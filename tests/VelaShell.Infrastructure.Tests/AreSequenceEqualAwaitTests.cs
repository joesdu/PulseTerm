using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// MSTest 4.4.0 的 <c>Assert.AreSequenceEqual</c> 有一个会误导人的 bug,这里把它钉住。
/// </summary>
/// <remarks>
/// <para>
/// <b>症状</b>:把 <c>await</c> 直接写在实参位置、并且那个 await <b>真的挂起</b>时,
/// 第一个操作数会变成空序列,断言随即以
/// <c>序列长度不同(预期: 0,实际: N)</c> 失败。两边内容其实完全一样。
/// </para>
/// <code>
/// Assert.AreSequenceEqual(payload, await File.ReadAllBytesAsync(path));   // 挂
/// byte[] read = await File.ReadAllBytesAsync(path);
/// Assert.AreSequenceEqual(payload, read);                                 // 过
/// </code>
/// <para>
/// <b>为什么值得单独立一个文件</b>:这个 bug 报出来的是"两个序列不相等",
/// 把人直直地引向被测代码。`FtpFileServiceIntegrationTests` 的三条上传用例就这么红了一阵子,
/// 排查时先去查了 FTP 的上传路径、又插桩排除了落盘竞态,最后才发现文件一直是好的
/// (上传返回后立刻读就是完整的 40005 字节),是断言自己丢了操作数。
/// </para>
/// <para>
/// <b>为什么现在只有一部分调用中招</b>:await 的那个 <c>Task</c> 若已经完成
/// (例如 <c>await tcs.Task</c> 而结果早已 SetResult),await 不会真的挂起,于是碰不到这个 bug。
/// 也就是说没中招的那些是<b>侥幸</b> —— 换台慢机器、或改了那些 Task 的完成时机就会变红。
/// 所以全仓一律把 await 提到局部变量,而不是只修红了的那几条。
/// </para>
/// <para>
/// <b>什么时候可以删掉这个文件</b>:<see cref="Bug_IsStillPresent_WhenAwaitSitsInTheArgumentList" />
/// 是留给未来的引信 —— MSTest 哪天修好了,它会失败,那时就可以把这个文件连同全仓的
/// "先落局部变量"注释一起删掉。
/// </para>
/// </remarks>
[TestClass]
public sealed partial class AreSequenceEqualAwaitTests
{
    /// <summary>本文件自己会在字符串里写出那个模式,扫描时要排除自己。</summary>
    private const string SelfFileName = "AreSequenceEqualAwaitTests.cs";

    /// <summary>
    /// <c>AreSequenceEqual(</c> 到语句末尾的分号之间出现 <c>await</c>。
    /// </summary>
    /// <remarks>
    /// 必须跨行匹配:这个调用常常把实参拆成好几行(仓里就有一处那样写的,只扫单行会漏)。
    /// 非贪婪 + 排除分号,保证不会越过语句边界去够到下一条语句里的 await。
    /// </remarks>
    [GeneratedRegex(@"AreSequenceEqual\([^;]*?\bawait\b", RegexOptions.Singleline)]
    private static partial Regex AwaitInsideAssert { get; }

    private static async Task<(byte[] Payload, string Path)> WriteTempAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 40_000) + "-tail");
        string path = Path.Combine(Path.GetTempPath(), $"vela-seq-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, payload);
        return (payload, path);
    }

    /// <summary>
    /// 引信:MSTest 修好之前,这条"期望它失败"的写法确实会失败;修好之后这条用例就该红,
    /// 提醒把全仓的绕法撤掉。
    /// </summary>
    [TestMethod]
    public async Task Bug_IsStillPresent_WhenAwaitSitsInTheArgumentList()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 40_000) + "-tail");

        bool threw = false;
        try
        {
            // 就是这一句会误报。两边内容一模一样,它却说序列不等。
            Assert.AreSequenceEqual(payload, await SuspendThenReturnAsync(payload));
        }
        catch (AssertFailedException)
        {
            threw = true;
        }

        Assert.IsTrue(
            threw,
            "MSTest 的 Assert.AreSequenceEqual 看起来已经修好了(实参里的 await 不再丢操作数)。"
            + "确认之后,请把本文件删掉,并把全仓那些为绕开它而写的"
            + "「先 await 到局部变量」的注释一并清理。");
    }

    /// <summary>一定会挂起的 await —— 引信要的就是这个。</summary>
    /// <remarks>
    /// 早先这里读的是一个真实临时文件。但 <c>File.ReadAllBytesAsync</c> 命中页缓存时会
    /// **同步**返回,await 不挂起,于是碰不到那个 bug —— 引信就误报"MSTest 修好了"。
    /// 这恰恰是本文件注释里写的那种"侥幸",引信自己却踩了进去(全量跑时红过一次,
    /// 单跑三遍全绿)。<c>Task.Yield</c> 保证让出,与文件系统和机器快慢都无关。
    /// </remarks>
    private static async Task<byte[]> SuspendThenReturnAsync(byte[] payload)
    {
        await Task.Yield();
        return payload;
    }

    /// <summary>绕法本身必须是有效的 —— 否则上面那条引信就失去参照。</summary>
    [TestMethod]
    public async Task Workaround_HoistingTheAwaitIntoALocal_Works()
    {
        (byte[] payload, string path) = await WriteTempAsync();
        try
        {
            byte[] read = await File.ReadAllBytesAsync(path);
            Assert.AreSequenceEqual(payload, read);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 全仓不许再出现「await 写在 <c>AreSequenceEqual</c> 实参里」的写法。
    /// </summary>
    /// <remarks>
    /// 靠人记住是记不住的:这个写法写起来更顺手,而且只有在 await 真的挂起时才炸 ——
    /// 本机绿了推上去别人那儿红。所以直接扫源码。
    /// </remarks>
    [TestMethod]
    public void NoTestSource_PutsAnAwaitInsideAreSequenceEqual()
    {
        string testsRoot = Path.Combine(RepoRoot(), "tests");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == SelfFileName)
            {
                continue;
            }
            string text = File.ReadAllText(file);
            foreach (Match match in AwaitInsideAssert.Matches(text))
            {
                // 匹配可能跨行,行号按匹配起点(也就是 AreSequenceEqual 那一行)算。
                int line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"  {Path.GetRelativePath(RepoRoot(), file)}:{line}");
            }
        }

        Assert.IsEmpty(
            offenders,
            "以下位置把 await 写进了 Assert.AreSequenceEqual 的实参里,MSTest 4.4.0 会丢掉另一个操作数、"
            + "报成「序列不相等」(见本文件的注释)。请先 await 到局部变量再断言:\n"
            + string.Join("\n", offenders));
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

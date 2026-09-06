using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// 批量打印快路径与逐字符路径必须产出一模一样的屏幕。
/// </summary>
/// <remarks>
/// <para>
/// P-08 在 Ground 态加了条快路径:连续的可打印 ASCII 一次交给
/// <see cref="IVtActions.PrintRun" />,不再逐字符走状态机分发。快是快了,但它现在是
/// <b>第二套</b>写屏逻辑 —— 自动换行、待换行标志、行时间戳、宽字符边界这些细节
/// 只要有一处对不齐,表现出来就是"某些输出偶尔错一格",极难从现场倒推。
/// </para>
/// <para>
/// 所以这里不逐条断言行为,而是拿同一段输入喂给两条路径,<b>逐格比较整个缓冲区</b>。
/// 逐字符路径是语义的定义者,快路径必须与它逐字节一致。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("PrintRun")]
public sealed class PrintRunEquivalenceTests
{
    private const char Esc = (char)0x1B;

    /// <summary>逐字符喂:每次 Feed 一个字符,解析器永远凑不齐一段可打印 ASCII。</summary>
    /// <remarks>
    /// 这是让快路径失效的办法 —— 快路径靠"同一次 Parse 里向前扫"才成段,一次一个字符时
    /// 每段长度都是 1,走的就是原来那条逐字符语义。两者的差别正好被这样隔离出来。
    /// </remarks>
    private static TerminalEmulator FeedOneByOne(string text, int columns, bool autoWrap)
    {
        TerminalEmulator emulator = new(columns, 6, TerminalType.XtermColor256, 100);
        emulator.Modes.AutoWrap = autoWrap;
        foreach (char c in text)
        {
            emulator.Feed(Encoding.UTF8.GetBytes(c.ToString()));
        }
        return emulator;
    }

    /// <summary>整段喂:快路径会把连续 ASCII 合成一段。</summary>
    private static TerminalEmulator FeedWhole(string text, int columns, bool autoWrap)
    {
        TerminalEmulator emulator = new(columns, 6, TerminalType.XtermColor256, 100);
        emulator.Modes.AutoWrap = autoWrap;
        emulator.Feed(Encoding.UTF8.GetBytes(text));
        return emulator;
    }

    private static void AssertSameBuffer(string text, int columns, bool autoWrap)
    {
        TerminalEmulator slow = FeedOneByOne(text, columns, autoWrap);
        TerminalEmulator fast = FeedWhole(text, columns, autoWrap);

        // 先确认快路径真的接住了东西。不加这一句的话,快路径万一压根没触发,
        // 下面整套逐格比较就是拿逐字符路径和它自己比 —— 十条用例全部空过。
        Assert.IsGreaterThan(
            1,
            fast.PrintRunCharsForTest,
            "整段喂入时批量打印快路径没有接住成段的字符,这组等价性用例等于没测。");

        Assert.AreEqual(slow.Screen.TotalRows, fast.Screen.TotalRows, $"总行数不一致({text.Length} 字符,{columns} 列)。");
        Assert.AreEqual(slow.Screen.CursorX, fast.Screen.CursorX, "光标列不一致。");
        Assert.AreEqual(slow.Screen.CursorY, fast.Screen.CursorY, "光标行不一致。");

        for (int row = 0; row < slow.Screen.TotalRows; row++)
        {
            TerminalRow a = slow.Screen.ViewLine(row);
            TerminalRow b = fast.Screen.ViewLine(row);
            Assert.AreEqual(a.Wrapped, b.Wrapped, $"第 {row} 行的 wrapped 标志不一致。");
            for (int col = 0; col < columns; col++)
            {
                Assert.AreEqual(a[col], b[col],
                    $"第 {row} 行第 {col} 列不一致:逐字符 = {a[col].Rune}、批量 = {b[col].Rune}。");
            }
        }
    }

    [TestMethod]
    public void APlainLineIsIdentical() =>
        AssertSameBuffer("hello world", 20, autoWrap: true);

    [TestMethod]
    public void ALineThatExactlyFillsTheWidthIsIdentical() =>
        // 恰好写满一行是最容易错的边界:待换行标志置或不置,差一格。
        AssertSameBuffer(new string('x', 20), 20, autoWrap: true);

    [TestMethod]
    public void ALineThatOverflowsIsIdentical() =>
        AssertSameBuffer(new string('x', 45), 20, autoWrap: true);

    [TestMethod]
    public void OverflowWithoutAutoWrapIsIdentical() =>
        // 关掉自动换行后,超出的字符反复覆盖最后一列 —— 批量路径必须逐字复现这个"退化"。
        AssertSameBuffer(new string('x', 45), 20, autoWrap: false);

    [TestMethod]
    public void TextInterleavedWithControlCharactersIsIdentical() =>
        AssertSameBuffer("aaa\r\nbbb\tccc\r\nddd", 20, autoWrap: true);

    [TestMethod]
    public void TextInterleavedWithColourSequencesIsIdentical() =>
        // SGR 会打断快路径的连续段,而且颜色要落在正确的那几格上。
        AssertSameBuffer($"{Esc}[31mred{Esc}[0m plain {Esc}[1;44mbold-on-blue{Esc}[0m tail", 20, autoWrap: true);

    [TestMethod]
    public void TextMixedWithWideCharactersIsIdentical() =>
        // CJK 不在快路径的字符范围里,它会把 ASCII 段切开;切点两侧的宽字符边界最容易错位。
        AssertSameBuffer("abc中文def中文ghi", 12, autoWrap: true);

    [TestMethod]
    public void ScrollingPastTheBottomIsIdentical() =>
        // 写到滚出屏幕:退休行进回滚区(还会被截短),两条路径的回滚内容必须一致。
        AssertSameBuffer(string.Concat(Enumerable.Range(0, 40).Select(i => $"line {i}\r\n")), 20, autoWrap: true);

    [TestMethod]
    public void InsertModeFallsBackAndStaysIdentical() =>
        // 插入模式下每写一格都要挪动整行,批量路径交回逐字符 —— 这条钉住那次退化是对的。
        AssertSameBuffer($"abcdef{Esc}[4hXYZ{Esc}[4l!", 20, autoWrap: true);

    [TestMethod]
    public void DecGraphicsFallsBackAndStaysIdentical() =>
        // 切到 DEC 图形集后 ASCII 要逐个映射成制表符,同样交回逐字符路径。
        AssertSameBuffer($"{Esc}(0lqqqk{Esc}(Bplain", 20, autoWrap: true);
}

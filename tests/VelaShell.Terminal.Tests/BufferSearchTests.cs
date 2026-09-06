using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Search")]
public class BufferSearchTests
{
    private static TerminalEmulator Feed(params string[] lines)
    {
        var e = new TerminalEmulator(80, 24);
        e.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
        return e;
    }

    [TestMethod]
    public void CopyTextTo_MatchesGetText()
    {
        // FindAll 已改为逐行写进复用缓冲(不再每行 GetText() 造 string),
        // 这里锁住这条零分配路径与 GetText 的逐字等价 —— 含尾部裁剪、宽字符与组合标记。
        TerminalEmulator e = Feed(
            "plain ascii",
            "中文宽字符 mixed 混排",
            "é 组合标记",
            "",
            "trailing spaces then nothing");

        Span<char> buffer = stackalloc char[512];
        for (int row = 0; row < e.Screen.TotalRows; row++)
        {
            TerminalRow line = e.Screen.ViewLine(row);
            int written = line.CopyTextTo(buffer);
            Assert.IsGreaterThanOrEqualTo(0, written, $"第 {row} 行放不进 512 字符缓冲。");
            Assert.AreEqual(line.GetText(), new string(buffer[..written]), $"第 {row} 行两条路径产出不一致。");
        }
    }

    [TestMethod]
    public void CopyTextTo_ReturnsNegativeOne_WhenBufferTooSmall()
    {
        TerminalEmulator e = Feed("this line is definitely longer than eight characters");
        Span<char> tooSmall = stackalloc char[8];
        Assert.AreEqual(-1, e.Screen.ViewLine(0).CopyTextTo(tooSmall));
    }

    [TestMethod]
    public void FindAll_HandlesWideCharsAndCombiningMarks()
    {
        // 缓冲按 Columns*2 起步、命中 -1 才扩容,这条覆盖"一行字符数可能超过列数"的扩容分支。
        TerminalEmulator e = Feed("needle 中文中文中文 ééé needle");

        IReadOnlyList<BufferSearchHit> hits = BufferSearch.FindAll(e.Screen, "needle");

        Assert.HasCount(2, hits);
        Assert.AreEqual(0, hits[0].StartCol);
    }

    [TestMethod]
    public void FindAll_FindsHits_CaseInsensitive_WithPositions()
    {
        TerminalEmulator e = Feed("hello world", "no match here", "WORLD of Hello");

        IReadOnlyList<BufferSearchHit> hits = BufferSearch.FindAll(e.Screen, "world");

        Assert.HasCount(2, hits);
        Assert.AreEqual(0, hits[0].Row);
        Assert.AreEqual(6, hits[0].StartCol);
        Assert.AreEqual(2, hits[1].Row);
        Assert.AreEqual(0, hits[1].StartCol);
        Assert.AreEqual(5, hits[1].Length);
    }

    [TestMethod]
    public void FindAll_MultipleHitsPerLine()
    {
        TerminalEmulator e = Feed("ab ab ab");

        IReadOnlyList<BufferSearchHit> hits = BufferSearch.FindAll(e.Screen, "ab");

        Assert.HasCount(3, hits);
        Assert.AreEqual(0, hits[0].StartCol);
        Assert.AreEqual(3, hits[1].StartCol);
        Assert.AreEqual(6, hits[2].StartCol);
    }

    [TestMethod]
    public void FindAll_SearchesScrollback()
    {
        var e = new TerminalEmulator(80, 4); // tiny screen so early lines scroll out
        var sb = new StringBuilder();
        sb.Append("needle-in-history\r\n");
        for (int i = 0; i < 10; i++)
            sb.Append($"filler {i}\r\n");
        e.Feed(Encoding.UTF8.GetBytes(sb.ToString()));

        Assert.IsGreaterThan(0, e.Screen.ScrollbackCount, "line must have scrolled out");
        IReadOnlyList<BufferSearchHit> hits = BufferSearch.FindAll(e.Screen, "needle-in-history");
        Assert.HasCount(1, hits);
        Assert.AreEqual(0, hits[0].Row);
    }

    [TestMethod]
    public void FindAll_EmptyQuery_ReturnsNothing()
    {
        TerminalEmulator e = Feed("anything");
        Assert.IsEmpty(BufferSearch.FindAll(e.Screen, ""));
    }

    // ———————————————————— 命中坐标是屏幕列,不是字符下标 ————————————————————
    //
    // 渲染层拿 StartCol/Length 直接画高亮、拉选区。行文本里的字符下标与屏幕列在三种
    // 情形下会分道扬镳(宽字符、组合标记、代理对),纯 ASCII 上永远看不出来 ——
    // 上面那些用例正好全是 ASCII,所以这个错位一直没被发现。

    /// <summary>宽字符之后的匹配:列 = 字符下标 + 已经过的宽字符个数。</summary>
    [TestMethod]
    public void FindAll_AfterAWideChar_ReportsScreenColumnNotCharIndex()
    {
        TerminalEmulator e = Feed("中a");

        BufferSearchHit hit = BufferSearch.FindAll(e.Screen, "a").Single();

        // "中"占第 0、1 两列,"a" 的字符下标是 1、屏幕列是 2。
        Assert.AreEqual(2, hit.StartCol, "宽字符后面的命中被当成字符下标,高亮整体左移了一列。");
        Assert.AreEqual(1, hit.Length);
    }

    /// <summary>组合标记之后的匹配:列 = 字符下标 - 已经过的组合标记个数。</summary>
    [TestMethod]
    public void FindAll_AfterACombiningMark_ReportsScreenColumnNotCharIndex()
    {
        TerminalEmulator e = Feed("éx"); // e + 组合锐音符,合起来占一列

        BufferSearchHit hit = BufferSearch.FindAll(e.Screen, "x").Single();

        // 行文本是 e、U+0301、x 三个字符,x 的字符下标是 2、屏幕列是 1。
        Assert.AreEqual(1, hit.StartCol, "组合标记被当成占了一列,高亮整体右移了。");
    }

    /// <summary>命中项本身是宽字符时,两列都要盖住,不能只盖前半格。</summary>
    [TestMethod]
    public void FindAll_WideCharHit_SpansBothOfItsColumns()
    {
        TerminalEmulator e = Feed("ab中cd");

        BufferSearchHit hit = BufferSearch.FindAll(e.Screen, "中").Single();

        Assert.AreEqual(2, hit.StartCol);
        Assert.AreEqual(2, hit.Length, "宽字符命中只盖了一列,右半格漏色。");
    }

    /// <summary>跨越宽字符的多字符匹配:长度按列算,不是按字符数。</summary>
    [TestMethod]
    public void FindAll_HitSpanningWideChars_MeasuresLengthInColumns()
    {
        TerminalEmulator e = Feed("x中文y");

        BufferSearchHit hit = BufferSearch.FindAll(e.Screen, "中文").Single();

        Assert.AreEqual(1, hit.StartCol);
        Assert.AreEqual(4, hit.Length, "两个宽字符占四列,按字符数算只有二。");
    }

    /// <summary>行尾宽字符:结束列不能越过行宽,也不能漏掉尾格。</summary>
    [TestMethod]
    public void FindAll_WideCharAtEndOfLine_StaysWithinTheRow()
    {
        var e = new TerminalEmulator(4, 4);
        e.Feed(Encoding.UTF8.GetBytes("ab中")); // 恰好占满 4 列

        BufferSearchHit hit = BufferSearch.FindAll(e.Screen, "中").Single();

        Assert.AreEqual(2, hit.StartCol);
        Assert.AreEqual(2, hit.Length);
        Assert.IsLessThanOrEqualTo(4, hit.StartCol + hit.Length, "选区结束列越过了行宽。");
    }

    /// <summary>之前只断言了第一个命中的列 —— 而错位恰恰只在宽字符之后的那个命中上现形。</summary>
    [TestMethod]
    public void FindAll_SecondHitAfterWideRun_IsAlsoInScreenColumns()
    {
        TerminalEmulator e = Feed("needle 中文中文中文 needle");

        IReadOnlyList<BufferSearchHit> hits = BufferSearch.FindAll(e.Screen, "needle");

        Assert.HasCount(2, hits);
        Assert.AreEqual(0, hits[0].StartCol);
        // "needle"(6) + " "(1) + 六个宽字符(12) + " "(1) = 20 列。
        Assert.AreEqual(20, hits[1].StartCol);
    }
}

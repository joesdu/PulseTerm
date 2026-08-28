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
}

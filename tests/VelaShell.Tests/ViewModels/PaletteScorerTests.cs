using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 命令面板相关度打分:同样能匹配的结果里,谁排前面。
/// </summary>
[TestClass]
[TestCategory("CommandPalette")]
public sealed class PaletteScorerTests
{
    private static int Score(string title, string query) =>
        PaletteScorer.Score(title, hint: null, query, out _);

    [TestMethod]
    public void PrefixBeatsWordStart_BeatsSubstring_BeatsSubsequence()
    {
        // 这条是整套打分的存在理由:同一个查询词能命中一堆条目时,
        // 用户十有八九要的是前缀命中的那条,它却可能排在第七位 —— 体感就是"搜不到"。
        int prefix = Score("Startup Options", "st");
        int wordStart = Score("Session Tree", "st");
        int substring = Score("Restart", "st");
        // 真正的子序列:字符按顺序出现,但既不连续、也不落在单词首字母上。
        int subsequence = Score("Broadcast Input", "ai");

        Assert.IsGreaterThan(wordStart, prefix, "前缀命中应当排在单词首字母命中之前。");
        Assert.IsGreaterThan(substring, wordStart, "单词首字母命中应当排在中间子串命中之前。");
        Assert.IsGreaterThan(subsequence, substring, "中间子串命中应当排在松散子序列之前。");
    }

    [TestMethod]
    public void NonMatchingTitle_ReturnsNoMatch()
    {
        Assert.AreEqual(PaletteScorer.NoMatch, Score("Settings", "zzz"));
    }

    [TestMethod]
    public void EmptyQuery_MatchesEverythingAtTheSameScore()
    {
        Assert.AreEqual(0, Score("Settings", ""));
        Assert.AreEqual(0, Score("Anything", ""));
    }

    [TestMethod]
    public void ShorterTitleWins_AmongPrefixMatches()
    {
        // "Sftp" 该排在 "Sftp 传输队列" 前面 —— 用户敲 sftp 多半就是要那条最短的。
        Assert.IsGreaterThan(Score("Sftp Transfer Queue", "sftp"), Score("Sftp", "sftp"));
    }

    [TestMethod]
    public void EarlierSubstringWins()
    {
        Assert.IsGreaterThan(Score("Very Long Prefix Restart", "st"), Score("Restart", "st"));
    }

    [TestMethod]
    public void MatchingIsCaseInsensitive()
    {
        Assert.AreEqual(Score("Settings", "st"), Score("Settings", "ST"));
        Assert.AreEqual(Score("Settings", "st"), Score("SETTINGS", "st"));
    }

    [TestMethod]
    public void PrefixMatch_HighlightsTheLeadingCharacters()
    {
        PaletteScorer.Score("Settings", null, "set", out (int Start, int Length)[] spans);

        Assert.HasCount(1, spans);
        Assert.AreEqual((0, 3), spans[0]);
    }

    [TestMethod]
    public void WordStartMatch_HighlightsEachInitial()
    {
        // 用 "pm" 而不是 "tr":后者在 "Trace Route" 里其实是**前缀**(Tr),走不到首字母这条路。
        PaletteScorer.Score("Process Manager", null, "pm", out (int Start, int Length)[] spans);

        Assert.HasCount(2, spans);
        Assert.AreEqual(0, spans[0].Start);
        Assert.AreEqual(8, spans[1].Start, "第二个字符应当落在 Manager 的首字母上。");
    }

    [TestMethod]
    public void SubstringMatch_HighlightsTheRun()
    {
        PaletteScorer.Score("Restart", null, "sta", out (int Start, int Length)[] spans);

        Assert.HasCount(1, spans);
        Assert.AreEqual((2, 3), spans[0]);
    }

    [TestMethod]
    public void HintOnlyMatch_StaysInResultsButRanksLast()
    {
        int hintOnly = PaletteScorer.Score("Something Else", "Ctrl+Shift+P", "shift", out _);
        int titleMatch = Score("Shift Rows", "shift");

        Assert.AreNotEqual(PaletteScorer.NoMatch, hintOnly, "提示文本命中的条目仍该留在结果里。");
        Assert.IsGreaterThan(hintOnly, titleMatch, "标题命中永远排在只有提示命中的前面。");
    }

    [TestMethod]
    public void HintOnlyMatch_HighlightsNothingInTheTitle()
    {
        PaletteScorer.Score("Something Else", "Ctrl+Shift+P", "shift", out (int Start, int Length)[] spans);

        Assert.IsEmpty(spans, "命中在提示里,标题不该出现莫名其妙的高亮。");
    }

    [TestMethod]
    public void RecencyBonus_RewardsUseCount_AndRecentUse()
    {
        DateTime now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        int never = PaletteScorer.RecencyBonus(0, null, now);
        int usedOnceLongAgo = PaletteScorer.RecencyBonus(1, now.AddDays(-90), now);
        int usedOnceRecently = PaletteScorer.RecencyBonus(1, now.AddHours(-2), now);
        int usedOften = PaletteScorer.RecencyBonus(20, now.AddHours(-2), now);

        Assert.AreEqual(0, never);
        Assert.IsGreaterThan(never, usedOnceLongAgo);
        Assert.IsGreaterThan(usedOnceLongAgo, usedOnceRecently, "七天内用过再加一档。");
        Assert.IsGreaterThan(usedOnceRecently, usedOften);
    }

    [TestMethod]
    public void RecencyBonus_IsCapped_SoOneHotCommandCannotBuryEverythingElse()
    {
        DateTime now = DateTime.UtcNow;

        int heavy = PaletteScorer.RecencyBonus(500, now, now);
        int moderate = PaletteScorer.RecencyBonus(5, now, now);

        Assert.AreEqual(moderate, heavy, "次数加成必须封顶,否则用了 500 次的命令会永远压住前缀命中。");
    }

    [TestMethod]
    public void RecencyBonus_NeverOutweighsAMatchTierGap()
    {
        // 加成再大也不能把一条子序列命中顶到前缀命中之上 —— 那会让搜索结果显得随机。
        DateTime now = DateTime.UtcNow;
        int maxBonus = PaletteScorer.RecencyBonus(int.MaxValue, now, now);

        int subsequence = Score("Broadcast Input", "ai") + maxBonus;
        int prefix = Score("Startup Options", "st");

        Assert.IsGreaterThan(subsequence, prefix, "档位差必须大于最近使用加成的上限。");
    }
}

namespace VelaShell.ViewModels;

/// <summary>
/// 命令面板的相关度打分:决定同样能匹配的一堆结果里,谁该排在最前面。
/// </summary>
/// <remarks>
/// <para>
/// 原先只有一个布尔的"能不能匹配",排序靠注册顺序 —— 输入 <c>st</c> 会同时命中
/// "Settings""Sftp""Trace Route"等一大串,而用户十有八九要的是前缀命中的那条,
/// 它却可能排在第七位。表现出来就是"搜不到"。
/// </para>
/// <para>
/// 打分**故意**只分四档、可解释,不引入第三方模糊匹配库:档位一眼能对应到
/// "为什么它排前面",出了问题也好调。同档内按命中位置越靠前越高。
/// </para>
/// </remarks>
public static class PaletteScorer
{
    /// <summary>完全不匹配。</summary>
    public const int NoMatch = int.MinValue;

    private const int PrefixBase = 4000;
    private const int WordStartBase = 3000;
    private const int SubstringBase = 2000;
    private const int SubsequenceBase = 1000;
    private const int HintBase = 100;

    /// <summary>
    /// 给一个条目打分,并给出标题里应当高亮的字符区间。
    /// </summary>
    /// <param name="title">条目标题(主要匹配目标)。</param>
    /// <param name="hint">尾部提示(快捷键、"Enter 连接"等),命中只给很低的分。</param>
    /// <param name="query">查询词;空串表示不过滤,统一给 0 分。</param>
    /// <param name="spans">标题里命中的字符区间(起点, 长度),按位置升序。</param>
    /// <returns>分数,越大越靠前;<see cref="NoMatch" /> 表示不匹配。</returns>
    public static int Score(string title, string? hint, string query, out (int Start, int Length)[] spans)
    {
        spans = [];
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }
        title ??= string.Empty;

        // ① 前缀:输入的就是标题开头。最强信号。
        int index = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index == 0)
        {
            spans = [(0, query.Length)];
            // 同为前缀命中时短标题优先("Sftp" 该排在 "Sftp 传输队列" 前面)。
            return PrefixBase - Math.Min(title.Length, 200);
        }

        // ② 单词首字母:"tr" → "Trace Route"。用户敲缩写时要的就是这个。
        if (MatchesWordStarts(title, query, out (int Start, int Length)[] wordSpans))
        {
            spans = wordSpans;
            return WordStartBase - wordSpans[0].Start;
        }

        // ③ 连续子串:命中在标题中间。
        if (index > 0)
        {
            spans = [(index, query.Length)];
            return SubstringBase - Math.Min(index, 200);
        }

        // ④ 子序列:字符按顺序出现但不连续。最弱,但"能找到"总比找不到强。
        if (MatchesSubsequence(title, query, out (int Start, int Length)[] subsequenceSpans))
        {
            spans = subsequenceSpans;
            // 跨度越大越松散,排得越靠后。
            int span = subsequenceSpans[^1].Start - subsequenceSpans[0].Start;
            return SubsequenceBase - Math.Min(span, 500);
        }

        // 标题完全不沾边,但提示文本命中 —— 留在结果里,排到最后。
        if (hint is { Length: > 0 } && hint.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return HintBase;
        }
        return NoMatch;
    }

    /// <summary>
    /// 最近使用加成:用过的排前面,最近用过的再加一档。
    /// </summary>
    /// <param name="useCount">累计使用次数。</param>
    /// <param name="lastUsedUtc">上次使用时间(UTC);从未用过传 null。</param>
    /// <param name="nowUtc">当前时间(UTC)。</param>
    /// <returns>要加到基础分上的加成。</returns>
    public static int RecencyBonus(int useCount, DateTime? lastUsedUtc, DateTime nowUtc)
    {
        // 次数封顶,避免一条用了 500 次的命令永远压住所有其它结果。
        int bonus = Math.Min(useCount, 5) * 20;
        if (lastUsedUtc is { } last && nowUtc - last <= TimeSpan.FromDays(7))
        {
            bonus += 50;
        }
        return bonus;
    }

    /// <summary>查询的每个字符依次落在标题各单词的首字母上。</summary>
    private static bool MatchesWordStarts(string title, string query, out (int Start, int Length)[] spans)
    {
        spans = [];
        List<(int, int)> hits = [];
        int q = 0;
        bool atWordStart = true;
        for (int i = 0; i < title.Length && q < query.Length; i++)
        {
            char c = title[i];
            if (atWordStart && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[q]))
            {
                hits.Add((i, 1));
                q++;
            }
            atWordStart = !char.IsLetterOrDigit(c);
        }
        if (q != query.Length)
        {
            return false;
        }
        spans = [.. hits];
        return true;
    }

    /// <summary>查询的每个字符按顺序出现在标题中(可不连续)。</summary>
    private static bool MatchesSubsequence(string title, string query, out (int Start, int Length)[] spans)
    {
        spans = [];
        List<(int, int)> hits = [];
        int q = 0;
        for (int i = 0; i < title.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(title[i]) == char.ToLowerInvariant(query[q]))
            {
                hits.Add((i, 1));
                q++;
            }
        }
        if (q != query.Length)
        {
            return false;
        }
        spans = [.. hits];
        return true;
    }
}

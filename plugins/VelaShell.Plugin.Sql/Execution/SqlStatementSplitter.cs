namespace VelaShell.Plugin.Sql.Execution;

/// <summary>编辑器里切出来的一条语句。</summary>
/// <param name="Text">语句原文(不含结尾分号)。</param>
/// <param name="StartLine">在整段文本里的起始行(1 起)。</param>
/// <param name="StartColumn">起始列(1 起)。</param>
/// <param name="StartOffset">在整段文本里的起始字符偏移(0 起)。</param>
internal sealed record SqlStatement(string Text, int StartLine, int StartColumn, int StartOffset)
{
    /// <summary>是不是空语句(只有空白与注释)。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// 按分号切句,并记下每条的起始行列。
/// <para>
/// <b>为什么不能简单 <c>Split(';')</c></b>:分号会出现在字符串里(<c>'a;b'</c>)、
/// 注释里(<c>-- 见 §3;§4</c>)、以及方言的美元引用里(PG 的 <c>$$ ... $$</c> 函数体)。
/// 切错的后果不是报错,是**把半条语句发出去** —— 那可能正好是一条能跑通但语义不同的 SQL。
/// </para>
/// <para>
/// <b>起始行列是错误定位算法的地基</b>(设计文档 §7.4):PG 的 <c>Position</c> 相对的是
/// "改写后的、单条"语句,MSSQL 的 <c>LineNumber</c> 相对整批 —— 两者都要靠这里记下的起点
/// 才能换算回用户原文里的位置。
/// </para>
/// </summary>
internal static class SqlStatementSplitter
{
    /// <summary>切句。</summary>
    /// <param name="text">编辑器里的全部文本。</param>
    /// <param name="dialect">方言(决定认哪些定界符)。</param>
    /// <returns>非空语句列表。</returns>
    public static IReadOnlyList<SqlStatement> Split(string text, SqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<SqlStatement> statements = [];
        int line = 1;
        int column = 1;
        int segmentStart = 0;
        int segmentLine = 1;
        int segmentColumn = 1;
        bool segmentStarted = false;

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];

            // ── 注释 ──
            if (c == '-' && Next(text, i) == '-')
            {
                int end = LineEnd(text, i);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            if (c == '/' && Next(text, i) == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            // MySQL 的 # 行注释。
            if (c == '#' && dialect == SqlDialect.MySql)
            {
                int end = LineEnd(text, i);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }

            // ── 字符串与带定界符的标识符 ──
            if (c == '\'')
            {
                int end = SkipQuoted(text, i, '\'');
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            if (c == '"' && dialect is not SqlDialect.MySql)
            {
                // MySQL 默认用反引号,双引号是字符串;其余方言双引号是标识符。
                // (服务端开了 ANSI_QUOTES 时 MySQL 也会变成标识符 —— 那种情况下两种处理都能正确跳过,
                //  因为这里只是"成对跳过",不解释语义。)
                int end = SkipQuoted(text, i, '"');
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            if (c == '"' && dialect == SqlDialect.MySql)
            {
                int end = SkipQuoted(text, i, '"');
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            if (c == '`' && dialect == SqlDialect.MySql)
            {
                int end = SkipQuoted(text, i, '`');
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            if (c == '[' && dialect == SqlDialect.SqlServer)
            {
                int end = text.IndexOf(']', i + 1);
                end = end < 0 ? text.Length : end + 1;
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }
            // PG 的美元引用:$$ … $$ 或 $tag$ … $tag$。函数体里全是分号,不认它就切得稀碎。
            if (c == '$' && dialect == SqlDialect.PostgreSql && TryReadDollarTag(text, i, out string tag))
            {
                int end = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + tag.Length;
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
                Advance(text, i, end, ref line, ref column);
                i = end;
                continue;
            }

            // ── 分号:切 ──
            if (c == ';')
            {
                if (segmentStarted)
                {
                    Emit(statements, text, segmentStart, i, segmentLine, segmentColumn);
                }
                segmentStarted = false;
                Step(c, ref line, ref column);
                i++;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                MarkStart(text, i, ref segmentStart, ref segmentLine, ref segmentColumn, ref segmentStarted, line, column);
            }
            Step(c, ref line, ref column);
            i++;
        }

        if (segmentStarted)
        {
            Emit(statements, text, segmentStart, text.Length, segmentLine, segmentColumn);
        }
        return statements;
    }

    /// <summary>取光标所在的那一条语句(<c>Ctrl+Enter</c> 执行当前语句)。</summary>
    /// <param name="text">全部文本。</param>
    /// <param name="dialect">方言。</param>
    /// <param name="caretOffset">光标字符偏移。</param>
    /// <returns>命中的语句;没有则 <see langword="null" />。</returns>
    public static SqlStatement? StatementAt(string text, SqlDialect dialect, int caretOffset)
    {
        IReadOnlyList<SqlStatement> all = Split(text, dialect);
        // 光标停在语句之间(比如两条之间的空行)时,取**前一条** ——
        // 用户刚敲完一条按 Ctrl+Enter 是最常见的动作,光标就在分号后面。
        SqlStatement? best = null;
        foreach (SqlStatement statement in all)
        {
            if (statement.StartOffset <= caretOffset)
            {
                best = statement;
                continue;
            }
            break;
        }
        return best ?? (all.Count > 0 ? all[0] : null);
    }

    private static void Emit(List<SqlStatement> into, string text, int start, int end, int line, int column)
    {
        string raw = text[start..end].Trim();
        if (raw.Length > 0)
        {
            into.Add(new(raw, line, column, start));
        }
    }

    private static void MarkStart(
        string text, int index,
        ref int segmentStart, ref int segmentLine, ref int segmentColumn, ref bool started,
        int line, int column)
    {
        if (started)
        {
            return;
        }
        _ = text;
        segmentStart = index;
        segmentLine = line;
        segmentColumn = column;
        started = true;
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private static int LineEnd(string text, int i)
    {
        int n = text.IndexOf('\n', i);
        return n < 0 ? text.Length : n;
    }

    /// <summary>跳过一段被 <paramref name="quote" /> 包着的文本;定界符加倍视为转义。</summary>
    private static int SkipQuoted(string text, int i, char quote)
    {
        int j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == '\\' && quote == '\'' && j + 1 < text.Length)
            {
                // 反斜杠转义:MySQL 默认开着,PG 的 E'' 字面量里也有。
                // 认它是保守做法 —— 认错只会让我们多跳一个字符,不认会让引号提前结束。
                j += 2;
                continue;
            }
            if (text[j] == quote)
            {
                if (j + 1 < text.Length && text[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            j++;
        }
        return text.Length;
    }

    private static bool TryReadDollarTag(string text, int i, out string tag)
    {
        tag = "";
        int j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
        {
            j++;
        }
        if (j >= text.Length || text[j] != '$')
        {
            return false;
        }
        tag = text[i..(j + 1)];
        return true;
    }

    private static void Advance(string text, int from, int to, ref int line, ref int column)
    {
        for (int k = from; k < to && k < text.Length; k++)
        {
            Step(text[k], ref line, ref column);
        }
    }

    private static void Step(char c, ref int line, ref int column)
    {
        if (c == '\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column++;
        }
    }
}

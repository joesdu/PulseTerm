using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal;

/// <summary>终端缓冲区内的一次搜索命中,采用绝对行/字符坐标。</summary>
public readonly record struct BufferSearchHit(int Row, int StartCol, int Length);

/// <summary>
/// 对整个终端缓冲区(回滚历史 + 当前屏幕)进行不区分大小写的纯文本搜索,
/// 供终端内搜索栏使用(规范 §5.3)。纯逻辑实现,不依赖任何 UI。
/// </summary>
public static class BufferSearch
{
    /// <summary>
    /// 在整个终端缓冲区(回滚历史 + 当前屏幕)中返回 <paramref name="query" /> 的全部不区分大小写匹配项;
    /// 空查询不会产生任何命中。
    /// </summary>
    /// <param name="screen">要搜索的终端缓冲区。</param>
    /// <param name="query">要搜索的纯文本。</param>
    /// <returns>全部命中,以绝对行/字符坐标表示,按缓冲区顺序排列。</returns>
    public static IReadOnlyList<BufferSearchHit> FindAll(TerminalScreen screen, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }
        var hits = new List<BufferSearchHit>();
        int totalRows = screen.TotalRows;

        // 逐行文本写进一个复用缓冲,而不是每行 GetText() 造一个 string:本方法在搜索框
        // 每次按键时对整个缓冲区(回滚 + 屏幕,默认上限 1 万行)跑一遍,原写法每敲一个
        // 字符就分配上万个 StringBuilder + string(长会话下每次按键约 1.6 MB),
        // 直接表现为搜索框打字卡顿。现在整次搜索只有这一个缓冲。
        char[] buffer = new char[Math.Max(256, screen.Columns * 2)];
        for (int row = 0; row < totalRows; row++)
        {
            TerminalRow line = screen.ViewLine(row);
            int length;
            while ((length = line.CopyTextTo(buffer)) < 0)
            {
                // 组合标记可以让一行的字符数超过列数,故按需扩容(极罕见,只发生一次)。
                buffer = new char[buffer.Length * 2];
            }
            ReadOnlySpan<char> text = buffer.AsSpan(0, length);
            int index = 0;
            while (index < text.Length)
            {
                int found = text[index..].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                found += index;
                hits.Add(new(row, found, query.Length));
                index = found + Math.Max(1, query.Length);
            }
        }
        return hits;
    }
}

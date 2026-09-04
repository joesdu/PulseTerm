namespace VelaShell.Terminal;

/// <summary>终端回滚缓冲区,使用环形缓冲保存已滚出可视区的历史行,并维护视口滚动位置与搜索能力。</summary>
public class ScrollbackBuffer
{
    private readonly TerminalLine[] _buffer;
    private int _head;

    /// <summary>创建回滚缓冲区。</summary>
    /// <param name="maxLines">缓冲区可保存的最大历史行数,必须为正数。</param>
    public ScrollbackBuffer(int maxLines = 10000)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), @"MaxLines must be positive.");
        }
        MaxLines = maxLines;
        _buffer = new TerminalLine[maxLines];
    }

    /// <summary>缓冲区可保存的最大历史行数。</summary>
    public int MaxLines { get; }

    /// <summary>当前已存入回滚缓冲区的历史行数(不含可视区)。</summary>
    public int ScrollbackLineCount { get; private set; }

    /// <summary>当前可视区的行数。</summary>
    public int VisibleRows { get; set; }

    /// <summary>回滚历史行与可视区行之和,即可滚动的总行数。</summary>
    public int TotalLines => ScrollbackLineCount + VisibleRows;

    /// <summary>当前视口顶部对应的绝对行号。</summary>
    public int ViewportRow { get; private set; }

    /// <summary>向回滚缓冲区追加一行;缓冲区满时覆盖最旧的一行。</summary>
    /// <param name="line">要追加的终端行。</param>
    public void AddLine(TerminalLine line)
    {
        _buffer[_head] = line;
        _head = (_head + 1) % MaxLines;
        if (ScrollbackLineCount < MaxLines)
        {
            ScrollbackLineCount++;
        }
    }

    /// <summary>按绝对行号获取回滚缓冲区中的历史行。</summary>
    /// <param name="absoluteRow">从最旧一行起算的绝对行号,取值范围为 [0, ScrollbackLineCount)。</param>
    /// <returns>对应位置的终端行。</returns>
    public TerminalLine GetLine(int absoluteRow)
    {
        if (absoluteRow < 0 || absoluteRow >= ScrollbackLineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteRow));
        }
        int startIndex = (_head - ScrollbackLineCount + MaxLines) % MaxLines;
        int index = (startIndex + absoluteRow) % MaxLines;
        return _buffer[index];
    }

    /// <summary>将视口滚动到指定绝对行号,超出范围时自动夹取到有效区间。</summary>
    /// <param name="absoluteRow">目标绝对行号。</param>
    public void ScrollTo(int absoluteRow) => ViewportRow = Math.Clamp(absoluteRow, 0, Math.Max(0, ScrollbackLineCount));

    /// <summary>向上滚动指定行数(朝更早的历史方向)。</summary>
    /// <param name="lines">要向上滚动的行数。</param>
    public void ScrollUp(int lines) => ScrollTo(ViewportRow - lines);

    /// <summary>向下滚动指定行数(朝更新的内容方向)。</summary>
    /// <param name="lines">要向下滚动的行数。</param>
    public void ScrollDown(int lines) => ScrollTo(ViewportRow + lines);

    /// <summary>在回滚历史行内按序查找指定文本的全部匹配项(区分大小写)。</summary>
    /// <param name="query">要搜索的文本;为空时返回空结果。</param>
    /// <returns>所有匹配项的列表,每项包含所在行、列与长度。</returns>
    public List<SearchMatch> Search(string query)
    {
        var matches = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }
        for (int row = 0; row < ScrollbackLineCount; row++)
        {
            TerminalLine line = GetLine(row);
            int startIndex = 0;
            while (startIndex <= line.Content.Length - query.Length)
            {
                int found = line.Content.IndexOf(query, startIndex, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }
                matches.Add(new()
                {
                    Row = row,
                    Column = found,
                    Length = query.Length
                });
                startIndex = found + 1;
            }
        }
        return matches;
    }

    /// <summary>清空回滚缓冲区并重置行数与视口位置。</summary>
    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        ScrollbackLineCount = 0;
        ViewportRow = 0;
    }
}

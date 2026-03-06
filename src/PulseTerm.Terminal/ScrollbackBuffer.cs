namespace PulseTerm.Terminal;

public class ScrollbackBuffer
{
    private readonly TerminalLine[] _buffer;
    private int _head;
    private int _count;

    public ScrollbackBuffer(int maxLines = 10000)
    {
        if (maxLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLines), "MaxLines must be positive.");

        MaxLines = maxLines;
        _buffer = new TerminalLine[maxLines];
    }

    public int MaxLines { get; }

    public int ScrollbackLineCount => _count;

    public int VisibleRows { get; set; }

    public int TotalLines => _count + VisibleRows;

    public int ViewportRow { get; private set; }

    public void AddLine(TerminalLine line)
    {
        _buffer[_head] = line;
        _head = (_head + 1) % MaxLines;

        if (_count < MaxLines)
            _count++;
    }

    public TerminalLine GetLine(int absoluteRow)
    {
        if (absoluteRow < 0 || absoluteRow >= _count)
            throw new ArgumentOutOfRangeException(nameof(absoluteRow));

        int startIndex = (_head - _count + MaxLines) % MaxLines;
        int index = (startIndex + absoluteRow) % MaxLines;
        return _buffer[index];
    }

    public void ScrollTo(int absoluteRow)
    {
        ViewportRow = Math.Clamp(absoluteRow, 0, Math.Max(0, _count));
    }

    public void ScrollUp(int lines)
    {
        ScrollTo(ViewportRow - lines);
    }

    public void ScrollDown(int lines)
    {
        ScrollTo(ViewportRow + lines);
    }

    public List<SearchMatch> Search(string query)
    {
        var matches = new List<SearchMatch>();

        if (string.IsNullOrEmpty(query))
            return matches;

        for (int row = 0; row < _count; row++)
        {
            var line = GetLine(row);
            int startIndex = 0;

            while (startIndex <= line.Content.Length - query.Length)
            {
                int found = line.Content.IndexOf(query, startIndex, StringComparison.Ordinal);
                if (found < 0)
                    break;

                matches.Add(new SearchMatch
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

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _count = 0;
        ViewportRow = 0;
    }
}

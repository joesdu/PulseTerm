using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端网格中的一行:一个固定长度的 <see cref="TerminalCell" /> 数组,外加一个 "wrapped" 标志,
/// 用于在改变列宽时重新排版软换行行,以及为复制而合并行。
/// </summary>
public sealed class TerminalRow(int columns)
{
    private TerminalCell[] _cells = new TerminalCell[columns];

    /// <summary>当该行由自动换行结束(而非显式换行)时为 true。</summary>
    public bool Wrapped { get; set; }

    /// <summary>
    /// 该行最后收到输出的墙上时钟时间(行号/时间侧栏用)。Null 表示尚未写入过内容
    /// 的空行——侧栏据此对空行不显示时间。行对象在滚动/换行时按引用迁入 scrollback,时间戳随之保留。
    /// </summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>本行的单元格(列)数量。</summary>
    public int Columns => _cells.Length;

    /// <summary>获取或设置指定列索引处的单元格。</summary>
    public TerminalCell this[int col]
    {
        get => _cells[col];
        set => _cells[col] = value;
    }

    /// <summary>返回指定列处单元格的可变引用,用于就地编辑。</summary>
    public ref TerminalCell CellRef(int col) => ref _cells[col];

    /// <summary>整行单元格的只读切片(reflow 等批量拷贝路径用,免去逐格索引)。</summary>
    public ReadOnlySpan<TerminalCell> Span => _cells;

    /// <summary>用给定单元格填满整行,并清除 wrapped 标志与时间戳。</summary>
    public void Fill(in TerminalCell cell)
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = cell;
        }
        Wrapped = false;
        Timestamp = null; // 整行清空(擦除/复用作滚动新行)→ 视为未写入,时间戳作废。
    }

    /// <summary>
    /// 把 <paramref name="start" />..<paramref name="endExclusive" /> 范围内的单元格用给定单元格填充,
    /// 并裁剪到本行边界。若擦完整行已空,时间戳一并作废(与 <see cref="Fill" /> 同一不变量)。
    /// </summary>
    /// <remarks>
    /// 这里必须与 <see cref="Fill" /> 守同一条「空行 = 未写入 = 无时间戳」的规矩:重绘型 shell
    /// (PSReadLine 等)清行用的是 ESC[K(EL 0,擦到行尾)而非 ESC[2K,走的正是这里。少了这一步,
    /// 行被擦空却留着时间戳,侧栏据 Timestamp 认定「有内容」→ 提示符下方的空行凭空显示时间,
    /// 折叠导引线也跟着画过光标位置把光标盖住。
    /// </remarks>
    public void FillRange(int start, int endExclusive, in TerminalCell cell)
    {
        for (int i = Math.Max(0, start); i < Math.Min(_cells.Length, endExclusive); i++)
        {
            _cells[i] = cell;
        }
        if (Timestamp is not null && LastNonBlank() < 0)
        {
            Wrapped = false;
            Timestamp = null;
        }
    }

    /// <summary>
    /// 硬性地增缩到精确宽度。仅用于不适用重新排版的场合(备用屏,其程序在改变列宽时整体重绘)——
    /// 主屏通过 <see cref="TerminalScreen" /> 的重新排版调整大小,以保留内容。
    /// </summary>
    public void Resize(int columns, in TerminalCell blank)
    {
        if (columns == _cells.Length)
        {
            return;
        }
        var next = new TerminalCell[columns];
        int copy = Math.Min(columns, _cells.Length);
        Array.Copy(_cells, next, copy);
        for (int i = copy; i < columns; i++)
        {
            next[i] = blank;
        }
        _cells = next;
    }

    /// <summary>在 <paramref name="col" /> 处删除 <paramref name="count" /> 个单元格,并将尾部左移。</summary>
    public void DeleteCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col + count, _cells, col, _cells.Length - col - count);
        FillRange(_cells.Length - count, _cells.Length, blank);
    }

    /// <summary>在 <paramref name="col" /> 处插入 <paramref name="count" /> 个空白单元格,并将尾部右移。</summary>
    public void InsertCells(int col, int count, in TerminalCell blank)
    {
        if (count <= 0 || col >= _cells.Length)
        {
            return;
        }
        count = Math.Min(count, _cells.Length - col);
        Array.Copy(_cells, col, _cells, col + count, _cells.Length - col - count);
        FillRange(col, col + count, blank);
    }

    /// <summary>最后一个有内容的单元格索引;全空行返回 -1。</summary>
    public int LastNonBlank()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i].Rune != 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 最后一个<b>被占用</b>的单元格索引 —— 有字符的格,或双宽字符的尾格;全空行返回 -1。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LastNonBlank" /> 的区别只在双宽字符的尾格上:尾格自身不承载字形
    /// (<c>Rune == 0</c>),但它是那个宽字符的一半,不能与"从没写过的空格子"混为一谈。
    /// <para>
    /// reflow 的收集步骤靠它区分两种同样 <c>Rune == 0</c> 的格子:
    /// </para>
    /// <list type="bullet">
    /// <item><b>宽字符尾格</b> —— 必须保留,否则前导格会被当成单宽字符,宽字符就散了。</item>
    /// <item><b>换行填充格</b> —— 双宽字符在行尾只剩一列放不下时,自动换行会在那里留下一个
    /// 永远不会被写入的空格子。它不是内容,重排时必须丢掉,否则每经一次 reflow 就在
    /// 断点处凭空多出一个空格(<c>"触发"</c> 变 <c>"触 发"</c>)。</item>
    /// </list>
    /// </remarks>
    public int LastOccupied()
    {
        for (int i = _cells.Length - 1; i >= 0; i--)
        {
            if (_cells[i].Rune != 0 || _cells[i].IsWideTrailing)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>本行截至最后一个非空单元格的文本(尾部空格已裁剪)。</summary>
    public string GetText()
    {
        var sb = new StringBuilder(_cells.Length);
        int lastNonBlank = LastNonBlank();
        for (int i = 0; i <= lastNonBlank; i++)
        {
            _cells[i].AppendText(sb);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把 <see cref="GetText" /> 的内容写进 <paramref name="destination" />,返回写入的字符数;
    /// 空间不足时返回 -1(调用方据此扩容重试,已写入的内容作废)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetText" /> 逐字等价,只是不物化 string。整缓冲区扫描的场合
    /// (<see cref="BufferSearch.FindAll" /> 每次按键要过一遍全部回滚行)用它,把
    /// "每行一个 StringBuilder + 一个 string"降为"每次搜索一个复用缓冲"。
    /// 一致性由 <c>BufferSearchTests.CopyTextTo_MatchesGetText</c> 把守。
    /// </remarks>
    /// <param name="destination">接收行文本的目标缓冲。</param>
    /// <returns>写入的字符数;缓冲不足时为 -1。</returns>
    public int CopyTextTo(Span<char> destination)
    {
        int lastNonBlank = LastNonBlank();
        int written = 0;
        for (int i = 0; i <= lastNonBlank; i++)
        {
            int n = _cells[i].AppendTo(destination[written..]);
            if (n < 0)
            {
                return -1;
            }
            written += n;
        }
        return written;
    }

    /// <summary>
    /// 把本行原地复位成指定宽度的空白行(等价于 <c>new TerminalRow(columns)</c> 后 <see cref="Fill" />),
    /// 宽度不变时连单元格数组都不重新分配。
    /// </summary>
    /// <remarks>
    /// 供 <see cref="TerminalScreen" /> 的 reflow 回收复用旧行对象。改列宽会对整个缓冲区重排,
    /// 每次都为上万行各 new 一个 <see cref="TerminalCell" /> 数组(1 万行 × 200 列 × 16B ≈ 32MB),
    /// 而拖拽改宽会连着触发几十次 —— 那些数组多数能活过一次 gen0 回收被提升,代价远不止分配本身。
    /// <b>只能对确定已经没人引用的行调用</b>(reflow 里旧行的内容已复制进收集缓冲,即为此)。
    /// </remarks>
    /// <param name="columns">复位后的列数。</param>
    /// <param name="blank">用于填充的空白单元格。</param>
    public void ResetFor(int columns, in TerminalCell blank)
    {
        if (_cells.Length != columns)
        {
            _cells = new TerminalCell[columns];
        }
        _cells.AsSpan().Fill(blank);
        Wrapped = false;
        Timestamp = null;
    }

    /// <summary>创建本行的深拷贝,保留单元格、wrapped 标志与时间戳。</summary>
    public TerminalRow Clone()
    {
        var clone = new TerminalRow(_cells.Length) { Wrapped = Wrapped, Timestamp = Timestamp };
        Array.Copy(_cells, clone._cells, _cells.Length);
        return clone;
    }
}

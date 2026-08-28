namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端网格:一个由 <see cref="TerminalRow" /> 构成的活动屏幕,外加主屏(非备用屏)缓冲区的
/// 回滚历史。持有光标、垂直滚动区域(DECSTBM)以及仿真器驱动的所有结构性编辑原语。
/// 本类刻意保持 UI 无关且单线程;渲染控件仅在 UI 线程上读取它。
/// </summary>
public sealed class TerminalScreen
{
    /// <summary>头部搬移的攒批粒度:每退休这么多行才做一次 RemoveRange 物理搬移。</summary>
    private const int ScrollbackTrimChunk = 1024;

    private readonly List<TerminalRow> _scrollback = [];

    /// <summary>
    /// 头部已裁剪但尚未物理搬移的行数。scrollback 满容量后每滚动一行都退休一行,
    /// 逐行 RemoveAt(0) 是 O(n) 整表搬移(1 万行缓冲下 cat 大文件 = O(n²) 卡死 UI 线程);
    /// 改为头指针前移 O(1),攒满 <see cref="ScrollbackTrimChunk" /> 行才搬移一次,
    /// 对外的 <see cref="ScrollbackCount" /> 语义与逐行裁剪完全一致。
    /// </summary>
    private int _scrollbackStart;

    private TerminalRow[] _lines;

    /// <summary>创建指定大小、具有给定回滚容量的屏幕。</summary>
    public TerminalScreen(int columns, int rows, int maxScrollback = 10_000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        MaxScrollback = Math.Max(0, maxScrollback);
        _lines = NewLines(Rows, Columns);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    /// <summary>活动屏幕的列数。</summary>
    public int Columns { get; private set; }

    /// <summary>活动屏幕的行数。</summary>
    public int Rows { get; private set; }

    /// <summary>为主屏保留的最大回滚行数。</summary>
    public int MaxScrollback { get; set; }

    /// <summary>当前光标列(从 0 开始)。</summary>
    public int CursorX { get; private set; }

    /// <summary>活动屏幕内的当前光标行(从 0 开始)。</summary>
    public int CursorY { get; private set; }

    /// <summary>滚动区域的上边界(从 0 开始,含)。</summary>
    public int ScrollTop { get; private set; }

    /// <summary>滚动区域的下边界(从 0 开始,含)。</summary>
    public int ScrollBottom { get; private set; }

    /// <summary>回滚区当前持有的行数。</summary>
    public int ScrollbackCount => _scrollback.Count - _scrollbackStart;

    /// <summary>视口可用的总行数(回滚 + 活动屏幕)。</summary>
    public int TotalRows => ScrollbackCount + Rows;

    private static TerminalRow[] NewLines(int rows, int cols)
    {
        var lines = new TerminalRow[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = new(cols);
        }
        return lines;
    }

    /// <summary>返回给定索引处的活动屏幕行(已裁剪到范围内)。</summary>
    public TerminalRow ActiveLine(int screenRow) => _lines[Math.Clamp(screenRow, 0, Rows - 1)];

    /// <summary>
    /// 返回以"总"空间寻址的行:0..ScrollbackCount-1 为历史,
    /// ScrollbackCount..TotalRows-1 为活动屏幕。供渲染层使用。
    /// </summary>
    public TerminalRow ViewLine(int absoluteRow)
    {
        // 调用方可能持有过期的行索引(改变列宽前所做的选区、拖到顶边之外的指针);
        // 此处裁剪而非抛异常。
        absoluteRow = Math.Clamp(absoluteRow, 0, TotalRows - 1);
        if (absoluteRow < ScrollbackCount)
        {
            return _scrollback[_scrollbackStart + absoluteRow];
        }
        return _lines[absoluteRow - ScrollbackCount];
    }

    /// <summary>
    /// 退休语义裁剪:超出容量时头指针前移(O(1)),被裁行置空让 GC 尽早回收;
    /// 攒满一块才做一次 RemoveRange 物理搬移,把逐行 O(n) 摊薄为 O(1)。
    /// </summary>
    private void TrimScrollbackToMax()
    {
        while (ScrollbackCount > MaxScrollback)
        {
            _scrollback[_scrollbackStart] = null!;
            _scrollbackStart++;
        }
        if (_scrollbackStart >= ScrollbackTrimChunk)
        {
            _scrollback.RemoveRange(0, _scrollbackStart);
            _scrollbackStart = 0;
        }
    }

    /// <summary>
    /// 把头部已裁剪区立即物理搬移掉,使 <c>_scrollback[0]</c> 重新对齐逻辑首行。
    /// 需要整表遍历/展平 scrollback 的冷路径(resize/reflow)先调用它,
    /// 之后即可按无偏移方式直接使用列表。
    /// </summary>
    private void CompactScrollback()
    {
        if (_scrollbackStart > 0)
        {
            _scrollback.RemoveRange(0, _scrollbackStart);
            _scrollbackStart = 0;
        }
    }

    /// <summary>返回 (<paramref name="x" />, <paramref name="y" />) 处单元格的可变引用。</summary>
    public ref TerminalCell CellRef(int x, int y) => ref _lines[y].CellRef(x);

    /// <summary>在 (<paramref name="x" />, <paramref name="y" />) 处写入单元格;超出范围的坐标被忽略。</summary>
    public void SetCell(int x, int y, in TerminalCell cell)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
        {
            _lines[y][x] = cell;
        }
    }

    /// <summary>读取 (<paramref name="x" />, <paramref name="y" />) 处的单元格;超出范围时返回 <see cref="TerminalCell.Empty" />。</summary>
    public TerminalCell GetCell(int x, int y)
    {
        if ((uint)x < (uint)Columns && (uint)y < (uint)Rows)
        {
            return _lines[y][x];
        }
        return TerminalCell.Empty;
    }

    // ---- 光标 -------------------------------------------------------------

    /// <summary>把光标移动到 (<paramref name="x" />, <paramref name="y" />),并裁剪到屏幕边界内。</summary>
    public void SetCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
    }

    /// <summary>设置光标列,裁剪到范围内。</summary>
    public void SetCursorX(int x) => CursorX = Math.Clamp(x, 0, Columns - 1);

    /// <summary>设置光标行,裁剪到范围内。</summary>
    public void SetCursorY(int y) => CursorY = Math.Clamp(y, 0, Rows - 1);

    /// <summary>设置垂直滚动区域(DECSTBM);范围无效时回退为整屏。</summary>
    public void SetMargins(int top, int bottom)
    {
        if (top < 0)
        {
            top = 0;
        }
        if (bottom > Rows - 1)
        {
            bottom = Rows - 1;
        }
        if (top >= bottom)
        {
            top = 0;
            bottom = Rows - 1;
        }
        ScrollTop = top;
        ScrollBottom = bottom;
    }

    /// <summary>把滚动区域重置为覆盖整屏。</summary>
    public void ResetMargins()
    {
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    // ---- 垂直移动 --------------------------------------------------

    /// <summary>换行 / Index:下移一行,在底部边界处让区域向上滚动。</summary>
    public void Index(in TerminalCell blank)
    {
        if (CursorY == ScrollBottom)
        {
            ScrollUp(1, blank);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
    }

    /// <summary>反向 Index:上移一行,在顶部边界处让区域向下滚动。</summary>
    public void ReverseIndex(in TerminalCell blank)
    {
        if (CursorY == ScrollTop)
        {
            ScrollDown(1, blank);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
    }

    /// <summary>
    /// 把滚动区域向上滚动 <paramref name="count" /> 行。当区域覆盖整屏时,
    /// 退休的顶部行会被压入回滚区。
    /// </summary>
    public void ScrollUp(int count, in TerminalCell blank)
    {
        count = Math.Clamp(count, 0, ScrollBottom - ScrollTop + 1);
        bool fullScreen = ScrollTop == 0 && ScrollBottom == Rows - 1;
        for (int i = 0; i < count; i++)
        {
            TerminalRow retired = _lines[ScrollTop];
            if (fullScreen && MaxScrollback > 0)
            {
                _scrollback.Add(retired);
                TrimScrollbackToMax();
            }
            for (int y = ScrollTop; y < ScrollBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    /// <summary>把滚动区域向下滚动 <paramref name="count" /> 行,在顶部边界插入空白行。</summary>
    public void ScrollDown(int count, in TerminalCell blank)
    {
        count = Math.Clamp(count, 0, ScrollBottom - ScrollTop + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > ScrollTop; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollTop] = fresh;
        }
    }

    // ---- 行编辑(滚动区域内) ----------------------------------------

    /// <summary>IL —— 在光标行插入 <paramref name="count" /> 个空行,将区域中的其余行下移。</summary>
    public void InsertLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = ScrollBottom; y > CursorY; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[CursorY] = fresh;
        }
    }

    /// <summary>DL —— 删除光标行的 <paramref name="count" /> 行,将区域中的其余行上移。</summary>
    public void DeleteLines(int count, in TerminalCell blank)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }
        count = Math.Clamp(count, 0, ScrollBottom - CursorY + 1);
        for (int i = 0; i < count; i++)
        {
            for (int y = CursorY; y < ScrollBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            var fresh = new TerminalRow(Columns);
            fresh.Fill(blank);
            _lines[ScrollBottom] = fresh;
        }
    }

    /// <summary>ICH —— 在光标处插入 <paramref name="count" /> 个空单元格,将本行其余部分右移。</summary>
    public void InsertChars(int count, in TerminalCell blank) => _lines[CursorY].InsertCells(CursorX, count, blank);

    /// <summary>DCH —— 删除光标处的 <paramref name="count" /> 个单元格,将本行其余部分左移。</summary>
    public void DeleteChars(int count, in TerminalCell blank) => _lines[CursorY].DeleteCells(CursorX, count, blank);

    /// <summary>ECH —— 从光标开始擦除 <paramref name="count" /> 个单元格,不移动本行。</summary>
    public void EraseChars(int count, in TerminalCell blank) => _lines[CursorY].FillRange(CursorX, CursorX + count, blank);

    // ---- 擦除 --------------------------------------------------------------

    /// <summary>ED —— 整屏擦除。模式 0:光标→行尾,1:行首→光标,2:全部,3:回滚区。</summary>
    public void EraseInDisplay(int mode, in TerminalCell blank)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorY].FillRange(CursorX, Columns, blank);
                // 该行不再延续到下一行;陈旧的软换行标志会让改变列宽时的重排合并无关的行(提示符重绘 bug)。
                _lines[CursorY].Wrapped = false;
                for (int y = CursorY + 1; y < Rows; y++)
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 1:
                _lines[CursorY].FillRange(0, CursorX + 1, blank);
                for (int y = 0; y < CursorY; y++)
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 2:
                for (int y = 0; y < Rows; y++)
                {
                    _lines[y].Fill(blank);
                }
                break;
            case 3:
                _scrollback.Clear();
                _scrollbackStart = 0;
                break;
        }
    }

    /// <summary>EL —— 整行擦除。模式 0:光标→行尾,1:行首→光标,2:整行。</summary>
    public void EraseInLine(int mode, in TerminalCell blank)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorY].FillRange(CursorX, Columns, blank);
                // 擦到行尾会切断其软换行延续——这正是 readline 的 "\r ESC[K + 提示符" 重绘在每次
                // WINCH 时发出的内容;此处陈旧的 Wrapped 标志会让改变列宽的重排把重绘后的提示符与
                // 其后的内容合并(反复拖拽时逐步破坏缓冲区)。
                _lines[CursorY].Wrapped = false;
                break;
            case 1:
                _lines[CursorY].FillRange(0, CursorX + 1, blank);
                break;
            case 2:
                _lines[CursorY].Fill(blank);
                break;
        }
    }

    // ---- 缓冲区切换与改变大小 -----------------------------------------

    /// <summary>把所有活动行清空为空白,光标归位,并重置滚动边距。</summary>
    public void ResetToBlank(in TerminalCell blank)
    {
        foreach (TerminalRow line in _lines)
        {
            line.Fill(blank);
        }
        CursorX = 0;
        CursorY = 0;
        ResetMargins();
    }

    /// <summary>丢弃全部回滚历史。</summary>
    public void ClearScrollback()
    {
        _scrollback.Clear();
        _scrollbackStart = 0;
    }

    /// <summary>
    /// 把屏幕调整为 <paramref name="columns" />×<paramref name="rows" />,列变化时重新排版主缓冲区,
    /// 行变化时通过回滚区退休/拉取行。
    /// </summary>
    public void Resize(int columns, int rows, in TerminalCell blank)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // resize/reflow 要整表遍历或展平 scrollback,先把头部裁剪区搬移掉,
        // 后续逻辑保持无偏移访问(冷路径,一次 O(n) 可接受)。
        CompactScrollback();

        // 主屏的列变化会对整个缓冲区重新排版(主流做法 —— Windows Terminal / iTerm2 / VTE / kitty):
        // 软换行行被重新合并为逻辑行,并按新宽度重新换行,因此变窄不会破坏内容,变宽则会重新接回。
        // 备用屏(MaxScrollback 为 0 —— htop/vim/tmux)不做重排:这些程序在 SIGWINCH 时自行重绘,
        // 与所有主流终端一致。
        if (columns != Columns && MaxScrollback > 0)
        {
            ReflowResize(columns, rows, blank);
            return;
        }

        // 备用屏列尺寸变化:就地硬性地增缩每一行。
        if (columns != Columns)
        {
            foreach (TerminalRow line in _lines)
            {
                line.Resize(columns, blank);
            }
            foreach (TerminalRow line in _scrollback)
            {
                line.Resize(columns, blank);
            }
        }

        // 行尺寸变化:缩小时,只丢弃真正空白的底部行;其余行从顶部退休进入回滚区。
        // (旧逻辑会丢弃光标下方的任意行——在拖拽改变大小的风暴中,光标可能位于缓冲区中部,
        // 这会悄悄吃掉真实内容,每次缩小几行。)增大时,若回滚区有内容则把行拉回。
        if (rows < Rows)
        {
            int remove = Rows - rows;
            int blankBottom = 0;
            for (int y = Rows - 1; y > CursorY && blankBottom < remove; y--)
            {
                if (_lines[y].Wrapped || _lines[y].LastNonBlank() >= 0)
                {
                    break;
                }
                blankBottom++;
            }
            int fromBottom = blankBottom;
            int fromTop = remove - fromBottom;
            var next = new TerminalRow[rows];
            for (int i = 0; i < fromTop; i++)
            {
                if (MaxScrollback > 0)
                {
                    _scrollback.Add(_lines[i]);
                    TrimScrollbackToMax();
                }
            }
            Array.Copy(_lines, fromTop, next, 0, rows);
            _lines = next;
            CursorY = Math.Max(0, CursorY - fromTop);
        }
        else if (rows > Rows)
        {
            int add = rows - Rows;
            var next = new TerminalRow[rows];
            int pulled = Math.Min(add, _scrollback.Count);
            for (int i = 0; i < pulled; i++)
            {
                next[i] = _scrollback[_scrollback.Count - pulled + i];
            }
            _scrollback.RemoveRange(_scrollback.Count - pulled, pulled);
            Array.Copy(_lines, 0, next, pulled, Rows);
            for (int i = pulled + Rows; i < rows; i++)
            {
                var fresh = new TerminalRow(columns);
                fresh.Fill(blank);
                next[i] = fresh;
            }
            _lines = next;
            CursorY += pulled;
        }
        Columns = columns;
        Rows = rows;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
    }

    // ---- 重新排版(主屏列尺寸变化) -------------------------------

    /// <summary>
    /// 以新宽度重建整个缓冲区:物理行沿其 <see cref="TerminalRow.Wrapped" /> 标志合并为逻辑行,
    /// 每条逻辑行按 <paramref name="newCols" /> 重新换行(宽字符保持原子性),结果再拆分为
    /// 回滚区 + 一个底部锚定的屏幕。光标以(逻辑行,单元格偏移)的形式被带过,
    /// 从而落在同一个字符上。
    /// </summary>
    private void ReflowResize(int newCols, int newRows, in TerminalCell blank)
    {
        int cursorAbs = _scrollback.Count + CursorY;

        // 1. 将回滚区与屏幕展平为一个物理行列表。
        var physical = new List<TerminalRow>(_scrollback.Count + Rows);
        physical.AddRange(_scrollback);
        physical.AddRange(_lines);

        // 丢弃光标下方末尾的空白、未换行行——它们只是屏幕未使用的底部,否则会用空行填充回滚区。
        while (physical.Count > cursorAbs + 1)
        {
            TerminalRow last = physical[^1];
            if (last.Wrapped || last.LastNonBlank() >= 0)
            {
                break;
            }
            physical.RemoveAt(physical.Count - 1);
            Recycle(last); // 这些行到此确定无人引用,直接进回收池。
        }

        // 2. 按新宽度重新生成逻辑行。
        var rebuilt = new List<TerminalRow>(physical.Count);
        // 收集缓冲跨逻辑行复用:拖拽改宽会对整个缓冲区反复 reflow,若每条逻辑行
        // 都 new 一个 List 再逐格 Add,就是一场 O(缓冲区) 的分配风暴。
        var cells = new List<TerminalCell>(newCols * 2);
        int newCursorRow = -1, newCursorCol = 0;
        int i = 0;
        while (i < physical.Count)
        {
            // 逻辑行跨越 [i..j]:除最后一行外每行都带有软换行标志。
            int j = i;
            while (j < physical.Count - 1 && physical[j].Wrapped)
            {
                j++;
            }

            // 收集其单元格:被换行的段落贡献其完整宽度,最后一段
            // 在最后一个非空单元格处截断(扩展以覆盖光标)。
            cells.Clear();
            int cursorOffset = -1;
            DateTime? lineTimestamp = null;
            for (int r = i; r <= j; r++)
            {
                TerminalRow row = physical[r];
                // 行时间戳(时间/行号侧栏)必须穿过 reflow —— 否则改列宽(含开关侧栏导致的列变化)
                // 会把历史行的时间戳清空。取该逻辑行各物理段中最后一个非空时间戳(= 最后收到输出的时间)。
                if (row.Timestamp is { } t)
                {
                    lineTimestamp = t;
                }
                // 取到"最后一个被占用的格"为止,而不是整行宽度。
                //
                // 被换行的段落原先按 row.Columns 整行收:对普通换行没差别(整行都是内容),
                // 但双宽字符在行尾只剩一列放不下时,自动换行会在那里留下一个永远不会被写入的
                // 空格子 —— 按整行收就把这个填充格当成了内容,每经一次 reflow 就在断点处多出
                // 一个空格("触发" → "触 发",反复拖拽改宽会越积越多)。
                //
                // 不能改用 LastNonBlank:宽字符的尾格 Rune 同样是 0,砍掉它前导格就会被当成
                // 单宽字符,宽字符散架。LastOccupied 正是为区分这两者而设。
                int len = row.LastOccupied() + 1;
                if (r == cursorAbs)
                {
                    int cursorCol = Math.Min(CursorX, row.Columns - 1);
                    len = Math.Max(len, cursorCol + 1);
                    cursorOffset = cells.Count + cursorCol;
                }
                cells.AddRange(row.Span[..len]);
            }

            // 内容已整段复制进 cells,physical[i..j] 这些行对象到此确定无人再读 ——
            // 立刻回收,下面 EmitLogicalLine 就地取用。滚动回收使池的驻留量只有
            // 「一条逻辑行所跨的物理行数」(通常 1~3 行),不额外占内存:
            // physical 本来就把全部旧行持有到本方法结束,2 倍峰值是既有事实,这里只是不再重新分配。
            for (int r = i; r <= j; r++)
            {
                Recycle(physical[r]);
            }
            int emittedStart = rebuilt.Count;
            EmitLogicalLine(cells, cursorOffset, newCols, blank, rebuilt, ref newCursorRow, ref newCursorCol);
            for (int r = emittedStart; r < rebuilt.Count; r++)
            {
                rebuilt[r].Timestamp = lineTimestamp;
            }
            i = j + 1;
        }
        if (rebuilt.Count == 0)
        {
            rebuilt.Add(NewBlankRow(newCols, blank));
        }
        if (newCursorRow < 0)
        {
            newCursorRow = rebuilt.Count - 1;
            newCursorCol = 0;
        }

        // 3. 切回:屏幕是最底部 newRows 行(内容像所有终端一样锚定在底部)。拆分绝不丢弃行:
        //    屏幕之上的所有内容都进入回滚区,而映射到窗口之上的光标会被裁剪进窗口,
        //    而非把窗口上拖并悄悄丢弃尾部行(正是这种丢弃在反复拖拽改变大小时吃掉了缓冲区)。
        int screenStart = Math.Max(0, rebuilt.Count - newRows);
        _scrollback.Clear();
        _scrollbackStart = 0;
        for (int r = 0; r < screenStart; r++)
        {
            _scrollback.Add(rebuilt[r]);
        }
        if (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
        }
        var lines = new TerminalRow[newRows];
        int idx = 0;
        for (int r = screenStart; r < rebuilt.Count && idx < newRows; r++, idx++)
        {
            lines[idx] = rebuilt[r];
        }
        for (; idx < newRows; idx++)
        {
            lines[idx] = NewBlankRow(newCols, blank);
        }
        _lines = lines;
        Columns = newCols;
        Rows = newRows;
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
        CursorY = Math.Clamp(newCursorRow - screenStart, 0, newRows - 1);
        CursorX = Math.Clamp(newCursorCol, 0, newCols - 1);

        // 回收池不跨调用驻留:留着就等于让屏幕长期多占一份行内存,而它的收益只在本次重排内。
        _reflowPool.Clear();
    }

    /// <summary>
    /// 把一条逻辑行的单元格换行成若干 <paramref name="cols" /> 宽的行,使宽字符的前导/尾随成对
    /// 留在同一行,除最后一行外每行都标记为软换行,并报告 <paramref name="cursorOffset" /> 落点。
    /// </summary>
    private void EmitLogicalLine(List<TerminalCell> cells,
        int cursorOffset,
        int cols,
        in TerminalCell blank,
        List<TerminalRow> output,
        ref int cursorRow,
        ref int cursorCol)
    {
        TerminalRow row = NewBlankRow(cols, blank);
        output.Add(row);
        int col = 0;
        for (int k = 0; k < cells.Count; k++)
        {
            TerminalCell cell = cells[k];
            // 宽字符对(前导 + 尾随标记)必须留在同一行。
            bool wide = cols >= 2 &&
                        !cell.IsWideTrailing &&
                        k + 1 < cells.Count &&
                        cells[k + 1].IsWideTrailing;
            int need = wide ? 2 : 1;
            if (col + need > cols)
            {
                row.Wrapped = true;
                row = NewBlankRow(cols, blank);
                output.Add(row);
                col = 0;
            }
            if (k == cursorOffset)
            {
                cursorRow = output.Count - 1;
                cursorCol = col;
            }
            row[col++] = cell;
            if (wide)
            {
                if (k + 1 == cursorOffset)
                {
                    cursorRow = output.Count - 1;
                    cursorCol = col - 1;
                }
                row[col++] = cells[k + 1];
                k++;
            }
        }

        // 光标落在已收集内容之后紧邻的(空白)单元格上。
        if (cursorOffset == cells.Count && cursorOffset >= 0)
        {
            cursorRow = output.Count - 1;
            cursorCol = Math.Min(col, cols - 1);
        }
    }

    /// <summary>
    /// reflow 期间的行对象回收池。
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="ReflowResize" /> 这一次调用内有效,方法结束即清空 —— 不跨调用驻留,
    /// 因此不会让屏幕长期多占一份行内存。往里放的必须是「内容已复制走、确定无人引用」的行。
    /// </remarks>
    private readonly Stack<TerminalRow> _reflowPool = new();

    /// <summary>把一个确定已无人引用的行交还回收池。</summary>
    private void Recycle(TerminalRow row) => _reflowPool.Push(row);

    /// <summary>取一个指定宽度的空白行:优先复用回收池里的旧行对象。</summary>
    private TerminalRow NewBlankRow(int cols, in TerminalCell blank)
    {
        if (_reflowPool.TryPop(out TerminalRow? recycled))
        {
            recycled.ResetFor(cols, blank);
            return recycled;
        }
        var row = new TerminalRow(cols);
        row.Fill(blank);
        return row;
    }
}

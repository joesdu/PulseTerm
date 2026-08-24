namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 选区几何的纯计算部分:线性选区(常规拖拽)与矩形块选(Alt+拖拽,#128)共用一套归一化 /
/// 逐行取列区间的规则;不连续的多段选区(Ctrl+Shift+拖拽追加)就是若干 <see cref="SelectionSpan" />
/// 各自套用这同一套规则。
/// <para>
/// 行为对齐 Windows Terminal:块选与否在<b>按下鼠标那一刻</b>由 Alt 决定,拖拽途中松开或按下 Alt 不改变模式;
/// 线性选区按「起点 → 终点」顺序展开(整行贯通),块选则取两点围成矩形的行/列区间交集。
/// 两种模式下终点列都是<b>排它</b>的,与控件既有的选区约定一致:单击不拖动 = 空选区 = 不高亮、不复制。
/// </para>
/// </summary>
internal static class TerminalSelectionMath
{
    /// <summary>
    /// 把锚点与光标归一化成 <c>Start ≤ End</c> 的一对端点。
    /// 线性模式按行优先比较;块选模式分别取行、列的最小/最大值(即拖拽矩形的左上角与右下角)。
    /// </summary>
    /// <param name="anchor">按下鼠标处的单元(绝对行)。</param>
    /// <param name="caret">当前指针所在单元(绝对行)。</param>
    /// <param name="block">是否为矩形块选。</param>
    public static ((int Row, int Col) Start, (int Row, int Col) End) Normalize(
        (int Row, int Col) anchor,
        (int Row, int Col) caret,
        bool block
    )
    {
        if (block)
        {
            return (
                (Math.Min(anchor.Row, caret.Row), Math.Min(anchor.Col, caret.Col)),
                (Math.Max(anchor.Row, caret.Row), Math.Max(anchor.Col, caret.Col))
            );
        }
        if (anchor.Row < caret.Row || (anchor.Row == caret.Row && anchor.Col <= caret.Col))
        {
            return (anchor, caret);
        }
        return (caret, anchor);
    }

    /// <summary>
    /// 求某一行落在选区内的列区间 <c>[From, To)</c>(复制文本用),已按行宽夹取。
    /// 行不在选区内、或该行没有任何列被选中时,From 等于 To。
    /// </summary>
    /// <param name="sel">已归一化的选区端点。</param>
    /// <param name="block">是否为矩形块选。</param>
    /// <param name="row">绝对缓冲行号。</param>
    /// <param name="lineColumns">该行的列数。</param>
    public static (int From, int To) RowSpan(
        ((int Row, int Col) Start, (int Row, int Col) End) sel,
        bool block,
        int row,
        int lineColumns
    )
    {
        if (row < sel.Start.Row || row > sel.End.Row)
        {
            return (0, 0);
        }
        int from = block ? sel.Start.Col
            : row == sel.Start.Row ? sel.Start.Col
            : 0;
        int to = block ? sel.End.Col
            : row == sel.End.Row ? sel.End.Col
            : lineColumns;
        from = Math.Clamp(from, 0, Math.Max(0, lineColumns));
        to = Math.Clamp(to, 0, Math.Max(0, lineColumns));
        return to <= from ? (from, from) : (from, to);
    }
}

/// <summary>
/// 一段已归一化的选区(<c>Start ≤ End</c>)连同它的模式。不连续的多段选区
/// (Ctrl+Shift+拖拽追加)就是一串 SelectionSpan,单段选区是它的退化情形 ——
/// 每一段各自记住自己是线性还是块选,故"先线性选一段、再追加一段块选"是合法的。
/// </summary>
/// <param name="Start">起点(块选时为矩形左上角)。</param>
/// <param name="End">终点(块选时为矩形右下角);列一律<b>排它</b>。</param>
/// <param name="Block">该段是否为矩形块选(#128)。</param>
internal readonly record struct SelectionSpan(
    (int Row, int Col) Start,
    (int Row, int Col) End,
    bool Block
)
{
    /// <summary>该段是否什么都没选中(单击不拖 = 空段:不高亮、不复制、不入列)。</summary>
    public bool IsEmpty => Block ? Start.Col >= End.Col : Start == End;

    /// <summary>由一次拖拽的锚点与游标归一化出一段选区。</summary>
    public static SelectionSpan FromDrag(
        (int Row, int Col) anchor,
        (int Row, int Col) caret,
        bool block
    )
    {
        ((int Row, int Col) start, (int Row, int Col) end) = TerminalSelectionMath.Normalize(
            anchor,
            caret,
            block
        );
        return new(start, end, block);
    }
}

namespace VelaShell.Views;

/// <summary>
/// 框选(拖出一个矩形选中一片行)的纯几何部分。
/// <para>
/// 命中判定刻意<b>不</b>去遍历已实现的容器:列表是虚拟化的,视口外的行根本没有容器,
/// 一边拖一边自动滚动时就会漏掉划过的行。行高统一,于是把矩形的上下边换算到内容坐标后
/// 直接除以行高即可 —— 与虚拟化、与滚动位置都无关,而且能在没有 UI 的情况下测。
/// </para>
/// </summary>
internal static class MarqueeSelectionMath
{
    /// <summary>
    /// 求内容坐标区间 <paramref name="top" />..<paramref name="bottom" /> 覆盖到的行下标(闭区间)。
    /// 两端可以任意顺序传入(向上拖时 bottom &lt; top)。没有覆盖到任何行时返回 <c>(-1, -1)</c>。
    /// </summary>
    /// <param name="top">矩形一端在内容坐标系里的 Y。</param>
    /// <param name="bottom">矩形另一端在内容坐标系里的 Y。</param>
    /// <param name="rowHeight">行高(像素),必须为正。</param>
    /// <param name="itemCount">列表当前的行数。</param>
    public static (int First, int Last) RowsInBand(double top, double bottom, double rowHeight, int itemCount)
    {
        if (rowHeight <= 0 || itemCount <= 0)
        {
            return (-1, -1);
        }
        (double lower, double upper) = top <= bottom ? (top, bottom) : (bottom, top);

        // 整条带子落在内容之外(上方或下方)时不该选中任何行 —— 夹紧之前先判掉,
        // 否则夹紧会把它压成贴边的一行,在空白处随手一拖就莫名其妙选中首行/末行。
        double contentHeight = rowHeight * itemCount;
        if (upper <= 0 || lower >= contentHeight)
        {
            return (-1, -1);
        }

        int first = (int)Math.Floor(Math.Max(lower, 0) / rowHeight);
        int last = (int)Math.Floor(Math.Min(upper, contentHeight - 0.0001) / rowHeight);
        return (Math.Clamp(first, 0, itemCount - 1), Math.Clamp(last, 0, itemCount - 1));
    }
}

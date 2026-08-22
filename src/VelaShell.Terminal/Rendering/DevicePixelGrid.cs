using Avalonia;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 设备像素栅格:把绘制坐标(DIP)吸附到整数设备像素边界。
/// <para>
/// 终端的格子尺寸取整到<b>整数 DIP</b>(见 <c>VelaTerminalControl.RecomputeMetrics</c>),
/// 而这只在 <c>RenderScaling</c> 为整数时才等于整数设备像素。125% / 150% 这类分数缩放下,
/// 相邻单元格的背景矩形共享的那条边落在设备像素中间,Skia 对两个矩形各自做抗锯齿,
/// 两次半覆盖叠加起来凑不回 100%(覆盖率 = 1 − f(1−f),最坏 0.75),于是每条格线上
/// 都留下一道比底色浅一档的缝 —— 表现为整屏的"方块网格"(issue #245)。
/// </para>
/// <para>
/// 修法是绘制前把矩形四边各自吸附到最近的设备像素:相邻矩形的公共边吸附到<b>同一个</b>
/// 整数上,于是严丝合缝、既无缝隙也无重叠,抗锯齿在这些边上退化为无操作。
/// 代价是格子的背景带宽度会在 ±1 设备像素间浮动(21.25 的行距必然如此),
/// 但字形位置不吸附,字距保持均匀。
/// </para>
/// </summary>
/// <param name="originX">当前绘制原点相对渲染根的 X 偏移(DIP)。含控件在窗口中的位置与内边距/侧栏平移。</param>
/// <param name="originY">当前绘制原点相对渲染根的 Y 偏移(DIP)。</param>
/// <param name="scale">渲染缩放(<c>IRenderRoot.RenderScaling</c>);非正/非有限值回退为 1。</param>
public readonly struct DevicePixelGrid(double originX, double originY, double scale)
{
    /// <summary>整数缩放(含 1.0)下吸附是无操作,可整体跳过。</summary>
    private static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-9;

    /// <summary>渲染缩放;非正/非有限值回退为 1。</summary>
    public double Scale { get; } = scale > 0 && double.IsFinite(scale) ? scale : 1;

    /// <summary>绘制原点相对渲染根的 X 偏移(DIP)。</summary>
    public double OriginX { get; } = double.IsFinite(originX) ? originX : 0;

    /// <summary>绘制原点相对渲染根的 Y 偏移(DIP)。</summary>
    public double OriginY { get; } = double.IsFinite(originY) ? originY : 0;

    /// <summary>本栅格下坐标是否天然对齐(缩放与原点都落在整数设备像素上),对齐时可跳过吸附。</summary>
    public bool IsAligned =>
        IsWhole(Scale) && IsWhole(OriginX * Scale) && IsWhole(OriginY * Scale);

    /// <summary>把绘制坐标 <paramref name="x" /> 吸附到最近的设备像素边界(返回值仍是绘制坐标)。</summary>
    public double SnapX(double x) => Snap(x, OriginX);

    /// <summary>把绘制坐标 <paramref name="y" /> 吸附到最近的设备像素边界(返回值仍是绘制坐标)。</summary>
    public double SnapY(double y) => Snap(y, OriginY);

    /// <summary>
    /// 把矩形的四条边分别吸附到设备像素边界。四边独立吸附(而非"吸附左上角再保持宽高")
    /// 是无缝拼接的关键:相邻矩形的公共边输入相同,输出必然相同。
    /// </summary>
    public Rect Snap(Rect rect)
    {
        double left = SnapX(rect.X);
        double top = SnapY(rect.Y);
        return new(left, top, SnapX(rect.Right) - left, SnapY(rect.Bottom) - top);
    }

    // 中点一律远离零取整:同一条公共边的输入相同,取整结果自然相同(拼接只要求"一致",
    // 不要求某个方向);相比银行家舍入,行高 21.25 这类节律下的带宽变化也更可预测。
    private double Snap(double value, double origin) =>
        (Math.Round((origin + value) * Scale, MidpointRounding.AwayFromZero) / Scale) - origin;
}

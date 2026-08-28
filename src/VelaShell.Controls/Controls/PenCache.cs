using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 每控件一份的画笔缓存:把 <c>Render</c> 里反复 <c>new Pen(...)</c> 换成按参数复用的
/// <see cref="ImmutablePen" />。
/// </summary>
/// <remarks>
/// <para>
/// <c>Render</c> 每次失效都会重跑,而这些控件的画笔参数在整个生命周期里基本恒定
/// (轨道色 + 进度色、网格线 + 中线、图标描边……)。原先每帧新建的 <c>Pen</c> 还是可变的
/// <c>AvaloniaObject</c>,交给绘制上下文时框架要再快照一次,一次重绘两笔开销。
/// </para>
/// <para>
/// <b>只缓存纯色画笔</b>:键里带上颜色值,因此即便调用方复用同一个 <see cref="SolidColorBrush" />
/// 实例、只改了它的 <c>Color</c>(主题令牌就地改色、颜色动画),也不会取到旧画笔。
/// 渐变等非纯色画笔无法这样廉价地判等,直接现建 —— 那些场景本就不在热路径上。
/// </para>
/// </remarks>
internal sealed class PenCache
{
    /// <summary>缓存条目上限;越限直接清空重来(画笔种类恒定的控件永远到不了)。</summary>
    private const int MaxEntries = 16;

    private readonly Dictionary<(uint Color, double Thickness, PenLineCap Cap, PenLineJoin Join), ImmutablePen> _pens = [];

    /// <summary>取一支画笔;参数完全相同即复用同一实例。</summary>
    /// <param name="brush">描边画刷。非纯色画刷不缓存,每次现建。</param>
    /// <param name="thickness">线宽。</param>
    /// <param name="cap">线端样式。</param>
    /// <param name="join">拐角样式。</param>
    public IPen Get(
        IBrush brush,
        double thickness,
        PenLineCap cap = PenLineCap.Flat,
        PenLineJoin join = PenLineJoin.Miter)
    {
        if (brush is not ISolidColorBrush solid)
        {
            return new ImmutablePen(brush.ToImmutable(), thickness, null, cap, join);
        }
        uint color = solid.Color.ToUInt32();
        (uint, double, PenLineCap, PenLineJoin) key = (color, thickness, cap, join);
        if (_pens.TryGetValue(key, out ImmutablePen? cached))
        {
            return cached;
        }
        if (_pens.Count >= MaxEntries)
        {
            _pens.Clear();
        }
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(solid.Color, solid.Opacity), thickness, null, cap, join);
        _pens[key] = pen;
        return pen;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 以设计稿中的方式渲染一个 lucide 图标:基于 24×24 描边几何路径绘制,
/// 使用 2px 圆头/圆角画笔,并等比缩放到控件尺寸。Avalonia 的
/// <c>PathIcon</c> 会填充几何路径,这会破坏描边风格的图标集 —— 本控件改用描边方式。
/// 几何数据存放在 <c>Themes/Icons.axaml</c> 中,以 <c>Icon.&lt;lucide-name&gt;</c> 为键。
/// </summary>
public class LucideIcon : Control
{
    /// <summary>lucide 路径几何(以其原生 24×24 视图框为准)。</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    /// <summary>描边画刷(对应设计中的图标填充令牌)。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Foreground));

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty);
    }

    /// <summary>要绘制的 lucide 路径几何(以其原生 24×24 视图框为准)。</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>描边画刷(对应设计中的图标填充令牌)。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>测量控件尺寸,在无约束时回退到设计稿的默认图标大小。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // 无约束时默认使用设计稿中最常见的图标尺寸。
        double w = double.IsFinite(Width) ? Width : 12;
        double h = double.IsFinite(Height) ? Height : 12;
        return new(w, h);
    }

    private readonly PenCache _pens = new();

    /// <summary>使用当前前景画刷绘制图标几何。</summary>
    public override void Render(DrawingContext context)
    {
        Geometry? geometry = Data;
        IBrush? brush = Foreground;
        if (geometry is null || brush is null)
        {
            return;
        }
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // 从 24×24 的 lucide 视图框等比缩放,并在边界内居中。画笔随变换一起缩放,
        // 因此描边在任何尺寸下都保持 lucide 2/24 的粗细比例。
        double scale = Math.Min(w, h) / 24.0;
        var offset = new Point((w - 24 * scale) / 2, (h - 24 * scale) / 2);

        // 画笔按 (颜色, 线宽, 端点, 拐角) 复用:图标在工具栏/会话树/文件浏览器里成百上千个,
        // 每个每次重绘都新建一支可变 Pen(框架还要再快照一次)纯属白扔。
        IPen pen = _pens.Get(brush, 2, PenLineCap.Round, PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}

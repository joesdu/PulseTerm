using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 容量 / 占用条:一条圆角轨道加一段填充,按阈值自动转警告色与危险色(规范 §11:&gt;70% 警告、&gt;90% 危险)。
/// 用它而不是 ProgressBar,是为了避免每个列表项都去挂 Classes.warn / Classes.crit 的样式绑定。
/// </summary>
public sealed class MeterBar : Control
{
    /// <summary>当前值。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Value));

    /// <summary>满量程,默认 100。</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Maximum), 100);

    /// <summary>轨道颜色。</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(TrackBrush));

    /// <summary>正常区间的填充色。</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(FillBrush));

    /// <summary>警告区间的填充色。</summary>
    public static readonly StyledProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(WarnBrush));

    /// <summary>危险区间的填充色。</summary>
    public static readonly StyledProperty<IBrush?> CritBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(CritBrush));

    /// <summary>转为警告色的百分比阈值,默认 70。</summary>
    public static readonly StyledProperty<double> WarnThresholdProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(WarnThreshold), 70);

    /// <summary>转为危险色的百分比阈值,默认 90。</summary>
    public static readonly StyledProperty<double> CritThresholdProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(CritThreshold), 90);

    /// <summary>圆角半径;默认取高度的一半(全圆头)。</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Radius), -1);

    static MeterBar()
    {
        AffectsRender<MeterBar>(
            ValueProperty, MaximumProperty, TrackBrushProperty, FillBrushProperty,
            WarnBrushProperty, CritBrushProperty, WarnThresholdProperty, CritThresholdProperty, RadiusProperty);
    }

    /// <inheritdoc cref="ValueProperty" />
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty" />
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="FillBrushProperty" />
    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <inheritdoc cref="WarnBrushProperty" />
    public IBrush? WarnBrush
    {
        get => GetValue(WarnBrushProperty);
        set => SetValue(WarnBrushProperty, value);
    }

    /// <inheritdoc cref="CritBrushProperty" />
    public IBrush? CritBrush
    {
        get => GetValue(CritBrushProperty);
        set => SetValue(CritBrushProperty, value);
    }

    /// <inheritdoc cref="WarnThresholdProperty" />
    public double WarnThreshold
    {
        get => GetValue(WarnThresholdProperty);
        set => SetValue(WarnThresholdProperty, value);
    }

    /// <inheritdoc cref="CritThresholdProperty" />
    public double CritThreshold
    {
        get => GetValue(CritThresholdProperty);
        set => SetValue(CritThresholdProperty, value);
    }

    /// <inheritdoc cref="RadiusProperty" />
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>宽度铺满,高度取显式设置值(默认 6px,与设计一致)。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double h = double.IsFinite(Height) ? Height : 6;
        return new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, h);
    }

    /// <summary>绘制轨道与填充段。</summary>
    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        double radius = Radius >= 0 ? Radius : h / 2;
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, new Rect(0, 0, w, h), radius, radius);
        }
        double max = Maximum > 0 ? Maximum : 100;
        double percent = Math.Clamp(Value / max * 100, 0, 100);
        if (percent <= 0)
        {
            return;
        }
        IBrush? fill = percent > CritThreshold ? CritBrush ?? FillBrush
            : percent > WarnThreshold ? WarnBrush ?? FillBrush
            : FillBrush;
        if (fill is null)
        {
            return;
        }
        // 极小值也要看得见一小截,否则 0.4% 的磁盘看起来像空条。
        double filled = Math.Max(Math.Min(w, radius * 2), w * percent / 100);
        context.DrawRectangle(fill, null, new Rect(0, 0, filled, h), radius, radius);
    }
}

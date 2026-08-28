using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 状态栏用的环形进度指示器(JetBrains 风格):一圈淡色轨道 + 一段主题色圆弧。
/// <para>
/// <see cref="IsIndeterminate" /> 为真时圆弧以固定长度绕圈,表示"在做事但说不出还剩多少";
/// 为假时圆弧从 12 点方向顺时针铺开到 <see cref="Value" /> 的比例。
/// </para>
/// <para>
/// 不用 <c>ProgressBar</c> 的模板改造,是因为那条路要为一个 14px 的小圆环拖进整套
/// 模板/样式/过渡,而这里真正需要的只有两笔弧。旋转由 <see cref="DispatcherTimer" /> 驱动,
/// 且只在"确实在转 + 确实看得见"时才跑 —— 状态栏常驻控件,空转的代价是全天候的。
/// </para>
/// </summary>
public sealed class CircularProgressRing : Control
{
    /// <summary>不确定动画的帧间隔:20fps,肉眼流畅,又不至于让一个小圆环常驻占用调度器。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>不确定模式下旋转弧的张角(度)。</summary>
    private const double IndeterminateSweep = 100;

    /// <summary>不确定模式下转满一圈的耗时(毫秒)。</summary>
    private const double IndeterminatePeriodMs = 1100;

    /// <summary>当前进度,取值 0~1(超出范围自动夹紧)。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CircularProgressRing, double>(nameof(Value));

    /// <summary>是否为不确定进度(圆弧绕圈,忽略 <see cref="Value" />)。</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<CircularProgressRing, bool>(nameof(IsIndeterminate), true);

    /// <summary>底圈轨道颜色。</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<CircularProgressRing, IBrush?>(nameof(TrackBrush));

    /// <summary>进度圆弧颜色。</summary>
    public static readonly StyledProperty<IBrush?> ArcBrushProperty =
        AvaloniaProperty.Register<CircularProgressRing, IBrush?>(nameof(ArcBrush));

    /// <summary>圆环线宽,默认 2px。</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<CircularProgressRing, double>(nameof(StrokeThickness), 2);

    private DispatcherTimer? _timer;
    private long _phaseStartTicks;

    static CircularProgressRing() =>
        AffectsRender<CircularProgressRing>(
            ValueProperty, IsIndeterminateProperty, TrackBrushProperty, ArcBrushProperty, StrokeThicknessProperty);

    /// <inheritdoc cref="ValueProperty" />
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="IsIndeterminateProperty" />
    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="ArcBrushProperty" />
    public IBrush? ArcBrush
    {
        get => GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    /// <inheritdoc cref="StrokeThicknessProperty" />
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>取显式尺寸,未指定时给 14×14(状态栏默认)。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(Width) ? Width : 14;
        double h = double.IsFinite(Height) ? Height : 14;
        return new(w, h);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimationState();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAnimation();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // IsEffectivelyVisible 在 Avalonia 12 不是 AvaloniaProperty,订阅不到;
        // 它由父链决定,因此在每帧回调里复核(见 StartAnimation)。
        if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty)
        {
            UpdateAnimationState();
        }
    }

    /// <summary>轨道与进度弧两支画笔的复用缓存 —— 不确定态每帧重绘,现建就是每帧两个对象。</summary>
    private readonly PenCache _pens = new();

    /// <summary>绘制轨道与进度弧。</summary>
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double thickness = Math.Max(1, StrokeThickness);
        if (size <= thickness * 2)
        {
            return;
        }
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = (size - thickness) / 2;

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, _pens.Get(track, thickness), center, radius, radius);
        }
        if (ArcBrush is not { } arc)
        {
            return;
        }

        double startDegrees, sweepDegrees;
        if (IsIndeterminate)
        {
            double elapsed = (Environment.TickCount64 - _phaseStartTicks) % IndeterminatePeriodMs;
            startDegrees = elapsed / IndeterminatePeriodMs * 360;
            sweepDegrees = IndeterminateSweep;
        }
        else
        {
            startDegrees = 0;
            sweepDegrees = Math.Clamp(Value, 0, 1) * 360;
            if (sweepDegrees <= 0)
            {
                return;
            }
        }

        // 满圈单独走 DrawEllipse:圆弧的起终点重合时 ArcTo 画不出闭合圆(退化成不画)。
        IPen pen = _pens.Get(arc, thickness, PenLineCap.Round);
        if (sweepDegrees >= 359.9)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        context.DrawGeometry(null, pen, BuildArc(center, radius, startDegrees, sweepDegrees));
    }

    /// <summary>构造一段从 12 点方向起算、顺时针的圆弧。</summary>
    private static StreamGeometry BuildArc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(center, radius, startDegrees), isFilled: false);
            ctx.ArcTo(PointOnCircle(center, radius, startDegrees + sweepDegrees), new(radius, radius),
                rotationAngle: 0, isLargeArc: sweepDegrees > 180, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }
        return geometry;
    }

    /// <summary>圆周取点;0° 在 12 点方向,角度顺时针增长。</summary>
    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double radians = (degrees - 90) * Math.PI / 180;
        return new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    /// <summary>只在"不确定 + 已挂上可见的视觉树"时驱动重绘,其余情况一律停表。</summary>
    private void UpdateAnimationState()
    {
        if (IsIndeterminate && IsVisible && VisualRoot is not null)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    private void StartAnimation()
    {
        if (_timer is not null)
        {
            return;
        }
        _phaseStartTicks = Environment.TickCount64;
        // 每帧复核 IsEffectivelyVisible:主窗最小化 / 状态栏整体隐藏时,本控件自己的
        // IsVisible 仍是 true,只有父链能给出答案 —— 不复核就等于全天候空转重绘。
        _timer = new(FrameInterval, DispatcherPriority.Render, (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                InvalidateVisual();
            }
        });
        _timer.Start();
    }

    private void StopAnimation()
    {
        _timer?.Stop();
        _timer = null;
    }
}

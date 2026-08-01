using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// <see cref="TimeSeriesChart" /> 中的一条曲线,自绘"面积 + 折线"。
/// 它是图表的真实可视子元素(而不是轻量数据对象):只有进了可视树才能继承 DataContext、
/// 才能沿逻辑树解析 DynamicResource —— 否则 Values 与颜色全是 null,表现为"网格画得出、线画不出"。
/// </summary>
public sealed class ChartSeries : Control
{
    /// <summary>按时间先后排列的采样值;最后一个元素是"现在"。</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<ChartSeries, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>折线颜色。</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ChartSeries, IBrush?>(nameof(Stroke));

    /// <summary>折线下方的面积填充;为 null 时只画线。</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChartSeries, IBrush?>(nameof(Fill));

    /// <summary>折线粗细,默认 1.5(设计规范)。</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ChartSeries, double>(nameof(StrokeThickness), 1.5);

    /// <summary>true = 从中线向下绘制(网络页的上行曲线)。</summary>
    public static readonly StyledProperty<bool> MirrorProperty =
        AvaloniaProperty.Register<ChartSeries, bool>(nameof(Mirror));

    static ChartSeries()
    {
        AffectsRender<ChartSeries>(ValuesProperty, StrokeProperty, FillProperty, StrokeThicknessProperty, MirrorProperty);
    }

    /// <inheritdoc cref="ValuesProperty" />
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="StrokeProperty" />
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <inheritdoc cref="FillProperty" />
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <inheritdoc cref="StrokeThicknessProperty" />
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <inheritdoc cref="MirrorProperty" />
    public bool Mirror
    {
        get => GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    /// <summary>按父图表给定的量程绘制自身曲线。</summary>
    public override void Render(DrawingContext context)
    {
        if (Parent is not TimeSeriesChart chart)
        {
            return;
        }
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1 || Values is not { Count: > 1 } values)
        {
            return;
        }
        double max = chart.EffectiveMaximum();
        if (max <= 0)
        {
            return;
        }
        int capacity = Math.Max(2, chart.Capacity);
        bool mirrored = chart.Mirrored;
        double baseline = mirrored ? h / 2 : h;
        double span = mirrored ? h / 2 : h;
        int direction = Mirror ? 1 : -1;

        int count = Math.Min(values.Count, capacity);
        int skip = values.Count - count;
        double step = w / (capacity - 1);
        // 点数不足一屏时靠右对齐:新数据从右侧进入,与 Windows 资源管理器同向。
        double left = w - ((count - 1) * step);

        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (StreamGeometryContext ctx = area.Open())
            {
                ctx.BeginFigure(new(left, baseline), true);
                for (int i = 0; i < count; i++)
                {
                    double ratio = Math.Clamp(values[skip + i] / max, 0, 1);
                    ctx.LineTo(new(left + (i * step), baseline + (direction * ratio * span)));
                }
                ctx.LineTo(new(left + ((count - 1) * step), baseline));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }
        if (Stroke is not { } stroke)
        {
            return;
        }
        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            for (int i = 0; i < count; i++)
            {
                double ratio = Math.Clamp(values[skip + i] / max, 0, 1);
                var point = new Point(left + (i * step), baseline + (direction * ratio * span));
                if (i == 0)
                {
                    ctx.BeginFigure(point, false);
                }
                else
                {
                    ctx.LineTo(point);
                }
            }
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}

/// <summary>
/// 资源监视窗口的时序图:固定长度的滚动窗口(默认 60 个采样点),每条曲线是一个
/// <see cref="ChartSeries" /> 子元素。图表本身只画网格与中线,曲线由子元素按声明顺序叠加,
/// 因此后声明的曲线压在先声明的上面。外框圆角/背景交给包裹它的 Border(便于走主题令牌)。
/// </summary>
public sealed class TimeSeriesChart : Panel
{
    /// <summary>X 轴槽位数;曲线点数少于它时靠右对齐,右端即"现在"。</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<TimeSeriesChart, int>(nameof(Capacity), 60);

    /// <summary>Y 轴上限;≤ 0 表示按当前数据自动取值。</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TimeSeriesChart, double>(nameof(Maximum), 100);

    /// <summary>水平网格的分格数(默认 4 格 = 3 条线);0 = 不画。</summary>
    public static readonly StyledProperty<int> GridRowsProperty =
        AvaloniaProperty.Register<TimeSeriesChart, int>(nameof(GridRows), 4);

    /// <summary>垂直网格的分格数(默认 6 格 = 5 条线);0 = 不画。</summary>
    public static readonly StyledProperty<int> GridColumnsProperty =
        AvaloniaProperty.Register<TimeSeriesChart, int>(nameof(GridColumns), 6);

    /// <summary>网格线颜色。</summary>
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(GridBrush));

    /// <summary>true = 以中线为零点上下镜像绘制(网络页的上下行)。</summary>
    public static readonly StyledProperty<bool> MirroredProperty =
        AvaloniaProperty.Register<TimeSeriesChart, bool>(nameof(Mirrored));

    /// <summary>中线颜色(仅 <see cref="Mirrored" /> 时绘制)。</summary>
    public static readonly StyledProperty<IBrush?> MidlineBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(MidlineBrush));

    /// <summary>
    /// 版本号:视图模型每次追加采样后自增,用于触发重绘。历史缓冲是原地复用的,
    /// 光靠属性变更通知不会触发渲染。
    /// </summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<TimeSeriesChart, int>(nameof(Revision));

    /// <inheritdoc cref="CapacityProperty" />
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty" />
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc cref="GridRowsProperty" />
    public int GridRows
    {
        get => GetValue(GridRowsProperty);
        set => SetValue(GridRowsProperty, value);
    }

    /// <inheritdoc cref="GridColumnsProperty" />
    public int GridColumns
    {
        get => GetValue(GridColumnsProperty);
        set => SetValue(GridColumnsProperty, value);
    }

    /// <inheritdoc cref="GridBrushProperty" />
    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <inheritdoc cref="MirroredProperty" />
    public bool Mirrored
    {
        get => GetValue(MirroredProperty);
        set => SetValue(MirroredProperty, value);
    }

    /// <inheritdoc cref="MidlineBrushProperty" />
    public IBrush? MidlineBrush
    {
        get => GetValue(MidlineBrushProperty);
        set => SetValue(MidlineBrushProperty, value);
    }

    /// <inheritdoc cref="RevisionProperty" />
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>
    /// 实际使用的 Y 轴上限:<see cref="Maximum" /> 为正时直接用它,否则取全部曲线的峰值
    /// 并留一成余量(顶格的曲线贴着上边缘不好看)。
    /// </summary>
    /// <returns>大于 0 的量程。</returns>
    public double EffectiveMaximum()
    {
        if (Maximum > 0)
        {
            return Maximum;
        }
        double max = 0;
        foreach (Control child in Children)
        {
            if (child is not ChartSeries { Values: { Count: > 0 } values })
            {
                continue;
            }
            foreach (double v in values)
            {
                max = Math.Max(max, v);
            }
        }
        return max > 0 ? max * 1.15 : 1;
    }

    /// <summary>曲线数据原地更新,靠版本号驱动子元素重绘。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RevisionProperty
            && change.Property != MaximumProperty
            && change.Property != CapacityProperty
            && change.Property != MirroredProperty
            && change.Property != GridBrushProperty
            && change.Property != GridRowsProperty
            && change.Property != GridColumnsProperty
            && change.Property != MidlineBrushProperty)
        {
            return;
        }
        foreach (Control child in Children)
        {
            child.InvalidateVisual();
        }
    }

    /// <summary>
    /// 网格与中线画在一个私有子元素里 —— <see cref="Panel" /> 把 Render 封死了(它要画自己的
    /// Background),只能另起一层。它是第一个子元素,因此永远在曲线下方。
    /// </summary>
    public TimeSeriesChart() => Children.Add(new ChartGridLayer());
}

/// <summary>图表的网格层:只画水平/垂直网格与镜像中线,永远垫在曲线下方。</summary>
internal sealed class ChartGridLayer : Control
{
    /// <summary>按父图表的配置绘制网格。</summary>
    public override void Render(DrawingContext context)
    {
        if (Parent is not TimeSeriesChart chart)
        {
            return;
        }
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1)
        {
            return;
        }
        if (chart.GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            for (int i = 1; i < chart.GridRows; i++)
            {
                double y = Math.Round(h * i / chart.GridRows) + 0.5;
                context.DrawLine(pen, new(0, y), new(w, y));
            }
            for (int i = 1; i < chart.GridColumns; i++)
            {
                double x = Math.Round(w * i / chart.GridColumns) + 0.5;
                context.DrawLine(pen, new(x, 0), new(x, h));
            }
        }
        if (chart.Mirrored && chart.MidlineBrush is { } mid)
        {
            double y = Math.Round(h / 2) + 0.5;
            context.DrawLine(new Pen(mid, 1), new(0, y), new(w, y));
        }
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>逻辑处理器网格的呈现方式。</summary>
public enum CoreGridMode
{
    /// <summary>热力图:一格一色,核心数多时唯一读得过来的形态。</summary>
    HeatMap,

    /// <summary>迷你折线:每格一条该核心的 60 秒趋势(核心数少时更有信息量)。</summary>
    Sparkline
}

/// <summary>
/// 逻辑处理器网格:一格一个核心,支持热力图与迷你折线两种呈现。整块用一次 Render 直接画完,
/// 不为每个核心建控件 —— 128 核以上时逐格建控件(尤其逐格一个图表)会让每秒一次的刷新变成卡顿源。
/// 高度按行数自然增长,外面套 ScrollViewer 即得到设计稿里的垂直滚动。
/// </summary>
public sealed class UsageHeatGrid : Control
{
    /// <summary>各核心占用率(0-100);顺序由调用方决定(可按核心号或按负载排过序)。</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>呈现方式。</summary>
    public static readonly StyledProperty<CoreGridMode> ModeProperty =
        AvaloniaProperty.Register<UsageHeatGrid, CoreGridMode>(nameof(Mode));

    /// <summary>各核心的历史序列,与 <see cref="Values" /> 同序;迷你折线模式下使用。</summary>
    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<double>>?> HistoriesProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IReadOnlyList<IReadOnlyList<double>>?>(nameof(Histories));

    /// <summary>各格的标签,与 <see cref="Values" /> 同序;为空时用 <see cref="LabelPrefix" /> + 下标。</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IReadOnlyList<string>?>(nameof(Labels));

    /// <summary>迷你折线的线色。</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(LineBrush));

    /// <summary>迷你折线的面积填充色。</summary>
    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(AreaBrush));

    /// <summary>迷你折线模式下的单元格底色。</summary>
    public static readonly StyledProperty<IBrush?> CellBackgroundProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(CellBackground));

    /// <summary>
    /// 迷你折线模式下压在曲线上的文字底片。曲线面积铺满格子时,直接写字会糊进填充里。
    /// </summary>
    public static readonly StyledProperty<IBrush?> LabelBackgroundProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(LabelBackground));

    /// <summary>版本号:视图模型每次刷新后自增以触发重绘(缓冲原地复用)。</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<UsageHeatGrid, int>(nameof(Revision));

    /// <summary>列数;≤ 0 表示按可用宽度与 <see cref="MinCellWidth" /> 自动决定。</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<UsageHeatGrid, int>(nameof(Columns));

    /// <summary>单元格最小高度:低于它就宁可滚动,不再压扁。</summary>
    public static readonly StyledProperty<double> MinCellHeightProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(MinCellHeight), 34);

    /// <summary>单元格最大高度:核心少时格子会长高填满容器,但不至于变成几块巨砖。</summary>
    public static readonly StyledProperty<double> MaxCellHeightProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(MaxCellHeight), 120);

    /// <summary>
    /// 可用高度(通常绑到外层 ScrollViewer 的 Viewport.Height)。给了它才能把格子铺满容器:
    /// 控件放在 ScrollViewer 里时量到的可用高度是无限大,只能退回最小格高,于是核心少时
    /// 格子很小、下方一大片空白。
    /// </summary>
    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(ViewportHeight));

    /// <summary>单元格最小宽度,决定自动列数上限。</summary>
    public static readonly StyledProperty<double> MinCellWidthProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(MinCellWidth), 52);

    /// <summary>单元格最大宽度:核心少的机器上不让一两个格子拉成一条横杠。</summary>
    public static readonly StyledProperty<double> MaxCellWidthProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(MaxCellWidth), 320);

    /// <summary>单元格间距。</summary>
    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<UsageHeatGrid, double>(nameof(CellGap), 4);

    /// <summary>核心号前缀(如 “CPU”)。</summary>
    public static readonly StyledProperty<string> LabelPrefixProperty =
        AvaloniaProperty.Register<UsageHeatGrid, string>(nameof(LabelPrefix), "CPU");

    /// <summary>当前选中的核心下标;-1 = 未选中。</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<UsageHeatGrid, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>选中格的描边色。</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(SelectionBrush));

    /// <summary>核心号文字颜色。</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(LabelBrush));

    /// <summary>色阶第 1 级(&lt;10%)。</summary>
    public static readonly StyledProperty<IBrush?> Level1BrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(Level1Brush));

    /// <summary>色阶第 2 级(10-30%)。</summary>
    public static readonly StyledProperty<IBrush?> Level2BrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(Level2Brush));

    /// <summary>色阶第 3 级(30-60%)。</summary>
    public static readonly StyledProperty<IBrush?> Level3BrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(Level3Brush));

    /// <summary>色阶第 4 级(60-85%)。</summary>
    public static readonly StyledProperty<IBrush?> Level4BrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(Level4Brush));

    /// <summary>色阶第 5 级(&gt;85%)。</summary>
    public static readonly StyledProperty<IBrush?> Level5BrushProperty =
        AvaloniaProperty.Register<UsageHeatGrid, IBrush?>(nameof(Level5Brush));

    /// <summary>字体族(与界面字号令牌一致,不要在这里写死)。</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<UsageHeatGrid>();

    static UsageHeatGrid()
    {
        AffectsRender<UsageHeatGrid>(
            ValuesProperty, RevisionProperty, SelectedIndexProperty, LabelPrefixProperty, ModeProperty,
            HistoriesProperty, LabelsProperty, LineBrushProperty, AreaBrushProperty, CellBackgroundProperty,
            SelectionBrushProperty, LabelBrushProperty, LabelBackgroundProperty,
            Level1BrushProperty, Level2BrushProperty, Level3BrushProperty, Level4BrushProperty, Level5BrushProperty);
        AffectsMeasure<UsageHeatGrid>(
            ValuesProperty, RevisionProperty, ColumnsProperty, CellGapProperty, ViewportHeightProperty,
            MinCellWidthProperty, MaxCellWidthProperty, MinCellHeightProperty, MaxCellHeightProperty);
    }

    /// <inheritdoc cref="ValuesProperty" />
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="ModeProperty" />
    public CoreGridMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <inheritdoc cref="HistoriesProperty" />
    public IReadOnlyList<IReadOnlyList<double>>? Histories
    {
        get => GetValue(HistoriesProperty);
        set => SetValue(HistoriesProperty, value);
    }

    /// <inheritdoc cref="LabelsProperty" />
    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <inheritdoc cref="LineBrushProperty" />
    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <inheritdoc cref="AreaBrushProperty" />
    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    /// <inheritdoc cref="CellBackgroundProperty" />
    public IBrush? CellBackground
    {
        get => GetValue(CellBackgroundProperty);
        set => SetValue(CellBackgroundProperty, value);
    }

    /// <inheritdoc cref="LabelBackgroundProperty" />
    public IBrush? LabelBackground
    {
        get => GetValue(LabelBackgroundProperty);
        set => SetValue(LabelBackgroundProperty, value);
    }

    /// <inheritdoc cref="RevisionProperty" />
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <inheritdoc cref="ColumnsProperty" />
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <inheritdoc cref="MinCellHeightProperty" />
    public double MinCellHeight
    {
        get => GetValue(MinCellHeightProperty);
        set => SetValue(MinCellHeightProperty, value);
    }

    /// <inheritdoc cref="MaxCellHeightProperty" />
    public double MaxCellHeight
    {
        get => GetValue(MaxCellHeightProperty);
        set => SetValue(MaxCellHeightProperty, value);
    }

    /// <inheritdoc cref="ViewportHeightProperty" />
    public double ViewportHeight
    {
        get => GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    /// <inheritdoc cref="MinCellWidthProperty" />
    public double MinCellWidth
    {
        get => GetValue(MinCellWidthProperty);
        set => SetValue(MinCellWidthProperty, value);
    }

    /// <inheritdoc cref="MaxCellWidthProperty" />
    public double MaxCellWidth
    {
        get => GetValue(MaxCellWidthProperty);
        set => SetValue(MaxCellWidthProperty, value);
    }

    /// <inheritdoc cref="CellGapProperty" />
    public double CellGap
    {
        get => GetValue(CellGapProperty);
        set => SetValue(CellGapProperty, value);
    }

    /// <inheritdoc cref="LabelPrefixProperty" />
    public string LabelPrefix
    {
        get => GetValue(LabelPrefixProperty);
        set => SetValue(LabelPrefixProperty, value);
    }

    /// <inheritdoc cref="SelectedIndexProperty" />
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <inheritdoc cref="SelectionBrushProperty" />
    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc cref="Level1BrushProperty" />
    public IBrush? Level1Brush
    {
        get => GetValue(Level1BrushProperty);
        set => SetValue(Level1BrushProperty, value);
    }

    /// <inheritdoc cref="Level2BrushProperty" />
    public IBrush? Level2Brush
    {
        get => GetValue(Level2BrushProperty);
        set => SetValue(Level2BrushProperty, value);
    }

    /// <inheritdoc cref="Level3BrushProperty" />
    public IBrush? Level3Brush
    {
        get => GetValue(Level3BrushProperty);
        set => SetValue(Level3BrushProperty, value);
    }

    /// <inheritdoc cref="Level4BrushProperty" />
    public IBrush? Level4Brush
    {
        get => GetValue(Level4BrushProperty);
        set => SetValue(Level4BrushProperty, value);
    }

    /// <inheritdoc cref="Level5BrushProperty" />
    public IBrush? Level5Brush
    {
        get => GetValue(Level5BrushProperty);
        set => SetValue(Level5BrushProperty, value);
    }

    /// <inheritdoc cref="FontFamilyProperty" />
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>按可用宽高把格子铺满容器;放不下时退回最小格高,让外层 ScrollViewer 滚动。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Values?.Count ?? 0;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        if (count == 0)
        {
            return new(width, 0);
        }
        CellLayout layout = ResolveLayout(width, count);
        double gap = Math.Max(0, CellGap);
        double content = (layout.Rows * layout.CellHeight) + ((layout.Rows - 1) * gap);
        // 内容比容器矮时占满容器高度,好让整块网格在容器里居中 —— 否则会缩在左上角,
        // 下方一大片空白。
        return new(width, Math.Max(content, ViewportHeight));
    }

    /// <summary>最近一次布局算出的列数(测试与排障用)。</summary>
    public int EffectiveColumns { get; private set; }

    /// <summary>最近一次布局算出的单元格尺寸(测试与排障用)。</summary>
    public Size EffectiveCellSize { get; private set; }

    /// <summary>一次布局的结果:列数、行数与单元格尺寸。</summary>
    private readonly record struct CellLayout(int Columns, int Rows, double CellWidth, double CellHeight);

    /// <summary>格子被最大尺寸夹住、整块网格小于容器时的居中偏移。</summary>
    private Vector GridOffset(CellLayout layout, double gap)
    {
        double gridWidth = (layout.Columns * layout.CellWidth) + ((layout.Columns - 1) * gap);
        double gridHeight = (layout.Rows * layout.CellHeight) + ((layout.Rows - 1) * gap);
        return new(Math.Max(0, (Bounds.Width - gridWidth) / 2), Math.Max(0, (Bounds.Height - gridHeight) / 2));
    }

    /// <summary>
    /// 选一组列数/格子尺寸:在不低于最小尺寸的前提下让单个格子面积最大,并轻微偏好接近 2:1 的
    /// 宽高比(纯比面积会选出又高又窄的一列)。全都放不下时退回"最密一行 + 最小格高",交给滚动。
    /// </summary>
    private CellLayout ResolveLayout(double width, int count)
    {
        double gap = Math.Max(0, CellGap);
        double minWidth = Math.Max(8, MinCellWidth);
        int maxColumns = Columns > 0
                             ? Math.Min(Columns, count)
                             : Math.Clamp((int)Math.Floor((width + gap) / (minWidth + gap)), 1, count);
        double viewport = ViewportHeight;

        CellLayout best = default;
        double bestScore = -1;
        for (int columns = 1; columns <= maxColumns; columns++)
        {
            // 显式指定列数时只认那一档。
            if (Columns > 0 && columns != maxColumns)
            {
                continue;
            }
            int rows = (int)Math.Ceiling(count / (double)columns);
            double cellWidth = (width - ((columns - 1) * gap)) / columns;
            if (cellWidth < minWidth)
            {
                continue;
            }
            cellWidth = Math.Min(cellWidth, MaxCellWidth > 0 ? MaxCellWidth : cellWidth);

            double cellHeight = MinCellHeight;
            if (viewport > 0)
            {
                cellHeight = (viewport - ((rows - 1) * gap)) / rows;
                if (cellHeight < MinCellHeight)
                {
                    continue;
                }
                cellHeight = Math.Min(cellHeight, MaxCellHeight > 0 ? MaxCellHeight : cellHeight);
            }
            double aspectPenalty = 1 / (1 + (Math.Abs((cellWidth / cellHeight) - 2) * 0.15));
            double score = cellWidth * cellHeight * aspectPenalty;
            // 格子被最大尺寸夹住后,加列并不会让单格变小,得分会打平 —— 这时选列多的那档,
            // 否则少核机器会排成竖着的一列,右边空一大片。
            if (score > bestScore * 1.0001 || (score >= bestScore * 0.9999 && columns > best.Columns))
            {
                bestScore = Math.Max(bestScore, score);
                best = new(columns, rows, cellWidth, cellHeight);
            }
        }
        if (bestScore < 0)
        {
            // 一档都放不下:按最密排布 + 最小格高,交给滚动。
            int fallbackRows = (int)Math.Ceiling(count / (double)maxColumns);
            double fallbackWidth = (width - ((maxColumns - 1) * gap)) / maxColumns;
            best = new(maxColumns, fallbackRows,
                Math.Min(fallbackWidth, MaxCellWidth > 0 ? MaxCellWidth : fallbackWidth), MinCellHeight);
        }
        EffectiveColumns = best.Columns;
        EffectiveCellSize = new(best.CellWidth, best.CellHeight);
        return best;
    }

    /// <summary>点选核心:落点换算为下标写入 <see cref="SelectedIndex" />(再次点击同一格取消选中)。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int count = Values?.Count ?? 0;
        if (count == 0)
        {
            return;
        }
        CellLayout layout = ResolveLayout(Bounds.Width, count);
        double gap = Math.Max(0, CellGap);
        if (layout.CellWidth <= 0)
        {
            return;
        }
        Point p = e.GetPosition(this) - GridOffset(layout, gap);
        if (p.X < 0 || p.Y < 0)
        {
            return;
        }
        int column = (int)(p.X / (layout.CellWidth + gap));
        int row = (int)(p.Y / (layout.CellHeight + gap));
        int index = (row * layout.Columns) + Math.Clamp(column, 0, layout.Columns - 1);
        if (index >= 0 && index < count)
        {
            SelectedIndex = SelectedIndex == index ? -1 : index;
            e.Handled = true;
        }
    }

    /// <summary>逐格绘制底色与文字。</summary>
    public override void Render(DrawingContext context)
    {
        if (Values is not { Count: > 0 } values || Bounds.Width <= 1)
        {
            return;
        }
        int count = values.Count;
        CellLayout layout = ResolveLayout(Bounds.Width, count);
        int columns = layout.Columns;
        double gap = Math.Max(0, CellGap);
        double cellWidth = layout.CellWidth;
        double cellHeight = layout.CellHeight;
        if (cellWidth <= 1)
        {
            return;
        }

        // 格子窄到放不下"CPU12"就只留百分比,再窄则纯色块 —— 256 核那一档仍然可读。
        bool sparkline = Mode == CoreGridMode.Sparkline;
        bool showIndex = cellWidth >= 44 && cellHeight >= 30;
        bool showValue = cellWidth >= 28;
        // 字号跟着格子走:核心少时格子会长到几百像素,固定 11px 的读数会显得像掉在角落里。
        double valueSize = sparkline ? 9 : Math.Clamp(Math.Min(cellWidth * 0.22, cellHeight * 0.26), 11, 26);
        double indexSize = sparkline ? 8 : Math.Clamp(valueSize * 0.72, 8, 15);
        var typeface = new Typeface(FontFamily ?? FontFamily.Default);
        IBrush labelBrush = LabelBrush ?? Brushes.Gray;
        int selected = SelectedIndex;
        IReadOnlyList<IReadOnlyList<double>>? histories = Histories;
        IReadOnlyList<string>? labels = Labels;

        Vector offset = GridOffset(layout, gap);
        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int column = i % columns;
            double x = offset.X + (column * (cellWidth + gap));
            double y = offset.Y + (row * (cellHeight + gap));
            var rect = new Rect(x, y, cellWidth, cellHeight);
            double value = Math.Clamp(values[i], 0, 100);
            context.DrawRectangle(sparkline ? CellBackground : LevelBrush(value), null, rect, 3, 3);
            if (sparkline && histories is not null && i < histories.Count)
            {
                DrawSparkline(context, rect, histories[i]);
            }
            if (i == selected && SelectionBrush is { } selectionBrush)
            {
                context.DrawRectangle(null, new Pen(selectionBrush, 1), rect.Deflate(0.5), 3, 3);
            }
            if (!showValue)
            {
                continue;
            }
            string label = labels is not null && i < labels.Count
                               ? labels[i]
                               : LabelPrefix + i.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(
                value.ToString("F0", CultureInfo.InvariantCulture) + "%",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                valueSize,
                labelBrush);
            var index = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                indexSize,
                labelBrush);
            if (sparkline)
            {
                // 折线要占满格子,文字压在左上/右下角,不居中盖住曲线;各垫一层底片保证读得清。
                var indexAt = new Point(rect.X + 4, rect.Y + 2);
                var valueAt = new Point(rect.Right - text.Width - 4, rect.Bottom - text.Height - 2);
                DrawLabelBackdrop(context, indexAt, index.Width, index.Height);
                DrawLabelBackdrop(context, valueAt, text.Width, text.Height);
                context.DrawText(index, indexAt);
                context.DrawText(text, valueAt);
                continue;
            }
            double textY = showIndex ? rect.Center.Y - 1 : rect.Center.Y - (text.Height / 2);
            context.DrawText(text, new(rect.Center.X - (text.Width / 2), textY));
            if (showIndex)
            {
                context.DrawText(index, new(rect.Center.X - (index.Width / 2), rect.Center.Y - text.Height - 1));
            }
        }
    }

    /// <summary>给压在曲线上的文字垫一层底片。</summary>
    private void DrawLabelBackdrop(DrawingContext context, Point at, double width, double height)
    {
        if (LabelBackground is not { } backdrop)
        {
            return;
        }
        context.DrawRectangle(backdrop, null, new Rect(at.X - 3, at.Y - 1, width + 6, height + 2), 3, 3);
    }

    /// <summary>在一个格子里画该核心的 60 秒趋势(0-100 定量程,与热力图的读数口径一致)。</summary>
    private void DrawSparkline(DrawingContext context, Rect rect, IReadOnlyList<double> history)
    {
        if (history.Count < 2)
        {
            return;
        }
        double step = rect.Width / (history.Count - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext lineCtx = line.Open())
        using (StreamGeometryContext areaCtx = area.Open())
        {
            areaCtx.BeginFigure(new(rect.X, rect.Bottom), true);
            for (int i = 0; i < history.Count; i++)
            {
                var point = new Point(
                    rect.X + (i * step),
                    rect.Bottom - (Math.Clamp(history[i], 0, 100) / 100 * rect.Height));
                if (i == 0)
                {
                    lineCtx.BeginFigure(point, false);
                }
                else
                {
                    lineCtx.LineTo(point);
                }
                areaCtx.LineTo(point);
            }
            areaCtx.LineTo(new(rect.Right, rect.Bottom));
            areaCtx.EndFigure(true);
            lineCtx.EndFigure(false);
        }
        if (AreaBrush is { } fill)
        {
            context.DrawGeometry(fill, null, area);
        }
        if (LineBrush is { } stroke)
        {
            context.DrawGeometry(null, new Pen(stroke, 1.2, lineJoin: PenLineJoin.Round), line);
        }
    }

    /// <summary>按列数摊分可用宽度,并夹到 <see cref="MaxCellWidth" />(核心少时不铺满整行)。</summary>


    private IBrush? LevelBrush(double value) => value switch
    {
        < 10 => Level1Brush,
        < 30 => Level2Brush,
        < 60 => Level3Brush,
        < 85 => Level4Brush,
        _ => Level5Brush
    };
}

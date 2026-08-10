using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离插件独立窗口的自绘卡片壳(与主程序资源监视/任务管理器同规格):透明窗口 +
/// 8px 圆角卡片 + 40px 标题栏 + min/max/close 三连按钮 + 自绘缩放抓取区。
/// 纯代码构建(PluginHost 不依赖 VelaShell.Controls);配色用宿主下发的 <c>Vela*</c> 令牌;
/// caption 图标用几何 Path(lucide minus/square/x)。
/// </summary>
internal sealed class PluginHostShellWindow : Window
{
    private const double InnerRadius = 7;
    private readonly Border _rootCard;
    private readonly Border _titleStrip;
    private readonly Panel _resizeGrips;

    public PluginHostShellWindow(string title, string subtitle, Control content)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _titleStrip = BuildTitleBar(title, subtitle);
        var grid = new Grid { RowDefinitions = new RowDefinitions("40,*") };
        grid.Children.Add(_titleStrip);
        var contentHost = new ContentControl { Content = content };
        Grid.SetRow(contentHost, 1);
        grid.Children.Add(contentHost);

        _rootCard = new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"),
            Child = grid
        };
        Bind(_rootCard, Border.BackgroundProperty, "VelaBgSurface");
        Bind(_rootCard, Border.BorderBrushProperty, "VelaBorderSecondary");

        _resizeGrips = BuildResizeGrips();

        var rootPanel = new Panel();
        rootPanel.Children.Add(_rootCard);
        rootPanel.Children.Add(_resizeGrips);
        Content = rootPanel;

        if (OperatingSystem.IsMacOS())
        {
            // 透明窗口在 macOS 拖累性能:退回不透明矩形(与主程序自绘窗体同一结论)。
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            _rootCard.BoxShadow = default;
            ApplyCardShape(rounded: false);
        }
    }

    private Border BuildTitleBar(string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Bind(titleText, TextBlock.ForegroundProperty, "VelaTextPrimary");
        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Bind(subtitleText, TextBlock.ForegroundProperty, "VelaTextMuted");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        left.Children.Add(titleText);
        left.Children.Add(subtitleText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(CaptionButton(MinusGeometry(), close: false, () => WindowState = WindowState.Minimized));
        buttons.Children.Add(CaptionButton(SquareGeometry(), close: false, ToggleMaximize));
        buttons.Children.Add(CaptionButton(CrossGeometry(), close: true, Close));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var strip = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(InnerRadius, InnerRadius, 0, 0),
            Child = grid
        };
        Bind(strip, Border.BackgroundProperty, "VelaBgSurface");
        Bind(strip, Border.BorderBrushProperty, "VelaBorderPrimary");
        strip.PointerPressed += OnHeaderPressed;
        return strip;
    }

    private Button CaptionButton(Geometry geometry, bool close, Action onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = geometry, StrokeThickness = 1.4, StrokeLineCap = PenLineCap.Round };
        Bind(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "VelaTextSecondary");
        var button = new Button
        {
            Width = 42,
            Height = 34,
            Padding = default,
            Background = Brushes.Transparent,
            BorderThickness = default,
            CornerRadius = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
        // hover 反馈:关闭键红,其余中性 —— 直接在事件里改背景(壳无样式表)。
        button.PointerEntered += (_, _) =>
            button.Background = close ? new SolidColorBrush(Color.Parse("#E81123")) : ThemeBrush("VelaBgHover");
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Panel BuildResizeGrips()
    {
        var panel = new Panel { ZIndex = 200 };
        AddGrip(panel, "North", WindowEdge.North, StandardCursorType.TopSide, height: 5, hMargin: 10);
        AddGrip(panel, "South", WindowEdge.South, StandardCursorType.BottomSide, height: 5, hMargin: 10, bottom: true);
        AddGrip(panel, "West", WindowEdge.West, StandardCursorType.LeftSide, width: 5, vMargin: 10);
        AddGrip(panel, "East", WindowEdge.East, StandardCursorType.RightSide, width: 5, vMargin: 10, right: true);
        AddCorner(panel, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, left: true, top: true);
        AddCorner(panel, WindowEdge.NorthEast, StandardCursorType.TopRightCorner, left: false, top: true);
        AddCorner(panel, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, left: true, top: false);
        AddCorner(panel, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, left: false, top: false);
        return panel;
    }

    private void AddGrip(Panel panel, string _, WindowEdge edge, StandardCursorType cursor,
        double width = double.NaN, double height = double.NaN, double hMargin = 0, double vMargin = 0,
        bool bottom = false, bool right = false)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            Margin = new Thickness(hMargin, vMargin)
        };
        if (!double.IsNaN(width))
        {
            border.Width = width;
            border.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        if (!double.IsNaN(height))
        {
            border.Height = height;
            border.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    private void AddCorner(Panel panel, WindowEdge edge, StandardCursorType cursor, bool left, bool top)
    {
        var border = new Border
        {
            Width = 10,
            Height = 10,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom
        };
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
        {
            return;
        }
        bool normal = WindowState == WindowState.Normal;
        _resizeGrips.IsVisible = normal;
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    private void ApplyCardShape(bool rounded)
    {
        _rootCard.Margin = rounded ? new Thickness(8) : default;
        _rootCard.BorderThickness = rounded ? new Thickness(1) : default;
        _rootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        _titleStrip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
    }

    private static void Bind(Control control, AvaloniaProperty property, string resourceKey) =>
        control[!property] = new DynamicResourceExtension(resourceKey);

    private IBrush ThemeBrush(string key) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    // ---- lucide 图标几何(minus / square / x)----
    private static Geometry MinusGeometry() => Geometry.Parse("M4,7 H14");
    private static Geometry SquareGeometry() => Geometry.Parse("M4,4 H14 V14 H4 Z");
    private static Geometry CrossGeometry() => Geometry.Parse("M4,4 L14,14 M14,4 L4,14");
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.PluginSdk.Ui;

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

    /// <summary>卡片外边距,即投影的画布宽度;与主程序 VelaShadowWindow 的最大延展一致。</summary>
    private const double CardGutter = 16;

    /// <summary>卡片贴边时(macOS 不透明矩形)的抓取区尺寸,压在内容上故取最小值。</summary>
    private const double FlushGripEdge = 5,
        FlushGripCorner = 10;

    /// <summary>
    /// 卡片投影,与主程序暗色的 VelaShadowWindow 令牌同值(此进程不加载宿主的主题字典,
    /// 拿不到那个令牌,只能照抄;改一处要记得改另一处)。近处一层压出边缘、远处一层给扩散。
    /// </summary>
    private const string CardShadow = "0 2 4 0 #59000000, 0 6 10 0 #A6000000";

    private readonly Border _rootCard;
    private readonly Border _titleStrip;
    private readonly Panel _resizeGrips;

    public PluginHostShellWindow(string title, string subtitle, Control content, IReadOnlyList<PanelTitleAction>? titleActions = null)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _titleStrip = BuildTitleBar(title, subtitle, titleActions ?? []);
        var grid = new Grid { RowDefinitions = [with("40,*")] };
        grid.Children.Add(_titleStrip);
        var contentHost = new ContentControl { Content = content };
        Grid.SetRow(contentHost, 1);
        grid.Children.Add(contentHost);

        _rootCard = new Border
        {
            // 外边距是投影的画布:透明窗体的投影超出窗口矩形的部分会被直接裁掉,
            // 故留白必须 ≥ 投影的最大延展(offsetY + blur = 16),与主程序同一口径。
            Margin = new Thickness(CardGutter),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse(CardShadow),
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

    private Border BuildTitleBar(string title, string subtitle, IReadOnlyList<PanelTitleAction> titleActions)
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
        // 插件声明的标题栏动作按钮(PanelOptions.TitleActions)插在最小化键左侧,与主程序的 PluginPanelWindow 同一位置
        foreach (PanelTitleAction action in titleActions)
        {
            buttons.Children.Add(ActionButton(action));
        }
        buttons.Children.Add(CaptionButton(MinusGeometry(), close: false, () => WindowState = WindowState.Minimized));
        buttons.Children.Add(CaptionButton(SquareGeometry(), close: false, ToggleMaximize));
        buttons.Children.Add(CaptionButton(CrossGeometry(), close: true, Close));

        var grid = new Grid { ColumnDefinitions = [with("*,Auto")] };
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
        return CaptionButton(icon, close, onClick);
    }

    /// <summary>插件的标题栏动作:lucide 24×24 路径缩到 12px,描边 2 与主程序 LucideIcon 一致。</summary>
    private Button ActionButton(PanelTitleAction action)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            Data = Geometry.Parse(action.IconPathData),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        Bind(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "VelaTextSecondary");
        Button button = CaptionButton(new Viewbox { Width = 12, Height = 12, Child = icon }, close: false, action.OnClick);
        ToolTip.SetTip(button, action.ToolTip);
        return button;
    }

    private Button CaptionButton(Control icon, bool close, Action onClick)
    {
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
        AddGrip(panel, WindowEdge.North, StandardCursorType.TopSide);
        AddGrip(panel, WindowEdge.South, StandardCursorType.BottomSide, bottom: true);
        AddGrip(panel, WindowEdge.West, StandardCursorType.LeftSide, vertical: true);
        AddGrip(panel, WindowEdge.East, StandardCursorType.RightSide, vertical: true, right: true);
        AddCorner(panel, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, left: true, top: true);
        AddCorner(panel, WindowEdge.NorthEast, StandardCursorType.TopRightCorner, left: false, top: true);
        AddCorner(panel, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, left: true, top: false);
        AddCorner(panel, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, left: false, top: false);
        ApplyGripThickness(panel, rounded: true);
        return panel;
    }

    private void AddGrip(Panel panel, WindowEdge edge, StandardCursorType cursor,
        bool vertical = false, bool bottom = false, bool right = false)
    {
        var border = new Border
        {
            Tag = edge,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor)
        };
        if (vertical)
        {
            border.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        else
        {
            border.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    private void AddCorner(Panel panel, WindowEdge edge, StandardCursorType cursor, bool left, bool top)
    {
        var border = new Border
        {
            Tag = edge,
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom
        };
        border.PointerPressed += (_, e) => BeginResize(edge, e);
        panel.Children.Add(border);
    }

    /// <summary>
    /// 抓取区厚度跟随卡片形态:圆角态铺满 16px 的投影留白(否则那圈留白看得见点不动),
    /// 铺满态收回 5px(否则会压在内容上吃掉最靠边控件的点击,比如滚动条)。
    /// </summary>
    private static void ApplyGripThickness(Panel grips, bool rounded)
    {
        double edge = rounded ? CardGutter : FlushGripEdge;
        double corner = rounded ? CardGutter + 6 : FlushGripCorner;
        foreach (Control child in grips.Children)
        {
            if (child is not Border { Tag: WindowEdge tag })
            {
                continue;
            }
            switch (tag)
            {
                // 上下边让开四角的宽度,否则角上的抓取区被边压住,拿不到斜向缩放。
                case WindowEdge.North or WindowEdge.South:
                    child.Height = edge;
                    child.Margin = new Thickness(corner, 0);
                    break;
                case WindowEdge.West or WindowEdge.East:
                    child.Width = edge;
                    child.Margin = new Thickness(0, corner);
                    break;
                default:
                    child.Width = corner;
                    child.Height = corner;
                    break;
            }
        }
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
        _rootCard.Margin = rounded ? new Thickness(CardGutter) : default;
        _rootCard.BorderThickness = rounded ? new Thickness(1) : default;
        _rootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        _titleStrip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
        ApplyGripThickness(_resizeGrips, rounded);
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

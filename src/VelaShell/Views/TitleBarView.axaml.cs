using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 无边框窗体的自绘标题栏(WindowDecorations="None" 全自绘模式)。
/// 拖动/双击遵循 Windows 原生标题栏手感:普通状态按下即拖(BeginMoveDrag,
/// 原生移动循环,Win11 边缘贴靠有效);最大化状态单击不动作、拖动超阈值才还原
/// 并按水平比例把窗口定位到鼠标下再继续拖;双击切换最大化。
/// 最大化按钮的 Win11 贴靠面板由 MainWindow 的 WndProc 钩子(HTMAXBUTTON)提供。
/// </summary>
public partial class TitleBarView : UserControl
{
    private const double DragThreshold = 4;

    private Window? _observedWindow;
    private PointerPressedEventArgs? _pendingMaximizedDrag;
    private Point _pressPoint;

    /// <summary>创建标题栏视图,并挂接指针移动/释放/捕获丢失处理以支持最大化态的延迟拖动判定。</summary>
    public TitleBarView()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, Bar_PointerMoved);
        AddHandler(PointerReleasedEvent, (_, _) => _pendingMaximizedDrag = null);
        AddHandler(PointerCaptureLostEvent, (_, _) => _pendingMaximizedDrag = null);
    }

    // 必须沿逻辑树找 Window:Avalonia 12 的视觉根是 TopLevelHost 而非 Window,
    // “VisualRoot as Window”恒为 null —— 曾令标题栏拖动与窗口按钮看似“无输入”,
    // 实为事件一直在触发、只是拿不到 Window 执行动作。
    private Window? HostWindow => this.FindLogicalAncestorOfType<Window>();

    /// <summary>供 MainWindow 的 WndProc 钩子做 HTMAXBUTTON 命中与 nc-hover 高亮。</summary>
    internal Button MaximizeButtonControl => MaximizeButton;

    /// <summary>设置最大化按钮的非客户区悬停高亮(供 MainWindow 的 WndProc HTMAXBUTTON 命中驱动)。</summary>
    /// <param name="hovered">true 添加 nc-hover 高亮样式类;false 移除。</param>
    internal void SetMaximizeNcHover(bool hovered)
    {
        if (hovered)
        {
            if (!MaximizeButton.Classes.Contains("nc-hover"))
            {
                MaximizeButton.Classes.Add("nc-hover");
            }
        }
        else
        {
            MaximizeButton.Classes.Remove("nc-hover");
        }
    }

    /// <summary>在最大化与普通窗口状态之间切换当前宿主窗口。</summary>
    internal void ToggleMaximize()
    {
        if (HostWindow is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                                     ? WindowState.Normal
                                     : WindowState.Maximized;
        }
    }

    /// <summary>附加到视觉树时开始监听宿主窗口状态变化,并同步最大化按钮图标。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 最大化按钮图标随窗口状态切换:方块 = 可最大化,双矩形 = 可还原。
        if (HostWindow is { } window)
        {
            _observedWindow = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    /// <summary>从视觉树分离时取消对窗口状态变化的监听。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_observedWindow is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _observedWindow = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && sender is Window window)
        {
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        string key = state == WindowState.Maximized ? "Icon.copy" : "Icon.square";
        if (this.TryFindResource(key, out object? value) && value is Geometry geometry)
        {
            MaximizeIcon.Data = geometry;
        }
    }

    private void Bar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 按钮自行消费点击;源头在任何按钮内都不启动拖动,以免吞掉 Click。
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            _pendingMaximizedDrag = null;
            ToggleMaximize();
            return;
        }
        if (HostWindow is not { } window)
        {
            return;
        }
        if (window.WindowState == WindowState.Maximized)
        {
            // 原生手感:最大化下单击不还原;只记录按下,拖动超阈值才还原并进入移动
            // (见 Bar_PointerMoved)。
            _pendingMaximizedDrag = e;
            _pressPoint = e.GetPosition(this);
            return;
        }
        window.BeginMoveDrag(e);
    }

    private void Bar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingMaximizedDrag is not { } pressed || HostWindow is not { } window)
        {
            return;
        }
        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressPoint.X) < DragThreshold && Math.Abs(position.Y - _pressPoint.Y) < DragThreshold)
        {
            return;
        }
        _pendingMaximizedDrag = null;

        // 原生手感:还原窗口,并按“鼠标在标题栏的水平比例”把窗口定位到鼠标下,
        // 标题栏保持跟手;还原后的尺寸要等一拍布局,故定位与 BeginMoveDrag 后置。
        double ratioX = Math.Clamp(_pressPoint.X / Math.Max(Bounds.Width, 1), 0, 1);
        PixelPoint screenPoint = this.PointToScreen(position);
        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.Post(() =>
        {
            double scaling = window.RenderScaling;
            int offsetX = (int)(window.Bounds.Width * ratioX * scaling);
            int offsetY = (int)(_pressPoint.Y * scaling);
            window.Position = new PixelPoint(screenPoint.X - offsetX, screenPoint.Y - offsetY);
            window.BeginMoveDrag(pressed);
        }, DispatcherPriority.Render);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>关闭走 Window.Close():托盘化/退出确认逻辑在 MainWindow.OnClosing 统一处理。</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => HostWindow?.Close();
}

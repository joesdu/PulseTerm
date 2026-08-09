using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 资源监视窗口。窗体规格与任务管理器、链路追踪一致(透明窗口 + 自绘圆角卡片 + 自绘缩放抓取区),
/// 打开即采样一次,关闭时停表。
/// </summary>
public partial class ResourceMonitorWindow : Window
{
    /// <summary>初始化窗口并接线关闭时的清理。</summary>
    public ResourceMonitorWindow()
    {
        InitializeComponent();

        // macOS 上透明窗口会拖垮滚动性能(与设置/追踪窗口同一处结论),那里改用不透明矩形窗口。
        if (OperatingSystem.IsMacOS())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            if (this.TryFindResource("VelaBgPage", out object? page) && page is IBrush brush)
            {
                Background = brush;
            }
            RootCard.BoxShadow = default;
            ApplyCardShape(rounded: false);
        }
        Opened += OnOpened;
        Closed += (_, _) => ViewModel?.Dispose();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                Title = Strings.Format("Monitor_TitleFormat", vm.HostName);
            }
        };
    }

    private ResourceMonitorWindowViewModel? ViewModel => DataContext as ResourceMonitorWindowViewModel;

    /// <summary>打开即拉一次数据 —— 不让用户对着空图表等一个采样周期。</summary>
    private void OnOpened(object? sender, EventArgs e) => _ = ViewModel?.RefreshAsync();

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
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

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
        {
            return;
        }
        bool normal = WindowState == WindowState.Normal;
        ResizeGrips.IsVisible = normal;
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    /// <summary>卡片外圆角 8 与 1px 边框;子元素在边框内侧,内圆弧半径是 8−1=7。</summary>
    private const double InnerRadius = 7;

    /// <summary>
    /// 切换卡片形态。标题栏必须跟着改:它的方角背景会遮掉外框圆角处的描边,表现为角上"断线"。
    /// </summary>
    /// <param name="rounded">true = 普通态圆角浮层,false = 铺满矩形。</param>
    private void ApplyCardShape(bool rounded)
    {
        RootCard.Margin = rounded ? new Thickness(8) : default;
        RootCard.BorderThickness = rounded ? new Thickness(1) : default;
        RootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        TitleBarStrip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
    }
}

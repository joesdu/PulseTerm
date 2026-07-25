using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 远端任务管理器窗口。键位与 Windows 任务管理器对齐:Del 结束任务、F5 立即刷新、
/// Esc 关闭窗口。
/// </summary>
public partial class ProcessManagerView : Window
{
    /// <summary>初始化窗口并在打开时立刻取一轮数据(不必等第一个定时器周期)。</summary>
    public ProcessManagerView()
    {
        InitializeComponent();

        // macOS 上透明窗口会让整窗每帧走全表面 alpha 合成,滚动明显掉帧(与设置窗口同一处
        // 结论)。那里改用不透明窗口,并把自绘的圆角/外边距/投影一并抹平成干净矩形 ——
        // macOS 本身会给窗口圆角,观感不吃亏。
        if (OperatingSystem.IsMacOS())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            if (this.TryFindResource("VelaBgPage", out object? page) && page is IBrush brush)
            {
                Background = brush; // 不透明窗口须有不透明底色,否则未覆盖区域露黑
            }
            RootCard.BoxShadow = default;
            ApplyCardShape(rounded: false);
        }
        Opened += OnOpened;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private ProcessManagerViewModel? ViewModel => DataContext as ProcessManagerViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }
        Title = Strings.Format("Proc_TitleFormat", viewModel.HostLabel);

        // 确认与剪贴板都只有视图层拿得到,按隧道面板的做法用委托注入而不是服务定位。
        viewModel.ConfirmAction = (title, body) =>
            MessageDialog.ConfirmAsync(this, title, body, danger: true);
        viewModel.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };
    }

    private void OnOpened(object? sender, EventArgs e) => _ = ViewModel?.RefreshAsync();

    private void OnClosed(object? sender, EventArgs e) => ViewModel?.Dispose();

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口,双击最大化/还原。</summary>
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

    /// <summary>自绘缩放抓取区:按下即进入原生缩放。最大化时整层已隐藏。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
        {
            return;
        }
        bool normal = WindowState == WindowState.Normal;

        // 最大化时窗口边缘就是屏幕边缘,抓取区留着只会挡住内容并给出错误的缩放光标。
        ResizeGrips.IsVisible = normal;

        // 圆角与阴影留白只在普通态成立:最大化后卡片必须铺满,否则四周会透出桌面,
        // 圆角也会在屏幕边缘切出四个缺口。macOS 已经在构造时压平,这里不再翻回来。
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    /// <summary>
    /// 切换卡片的圆角形态。标题栏与状态栏必须跟着一起改:它们的背景是方角的,
    /// 盖在外框上会把圆角处的描边遮掉一小段,看起来就是四个角"断线"。
    /// </summary>
    /// <param name="rounded">true = 普通态的圆角浮层,false = 铺满的矩形。</param>
    private void ApplyCardShape(bool rounded)
    {
        RootCard.Margin = rounded ? new Thickness(8) : default;
        RootCard.BorderThickness = rounded ? new Thickness(1) : default;
        RootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        TitleBarStrip.CornerRadius = rounded ? new CornerRadius(8, 8, 0, 0) : default;
        StatusStrip.CornerRadius = rounded ? new CornerRadius(0, 0, 8, 8) : default;
    }

    // 推迟关闭:同步 Close 会让本轮点击的后续路由打到已销毁的窗口(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                this.PostClose();
                e.Handled = true;
                return;
            case Key.F5:
                ViewModel?.RefreshCommand.Execute().Subscribe();
                e.Handled = true;
                return;
            // Del 只在列表有焦点时结束任务:焦点在搜索框里时它得留给文本编辑。
            case Key.Delete when ProcessList.IsKeyboardFocusWithin:
                ViewModel?.EndTaskCommand.Execute().Subscribe();
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }
}

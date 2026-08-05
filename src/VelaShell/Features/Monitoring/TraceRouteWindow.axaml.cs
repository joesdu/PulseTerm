using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Common;
using VelaShell.Core.Resources;

namespace VelaShell.Features.Monitoring;

/// <summary>链路追踪窗口。窗体规格与任务管理器一致(透明窗口 + 自绘圆角卡片 + 自绘缩放抓取区)。</summary>
public partial class TraceRouteWindow : Window
{
    /// <summary>初始化窗口;打开即对当前会话的主机发起一次追踪。</summary>
    public TraceRouteWindow()
    {
        InitializeComponent();

        // macOS 上透明窗口会拖垮滚动性能(与设置窗口同一处结论),那里改用不透明矩形窗口。
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
            if (ViewModel is not { } vm)
            {
                return;
            }
            Title = Strings.Format("Trace_TitleFormat", vm.SessionLabel);
            // 文件选择器与浏览器只有视图层拿得到,按隧道面板的做法用委托注入。
            vm.DatabaseFilePicker = PickDatabaseAsync;
            vm.UrlOpener = OpenUrlAsync;
        };
    }

    private TraceRouteViewModel? ViewModel => DataContext as TraceRouteViewModel;

    /// <summary>打开即开跑 —— 用户点这个按钮就是想看结果,不该再点一次开始。</summary>
    private void OnOpened(object? sender, EventArgs e) => ViewModel?.StartCommand.Execute().Subscribe();

    /// <summary>选择离线归属地库。官方下载是 .mmdb.gz,因此两种后缀都收,解压交给 VM。</summary>
    private async Task<string?> PickDatabaseAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Trace_GeoPick"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            FileTypeFilter =
            [
                new(Strings.Get("Trace_GeoFileType")) { Patterns = ["*.mmdb", "*.gz"] }
            ]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task OpenUrlAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new(url));
        }
    }

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

    /// <summary>
    /// 卡片外圆角 8 与 1px 边框;子元素被布局在边框内侧,内侧圆弧半径是 8−1=7。
    /// 子元素若也用 8,它的圆角背景会盖住外框在圆弧处的描边 —— 表现为四个角"断线"。
    /// </summary>
    private const double InnerRadius = 7;

    /// <summary>
    /// 切换卡片形态。标题栏与状态栏必须跟着改:它们的方角背景会遮掉外框圆角处的描边,
    /// 表现为四个角"断线"。
    /// </summary>
    /// <param name="rounded">true = 普通态圆角浮层,false = 铺满矩形。</param>
    private void ApplyCardShape(bool rounded)
    {
        RootCard.Margin = rounded ? new Thickness(8) : default;
        RootCard.BorderThickness = rounded ? new Thickness(1) : default;
        RootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        TitleBarStrip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
        StatusStrip.CornerRadius = rounded ? new CornerRadius(0, 0, InnerRadius, InnerRadius) : default;
    }
}

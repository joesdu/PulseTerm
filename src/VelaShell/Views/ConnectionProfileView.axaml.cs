using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接配置编辑窗口,用于新建或编辑连接档案并支持保存后立即连接。</summary>
public partial class ConnectionProfileView : Window
{
    private bool _protoIndicatorPlaced;
    private Avalonia.Animation.Transitions? _protoIndicatorTransitions;
    private (double X, double W) _protoIndicatorGeometry = (-1, -1);

    /// <summary>屏幕工作区四周留出的余量(DIP):窗口不贴边,也给投影留出位置。</summary>
    private const double ScreenEdgeMargin = 48;

    /// <summary>
    /// 对话框自身的高度上限(DIP,含卡片外的 16 投影余量)。屏幕再高也不越过它 ——
    /// 「能放下」不等于「该放这么高」:S3 展开高级选项后有二十来行,顺着屏幕长到 1300+ 的窗口
    /// 既难扫读、又把主窗口整个盖住。超出的部分交给表单区滚动。
    /// 取 768 与设置窗口(948×768)对齐,应用里的大弹窗保持同一个高度上限。
    /// </summary>
    private const double PreferredMaxHeight = 768;

    /// <summary>初始化连接配置窗口,并在打开时绑定命令与加载分组数据。</summary>
    public ConnectionProfileView()
    {
        InitializeComponent();
        // 必须在 Show 之前钳一次:等到 Opened 再钳,窗口已经按未钳制的高度量好并定过位了,
        // 用户会先看到一个高过屏幕的窗口闪一下,而且它的 Y 可能已经是负数。
        ApplyScreenBounds(preferCurrentScreen: false);
        Opened += OnOpened;
        // 展开「高级选项」会把窗口撑高(SizeToContent="Height"),贴着屏幕下沿开的对话框
        // 会就此长到工作区外面;每次尺寸变化都把窗口拉回工作区内。
        SizeChanged += (_, _) => ClampToWorkingArea();
        // 滑动下划线跟随布局(字体加载、DPI 变化都会改按钮宽度);几何未变时短路。
        LayoutUpdated += (_, _) => UpdateProtoTabIndicator();
    }

    /// <summary>
    /// 把窗口的最大尺寸钉在屏幕工作区与 <see cref="PreferredMaxHeight" /> 之内。
    /// 表单一列到底、字段数由协议决定 ——
    /// 插件协议(S3 声明了十来个字段)会让 <c>SizeToContent="Height"</c> 一路长过屏幕,
    /// 底部的保存/连接按钮点不到。超出的部分交给表单区的 ScrollViewer。
    /// </summary>
    /// <param name="preferCurrentScreen">
    /// 是否优先取窗口当前所在的屏幕(显示之后才有意义);否则取主屏。
    /// </param>
    private void ApplyScreenBounds(bool preferCurrentScreen)
    {
        if (ResolveScreen(preferCurrentScreen) is not { Scaling: > 0 } screen)
        {
            return;
        }
        double screenLimit = Math.Max(240, (screen.WorkingArea.Height / screen.Scaling) - ScreenEdgeMargin);
        // 小屏按屏幕钳,大屏按设计上限钳:两者取小。
        MaxHeight = Math.Min(PreferredMaxHeight, screenLimit);
        MaxWidth = Math.Max(320, (screen.WorkingArea.Width / screen.Scaling) - ScreenEdgeMargin);
    }

    /// <summary>把窗口位置夹回屏幕工作区(尺寸变化后仍留在原处会露到屏幕外)。</summary>
    private void ClampToWorkingArea()
    {
        if (ResolveScreen(preferCurrentScreen: true) is not { Scaling: > 0 } screen)
        {
            return;
        }
        PixelRect area = screen.WorkingArea;
        PixelSize size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        PixelPoint current = Position;
        int x = Math.Clamp(current.X, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = Math.Clamp(current.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        if (x != current.X || y != current.Y)
        {
            Position = new(x, y);
        }
    }

    private Screen? ResolveScreen(bool preferCurrentScreen)
    {
        try
        {
            return (preferCurrentScreen ? Screens.ScreenFromWindow(this) : null) ?? Screens.Primary;
        }
        catch
        {
            // 屏幕信息不是每个后端/时机都拿得到(无头测试、远程桌面):拿不到就不钳。
            return null;
        }
    }

    /// <summary>
    /// 把滑动下划线对齐到当前协议标签:首次落位不动画,此后位置与宽度经
    /// 180ms 过渡滑动 —— 取代旧实现里两个按钮各自下划线的瞬时跳变。
    /// </summary>
    private void UpdateProtoTabIndicator()
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        // 内建协议按枚举分派;插件协议的页签是动态生成的,没有可以 x:Name 引用的按钮,
        // 因此按 Tag(协议 id)在可视树里找那一个 —— 这也是"新增协议不必改界面"的代价与边界。
        Button? target = viewModel.ConnectionType switch
        {
            ConnectionType.SFTP => SftpTab,
            ConnectionType.FTP => FtpTab,
            ConnectionType.Plugin => FindPluginTab(viewModel.PluginProtocolId),
            _ => SshTab
        };
        if (target is null || target.Bounds.Width <= 0)
        {
            return;
        }
        Avalonia.Point origin = target.TranslatePoint(default, ProtoTabsPanel) ?? default;
        (double X, double W) geometry = (Math.Round(origin.X), Math.Round(target.Bounds.Width));
        if (geometry == _protoIndicatorGeometry && ProtoTabIndicator.IsVisible)
        {
            return;
        }
        _protoIndicatorGeometry = geometry;
        bool animate = _protoIndicatorPlaced;
        if (!animate)
        {
            _protoIndicatorTransitions ??= ProtoTabIndicator.Transitions;
            ProtoTabIndicator.Transitions = null;
        }
        ProtoTabIndicator.Width = geometry.W;
        ProtoTabIndicator.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"translateX({geometry.X}px)"));
        ProtoTabIndicator.IsVisible = true;
        if (!animate)
        {
            _protoIndicatorPlaced = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ProtoTabIndicator.Transitions ??= _protoIndicatorTransitions,
                Avalonia.Threading.DispatcherPriority.Render);
        }
    }

    /// <summary>按协议 id 找到对应的插件页签按钮;还没渲染出来时返回 null(下一次布局会再来)。</summary>
    private Button? FindPluginTab(string? protocolId) =>
        protocolId is null
            ? null
            : ProtoTabsPanel.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Tag as string == protocolId);

    private void ApplyProtoTabFocusAdorner()
    {
        var buttons = this.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("proto-tab"))
            .ToList();
        foreach (Button button in buttons)
        {
            var layer = AdornerLayer.GetAdornerLayer(button);
            layer?.DefaultFocusAdorner = null;

            var controls = new[] { button }
                .Concat(button.GetVisualDescendants().OfType<Control>())
                .ToList();
            foreach (Control control in controls)
            {
                control.FocusAdorner = null;
                control.SetValue(AdornerLayer.DefaultFocusAdornerProperty, null);
            }
            button.GotFocus += ProtoTab_GotFocus;
            button.LostFocus += ProtoTab_LostFocus;
        }
    }

    private void ProtoTab_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        // 焦点框只属于键盘导航:鼠标点击同样会落焦,若不区分,点完标签后强调色
        // 填充+描边会一直挂在按钮上直到焦点移走 —— 正是本窗口标签切换"高亮卡住"的元凶。
        if (sender is Button button && e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
        {
            AdornerLayer.SetAdorner(button, CreateVelaFocusAdorner());
        }
    }

    private static void ProtoTab_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Button button)
        {
            AdornerLayer.SetAdorner(button, null);
        }
    }

    private Border CreateVelaFocusAdorner()
    {
        Border border = new()
        {
            Background = this.FindResource("VelaAccentDim") as IBrush,
            BorderBrush = this.FindResource("VelaAccent") as IBrush,
            BorderThickness = new Avalonia.Thickness(1),
            IsHitTestVisible = false
        };
        return border;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        // 对话框是 CenterOwner:显示之后才知道落在哪块屏上,多屏下重新钳一次。
        ApplyScreenBounds(preferCurrentScreen: true);
        ClampToWorkingArea();
        ApplyProtoTabFocusAdorner();
        // 协议切换只改按钮前景色、不触发布局,滑动下划线必须由 VM 属性变化驱动。
        // 直接盯 ConnectionType 本身,而不是逐个列举 IsXxxSelected —— 后者每加一个协议
        // 都要回来补一项,漏掉就表现为「下划线停在上一个页签」。
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionProfileViewModel.ConnectionType)
                or nameof(ConnectionProfileViewModel.PluginProtocolId))
            {
                UpdateProtoTabIndicator();
            }
        };
        UpdateProtoTabIndicator();
        // 保存/连接/取消命令由按钮点击触发,回调仍在输入事件栈内:推迟关闭,避免后续路由
        // 打到已销毁的窗口刷 "PlatformImpl is null" 警告。
        viewModel.SaveCommand.Subscribe(result => this.PostClose(result));
        viewModel.ConnectCommand.Subscribe(result => this.PostClose(result));
        viewModel.CancelCommand.Subscribe(result => this.PostClose(result));
        await viewModel.LoadGroupsAsync();
    }

    /// <summary>窗口关闭时退订注册表事件,免得单例注册表上挂满已关闭对话框的视图模型。</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ConnectionProfileViewModel)?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Esc 等价于点击取消:经 CancelCommand 走与取消按钮完全相同的关闭路径(不保存改动)。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is ConnectionProfileViewModel viewModel)
            {
                viewModel.CancelCommand.Execute().Subscribe();
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}

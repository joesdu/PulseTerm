using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    /// <summary>初始化连接配置窗口,并在打开时绑定命令与加载分组数据。</summary>
    public ConnectionProfileView()
    {
        InitializeComponent();
        Opened += OnOpened;
        // 滑动下划线跟随布局(字体加载、DPI 变化都会改按钮宽度);几何未变时短路。
        LayoutUpdated += (_, _) => UpdateProtoTabIndicator();
    }

    /// <summary>
    /// 把滑动下划线对齐到当前协议标签(SSH/SFTP/FTP):首次落位不动画,此后位置与宽度经
    /// 180ms 过渡滑动 —— 取代旧实现里两个按钮各自下划线的瞬时跳变。
    /// </summary>
    private void UpdateProtoTabIndicator()
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        // 曾是「IsSftpSelected ? SftpTab : SshTab」的二元三目 —— 加第三个协议时必须改成按枚举分派,
        // 否则 FTP 选中后下划线会停在 SSH 上。
        Button target = viewModel.ConnectionType switch
        {
            ConnectionType.SFTP => SftpTab,
            ConnectionType.FTP => FtpTab,
            _ => SshTab
        };
        if (target.Bounds.Width <= 0)
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
        ApplyProtoTabFocusAdorner();
        // 协议切换只改按钮前景色、不触发布局,滑动下划线必须由 VM 属性变化驱动。
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionProfileViewModel.IsSshSelected)
                or nameof(ConnectionProfileViewModel.IsSftpSelected))
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

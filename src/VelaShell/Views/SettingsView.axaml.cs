using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>设置窗口视图,承载各设置分页并处理保存、重置与关闭等交互。</summary>
public partial class SettingsView : Window
{
    private SettingsViewModel? _viewModel;

    /// <summary>初始化 <see cref="SettingsView"/>,加载组件并绑定视图模型的关闭请求。</summary>
    public SettingsView()
    {
        InitializeComponent();
        ApplyMacOsOpaqueWindow();
        DataContextChanged += (_, _) =>
        {
            _viewModel?.CloseRequested -= OnCloseRequested;
            _viewModel = DataContext as SettingsViewModel;
            _viewModel?.CloseRequested += OnCloseRequested;
        };
    }

    /// <summary>
    /// macOS 上把设置窗口改为【不透明】,消除滚动卡顿。透明窗口(TransparencyLevelHint=Transparent)
    /// 在 macOS 上会让整窗每帧走全表面 alpha 合成,滚动时(即便内容只是纯文本行)明显掉帧;
    /// 不透明的主窗口则顺滑。代价是自绘的圆角/外投影浮层观感——故一并抹平外边距、圆角、外框与投影,
    /// 让窗口成为干净的矩形。其他平台保持原透明浮层不变。
    /// </summary>
    private void ApplyMacOsOpaqueWindow()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        if (this.TryFindResource("VelaBgSurface", out object? surface) && surface is IBrush brush)
        {
            Background = brush; // 不透明窗口须有不透明底色,避免未覆盖区域露黑
        }
        if (this.FindControl<Border>("RootBorder") is { } root)
        {
            root.Margin = new Thickness(0);
            root.CornerRadius = new CornerRadius(0);
            root.BorderThickness = new Thickness(0);
            root.BoxShadow = default; // 清空 32px 模糊投影(实心底上无意义且徒增开销)
        }
    }

    // CloseRequested 由保存/取消命令(按钮点击)触发,仍在输入事件栈内:推迟关闭,
    // 避免后续路由打到已销毁的窗口刷 "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void OnCloseRequested(object? sender, EventArgs e) => this.PostClose();

    /// <summary>Esc 以取消语义关闭设置窗口,未保存预览由 <see cref="OnClosed" /> 回滚。</summary>
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

    /// <summary>窗口以任意方式关闭(取消/Esc/系统关闭)都要回滚未保存的外观预览。</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel?.NotifyClosed();
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>恢复默认是破坏性操作:先确认再执行,防止误点丢失全部设置(设置审计 C-11)。</summary>
    private async void ResetToDefaults_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (_viewModel is null)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(
            this,
            Strings.Get("Settings_ResetConfirmTitle"),
            Strings.Get("Settings_ResetConfirmMessage"),
            danger: true
        );
        if (confirmed)
        {
            _viewModel.ResetCommand.Execute().Subscribe();
        }
    }
}

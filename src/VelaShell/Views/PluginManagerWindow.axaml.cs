using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 插件管理窗口(自绘卡片窗口,与资源监视器同规格):列出插件、启停、卸载、
/// 撤销终端授权、从 .vpx 安装。
/// </summary>
public partial class PluginManagerWindow : Window
{
    /// <summary>初始化窗口(macOS 退回不透明矩形,与其它自绘窗体同一结论)。</summary>
    public PluginManagerWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            if (this.TryFindResource("VelaBgPage", out object? page) && page is IBrush brush)
            {
                Background = brush;
            }
            ApplyCardShape(rounded: false);
        }
        Closed += (_, _) => (DataContext as PluginManagerViewModel)?.Dispose();
    }

    private PluginManagerViewModel? ViewModel => DataContext as PluginManagerViewModel;

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

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            _ = vm.ToggleAsync(row);
        }
    }

    private void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PluginRowViewModel row } && ViewModel is { } vm)
        {
            _ = vm.RevokeTerminalAsync(row);
        }
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PluginRowViewModel row } || ViewModel is not { } vm)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(this,
            Strings.Get("PluginManager_Uninstall"),
            Strings.Format("PluginManager_ConfirmUninstall", row.DisplayName),
            confirmText: Strings.Get("PluginManager_Uninstall"),
            danger: true);
        if (confirmed)
        {
            await vm.UninstallAsync(row);
        }
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("PluginManager_Install"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Get("PluginManager_VpxFilter")) { Patterns = ["*.vpx"] }
            ]
        });
        if (files is [{ } file] && file.TryGetLocalPath() is { } path)
        {
            await vm.InstallFromVpxAsync(path);
        }
    }

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
        if (this.FindControl<Panel>("ResizeGrips") is { } grips)
        {
            grips.IsVisible = normal;
        }
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    private const double InnerRadius = 7;

    private void ApplyCardShape(bool rounded)
    {
        if (this.FindControl<Border>("RootCard") is { } card)
        {
            card.Margin = rounded ? new Thickness(8) : default;
            card.BorderThickness = rounded ? new Thickness(1) : default;
            card.CornerRadius = rounded ? new CornerRadius(8) : default;
            card.BoxShadow = rounded ? card.BoxShadow : default;
        }
        if (this.FindControl<Border>("TitleBarStrip") is { } strip)
        {
            strip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
        }
    }
}

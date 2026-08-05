using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>资源监控视图:面板可见时每秒轮询刷新 CPU/内存等资源指标。</summary>
public partial class ResourceMonitorView : UserControl
{
    private DispatcherTimer? _pollTimer;

    /// <summary>初始化资源监控视图并挂接可见性驱动的轮询逻辑。</summary>
    public ResourceMonitorView()
    {
        InitializeComponent();

        // 仅在面板实际可见时轮询(它位于标签悬停提示气泡中):
        // 打开时立即拉取一次,之后每秒刷新(§11),关闭时停止。
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Refresh();
        _pollTimer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Refresh());
        _pollTimer.Start();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void Refresh()
    {
        if (DataContext is ResourceMonitorViewModel vm)
        {
            _ = vm.RefreshAsync();
        }
    }
}

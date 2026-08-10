using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.ViewModels;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 每插件一个的界面能力(<see cref="IUiApi" />)实现(进程内插件):
/// 在宿主 UI 线程调用内容工厂拿到插件自建的 Avalonia 控件,按
/// <see cref="PanelOptions.DisplayMode" /> 呈现为停靠文档或独立窗口;
/// 实例释放(插件停用)时全部关闭 —— 插件离场,宿主 UI 不残留。
/// </summary>
internal sealed class PluginUiApi(string pluginId, IPluginLogger log, Func<MainWindowViewModel?> mainViewModel)
    : IUiApi, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PluginPanel> _panels = [];
    private bool _disposed;

    public async Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contentFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        PluginPanel panel = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 工厂在 UI 线程调用:插件可在其中放心构造任何 Avalonia 控件(含编译期 AXAML 视图)。
            Control content = contentFactory() as Control
                ?? throw new ArgumentException("contentFactory must return an Avalonia Control.", nameof(contentFactory));
            if (options.DisplayMode == PanelDisplayMode.Document)
            {
                MainWindowViewModel viewModel = mainViewModel()
                    ?? throw new InvalidOperationException("Host main window is not ready; cannot open a docked panel.");
                return new PluginPanel(pluginId, log, options, content, viewModel.Layout);
            }
            return new PluginPanel(pluginId, log, options, content, MainWindow());
        });
        lock (_gate)
        {
            if (_disposed)
            {
                _ = panel.CloseAsync();
                throw new ObjectDisposedException(nameof(PluginUiApi));
            }
            _panels.Add(panel);
        }
        panel.Closed += () =>
        {
            lock (_gate)
            {
                _panels.Remove(panel);
            }
        };
        return panel;
    }

    private static Window? MainWindow() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>插件停用:关闭其全部面板(封送到 UI 线程,尽力而为)。</summary>
    public void Dispose()
    {
        List<PluginPanel> panels;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            panels = [.. _panels];
            _panels.Clear();
        }
        foreach (PluginPanel panel in panels)
        {
            _ = panel.CloseAsync();
        }
    }
}

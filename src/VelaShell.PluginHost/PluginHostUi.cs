using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离插件的界面能力:UI 在本进程(内建 Avalonia)呈现为**独立卡片窗口**
/// (<see cref="PluginHostShellWindow" />,与主程序资源监视窗口同规格),插件可用
/// 全部 Avalonia 能力(AXAML/样式/国际化/第三方组件包)。
/// </summary>
/// <remarks>
/// 隔离模式**不做** dock 内嵌:跨进程窗口收养(SetParent)与 dock 的单宿主
/// reparenting 有根本张力(切标签反复摘挂跨进程窗口会卡顿、窗口飘出)。因此
/// <see cref="PanelDisplayMode.Document" /> 与 <see cref="PanelDisplayMode.Window" />
/// 在隔离模式下都是独立卡片窗口;需要真·dock 标签页请用 <c>inProcess</c> 宿主模式。
/// 面板数变化上报宿主(空闲回收的"无打开面板"条件)。
/// </remarks>
internal sealed class PluginHostUi(string pluginId, IPluginLogger log, RpcConnection rpc)
    : IUiApi, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IPluginPanel> _panels = [];
    private bool _disposed;

    /// <summary>上报当前面板数(尽力而为)。</summary>
    private void NotifySurfaces()
    {
        int count;
        lock (_gate)
        {
            count = _panels.Count;
        }
        _ = rpc.NotifyAsync(PluginRpc.UiSurfaces, new UiSurfacesNotification(count));
    }

    public async Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contentFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.DisplayMode == PanelDisplayMode.Document)
        {
            // 隔离插件不做 dock 内嵌 —— 独立卡片窗口稳定且与主程序统一。这是常态,不是异常。
            log.Info($"Panel '{options.Title}': shown as a window (isolated plugins use windows; use inProcess for in-dock tabs).");
        }
        IPluginPanel panel = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Control content = RequireControl(contentFactory());
            // 自绘卡片壳:透明圆角卡片 + 标题栏 + 三连按钮 + 缩放抓取区,配色用宿主下发的
            // Vela* 令牌 —— 与主程序资源监视/任务管理器窗口统一。
            var window = new PluginHostShellWindow(options.Title, pluginId, content)
            {
                Width = Math.Max(options.WindowWidth, 280),
                Height = Math.Max(options.WindowHeight, 200),
                MinWidth = 280,
                MinHeight = 200
            };
            var created = new LocalPanel(window, log, pluginId);
            window.Show();
            return (IPluginPanel)created;
        });
        Track(panel);
        return panel;
    }

    private static Control RequireControl(object content) =>
        content as Control
        ?? throw new ArgumentException("contentFactory must return an Avalonia Control.");

    private void Track(IPluginPanel panel)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                _ = panel.CloseAsync();
                throw new ObjectDisposedException(nameof(PluginHostUi));
            }
            _panels.Add(panel);
        }
        panel.Closed += () =>
        {
            lock (_gate)
            {
                _panels.Remove(panel);
            }
            NotifySurfaces();
        };
        NotifySurfaces();
    }

    /// <summary>插件停用:关闭其全部窗口。</summary>
    public void Dispose()
    {
        List<IPluginPanel> panels;
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
        foreach (IPluginPanel panel in panels)
        {
            _ = panel.CloseAsync();
        }
    }

    /// <summary>本进程独立卡片窗口形态的面板句柄。</summary>
    private sealed class LocalPanel : IPluginPanel
    {
        private readonly Window _window;
        private readonly IPluginLogger _log;
        private readonly string _pluginId;
        private int _closed;

        public LocalPanel(Window window, IPluginLogger log, string pluginId)
        {
            _window = window;
            _log = log;
            _pluginId = pluginId;
            window.Closed += (_, _) => NotifyClosed();
        }

        public string PanelId { get; } = Guid.NewGuid().ToString("N");

        public bool IsOpen => _closed == 0;

        public event Action? Closed;

        public async Task CloseAsync()
        {
            if (!IsOpen)
            {
                return;
            }
            try
            {
                await Dispatcher.UIThread.InvokeAsync(_window.Close).GetTask().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 进程停机:派发循环收摊会取消排队作业,窗口随进程消亡,无需噪声。
                NotifyClosed();
            }
        }

        public ValueTask DisposeAsync() => new(CloseAsync());

        private void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }
            if (Closed is { } handlers)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handlers();
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Panel Closed handler threw (panel of {_pluginId}).", ex);
                    }
                });
            }
            Closed = null;
        }
    }
}

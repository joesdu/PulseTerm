using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.Views;

namespace VelaShell.Services.Plugins;

/// <summary>
/// <see cref="IPluginPanel" /> 的宿主实现(进程内插件):内容是插件自建的 Avalonia 控件,
/// 呈现为停靠文档或独立窗口。面板只管生命周期 —— 控件的事件与更新由插件直接操作,
/// 不经过宿主。关闭从任意线程封送到 UI 线程。
/// </summary>
internal sealed class PluginPanel : IPluginPanel
{
    private readonly string _pluginId;
    private readonly IPluginLogger _log;
    private readonly PluginDocument? _document;
    private readonly DockWorkspace? _workspace;
    private readonly PluginPanelWindow? _window;
    private readonly Action<DockDocument>? _onDocumentRemoved;
    private int _closed;

    public string PanelId { get; } = Guid.NewGuid().ToString("N");

    public bool IsOpen => _closed == 0;

    public event Action? Closed;

    /// <summary>停靠文档形态。调用方须在 UI 线程构造。</summary>
    public PluginPanel(string pluginId, IPluginLogger log, PanelOptions options, Control content, DockWorkspace workspace)
    {
        _pluginId = pluginId;
        _log = log;
        _workspace = workspace;
        _document = new(PanelId, options.Title, pluginId, content);
        // 用户关闭标签(CloseDocument)与程序撤除都会走 DocumentRemoved —— 单一挂点,
        // 面板生死与文档在树上的存在性严格一致。
        _onDocumentRemoved = removed =>
        {
            if (ReferenceEquals(removed, _document))
            {
                NotifyClosed();
            }
        };
        workspace.DocumentRemoved += _onDocumentRemoved;
        workspace.AddDocument(_document);
    }

    /// <summary>独立窗口形态(宿主同款自绘卡片窗口)。调用方须在 UI 线程构造。</summary>
    public PluginPanel(string pluginId, IPluginLogger log, PanelOptions options, Control content, Window? owner)
    {
        _pluginId = pluginId;
        _log = log;
        _window = new PluginPanelWindow
        {
            Width = Math.Max(options.WindowWidth, 280),
            Height = Math.Max(options.WindowHeight, 200)
        };
        _window.SetTitle(options.Title, pluginId);
        _window.SetContent(content);
        _window.Closed += (_, _) => NotifyClosed();
        if (owner is not null)
        {
            _window.Show(owner);
        }
        else
        {
            _window.Show();
        }
    }

    public async Task CloseAsync()
    {
        if (!IsOpen)
        {
            return;
        }
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_document is not null && _workspace is not null)
                {
                    _workspace.RemoveDocument(_document); // 程序性撤除;DocumentRemoved 挂点统一触发 NotifyClosed
                }
                _window?.Close();
            }).GetTask().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 应用退出:Dispatcher 关闭会取消排队作业,窗口/文档随进程消亡,无需噪声。
            NotifyClosed();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>面板生命终点(用户关闭/程序关闭/插件停用),幂等。</summary>
    private void NotifyClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        if (_workspace is not null && _onDocumentRemoved is not null)
        {
            _workspace.DocumentRemoved -= _onDocumentRemoved;
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

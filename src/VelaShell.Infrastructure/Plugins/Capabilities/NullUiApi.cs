using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 界面能力不可用时(无 UI 宿主、测试环境)的空实现:面板"打开"但不显示
/// (内容工厂不会被调用),关闭是空操作 —— 插件不必为 headless 场景写分支。
/// </summary>
internal sealed class NullUiApi(IPluginLogger log) : IUiApi
{
    public Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory, CancellationToken cancellationToken = default)
    {
        log.Warn($"Panel '{options.Title}' not shown: UI capability is unavailable in this host.");
        return Task.FromResult<IPluginPanel>(new NoopPanel());
    }

    private sealed class NoopPanel : IPluginPanel
    {
        public string PanelId { get; } = Guid.NewGuid().ToString("N");
        public bool IsOpen => false;
        public event Action? Closed { add { } remove { } }
        public Task CloseAsync() => Task.CompletedTask;

        public Task ActivateAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

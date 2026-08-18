using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Ui;

namespace VelaPlugin1;

/// <summary>
/// 插件入口。整个程序集里恰好有一个公开、非抽象、带 <see cref="VelaPluginAttribute" />
/// 且实现 <see cref="IVelaPlugin" /> 的类型,并且要有公开无参构造。
/// </summary>
[VelaPlugin]
public sealed class VelaPlugin1Plugin : IVelaPlugin
{
    private IPluginContext? _context;

    /// <summary>
    /// 激活。**必须快速返回**(宿主限时 10 秒):要跑长任务就自己开后台任务,
    /// 用 <c>context.Shutdown</c> 令牌响应停机。
    /// </summary>
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Log.Info("VelaPlugin1 activated.");

        context.Commands.Register(new(
            $"{context.PluginId}.open-panel",
            "VelaPlugin1: Open panel",
            "VelaPlugin1",
            OpenPanelAsync));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 打开面板。<c>contentFactory</c> 在 **UI 线程**被调用,必须返回一个
    /// <c>Avalonia.Controls.Control</c>;之后对控件的操作请经 Avalonia 的
    /// <c>Dispatcher.UIThread</c> 封送。插件停用时其全部面板由宿主自动关闭。
    /// <para>
    /// inProcess 插件的 <see cref="PanelDisplayMode.Document" /> 面板会停靠进主窗口标签区;
    /// isolated 插件一律是独立卡片窗口(跨进程停靠已弃用)。
    /// </para>
    /// </summary>
    private async Task OpenPanelAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        await context.Ui.ShowPanelAsync(
            new() { Title = "VelaPlugin1", DisplayMode = PanelDisplayMode.Document },
            () => new DemoPanel(context),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 停用(限时约 2 秒)。经 SDK 注册的命令、事件订阅与面板由宿主自动清理,
    /// 这里只收尾自己的资源。
    /// </summary>
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _context = null;
        return Task.CompletedTask;
    }
}

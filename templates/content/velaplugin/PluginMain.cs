using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

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

        // 注册与清单里声明的占位命令同 id 的真实处理器(id 必须以插件 id 为前缀)。
        context.Commands.Register(new(
            $"{context.PluginId}.hello",
            "VelaPlugin1: Say hello",
            "VelaPlugin1",
            SayHelloAsync));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 命令体。在后台线程调用;抛异常由宿主捕获并记进插件日志,不会波及宿主。
    /// 这里顺手演示一个能力调用:列出当前会话。
    /// 完整能力面见 <see cref="IPluginContext" /> —— Sessions / RemoteFs / RemoteExec /
    /// Terminal / Storage / Secrets / TimeSeries / Clipboard / Ui / Events / Protocols。
    /// </summary>
    private async Task SayHelloAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        context.Log.Info($"Hello! VelaShell currently has {sessions.Count} session(s).");
    }

    /// <summary>
    /// 停用(限时约 2 秒)。经 SDK 注册的命令与事件订阅由宿主自动清理,这里只收尾自己的资源。
    /// 注意别把自己的类型塞进宿主的静态字段或长命事件:停用后本插件的 ALC 要被卸载。
    /// </summary>
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _context = null;
        return Task.CompletedTask;
    }
}

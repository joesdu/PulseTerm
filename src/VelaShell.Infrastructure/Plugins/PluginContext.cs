using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Clipboard;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// <see cref="IPluginContext" /> 的宿主实现:聚合每插件的能力实例。
/// <see cref="Dispose" /> 负责拆掉一切把插件程序集钉在内存里的引用
/// (命令注册、事件订阅),这是可收集 ALC 真正可回收的前提。
/// </summary>
internal sealed class PluginContext : IPluginContext, IDisposable
{
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }
    public required string DataDirectory { get; init; }
    public required IHostInfo Host { get; init; }
    public required IPluginLogger Log { get; init; }
    public required IPluginStorage Storage { get; init; }
    public required ISessionsApi Sessions { get; init; }
    public required IRemoteFsApi RemoteFs { get; init; }
    public required IRemoteExecApi RemoteExec { get; init; }
    public required ICommandsApi Commands { get; init; }
    public required IHostEvents Events { get; init; }
    public required IUiApi Ui { get; init; }
    public required ISecretsApi Secrets { get; init; }
    public required IClipboardApi Clipboard { get; init; }
    public required PluginSdk.Terminal.ITerminalApi Terminal { get; init; }
    public required IProtocolsApi Protocols { get; init; }
    public required CancellationToken Shutdown { get; init; }

    public void Dispose()
    {
        (Commands as IDisposable)?.Dispose();
        (Events as IDisposable)?.Dispose();
        (Ui as IDisposable)?.Dispose();
        // 协议注册要在这里撤:它握着插件实现的引用,不撤 ALC 就回收不掉,
        // 而且用户还会在连接页看到一个再也连不上的协议页签。
        (Protocols as IDisposable)?.Dispose();
    }
}

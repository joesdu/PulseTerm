using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离进程侧的 <see cref="IPluginContext" />:远程能力为 RPC 代理,
/// 存储/日志/界面在本进程执行(界面 = 本进程内建的完整 Avalonia),
/// 插件源码与进程内模式完全一致 —— 这正是 SDK 传输无关设计兑现的地方。
/// </summary>
internal sealed class RemotePluginContext : IPluginContext, IDisposable
{
    /// <summary>组装上下文与各能力代理。</summary>
    public RemotePluginContext(RpcConnection rpc, string pluginId, string pluginVersion, string dataDirectory,
        HandshakeResponse hello, CancellationToken shutdown)
    {
        PluginId = pluginId;
        PluginVersion = pluginVersion;
        DataDirectory = dataDirectory;
        Shutdown = shutdown;
        HostInfo = new(hello);
        Log = new RpcLogger(rpc, pluginId);
        // KV 落宿主 SonnetDB(经 RPC):按插件 id 命名空间隔离,卸载整体清除;
        // DataDirectory 仍归插件自由写文件(大块数据),卸载时同样被清扫。
        Storage = new RpcStorage(rpc);
        TimeSeries = new RpcTimeSeries(rpc);
        Sessions = new RpcSessions(rpc);
        RemoteFsProxy = new(rpc);
        RemoteExec = new RpcRemoteExec(rpc);
        CommandsProxy = new(rpc, Log);
        EventsHub = new(Log);
        UiLocal = new(pluginId, Log, rpc);
        Secrets = new RpcSecrets(rpc);
        Clipboard = new RpcClipboard(rpc);
        Terminal = new RpcTerminal(rpc);

        // 语言/主题事件顺带刷新 HostInfo 的实时值;主题同步给本进程 Avalonia。
        EventsHub.LocaleChanged += locale => HostInfo.Locale = locale;
        EventsHub.ThemeChanged += theme =>
        {
            HostInfo.Theme = theme;
            PluginHostApp.ApplyHostTheme(theme);
        };
    }

    public string PluginId { get; }
    public string PluginVersion { get; }
    public string DataDirectory { get; }
    public IHostInfo Host => HostInfo;
    public IPluginLogger Log { get; }
    public IPluginStorage Storage { get; }
    public VelaShell.PluginSdk.TimeSeries.ITimeSeriesApi TimeSeries { get; }
    public ISessionsApi Sessions { get; }
    public IRemoteFsApi RemoteFs => RemoteFsProxy;
    public IRemoteExecApi RemoteExec { get; }
    public ICommandsApi Commands => CommandsProxy;
    public IHostEvents Events => EventsHub;
    public IUiApi Ui => UiLocal;
    public VelaShell.PluginSdk.Secrets.ISecretsApi Secrets { get; }
    public VelaShell.PluginSdk.Clipboard.IClipboardApi Clipboard { get; }
    public VelaShell.PluginSdk.Terminal.ITerminalApi Terminal { get; }
    public CancellationToken Shutdown { get; }

    /// <summary>通知路由用的具体代理(Program 分发宿主通知)。</summary>
    internal RemoteHostInfo HostInfo { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RpcRemoteFs RemoteFsProxy { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RpcCommands CommandsProxy { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RemoteEventHub EventsHub { get; }

    /// <inheritdoc cref="HostInfo" />
    internal PluginHostUi UiLocal { get; }

    /// <summary>停用收尾:关掉本插件的全部窗口。</summary>
    public void Dispose() => UiLocal.Dispose();
}

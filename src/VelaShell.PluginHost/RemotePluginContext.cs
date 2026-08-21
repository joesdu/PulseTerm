using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;
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
        // 保留具体类型的引用:流式执行的输出经通知回流,Program 要按 token 路由到它。
        RemoteExecProxy = new(rpc);
        RemoteExec = RemoteExecProxy;
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

    /// <summary>
    /// 协议能力在隔离进程里**不可用**:协议是宿主反向调用插件的高频通道(列目录、流式读、
    /// 传输进度),而本进程的 RPC 只承载插件→宿主的请求方向。
    /// <para>
    /// 这里给一个"注册即明确报错"的实现,而不是 <c>null!</c>:清单校验只在插件声明了
    /// <c>contributes.protocols</c> 时才拦 isolated(见 <c>PluginManifestReader.ValidateProtocols</c>),
    /// 不声明清单、直接在 <c>ActivateAsync</c> 里 Register 的插件只能靠这里兜住 ——
    /// 报一句能看懂的话,好过一个空引用。
    /// </para>
    /// </summary>
    public IProtocolsApi Protocols { get; } = new IsolatedProtocols();

    public PluginSdk.Workspaces.IWorkspacesApi Workspaces { get; } = new IsolatedWorkspaces();

    /// <summary>
    /// 隧道能力在隔离进程里**不可用**。它交出去的是一条活的 <see cref="Stream" />;
    /// 跨进程代理一条裸流,除了把每个字节多搬一次、再给它套上一层会断的 RPC 之外
    /// 得不到任何东西 —— 而隧道恰恰是用来承载"一条连接挂几小时"的流的。
    /// 明确报不可用,好过给一个语义已经变质的实现。
    /// </summary>
    public PluginSdk.RemoteTunnel.IRemoteTunnelApi RemoteTunnel { get; } = new IsolatedRemoteTunnel();

    /// <summary>
    /// 终端视图在隔离进程里**不可用**:它交出去的是一个活的原生控件,嵌不进另一个进程的窗口。
    /// (本进程确实有自己的 Avalonia,但那是用来画插件自己的界面的;终端仿真器归宿主进程,
    /// 跨进程把每一帧屏幕搬过来既慢又会丢输入时序。)
    /// </summary>
    public PluginSdk.TerminalView.ITerminalViewApi TerminalView { get; } = new IsolatedTerminalView();

    public CancellationToken Shutdown { get; }

    /// <summary>隔离进程的终端视图退化实现:调用即报不可用。</summary>
    private sealed class IsolatedTerminalView : PluginSdk.TerminalView.ITerminalViewApi
    {
        public bool IsAvailable => false;

        public PluginSdk.TerminalView.IPluginTerminalView Create(
            PluginSdk.TerminalView.TerminalViewOptions? options = null) =>
            throw new NotSupportedException(
                "Terminal views require hostMode \"inProcess\": a live native control cannot be embedded across processes.");
    }

    /// <summary>隔离进程的隧道能力退化实现:调用即报不可用。</summary>
    private sealed class IsolatedRemoteTunnel : PluginSdk.RemoteTunnel.IRemoteTunnelApi
    {
        private static NotSupportedException Unsupported() => new(
            "Remote tunnels require hostMode \"inProcess\": a live byte stream cannot be proxied across processes.");

        public int ActiveTunnels => 0;

        public Task<Stream> OpenUnixSocketAsync(string sessionId, string socketPath,
            PluginSdk.RemoteTunnel.TunnelOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<Stream>(Unsupported());

        public Task<Stream> OpenTcpAsync(string sessionId, string host, int port,
            PluginSdk.RemoteTunnel.TunnelOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<Stream>(Unsupported());
    }

    /// <summary>
    /// 隔离进程的工作台能力退化实现:注册即报不可用。原生控件无法跨进程嵌入,
    /// 所以这个组合在清单校验期就该被挡住 —— 这里只是最后一道兜底。
    /// </summary>
    private sealed class IsolatedWorkspaces : PluginSdk.Workspaces.IWorkspacesApi
    {
        public IDisposable Register(
            PluginSdk.Workspaces.WorkspaceDescriptor descriptor,
            PluginSdk.Workspaces.IWorkspaceProvider provider) =>
            throw new InvalidOperationException(
                "contributes.workspaces requires hostMode \"inProcess\": native controls cannot be embedded across processes.");

        public Task<bool> ProposeConnectionAsync(
            PluginSdk.Workspaces.WorkspaceConnectionProposal proposal,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>隔离进程的协议能力退化实现:注册即报不可用,读传输设置给"不限速"。</summary>
    private sealed class IsolatedProtocols : IProtocolsApi
    {
        public IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem) =>
            throw new InvalidOperationException(
                "contributes.protocols requires hostMode \"inProcess\": the isolated-process RPC does not carry host-to-plugin calls.");

        public IDisposable Register(ProtocolDescriptor descriptor, IProtocolTerminal terminal) =>
            throw new InvalidOperationException(
                "contributes.protocols requires hostMode \"inProcess\": the isolated-process RPC does not carry host-to-plugin calls.");

        public Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProtocolTransferOptions(0, 0, PreserveTimestamps: true));
    }

    /// <summary>通知路由用的具体代理(Program 分发宿主通知)。</summary>
    internal RemoteHostInfo HostInfo { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RpcRemoteFs RemoteFsProxy { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RpcRemoteExec RemoteExecProxy { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RpcCommands CommandsProxy { get; }

    /// <inheritdoc cref="HostInfo" />
    internal RemoteEventHub EventsHub { get; }

    /// <inheritdoc cref="HostInfo" />
    internal PluginHostUi UiLocal { get; }

    /// <summary>停用收尾:关掉本插件的全部窗口。</summary>
    public void Dispose() => UiLocal.Dispose();
}

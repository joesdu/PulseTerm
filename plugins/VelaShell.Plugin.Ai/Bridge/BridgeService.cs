using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// IM 桥接的总装:读设置 → 造渠道 → 起 <see cref="ChannelHub" /> → 把消息接到
/// <see cref="ConversationRouter" />。插件激活时调一次 <see cref="ReloadAsync" />,
/// 设置页保存后再调一次即可 —— 起停的差异由它自己算。
/// </summary>
public sealed class BridgeService(IPluginContext context, AiSettingsStore aiStore) : IAsyncDisposable
{
    private readonly BridgeSettingsStore _bridgeStore = new(context);
    private readonly ChatHistoryStore _history = new(context);
    private readonly McpManager _mcp = new(context);
    private readonly Loc _loc = new(context.Host.Locale);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private ChannelHub? _hub;
    private ConversationRouter? _router;
    private ImApprovalBroker? _approvals;
    private Timer? _idleTimer;

    /// <summary>
    /// 配对码与待放行聊天。
    /// </summary>
    /// <remarks>
    /// <b>由本服务持有而不是路由器</b> —— 设置页一保存就整体重建路由器,
    /// 而"刚才有个群敲过门"这件事不该跟着一起丢掉,不然用户点保存的那一下就把线索抹了。
    /// </remarks>
    public PairingService Pairing { get; } = new();

    /// <summary>任一渠道的状态变化(设置页刷状态灯)。</summary>
    public event Action<ChannelStatus>? StatusChanged;

    /// <summary>桥接此刻是不是开着。</summary>
    public bool IsRunning => _hub is not null;

    /// <summary>各渠道的状态快照。</summary>
    public IReadOnlyList<ChannelStatus> Statuses => _hub?.Snapshot() ?? [];

    /// <summary>读设置并把桥接调到该有的样子(关着就全停,开着就按配置起)。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BridgeSettings bridge = await _bridgeStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            AiSettings ai = await aiStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _loc.Switch(context.Host.Locale);
            if (!bridge.Enabled || bridge.Channels.Count(c => c.Enabled) == 0)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }
            // 简单起见:重载一律整体重建。渠道的建连成本是一次握手,
            // 而"只重启变了的那个"要比对的字段散在配置与机密两处,不值这个复杂度。
            await StopCoreAsync().ConfigureAwait(false);
            await _history.InitAsync(cancellationToken).ConfigureAwait(false);

            var hub = new ChannelHub(context);
            var approvals = new ImApprovalBroker(hub, context);
            var runner = new BridgeAgentRunner(context, aiStore, _history, _mcp, hub);
            var router = new ConversationRouter(context, hub, runner, approvals, _bridgeStore, Pairing);
            router.Apply(bridge, _loc);
            hub.StatusChanged += status => StatusChanged?.Invoke(status);
            _hub = hub;
            _router = router;
            _approvals = approvals;

            foreach (ChannelConfig config in bridge.Channels.Where(c => c.Enabled))
            {
                IMessageChannel? channel = await ChannelFactory
                    .CreateAsync(context, _bridgeStore, config, cancellationToken).ConfigureAwait(false);
                if (channel is null)
                {
                    continue;
                }
                await hub.StartAsync(channel, router.HandleAsync).ConfigureAwait(false);
            }
            _idleTimer = new Timer(_ => _router?.EvictIdle(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            context.Log.Info($"Bridge: started with {bridge.Channels.Count(c => c.Enabled)} channel(s), " +
                             $"mode {bridge.Mode}, approval {bridge.Approval}.");
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// 放行一个待放行的聊天(设置页上那个「允许」按钮)。
    /// </summary>
    /// <remarks>
    /// 桥接正开着就走路由器那条路(内存 + 落盘,立刻生效);没开着就只落盘 ——
    /// 用户完全可能先在设置页把门开好,再去打开桥接。
    /// </remarks>
    /// <param name="chat">敲过门的那个聊天。</param>
    /// <param name="grant">
    /// 给它的授权。<see langword="null" /> = 不限范围、挡位审批跟随全局。
    /// <b>单聊传 null 是对的</b>:它只有一个对端,而且是用户逐个放行的;
    /// 群则应当由设置页先问一句范围再传进来(见 <see cref="ChatGrant" />)。
    /// </param>
    /// <param name="cancellationToken">取消。</param>
    public async Task AllowAsync(PendingChat chat, ChatGrant? grant = null,
        CancellationToken cancellationToken = default)
    {
        ChatGrant resolved = (grant ?? new ChatGrant()).Clone();
        resolved.ChatId = chat.ChatId;
        resolved.IsGroup = chat.IsGroup;
        if (resolved.Label.Length == 0)
        {
            resolved.Label = chat.UserName;
        }
        if (_router is { } router
            && router.Settings.Channels.FirstOrDefault(c => c.Id == chat.ChannelId) is { } live)
        {
            await router.AllowChatAsync(live, resolved).ConfigureAwait(false);
            return;
        }
        BridgeSettings stored = await _bridgeStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored.Channels.FirstOrDefault(c => c.Id == chat.ChannelId) is { } config
            && config.GrantFor(chat.ChatId) is null)
        {
            config.Grants.Add(resolved);
            await _bridgeStore.SaveAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        Pairing.Forget(chat.ChannelId, chat.ChatId);
    }

    /// <summary>停掉桥接(设置里关掉、或插件停用时)。</summary>
    public async Task StopAsync()
    {
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_idleTimer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            _idleTimer = null;
        }
        _router?.CancelAll();
        if (_hub is { } hub)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
        _hub = null;
        _router = null;
        _approvals = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _mcp.DisposeAsync().ConfigureAwait(false);
        _reloadGate.Dispose();
    }
}

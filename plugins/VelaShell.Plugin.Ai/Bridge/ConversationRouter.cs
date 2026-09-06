using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 策略层:谁能说话、这句话是命令还是提问、一个聊天同时跑几轮、回复怎么发回去。
/// 渠道只管收发,agent 只管跑,"要不要理你"全在这里。
/// </summary>
public sealed class ConversationRouter(
    IPluginContext context,
    ChannelHub hub,
    BridgeAgentRunner runner,
    ImApprovalBroker approvals,
    BridgeSettingsStore bridgeStore,
    PairingService pairing)
{
    /// <summary>两次"改同一条消息"之间至少隔这么久 —— 各家平台的编辑接口都有限流。</summary>
    private static readonly TimeSpan EditInterval = TimeSpan.FromMilliseconds(1200);

    private readonly Dictionary<string, BridgeConversation> _conversations = [];
    private readonly HashSet<string> _unauthorizedNotified = [with(StringComparer.Ordinal)];
    private readonly Lock _sync = new();
    private SemaphoreSlim _turnGate = new(2, 2);

    /// <summary>
    /// 桥接这一轮"开着"的生命周期。停桥接即取消它,重启换一个新的。
    /// </summary>
    /// <remarks>
    /// 没有它的时候,取消范围只覆盖<b>已经跑起来</b>的那几轮(<c>conversation.Running</c>)——
    /// 而排在两道闸上的消息一个都不在里面。于是"停止桥接"之后,那些消息会在闸放开时
    /// 照常开跑:调模型、执行工具、往聊天里发消息,而用户以为已经关掉了。
    /// 排队时间也不计入轮次超时(超时源是拿到闸之后才建的),排得越久跑得越晚。
    /// </remarks>
    private CancellationTokenSource _lifetime = new();

    /// <summary>
    /// 单个聊天最多排多少条待处理消息(不含正在跑的那一轮),超出直接回绝。
    /// </summary>
    private const int MaxQueuedPerChat = 3;

    /// <summary>当前生效的桥接设置(由 <see cref="BridgeService" /> 在重载时换掉)。</summary>
    public BridgeSettings Settings { get; private set; } = new();

    /// <summary>界面语言(跟随宿主)。</summary>
    public Loc Loc { get; private set; } = new("en");

    /// <summary>
    /// 换一套桥接设置(总并发跟着变)。
    /// </summary>
    /// <remarks>
    /// <b>这里刻意不缓存 AI 设置</b>(模型、供应商、MCP)—— 那一份由
    /// <see cref="BridgeAgentRunner" /> 每轮现读。缓存过一次,代价是用户在面板里
    /// 登录订阅制供应商或换模型之后桥接毫不知情,详见那边的注释。
    /// </remarks>
    public void Apply(BridgeSettings bridge, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        // 授权在这里再折算一次。存储那一层已经折算过,但这里是**唯一**真正判"能不能说话"的地方,
        // 而设置可以不经过存储直接送进来(测试、以及将来任何别的调用方)——
        // 少了这一句,一份只填了 AllowedChats 的设置会静默地谁都不放行。
        foreach (ChannelConfig channel in bridge.Channels)
        {
            channel.NormalizeGrants();
        }
        Settings = bridge;
        Loc = loc;
        int limit = Math.Clamp(bridge.MaxConcurrentTurns, 1, 16);
        // 换掉旧闸但**不 Dispose**:此刻可能正有消息等在它上面,释放会让那些等待
        // 抛 ObjectDisposedException。SemaphoreSlim 只在用过 AvailableWaitHandle 时才需要
        // 显式释放,这里从来没用过,交给 GC 即可。
        _turnGate = new SemaphoreSlim(limit, limit);
    }

    /// <summary>处理一条入站消息。<b>不抛</b> —— 上游那条读循环不该被一条消息带崩。</summary>
    public async Task HandleAsync(InboundMessage message)
    {
        if (Settings.Channels.FirstOrDefault(c => c.Id == message.ChannelId) is not { } config)
        {
            return;
        }
        if (config.GrantFor(message.ChatId) is not { } grant)
        {
            // 配对码要在白名单之前处理 —— 它存在的全部意义就是"我还不在白名单里,请把我加进去"
            if (await TryPairAsync(config, message).ConfigureAwait(false))
            {
                return;
            }
            pairing.Remember(new PendingChat(message.ChannelId, message.ChatId, message.IsGroup,
                message.UserName, DateTimeOffset.UtcNow));
            await NotifyUnauthorizedOnceAsync(message).ConfigureAwait(false);
            return;
        }
        // 群里没 @ 就当没听见 —— 机器人不该插进同事之间的正常对话
        if (message.IsGroup && !message.MentionsBot)
        {
            return;
        }
        if (config.AllowedUsers.Count > 0 && !config.AllowedUsers.Contains(message.UserId))
        {
            context.Log.Info($"Bridge: ignoring {message.UserName} ({message.UserId}) — not in the user allowlist.");
            return;
        }
        // 审批回复优先:这句话是在回上一条"要不要放行",不该被当成新问题
        if (approvals.TryConsume(message, config, Loc))
        {
            return;
        }
        BridgeConversation conversation = GetOrCreate(config, message);
        conversation.LastActivity = DateTimeOffset.UtcNow;
        string text = message.Text.Trim();
        if (text.StartsWith('/') && await TryCommandAsync(conversation, config, grant, message, text).ConfigureAwait(false))
        {
            return;
        }
        if (text.Length == 0)
        {
            return;
        }
        await RunTurnAsync(conversation, config, grant, message).ConfigureAwait(false);
    }

    /// <summary>丢掉闲置太久的会话上下文(由 <see cref="BridgeService" /> 定时调)。</summary>
    public void EvictIdle()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(5, Settings.ConversationIdleMinutes));
        lock (_sync)
        {
            foreach (string key in _conversations
                         .Where(kv => kv.Value.LastActivity < cutoff && kv.Value.Running is null)
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _conversations.Remove(key);
            }
        }
    }

    /// <summary>
    /// 掐掉全部轮次(停桥接时):<b>正在跑的和还在排队的都算</b>。
    /// </summary>
    /// <remarks>
    /// 取消生命周期即可同时命中两者 —— 排队的等待带着这个令牌,正在跑的那一轮的
    /// 取消源也链到它。随后换上一个新的生命周期,好让桥接还能被重新启动。
    /// </remarks>
    public void CancelAll()
    {
        CancellationTokenSource stopping;
        lock (_sync)
        {
            stopping = _lifetime;
            _lifetime = new CancellationTokenSource();
            foreach (BridgeConversation conversation in _conversations.Values)
            {
                conversation.Running?.Cancel();
                approvals.Cancel(conversation.ChatKey);
            }
        }
        stopping.Cancel();
        stopping.Dispose();
    }

    /// <summary>取本轮生命周期的令牌(取的那一刻的那一份)。</summary>
    private CancellationToken CurrentLifetimeToken()
    {
        lock (_sync)
        {
            return _lifetime.Token;
        }
    }

    /// <summary>
    /// 处理未授权聊天里的 <c>/pair &lt;码&gt;</c>。认下来了返回 true。
    /// </summary>
    /// <remarks>
    /// <b>这里刻意不要求群里先 @ 机器人。</b>此刻它还不在白名单里,而"要不要理你"这一关就在
    /// 前面 —— 再叠一条"先 @ 我"等于把配对本身也挡在门外。配对码本身足够窄:一次性、
    /// 十分钟过期、猜错五次作废。
    /// <para>
    /// <b>配对码携带的是一份具体的授权,而不是一张通行证。</b>从前的顺序是"先放进来,
    /// 再去设置页收紧",这在权限上是反的:从放行到收紧之间那个群拥有全部权限,
    /// 而人往往就忘了第二步。现在范围在<b>发码时</b>就定死,群里的人从第一秒起
    /// 就只看得见范围内的机器。
    /// </para>
    /// </remarks>
    private async Task<bool> TryPairAsync(ChannelConfig config, InboundMessage message)
    {
        string text = message.Text.Trim();
        if (!text.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var target = new OutboundTarget(message.ChatId, message.ThreadId);
        string code = text[5..].Trim();
        if (code.Length == 0)
        {
            await hub.SendAsync(message.ChannelId, target, Loc["BridgePairUsage"], CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        if (!pairing.TryRedeem(code, out ChatGrant? template))
        {
            // 只记日志,不在群里说"码错了还是过期了" —— 那等于告诉试探的人他离对还有多远
            context.Log.Warn($"Bridge: a wrong or expired pairing code was presented in {message.ChatKey}.");
            await hub.SendAsync(message.ChannelId, target, Loc["BridgePairRejected"], CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        ChatGrant grant = (template ?? new ChatGrant()).Clone();
        grant.ChatId = message.ChatId;
        grant.IsGroup = message.IsGroup;
        await AllowChatAsync(config, grant, announce: false).ConfigureAwait(false);
        context.Log.Info($"Bridge: {message.ChatKey} was paired by {message.UserName} " +
                         $"(scope: {DescribeScope(grant)}).");
        await hub.SendAsync(message.ChannelId, target,
            grant.Scope.IsUnrestricted
                ? Loc["BridgePaired"]
                : Loc.F("BridgePairedScoped", DescribeScope(grant)),
            CancellationToken.None).ConfigureAwait(false);
        // 紧跟一条欢迎语:刚被放行的人此刻最需要知道的是"我能干什么、我受什么限",
        // 而不是自己去猜一个命令名。
        await hub.SendAsync(message.ChannelId, target,
            Welcome(null, config, grant),
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把一个聊天加进白名单:<b>内存里立刻生效</b>,同时落盘。
    /// </summary>
    /// <remarks>
    /// 两处都要写。只写库的话得等下一次重载才认;只写内存的话重启就没了。
    /// 落盘走"读—改—写"而不是把内存那份整个盖上去 —— 设置页可能正开着,
    /// 用它内存里的快照覆盖会把用户刚改的别的字段抹掉。
    /// </remarks>
    /// <param name="config">这个渠道。</param>
    /// <param name="grant">给这个聊天的授权。</param>
    /// <param name="announce">
    /// 放行之后往那个聊天里发一条欢迎语。设置页那个「允许」按钮要发(人在手机上等着,
    /// 不打声招呼他不知道成了没有);<c>/pair</c> 那条路<b>不要</b> ——
    /// 它自己会先回一句"配对成功"再跟欢迎语,顺序才对,由这里代劳会重复发一条。
    /// </param>
    public async Task AllowChatAsync(ChannelConfig config, ChatGrant grant, bool announce = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(grant);
        string chatId = grant.ChatId;
        if (config.GrantFor(chatId) is null)
        {
            config.Grants.Add(grant);
        }
        config.NormalizeGrants();
        BridgeSettings stored = await bridgeStore.LoadAsync().ConfigureAwait(false);
        if (stored.Channels.FirstOrDefault(c => c.Id == config.Id) is { } persisted)
        {
            if (persisted.GrantFor(chatId) is null)
            {
                // 落盘的是**另一份**对象:内存里那份归正在跑的路由器,
                // 两边共享同一个实例的话,设置页保存时的编辑会串到运行中的授权上。
                persisted.Grants.Add(grant.Clone());
                await bridgeStore.SaveAsync(stored).ConfigureAwait(false);
            }
        }
        else
        {
            // 库里没有这个渠道(多半是设置页加了但还没点保存):内存里已经放行了,
            // 但重启就会没。说一声,别让它静默地只活到下次重启。
            context.Log.Warn($"Bridge: {chatId} was allowed for a channel that is not saved yet; " +
                             "it will be forgotten on restart until the settings are saved.");
        }
        pairing.Forget(config.Id, chatId);
        if (announce)
        {
            try
            {
                await hub.SendAsync(config.Id, new OutboundTarget(chatId, null),
                    Welcome(null, config, grant), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 打招呼失败不该让放行本身失败 —— 授权已经写进去了,那才是这次操作的意义
                context.Log.Warn($"Bridge: {chatId} was authorized but the welcome message failed: {ex.Message}");
            }
        }
        lock (_sync)
        {
            // 放行之后那句"未授权"的提醒也该复位:万一以后又被移出白名单,还得再提示一次
            _unauthorizedNotified.Remove($"{config.Id}/{chatId}");
        }
    }

    private async Task RunTurnAsync(BridgeConversation conversation, ChannelConfig config, ChatGrant grant,
        InboundMessage message)
    {
        // 停止之后到达、或停止期间还排着的消息一律不再执行。这个令牌一路带到两道闸的
        // 等待里 —— 排队的消息因此和正在跑的那一轮一样,受"停止桥接"管辖。
        CancellationToken lifetime = CurrentLifetimeToken();
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        // 同一个聊天串行:后来的排队,而不是插进上一轮的工具循环中间
        bool queued = conversation.Gate.CurrentCount == 0;
        if (queued)
        {
            if (Interlocked.Increment(ref conversation.Queued) > MaxQueuedPerChat)
            {
                // 排太多了。继续收下只会在很久以后把一串陈年问题挨个真跑给模型。
                Interlocked.Decrement(ref conversation.Queued);
                await hub.SendAsync(conversation.ChannelId, conversation.Reply, Loc["BridgeQueueFull"], CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            await hub.SendAsync(conversation.ChannelId, conversation.Reply, Loc["BridgeBusy"], CancellationToken.None)
                .ConfigureAwait(false);
        }

        bool chatGateHeld = false;
        bool turnGateHeld = false;
        SemaphoreSlim turnGate = _turnGate;
        try
        {
            await conversation.Gate.WaitAsync(lifetime).ConfigureAwait(false);
            chatGateHeld = true;
            await turnGate.WaitAsync(lifetime).ConfigureAwait(false);
            turnGateHeld = true;
            // 拿到闸之后**再确认一次**。停桥接会同时做两件事:取消生命周期、以及让正在跑的
            // 那一轮结束(它随即归还闸)。于是排队的这条会同时看到"许可可用"与"令牌已取消",
            // 而 SemaphoreSlim 在两者同时成立时不保证优先报取消 —— 谁先谁后由调度决定。
            // Windows 上一直是取消赢,Linux/macOS 上则观察到许可赢,排队的消息照样开跑。
            lifetime.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 停了(或闸被换掉了):这条消息就到此为止,一个字都不发给模型。
            // 只归还真正拿到手的那几把。
            if (turnGateHeld)
            {
                turnGate.Release();
            }
            if (chatGateHeld)
            {
                conversation.Gate.Release();
            }
            return;
        }
        finally
        {
            if (queued)
            {
                Interlocked.Decrement(ref conversation.Queued);
            }
        }

        // 超时源链到生命周期:停桥接时正在跑的这一轮也当场结束,不必依赖 Running 被看见。
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        turn.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Settings.TurnTimeoutSeconds, 30, 3600)));
        conversation.Running = turn;
        string? placeholder = null;
        DateTimeOffset lastEdit = DateTimeOffset.MinValue;
        try
        {
            ChannelCapabilities capabilities = hub.CapabilitiesOf(conversation.ChannelId);
            placeholder = await hub.SendAsync(conversation.ChannelId, conversation.Reply, Loc["BridgeThinking"], turn.Token)
                .ConfigureAwait(false);

            void OnProgress(string accumulated)
            {
                if (!capabilities.CanEdit || placeholder is null || accumulated.Length == 0)
                {
                    return;
                }
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - lastEdit < EditInterval)
                {
                    return;
                }
                lastEdit = now;
                // 故意不 await:进度是锦上添花,改慢了、改失败了都不该拖住模型那条流
                _ = hub.EditAsync(conversation.ChannelId, conversation.Reply, placeholder,
                    Clip(accumulated, capabilities.MaxMessageChars), CancellationToken.None);
            }

            BridgeTurn result = await runner.RunAsync(
                conversation, Settings, message,
                request => approvals.RequestAsync(conversation, config, request,
                    Settings.ApprovalTimeoutSeconds, Loc, turn.Token),
                Loc, OnProgress, grant, turn.Token).ConfigureAwait(false);

            await DeliverAsync(conversation, placeholder, Compose(result), turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DeliverAsync(conversation, placeholder, Loc["BridgeTimeout"], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Error($"Bridge: a turn in {conversation.ChatKey} failed: {ex}");
            await DeliverAsync(conversation, placeholder, Loc.F("BridgeTurnFailed", ex.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            conversation.Running = null;
            approvals.Cancel(conversation.ChatKey);
            // 归还的必须是**当初拿的那一把**:设置重载会把 _turnGate 换掉,
            // 释放新闸等于凭空多给一个名额,总并发上限就此失守。
            if (turnGateHeld)
            {
                turnGate.Release();
            }
            if (chatGateHeld)
            {
                conversation.Gate.Release();
            }
        }
    }

    /// <summary>回复落地:能改就改回那条占位消息,不能改就再发一条;超长切段。</summary>
    private async Task DeliverAsync(BridgeConversation conversation, string? placeholder, string text,
        CancellationToken cancellationToken)
    {
        ChannelCapabilities capabilities = hub.CapabilitiesOf(conversation.ChannelId);
        List<string> parts = Split(text, capabilities.MaxMessageChars);
        for (int i = 0; i < parts.Count; i++)
        {
            if (i == 0 && capabilities.CanEdit && placeholder is not null)
            {
                await hub.EditAsync(conversation.ChannelId, conversation.Reply, placeholder, parts[i], cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            await hub.SendAsync(conversation.ChannelId, conversation.Reply, parts[i], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>回复末尾那行小字:哪个模型、跑了多久、动了几次工具。</summary>
    private string Compose(BridgeTurn turn)
        => turn.Model.Length == 0
            ? turn.Text
            : $"{turn.Text}\n\n{Loc.F("BridgeFooter", turn.Model, turn.Elapsed.TotalSeconds.ToString("0.0"), turn.ToolCalls)}";

    private async Task<bool> TryCommandAsync(BridgeConversation conversation, ChannelConfig config,
        ChatGrant grant, InboundMessage message, string text)
    {
        string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1] : "";
        string? reply = command switch
        {
            "/help" or "/?" => Welcome(conversation, config, grant),
            "/new" or "/reset" => Reset(conversation),
            "/stop" => Stop(conversation),
            "/status" => await StatusAsync(conversation, config, grant).ConfigureAwait(false),
            "/sessions" => await ListSessionsAsync(grant).ConfigureAwait(false),
            "/use" => await BindAsync(conversation, grant, argument).ConfigureAwait(false),
            "/mode" => SetMode(conversation, grant, argument),
            _ => null
        };
        if (reply is null)
        {
            return false;
        }
        await hub.SendAsync(message.ChannelId, conversation.Reply, reply, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private string Reset(BridgeConversation conversation)
    {
        conversation.Running?.Cancel();
        approvals.Cancel(conversation.ChatKey);
        conversation.Reset();
        return Loc["BridgeNewChat"];
    }

    private string Stop(BridgeConversation conversation)
    {
        if (conversation.Running is not { } running)
        {
            return Loc["BridgeNothingRunning"];
        }
        running.Cancel();
        return Loc["BridgeStopped"];
    }

    /// <summary>
    /// 欢迎语 / <c>/help</c>:自我介绍 + <b>此刻真实的</b>设定 + 命令表。
    /// </summary>
    /// <remarks>
    /// <b>刻意不是一段静态文案。</b>一个只读、只授权了"测试"分组的群里,印着
    /// "你可以让我重启服务"比不印更糟 —— 人会照着去下命令,然后对着一句拒绝发懵,
    /// 而那句拒绝(按设计)不会告诉他范围外有什么。把当前挡位、审批、范围、绑定
    /// 一并写在欢迎语里,人一进来就知道自己站在哪儿。
    /// <para>
    /// 同一份内容既当欢迎语(配对成功、设置页放行)也当 <c>/help</c>,
    /// 于是不存在"帮助说的和实际不一样"这种缝。
    /// </para>
    /// </remarks>
    private string Welcome(BridgeConversation? conversation, ChannelConfig config, ChatGrant grant)
    {
        ChatMode mode = conversation?.ModeOverride ?? grant.Mode ?? Settings.Mode;
        ApprovalMode approval = grant.Approval ?? Settings.Approval;
        string bound = conversation?.BoundTarget is { Length: > 0 } target
            ? target
            : config.DefaultTarget is { Length: > 0 } fallback
                ? fallback
                : "—";
        return Loc.F("BridgeWelcome", mode, approval, DescribeScope(grant), bound, Loc["BridgeHelp"]);
    }

    /// <summary><c>/status</c>:把这个聊天<b>实际</b>生效的那几个值报出来,包括范围。</summary>
    /// <remarks>
    /// 报的是授权算完之后的值,不是全局设置里的值 —— 一个只读群里显示"Agent 模式"
    /// 比不显示更糟,人会照着它去下命令,然后对着一句拒绝发懵。
    /// </remarks>
    private async Task<string> StatusAsync(BridgeConversation conversation, ChannelConfig config, ChatGrant grant)
    {
        ChatMode mode = conversation.ModeOverride ?? grant.Mode ?? Settings.Mode;
        ApprovalMode approval = grant.Approval ?? Settings.Approval;
        ISessionScope? scope = grant.Scope.Resolve(context);
        string target = conversation.BoundTarget.Length > 0 ? conversation.BoundTarget : "—";
        SessionInfo? session = conversation.BoundTarget.Length > 0
            ? await SessionTargets.ResolveAsync(context, conversation.BoundTarget, CancellationToken.None, scope)
                .ConfigureAwait(false)
            : null;
        return Loc.F("BridgeStatus", config.Label, mode, approval, target,
                   session is null ? Loc["BridgeSessionOffline"] : Loc["BridgeSessionOnline"])
               + "\n" + Loc.F("BridgeStatusScope", DescribeScope(grant));
    }

    private async Task<string> ListSessionsAsync(ChatGrant grant)
    {
        string list = await SessionTargets
            .DescribeAsync(context, CancellationToken.None, grant.Scope.Resolve(context)).ConfigureAwait(false);
        return list.Length == 0 ? Loc["BridgeNoSessions"] : Loc.F("BridgeSessions", list);
    }

    /// <summary><c>/use</c>:把这个聊天绑到一台机器上。范围外的当作<b>不存在</b>。</summary>
    /// <remarks>
    /// 回的是同一句"没找到",而不是"找到了但不给你" —— 后者会把这条命令变成探测接口:
    /// 试一个主机名就能问出它在不在用户的机器列表里。日志里记全,群里说一半。
    /// </remarks>
    private async Task<string> BindAsync(BridgeConversation conversation, ChatGrant grant, string argument)
    {
        if (argument.Length == 0)
        {
            return Loc["BridgeBindUsage"];
        }
        if (await SessionTargets.ResolveAsync(context, argument, CancellationToken.None, grant.Scope.Resolve(context))
                .ConfigureAwait(false) is not { } session)
        {
            return Loc.F("BridgeBindNotFound", argument);
        }
        conversation.BoundTarget = SessionTargets.Format(session);
        // 绑定要过夜:存进设置里,VelaShell 重开、会话重连之后群里不用再绑一次
        BridgeSettings settings = await bridgeStore.LoadAsync().ConfigureAwait(false);
        settings.ChatBindings[conversation.ChatKey] = conversation.BoundTarget;
        await bridgeStore.SaveAsync(settings).ConfigureAwait(false);
        Settings.ChatBindings[conversation.ChatKey] = conversation.BoundTarget;
        return Loc.F("BridgeBound", conversation.BoundTarget);
    }

    /// <summary>
    /// <c>/mode</c>:换这个聊天的挡位。<b>默认只让往低了换。</b>
    /// </summary>
    /// <remarks>
    /// 白名单里的任何人都能发 <c>/mode agent</c> 的话,设置页里那个"桥接默认只读"就形同虚设 ——
    /// 拉高权限这件事应该在 VelaShell 里做,不该在群里做。要开放得去设置页勾
    /// <see cref="BridgeSettings.AllowModeEscalation" />。
    /// </remarks>
    private string SetMode(BridgeConversation conversation, ChatGrant grant, string argument)
    {
        ChatMode? requested;
        switch (argument.Trim().ToLowerInvariant())
        {
            case "chat":
                requested = ChatMode.Chat;
                break;
            case "plan":
                requested = ChatMode.Plan;
                break;
            case "agent":
                requested = ChatMode.Agent;
                break;
            case "" or "reset" or "default":
                requested = null;
                break;
            default:
                return Loc["BridgeModeUsage"];
        }
        // 天花板是**这个聊天**的挡位,不是全局的:一个被设成只读的群,不该因为全局是 Agent
        // 就能用一句 /mode agent 把自己抬回去。
        ChatMode ceiling = grant.Mode ?? Settings.Mode;
        if (requested is { } mode && Rank(mode) > Rank(ceiling) && !Settings.AllowModeEscalation)
        {
            return Loc.F("BridgeModeLocked", ceiling);
        }
        conversation.ModeOverride = requested;
        return Loc.F("BridgeModeSet", conversation.ModeOverride ?? ceiling);

        static int Rank(ChatMode mode) => mode switch
        {
            ChatMode.Agent => 2,
            ChatMode.Plan => 1,
            _ => 0
        };
    }

    private BridgeConversation GetOrCreate(ChannelConfig config, InboundMessage message)
    {
        lock (_sync)
        {
            if (_conversations.TryGetValue(message.ChatKey, out BridgeConversation? existing))
            {
                return existing;
            }
            var created = new BridgeConversation(message.ChannelId, message.ChatId)
            {
                ThreadId = message.ThreadId,
                BoundTarget = Settings.ChatBindings.TryGetValue(message.ChatKey, out string? bound)
                    ? bound
                    : config.DefaultTarget
            };
            _conversations[message.ChatKey] = created;
            return created;
        }
    }

    /// <summary>
    /// 没授权的聊天回一次(且只回一次)带 id 的提示。
    /// </summary>
    /// <remarks>
    /// 沉默是更"安全"的做法,但它把用户卡在第一步:群 id 在飞书/钉钉的界面上根本看不到,
    /// 不回这一句,人就只能去翻日志。机器人已经在群里了,回一句并不多暴露什么。
    /// </remarks>
    private async Task NotifyUnauthorizedOnceAsync(InboundMessage message)
    {
        lock (_sync)
        {
            if (!_unauthorizedNotified.Add(message.ChatKey))
            {
                return;
            }
        }
        context.Log.Info($"Bridge: chat {message.ChatId} is not in the allowlist (channel {message.ChannelId}).");
        if (message.IsGroup && !message.MentionsBot)
        {
            return; // 群里没 @ 就别自说自话
        }
        await hub.SendAsync(message.ChannelId, new OutboundTarget(message.ChatId, message.ThreadId),
            Loc.F("BridgeUnauthorized", message.ChatId), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>范围的一句人话(给 <c>/status</c> 与日志用)。</summary>
    private string DescribeScope(ChatGrant grant)
        => grant.Scope.Resolve(context) is { } scope ? scope.Describe() : Loc["BridgeScopeAll"];

    private static string Clip(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, Math.Max(0, max - 1)), "…");

    /// <summary>超长回复切段。尽量断在换行处,断不了才硬切。</summary>
    private static List<string> Split(string text, int max)
    {
        if (text.Length <= max)
        {
            return [text];
        }
        var parts = new List<string>();
        ReadOnlySpan<char> rest = text;
        while (rest.Length > max)
        {
            int cut = rest[..max].LastIndexOf('\n');
            if (cut < max / 2)
            {
                cut = max;
            }
            parts.Add(rest[..cut].ToString());
            rest = rest[cut..].TrimStart('\n');
        }
        if (rest.Length > 0)
        {
            parts.Add(rest.ToString());
        }
        return parts;
    }
}

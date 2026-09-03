using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// IM 桥接的策略层:谁能说话、哪句话是命令、挡位能不能在群里被抬高。
/// </summary>
/// <remarks>
/// 这里<b>一条模型请求都不发</b> —— 用例只走白名单与斜杠命令这两条不碰模型的路。
/// 真正要挡住的也正是它们:放行判断错一次,后果是陌生人能在生产机上敲命令。
/// </remarks>
[TestClass]
public sealed class BridgeRouterTests
{
    /// <summary>一个只记录发了什么的渠道。<c>RunAsync</c> 挂着不动,直到被停掉。</summary>
    private sealed class FakeChannel(string id) : IMessageChannel
    {
        public List<string> Sent { get; } = [];

        public string Id { get; } = id;

        public ChannelKind Kind => ChannelKind.Telegram;

        public string Label => "fake";

        public ChannelCapabilities Capabilities => new(true, 4000);

        public event Action? Connected;

        public Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
        {
            Connected?.Invoke();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken)
        {
            Sent.Add(text);
            return Task.FromResult<string?>($"m{Sent.Count}");
        }

        public Task EditAsync(OutboundTarget target, string messageId, string text, CancellationToken cancellationToken)
        {
            Sent.Add($"[edit {messageId}] {text}");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TestPluginContext Context { get; } = new();

        public FakeChannel Channel { get; } = new("ch1");

        public ChannelHub Hub { get; }

        public ConversationRouter Router { get; }

        public BridgeSettingsStore Store { get; }

        public BridgeSettings Settings { get; }

        public PairingService Pairing { get; } = new();

        public Harness(BridgeSettings? settings = null, ChannelConfig? channel = null)
        {
            Store = new BridgeSettingsStore(Context);
            Hub = new ChannelHub(Context);
            var runner = new BridgeAgentRunner(Context, new AiSettingsStore(Context),
                new ChatHistoryStore(Context), new McpManager(Context));
            Router = new ConversationRouter(Context, Hub, runner, new ImApprovalBroker(Hub, Context), Store, Pairing);
            Settings = settings ?? new BridgeSettings
            {
                Enabled = true,
                Channels = [channel ?? new ChannelConfig { Id = "ch1", AllowedChats = ["chat-1"] }]
            };
            Router.Apply(Settings, new Loc("en"));
        }

        /// <summary>
        /// 起渠道。<b>顺带把设置落一次盘</b> —— 真实情形里渠道是用户在设置页存下来的,
        /// 而放行走的是"读库—改—写"那条路,库里没有这个渠道就没地方写。
        /// </summary>
        public async Task StartAsync()
        {
            await Store.SaveAsync(Settings);
            await Hub.StartAsync(Channel, Router.HandleAsync);
        }

        public Task SendAsync(string text, string chatId = "chat-1", bool isGroup = false, bool mentions = true)
            => Router.HandleAsync(new InboundMessage("ch1", chatId, isGroup, "u1", "Ann", text, "m1", mentions));

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            Context.Dispose();
        }
    }

    /// <summary>
    /// 白名单之外的聊天不该被伺候,但要回一句带 id 的话 —— 群 id 在飞书/钉钉界面上看不到,
    /// 不给这一句,用户根本没法完成配置。
    /// </summary>
    [TestMethod]
    public async Task Message_FromUnlistedChat_IsRefusedWithItsId()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("hello", chatId: "chat-stranger");

        Assert.AreEqual(1, harness.Channel.Sent.Count);
        StringAssert.Contains(harness.Channel.Sent[0], "chat-stranger");
        StringAssert.Contains(harness.Channel.Sent[0], "not authorised");
    }

    /// <summary>同一个陌生聊天只提示一次,不然它每说一句我们就刷一条。</summary>
    [TestMethod]
    public async Task Message_FromUnlistedChat_IsAnnouncedOnlyOnce()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("hello", chatId: "chat-stranger");
        await harness.SendAsync("anyone there?", chatId: "chat-stranger");

        Assert.AreEqual(1, harness.Channel.Sent.Count);
    }

    /// <summary>群里没 @ 就当没听见 —— 机器人不该插进同事之间的正常对话。</summary>
    [TestMethod]
    public async Task GroupMessage_WithoutMention_IsIgnored()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("we should restart nginx", isGroup: true, mentions: false);

        Assert.AreEqual(0, harness.Channel.Sent.Count);
    }

    [TestMethod]
    public async Task SlashSessions_ListsConnectedSessions()
    {
        await using var harness = new Harness();
        harness.Context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        await harness.StartAsync();

        await harness.SendAsync("/sessions");

        StringAssert.Contains(harness.Channel.Sent.Single(), "root@prod-1:22");
    }

    [TestMethod]
    public async Task SlashUse_BindsTheChatAndRemembersIt()
    {
        await using var harness = new Harness();
        harness.Context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        await harness.StartAsync();

        await harness.SendAsync("/use root@prod-1:22");

        StringAssert.Contains(harness.Channel.Sent.Single(), "root@prod-1:22");
        BridgeSettings stored = await harness.Store.LoadAsync();
        Assert.AreEqual("root@prod-1:22", stored.ChatBindings["ch1/chat-1"]);
    }

    [TestMethod]
    public async Task SlashUse_WithNoMatchingSession_SaysSo()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/use root@nowhere:22");

        StringAssert.Contains(harness.Channel.Sent.Single(), "No connected session matches");
    }

    // ---- 会话范围授权 ----

    /// <summary>一份收紧过的授权,只给"生产"那个分组。</summary>
    private static Harness Scoped()
    {
        var channel = new ChannelConfig
        {
            Id = "ch1",
            Grants =
            [
                new ChatGrant
                {
                    ChatId = "chat-1",
                    IsGroup = true,
                    Scope = new SessionScope { Kind = ScopeKind.Limited, Groups = ["生产"] }
                }
            ]
        };
        return new Harness(new BridgeSettings { Enabled = true, Channels = [channel] });
    }

    /// <summary><c>/sessions</c> 只列范围内的 —— 主机名本身就是信息。</summary>
    [TestMethod]
    public async Task SlashSessions_ListsOnlyWhatIsInScope()
    {
        await using Harness harness = Scoped();
        harness.Context.FakeSessions.AddSaved(name: "prod-1", host: "prod-1", username: "root", group: "生产");
        harness.Context.FakeSessions.AddSaved(name: "test-1", host: "test-1", username: "root", group: "测试");
        harness.Context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        harness.Context.FakeSessions.AddConnected(host: "test-1", username: "root");
        await harness.StartAsync();

        await harness.SendAsync("/sessions", isGroup: true);

        string reply = harness.Channel.Sent.Single();
        StringAssert.Contains(reply, "root@prod-1:22");
        Assert.IsFalse(reply.Contains("test-1", StringComparison.Ordinal), "范围外的机器不该出现在清单里");
    }

    /// <summary>
    /// <c>/use</c> 碰范围外的机器,回的是"没找到",<b>而不是"找到了但不给你"</b>。
    /// </summary>
    /// <remarks>
    /// 后者会把这条命令变成探测接口:试一个主机名就能问出它在不在用户的机器列表里。
    /// 日志里记全,群里说一半。
    /// </remarks>
    [TestMethod]
    public async Task SlashUse_TreatsAnOutOfScopeMachineAsNonexistent()
    {
        await using Harness harness = Scoped();
        harness.Context.FakeSessions.AddSaved(name: "test-1", host: "test-1", username: "root", group: "测试");
        harness.Context.FakeSessions.AddConnected(host: "test-1", username: "root");
        await harness.StartAsync();

        await harness.SendAsync("/use root@test-1:22", isGroup: true);

        string reply = harness.Channel.Sent.Single();
        StringAssert.Contains(reply, "No connected session matches");
        BridgeSettings stored = await harness.Store.LoadAsync();
        Assert.IsFalse(stored.ChatBindings.ContainsKey("ch1/chat-1"), "越界的绑定不该被记下来");
    }

    /// <summary><c>/status</c> 要把范围报出来 —— 不然人只能靠撞墙才知道自己受限。</summary>
    [TestMethod]
    public async Task SlashStatus_ReportsTheScope()
    {
        await using Harness harness = Scoped();
        await harness.StartAsync();

        await harness.SendAsync("/status", isGroup: true);

        StringAssert.Contains(harness.Channel.Sent.Single(), "生产");
    }

    /// <summary>
    /// <b>这条是安全用例。</b>挡位的天花板是<b>这个聊天</b>的,不是全局的。
    /// </summary>
    /// <remarks>
    /// 全局设成 Agent、某个群单独摁回 Plan,那个群就不该能用一句 <c>/mode agent</c> 把自己抬回去 ——
    /// 否则"只读群"这个概念根本立不住。
    /// </remarks>
    [TestMethod]
    public async Task SlashMode_CeilingIsThePerChatGrantNotTheGlobalSetting()
    {
        var channel = new ChannelConfig
        {
            Id = "ch1",
            Grants = [new ChatGrant { ChatId = "chat-1", Mode = ChatMode.Plan }]
        };
        await using var harness = new Harness(new BridgeSettings
        {
            Enabled = true,
            Mode = ChatMode.Agent,
            Channels = [channel]
        });
        await harness.StartAsync();

        await harness.SendAsync("/mode agent");

        StringAssert.Contains(harness.Channel.Sent.Single(), "turned off");
    }

    /// <summary>
    /// <b>不卡自己脖子。</b>不限范围的授权(单聊、以及升级折算出来的那些)照常够得着所有机器。
    /// </summary>
    [TestMethod]
    public async Task AnUnrestrictedGrant_StillSeesEverything()
    {
        await using var harness = new Harness();
        harness.Context.FakeSessions.AddSaved(name: "test-1", host: "test-1", username: "root", group: "测试");
        harness.Context.FakeSessions.AddConnected(host: "test-1", username: "root");
        // 会话树里没有的临时机器也够得着 —— 失败关闭只作用于受限的那些
        harness.Context.FakeSessions.AddConnected(host: "adhoc", username: "root");
        await harness.StartAsync();

        await harness.SendAsync("/sessions");

        string reply = harness.Channel.Sent.Single();
        StringAssert.Contains(reply, "root@test-1:22");
        StringAssert.Contains(reply, "root@adhoc:22");
    }

    /// <summary>
    /// <b>这条是安全用例。</b>白名单里的任何人都能发 <c>/mode agent</c> 的话,
    /// 设置页里那个"桥接默认只读"就形同虚设。
    /// </summary>
    [TestMethod]
    public async Task SlashMode_CannotRaiseThePrivilegeByDefault()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/mode agent");

        StringAssert.Contains(harness.Channel.Sent.Single(), "turned off");
    }

    [TestMethod]
    public async Task SlashMode_CanRaiseWhenTheOperatorAllowedIt()
    {
        var settings = new BridgeSettings
        {
            Enabled = true,
            Mode = ChatMode.Plan,
            AllowModeEscalation = true,
            Channels = [new ChannelConfig { Id = "ch1", AllowedChats = ["chat-1"] }]
        };
        await using var harness = new Harness(settings);
        await harness.StartAsync();

        await harness.SendAsync("/mode agent");

        StringAssert.Contains(harness.Channel.Sent.Single(), "Agent");
    }

    /// <summary>往低了换永远允许 —— 收紧权限不该需要谁批准。</summary>
    [TestMethod]
    public async Task SlashMode_CanAlwaysLowerThePrivilege()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/mode chat");

        StringAssert.Contains(harness.Channel.Sent.Single(), "Chat");
    }

    [TestMethod]
    public async Task SlashHelp_ListsTheCommands()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/help");

        StringAssert.Contains(harness.Channel.Sent.Single(), "/sessions");
    }

    /// <summary>用户白名单非空时,名单外的人说话一律不理(连提示都不给 —— 他不是配置者)。</summary>
    [TestMethod]
    public async Task Message_FromUnlistedUser_IsIgnored()
    {
        var settings = new BridgeSettings
        {
            Enabled = true,
            Channels = [new ChannelConfig { Id = "ch1", AllowedChats = ["chat-1"], AllowedUsers = ["boss"] }]
        };
        await using var harness = new Harness(settings);
        await harness.StartAsync();

        await harness.SendAsync("/help");

        Assert.AreEqual(0, harness.Channel.Sent.Count);
    }

    /// <summary>
    /// 配对码把「抄群 id」那一趟整个干掉:在群里发一句就进白名单,而且过夜有效。
    /// </summary>
    [TestMethod]
    public async Task SlashPair_WithAValidCode_AuthorisesTheChatAndPersistsIt()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        string code = harness.Pairing.Issue();

        await harness.SendAsync($"/pair {code}", chatId: "chat-new");

        StringAssert.Contains(harness.Channel.Sent.Single(), "Paired");
        // 内存里立刻生效:紧接着的一句话不该再被当成陌生人
        harness.Channel.Sent.Clear();
        await harness.SendAsync("/help", chatId: "chat-new");
        StringAssert.Contains(harness.Channel.Sent.Single(), "/sessions");
        // 而且落了盘,重启之后还在
        BridgeSettings stored = await harness.Store.LoadAsync();
        CollectionAssert.Contains(stored.Channels.Single().AllowedChats, "chat-new");
    }

    [TestMethod]
    public async Task SlashPair_WithAWrongCode_LeavesTheChatUnauthorised()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        string code = harness.Pairing.Issue();
        string wrong = code == "000000" ? "111111" : "000000";

        await harness.SendAsync($"/pair {wrong}", chatId: "chat-new");

        StringAssert.Contains(harness.Channel.Sent.Single(), "not valid");
        BridgeSettings stored = await harness.Store.LoadAsync();
        // 白名单里仍旧只有一开始那条,陌生聊天没被放进去
        CollectionAssert.DoesNotContain(stored.Channels.Single().AllowedChats, "chat-new");
        Assert.AreEqual(1, stored.Channels.Single().AllowedChats.Count);
    }

    /// <summary>没生成过码的时候,任何 /pair 都不该放行。</summary>
    [TestMethod]
    public async Task SlashPair_WithNoCodeIssued_IsRefused()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/pair 123456", chatId: "chat-new");

        StringAssert.Contains(harness.Channel.Sent.Single(), "not valid");
    }

    [TestMethod]
    public async Task SlashPair_WithoutACode_ExplainsHowToGetOne()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("/pair", chatId: "chat-new");

        StringAssert.Contains(harness.Channel.Sent.Single(), "Usage");
    }

    /// <summary>敲过门的聊天要被记下来,设置页那个「允许」按钮才有东西可点。</summary>
    [TestMethod]
    public async Task UnlistedChat_IsRememberedForOneClickApproval()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        await harness.SendAsync("hello", chatId: "chat-stranger");

        PendingChat pending = harness.Pairing.Pending().Single();
        Assert.AreEqual("chat-stranger", pending.ChatId);
        Assert.AreEqual("Ann", pending.UserName);
    }

    /// <summary>放行之后就不该再挂在待放行清单里。</summary>
    [TestMethod]
    public async Task Pairing_ClearsTheChatFromThePendingList()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        await harness.SendAsync("hello", chatId: "chat-new");
        string code = harness.Pairing.Issue();

        await harness.SendAsync($"/pair {code}", chatId: "chat-new");

        Assert.AreEqual(0, harness.Pairing.Pending().Count);
    }
}

/// <summary>绑定串与宿主会话之间的换算。</summary>
[TestClass]
public sealed class SessionTargetsTests
{
    [TestMethod]
    public async Task Resolve_MatchesOnUserHostAndPort()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");

        SessionInfo? session = await SessionTargets.ResolveAsync(context, "root@prod-1:22", CancellationToken.None);

        Assert.IsNotNull(session);
        Assert.AreEqual("prod-1", session.Host);
    }

    /// <summary>省掉用户名与端口也该认 —— 群里手打一个 host 是最自然的写法。</summary>
    [TestMethod]
    public async Task Resolve_AcceptsHostOnly()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");

        Assert.IsNotNull(await SessionTargets.ResolveAsync(context, "prod-1", CancellationToken.None));
        Assert.IsNotNull(await SessionTargets.ResolveAsync(context, "prod-1:22", CancellationToken.None));
        Assert.IsNotNull(await SessionTargets.ResolveAsync(context, "root@prod-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task Resolve_RejectsAWrongUserOrPort()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");

        Assert.IsNull(await SessionTargets.ResolveAsync(context, "deploy@prod-1", CancellationToken.None));
        Assert.IsNull(await SessionTargets.ResolveAsync(context, "prod-1:2222", CancellationToken.None));
        Assert.IsNull(await SessionTargets.ResolveAsync(context, "", CancellationToken.None));
    }

    [TestMethod]
    public async Task Describe_ListsEveryConnectedSession()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        context.FakeSessions.AddConnected(host: "db-2", username: "deploy");

        string text = await SessionTargets.DescribeAsync(context, CancellationToken.None);

        StringAssert.Contains(text, "root@prod-1:22");
        StringAssert.Contains(text, "deploy@db-2:22");
    }
}

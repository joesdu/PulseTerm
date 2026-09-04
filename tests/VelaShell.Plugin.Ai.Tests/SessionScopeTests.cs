using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 会话范围授权:一份授权能碰哪些机器。
/// </summary>
/// <remarks>
/// <b>这一整个文件都是安全用例。</b>从前"这个聊天绑了哪台机器"只是个默认值 ——
/// 工具箱里九个工具都收可选的 <c>session_id</c>,传了就绕开默认值。这里守的是
/// 收紧之后的那道闸:范围外的机器,不管从哪个入口进来都碰不到。
/// <para>
/// 反向的那一半同样要守:<b>不受限的那几条路必须一直不受限</b>。一套把作者自己也拦住的
/// 权限设计,结局是被整个关掉,回到零防护 —— 所以"面板不受限""不限范围 = 没有闸"
/// 也各有用例钉住。
/// </para>
/// </remarks>
[TestClass]
public sealed class SessionScopeTests
{
    private static SessionScope Limited(params string[] groups)
        => new() { Kind = ScopeKind.Limited, Groups = [.. groups] };

    // ---- 不限范围 = 压根没有闸 ----

    /// <summary>
    /// <b>这是"不卡自己脖子"那条红线的机器可读版本。</b>
    /// </summary>
    /// <remarks>
    /// 不限范围不该是"一个放行全部的过滤器",而该是<b>没有过滤器</b> ——
    /// 差别在于前者有一个可能写错的放行分支,后者没有分支可写。聊天面板走的就是这条路。
    /// </remarks>
    [TestMethod]
    public void UnrestrictedScope_ResolvesToNoGateAtAll()
    {
        using var context = new TestPluginContext();

        Assert.IsNull(new SessionScope().Resolve(context));
        Assert.IsNull(new SessionScope { Kind = ScopeKind.All, Groups = ["生产"] }.Resolve(context));
        Assert.IsNotNull(Limited("生产").Resolve(context));
    }

    /// <summary>反序列化一份没有 Kind 字段的旧配置,落在"不限范围"上 —— 升级不改变行为。</summary>
    [TestMethod]
    public void ScopeKind_DefaultsToAll() => Assert.AreEqual(ScopeKind.All, new SessionScope().Kind);

    /// <summary>
    /// <b>受限但一个都没勾 = 一台都不给。</b>
    /// </summary>
    /// <remarks>
    /// 空列表最自然的读法是"什么都没选",而权限的默认值一旦读错方向,错的方向是放开。
    /// 要不限范围就得明写 <see cref="ScopeKind.All" />。
    /// </remarks>
    [TestMethod]
    public async Task LimitedScopeWithNothingTicked_AllowsNothing()
    {
        using var context = new TestPluginContext();
        SavedSessionInfo saved = context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", group: "生产");
        ISessionScope scope = new SessionScope { Kind = ScopeKind.Limited }.Resolve(context)!;

        Assert.IsFalse(await scope.AllowsSavedAsync(saved, CancellationToken.None));
    }

    // ---- 分组与单台 ----

    [TestMethod]
    public async Task GroupScope_AllowsOnlyThatGroup()
    {
        using var context = new TestPluginContext();
        SavedSessionInfo prod = context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", group: "生产");
        SavedSessionInfo test = context.FakeSessions.AddSaved(name: "test-1", host: "10.0.0.2", group: "测试");
        ISessionScope scope = Limited("生产").Resolve(context)!;

        Assert.IsTrue(await scope.AllowsSavedAsync(prod, CancellationToken.None));
        Assert.IsFalse(await scope.AllowsSavedAsync(test, CancellationToken.None));
    }

    /// <summary>分组名不分大小写:用户在会话树里改个大小写,授权不该跟着失效。</summary>
    [TestMethod]
    public async Task GroupScope_MatchesCaseInsensitively()
    {
        using var context = new TestPluginContext();
        SavedSessionInfo saved = context.FakeSessions.AddSaved(host: "10.0.0.1", group: "Prod");
        ISessionScope scope = Limited("prod").Resolve(context)!;

        Assert.IsTrue(await scope.AllowsSavedAsync(saved, CancellationToken.None));
    }

    /// <summary>"就给这一台":分组之外还能单独放行某条配置。</summary>
    [TestMethod]
    public async Task SavedIdScope_AllowsOneMachineOutsideAnyGroup()
    {
        using var context = new TestPluginContext();
        SavedSessionInfo db = context.FakeSessions.AddSaved(name: "db-1", host: "10.0.0.9", group: "数据库");
        SavedSessionInfo other = context.FakeSessions.AddSaved(name: "db-2", host: "10.0.0.10", group: "数据库");
        ISessionScope scope = new SessionScope
        {
            Kind = ScopeKind.Limited,
            SavedIds = [db.SavedSessionId]
        }.Resolve(context)!;

        Assert.IsTrue(await scope.AllowsSavedAsync(db, CancellationToken.None));
        Assert.IsFalse(await scope.AllowsSavedAsync(other, CancellationToken.None));
    }

    // ---- 活会话怎么映射回配置 ----

    [TestMethod]
    public async Task LiveSession_IsJudgedByTheSavedConfigItMatches()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", username: "root", group: "生产");
        context.FakeSessions.AddSaved(name: "test-1", host: "10.0.0.2", username: "root", group: "测试");
        SessionInfo prod = context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
        SessionInfo test = context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
        ISessionScope scope = Limited("生产").Resolve(context)!;

        Assert.IsTrue(await scope.AllowsLiveAsync(prod, CancellationToken.None));
        Assert.IsFalse(await scope.AllowsLiveAsync(test, CancellationToken.None));
    }

    /// <summary>
    /// <b>失败关闭:对不上任何一条已保存配置的会话一律拒绝。</b>
    /// </summary>
    /// <remarks>
    /// 这是最容易写反的一处。用户在终端里手敲 <c>ssh root@10.0.0.99</c> 连出去的那条会话,
    /// 恰恰是<b>没人替它定过范围</b>的那种 —— 把它当成"不受管辖所以放行",
    /// 等于给任何手敲的连接开了一道后门,而范围收得越紧、这道后门越显眼。
    /// </remarks>
    [TestMethod]
    public async Task LiveSession_ThatMatchesNoSavedConfig_IsRefused()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", username: "root", group: "生产");
        SessionInfo adhoc = context.FakeSessions.AddConnected(host: "10.0.0.99", username: "root");
        ISessionScope scope = Limited("生产").Resolve(context)!;

        Assert.IsFalse(await scope.AllowsLiveAsync(adhoc, CancellationToken.None));
    }

    /// <summary>同一台机器保存了两条配置(不同分组),命中任意一条在范围内的就放行。</summary>
    [TestMethod]
    public async Task LiveSession_IsAllowedIfAnyMatchingConfigIsInScope()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddSaved(name: "a", host: "10.0.0.1", username: "root", group: "测试");
        context.FakeSessions.AddSaved(name: "b", host: "10.0.0.1", username: "root", group: "生产");
        SessionInfo live = context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
        ISessionScope scope = Limited("生产").Resolve(context)!;

        Assert.IsTrue(await scope.AllowsLiveAsync(live, CancellationToken.None));
    }

    // ---- 对外 MCP 那一份 ----

    /// <summary>
    /// <c>AllowedTargets</c> 的语义:清单为空 = 不限范围(默认值不动)。
    /// </summary>
    /// <remarks>
    /// MCP 这条路的边界是回环地址 + 令牌 + 只读挡位。把用户自己机器上的 Claude Code / Codex
    /// 一起收紧,挡不住任何攻击者,只挡得住用户自己 —— 所以这里的空值方向与桥接授权相反,
    /// 而且是刻意相反的。
    /// </remarks>
    [TestMethod]
    public void TargetListScope_IsEmptyWhenNothingIsConfigured()
    {
        Assert.IsTrue(new TargetListScope([]).IsEmpty);
        Assert.IsTrue(new TargetListScope(["", "   "]).IsEmpty);
        Assert.IsFalse(new TargetListScope(["root@10.0.0.1:22"]).IsEmpty);
    }

    [TestMethod]
    public async Task TargetListScope_MatchesUserHostPort()
    {
        using var context = new TestPluginContext();
        SessionInfo allowed = context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
        SessionInfo denied = context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
        var scope = new TargetListScope(["root@10.0.0.1:22"]);

        Assert.IsTrue(await scope.AllowsLiveAsync(allowed, CancellationToken.None));
        Assert.IsFalse(await scope.AllowsLiveAsync(denied, CancellationToken.None));
    }

    // ---- 授权的迁移 ----

    /// <summary>
    /// <b>升级不改变任何人当前的行为。</b>
    /// </summary>
    /// <remarks>
    /// 既有的 <c>AllowedChats</c> 折算成不限范围、挡位审批跟随全局的授权 ——
    /// 也就是与升级前逐字相同的行为。收紧是用户自己的决定,不该由一次升级替他做。
    /// </remarks>
    [TestMethod]
    public void NormalizeGrants_FoldsTheOldAllowlistIntoUnrestrictedGrants()
    {
        var config = new ChannelConfig { AllowedChats = ["chat-1", "chat-2"] };

        config.NormalizeGrants();

        Assert.HasCount(2, config.Grants);
        Assert.IsTrue(config.Grants.All(g => g.Scope.IsUnrestricted));
        Assert.IsTrue(config.Grants.All(g => g.Mode is null && g.Approval is null));
        Assert.IsNotNull(config.GrantFor("chat-1"));
        Assert.IsNull(config.GrantFor("chat-3"));
    }

    /// <summary>
    /// <c>AllowedChats</c> 是 <c>Grants</c> 的派生镜像,重算之后两边必须一致。
    /// </summary>
    /// <remarks>
    /// 一个派生字段只要有一头没算,它就会开始撒谎 —— 而这一份撒谎的后果是
    /// 用户降级回旧版本之后白名单缺了几条,机器人突然不理人。
    /// </remarks>
    [TestMethod]
    public void NormalizeGrants_RebuildsTheLegacyMirror()
    {
        var config = new ChannelConfig
        {
            AllowedChats = ["chat-1"],
            Grants = [new ChatGrant { ChatId = "chat-2", Scope = new SessionScope { Kind = ScopeKind.Limited } }]
        };

        config.NormalizeGrants();

        Assert.AreSequenceEqual(["chat-1", "chat-2"], config.AllowedChats, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        // 折算出来的那条不限范围,原来就有的那条保持它自己的范围
        Assert.IsTrue(config.GrantFor("chat-1")!.Scope.IsUnrestricted);
        Assert.IsFalse(config.GrantFor("chat-2")!.Scope.IsUnrestricted);
    }

    /// <summary>重复调用不该把授权翻倍(读和写两头都会调它)。</summary>
    [TestMethod]
    public void NormalizeGrants_IsIdempotent()
    {
        var config = new ChannelConfig { AllowedChats = ["chat-1"] };

        config.NormalizeGrants();
        config.NormalizeGrants();
        config.NormalizeGrants();

        Assert.HasCount(1, config.Grants);
        Assert.HasCount(1, config.AllowedChats);
    }

    /// <summary>配对码携带的是一份授权,而不是一张通行证。</summary>
    [TestMethod]
    public void PairingCode_CarriesTheScopeChosenWhenItWasIssued()
    {
        var pairing = new PairingService();
        string code = pairing.Issue(new ChatGrant { Scope = Limited("生产"), Mode = Configuration.ChatMode.Plan });

        Assert.IsTrue(pairing.TryRedeem(code, out ChatGrant? template));
        Assert.IsNotNull(template);
        Assert.AreEqual(ScopeKind.Limited, template.Scope.Kind);
        Assert.Contains("生产", template.Scope.Groups);
        Assert.AreEqual(Configuration.ChatMode.Plan, template.Mode);
    }

    /// <summary>不带模板发的码(给自己单聊的那种)兑现出不限范围的授权。</summary>
    [TestMethod]
    public void PairingCode_WithoutATemplate_YieldsAnUnrestrictedGrant()
    {
        var pairing = new PairingService();
        string code = pairing.Issue();

        Assert.IsTrue(pairing.TryRedeem(code, out ChatGrant? template));
        Assert.IsNull(template);
        Assert.IsTrue(new ChatGrant().Scope.IsUnrestricted);
    }
}

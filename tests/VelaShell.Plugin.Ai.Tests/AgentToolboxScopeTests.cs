using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 范围在<b>工具箱</b>这一层的强制点。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么闸必须设在这里。</b>"这个聊天绑了哪台机器"从来只是一个默认值:工具箱里九个工具
/// 都收可选的 <c>session_id</c>,<c>run_on_sessions</c> 收的还是一个 id <b>数组</b> ——
/// 模型只要先 <c>list_sessions</c> 拿到 id 再显式传进去,任何做在"绑定"上的限制都当场失效。
/// 所以这一组用例逐个入口去撞:显式 id、默认目标、批量、开新连接、两个列表工具。
/// </para>
/// <para>
/// 同样重要的是反面:<b>没有范围时一切照旧</b>。聊天面板走的就是那条路,
/// 它一个字节的行为都不该因为这套机制而改变。
/// </para>
/// </remarks>
[TestClass]
public sealed class AgentToolboxScopeTests
{
    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string name,
        Dictionary<string, object?>? args = null)
    {
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == name);
        object? result = await function.InvokeAsync([with(args ?? [])], CancellationToken.None);
        return result?.ToString() ?? "";
    }

    /// <summary>生产组一台、测试组一台,两台都连着。</summary>
    private static (SessionInfo Prod, SessionInfo Test, SavedSessionInfo SavedTest) Two(TestPluginContext context)
    {
        context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", username: "root", group: "生产");
        SavedSessionInfo savedTest =
            context.FakeSessions.AddSaved(name: "test-1", host: "10.0.0.2", username: "root", group: "测试");
        return (context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root"),
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root"),
            savedTest);
    }

    private static AgentToolbox Scoped(TestPluginContext context, params string[] groups)
        => new(context)
        {
            Scope = new SessionScope { Kind = ScopeKind.Limited, Groups = [.. groups] }.Resolve(context),
            Approval = ApprovalMode.Bypass
        };

    /// <summary>
    /// <b>这是整套设计的中心用例。</b>显式传一个范围外的 <c>session_id</c>,必须被挡下来。
    /// </summary>
    /// <remarks>
    /// 调研里发现的第一件事就是:绑定拦不住任何人,因为每个工具都能显式传 id。
    /// 这条用例要是绿的而实现却只挡默认目标,那这套授权就只是一层装饰。
    /// </remarks>
    [TestMethod]
    public async Task ExplicitSessionId_OutsideTheScope_IsRefused()
    {
        using var context = new TestPluginContext();
        (_, SessionInfo test, _) = Two(context);
        context.FakeTerminal.Output[test.SessionId] = ["secret from the test box"];
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = test.SessionId });

        Assert.Contains("outside the scope", result);
        Assert.DoesNotContain("secret from the test box", result, "范围外机器的终端内容一个字都不该漏出去");
    }

    /// <summary>范围内的那台照常能用 —— 闸是筛子,不是墙。</summary>
    [TestMethod]
    public async Task ExplicitSessionId_InsideTheScope_StillWorks()
    {
        using var context = new TestPluginContext();
        (SessionInfo prod, _, _) = Two(context);
        context.FakeTerminal.Output[prod.SessionId] = ["active (running)"];
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = prod.SessionId });

        Assert.Contains("active (running)", result);
    }

    /// <summary>
    /// 默认目标也要过闸:授权之后用户可能把那台机器移出了分组,而绑定还留着。
    /// </summary>
    [TestMethod]
    public async Task TheDefaultTarget_IsCheckedToo()
    {
        using var context = new TestPluginContext();
        (_, SessionInfo test, _) = Two(context);
        context.FakeTerminal.Output[test.SessionId] = ["secret from the test box"];
        AgentToolbox toolbox = Scoped(context, "生产");
        toolbox.SessionIdProvider = () => test.SessionId;

        string result = await InvokeAsync(toolbox, "read_terminal");

        Assert.Contains("outside the scope", result);
        Assert.DoesNotContain("secret", result);
    }

    /// <summary>范围外的会话在 <c>list_sessions</c> 里不该出现 —— 连主机名都是信息。</summary>
    [TestMethod]
    public async Task ListSessions_HidesWhatIsOutOfScope()
    {
        using var context = new TestPluginContext();
        Two(context);
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "list_sessions");

        Assert.Contains("10.0.0.1", result);
        Assert.DoesNotContain("10.0.0.2", result);
    }

    /// <summary>
    /// <c>list_saved_sessions</c> 同理,而且它泄露的更多:<b>分组名本身</b>就在说明这台机器归谁管。
    /// </summary>
    [TestMethod]
    public async Task ListSavedSessions_HidesWhatIsOutOfScope()
    {
        using var context = new TestPluginContext();
        Two(context);
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "list_saved_sessions");

        Assert.Contains("prod-1", result);
        Assert.DoesNotContain("test-1", result);
        Assert.DoesNotContain("测试", result);
    }

    /// <summary>范围内一台都没有时,说的是"范围内没有",而不是"你一台都没连"。</summary>
    /// <remarks>后者会让用户跑去连一台机器,然后发现还是不行,而且不知道为什么。</remarks>
    [TestMethod]
    public async Task ListSessions_WithNothingInScope_SaysSo()
    {
        using var context = new TestPluginContext();
        Two(context);
        AgentToolbox toolbox = Scoped(context, "根本不存在的分组");

        string result = await InvokeAsync(toolbox, "list_sessions");

        Assert.Contains("within the scope", result);
    }

    /// <summary>
    /// <b>批量执行是范围最容易漏掉的一处</b> —— 它收的是 id 数组,压根不经过单目标那条解析路径。
    /// </summary>
    [TestMethod]
    public async Task RunOnSessions_RefusesTheWholeBatchIfAnyTargetIsOutOfScope()
    {
        using var context = new TestPluginContext();
        (SessionInfo prod, SessionInfo test, _) = Two(context);
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "run_on_sessions", new Dictionary<string, object?>
        {
            ["sessionIds"] = new[] { prod.SessionId, test.SessionId },
            ["command"] = "df -h"
        });

        Assert.Contains("outside the scope", result);
    }

    /// <summary>
    /// <c>open_session</c> 的范围检查要排在<b>审批之前</b>:一个注定越界的请求
    /// 不该先去惊动用户点一次头。
    /// </summary>
    [TestMethod]
    public async Task OpenSession_OutsideTheScope_IsRefusedBeforeAskingForApproval()
    {
        using var context = new TestPluginContext();
        (_, _, SavedSessionInfo savedTest) = Two(context);
        int asked = 0;
        var toolbox = new AgentToolbox(context)
        {
            Scope = new SessionScope { Kind = ScopeKind.Limited, Groups = ["生产"] }.Resolve(context),
            Approval = ApprovalMode.Ask,
            ApprovalHandler = _ =>
            {
                asked++;
                return Task.FromResult(true);
            }
        };

        string result = await InvokeAsync(toolbox, "open_session", new Dictionary<string, object?>
        {
            ["savedSessionId"] = savedTest.SavedSessionId,
            ["reason"] = "Feishu group Ops: check disk"
        });

        Assert.Contains("outside the scope", result);
        Assert.AreEqual(0, asked, "越界的请求不该惊动用户");
    }

    /// <summary>
    /// 拒绝消息<b>不列出范围外有什么</b> —— 否则试一个 id 就能问出一份完整清单。
    /// </summary>
    [TestMethod]
    public async Task TheRefusal_DoesNotEnumerateWhatIsOutOfScope()
    {
        using var context = new TestPluginContext();
        (_, SessionInfo test, _) = Two(context);
        AgentToolbox toolbox = Scoped(context, "生产");

        string result = await InvokeAsync(toolbox, "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = test.SessionId });

        Assert.DoesNotContain("10.0.0.1", result, "拒绝消息不该顺带报出范围内有哪些机器");
        Assert.DoesNotContain("10.0.0.2", result);
        Assert.DoesNotContain("生产", result);
    }

    // ---- 反面:没有范围 = 什么都没变 ----

    /// <summary>
    /// <b>不卡自己脖子。</b>聊天面板那条路不设范围,它必须够得着每一台机器。
    /// </summary>
    /// <remarks>
    /// 一套把作者自己也拦住的权限设计,结局是被整个关掉、回到零防护。
    /// 所以"没有范围"这条路要和"有范围"一样被钉住。
    /// </remarks>
    [TestMethod]
    public async Task WithNoScope_EveryMachineIsStillReachable()
    {
        using var context = new TestPluginContext();
        (SessionInfo prod, SessionInfo test, _) = Two(context);
        context.FakeTerminal.Output[test.SessionId] = ["active (running)"];
        var toolbox = new AgentToolbox(context) { Approval = ApprovalMode.Bypass };

        string list = await InvokeAsync(toolbox, "list_sessions");
        string read = await InvokeAsync(toolbox, "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = test.SessionId });

        Assert.Contains(prod.Host, list);
        Assert.Contains(test.Host, list);
        Assert.Contains("active (running)", read);
    }

    /// <summary>
    /// 手敲 <c>ssh</c> 连出去的临时会话:不受限那条路照常够得着,受限那条路够不着。
    /// </summary>
    /// <remarks>
    /// 失败关闭的代价就在这里 —— 受限的群碰不到会话树之外的机器。这是对的:
    /// 那恰恰是没人替它定过范围的一类,而你自己那条路本来就不受限。
    /// </remarks>
    [TestMethod]
    public async Task AnAdHocSession_IsReachableWithoutAScopeAndRefusedWithOne()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", username: "root", group: "生产");
        SessionInfo adhoc = context.FakeSessions.AddConnected(host: "10.0.0.99", username: "root");
        context.FakeTerminal.Output[adhoc.SessionId] = ["ad-hoc box"];

        string open = await InvokeAsync(new AgentToolbox(context), "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = adhoc.SessionId });
        string limited = await InvokeAsync(Scoped(context, "生产"), "read_terminal",
            new Dictionary<string, object?> { ["sessionId"] = adhoc.SessionId });

        Assert.Contains("ad-hoc box", open);
        Assert.Contains("outside the scope", limited);
    }
}

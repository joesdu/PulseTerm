using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>无头 agent 回合里那些不需要真的调模型就能钉住的行为。</summary>
[TestClass]
public sealed class BridgeAgentRunnerTests
{
    private static readonly Loc English = new("en");

    private static BridgeAgentRunner Create(TestPluginContext context)
        => new(context, new AiSettingsStore(context), new ChatHistoryStore(context), new McpManager(context));

    private static InboundMessage Message(string text = "hi")
        => new("ch1", "chat-1", false, "u1", "Ann", text, "m1", true);

    private static Task<bool> Deny(ApprovalRequest _) => Task.FromResult(false);

    [TestMethod]
    public async Task RunAsync_WithNoModelConfigured_SaysSoInsteadOfThrowing()
    {
        using var context = new TestPluginContext();
        BridgeTurn turn = await Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
            new BridgeSettings(), Message(), Deny, English, null, CancellationToken.None);

        Assert.AreEqual(English["BridgeNoModel"], turn.Text);
        Assert.AreEqual("", turn.Model);
    }

    /// <summary>
    /// AI 设置必须<b>每轮现读</b>,不能吃桥接启动时的那份快照。
    /// </summary>
    /// <remarks>
    /// 这条是线上撞到的那个 bug 的形状:用户在设置窗口登录了订阅制供应商(OpenAI Codex),
    /// 聊天面板立刻好用,而桥接手里那份 <c>AiProvider</c> 还是登录之前的形态
    /// (<c>Auth</c> 仍是 ApiKey、没有 OAuth 配置),于是凭据解析走了"取 API Key"那条岔路,
    /// 把一个空 Key 发了出去 —— 群里看到 401「Could not parse your authentication token」,
    /// 而同一刻面板一切正常。
    /// <para>
    /// 用例做法:<b>先</b>造好 runner,<b>之后</b>才往库里写供应商。第一轮必须说"没配模型",
    /// 第二轮必须已经看得见它 —— 报错里带的模型名就是证据(端点指向一个必然连不上的地址,
    /// 所以第二轮一定失败,但失败的原因得是"连不上",不是"没模型")。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task RunAsync_RereadsTheAiSettingsEveryTurn()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        BridgeAgentRunner runner = Create(context);
        var conversation = new BridgeConversation("ch1", "chat-1");
        var bridge = new BridgeSettings { Mode = ChatMode.Chat }; // 纯对话:不碰工具,免得牵进 MCP

        BridgeTurn before = await runner.RunAsync(conversation, bridge, Message(), Deny, English, null,
            CancellationToken.None);
        Assert.AreEqual(English["BridgeNoModel"], before.Text, "with nothing configured it should say so");

        // runner 造好之后才配上模型 —— 快照式实现在这一步之后仍然会说"没配模型"
        await store.SaveAsync(new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Id = "p1",
                    Name = "Local",
                    // 127.0.0.1:1 上不会有人监听,于是这一轮必定连不上 —— 我们要的正是这个
                    BaseUrl = "http://127.0.0.1:1/v1",
                    DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                    Models = [new AiModelConfig { Id = "m1", Model = "probe" }]
                }
            ],
            ActiveModelId = "m1"
        });

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runner.RunAsync(conversation, bridge, Message(), Deny, English, null, CancellationToken.None));

        // 报错里带着模型名 = 它确实读到了刚写进去的那份设置
        StringAssert.Contains(error.Message, "Local / probe");
    }

    /// <summary>
    /// 报错必须点名是<b>哪个模型</b>。
    /// </summary>
    /// <remarks>
    /// 群里只看到一句 401 时,人第一反应是去查飞书的凭证 —— 而那条 401 来自模型服务商。
    /// 两者差着十万八千里,不点名就得靠猜。
    /// </remarks>
    [TestMethod]
    public async Task RunAsync_NamesTheModelWhenTheProviderRejectsTheCall()
    {
        using var context = new TestPluginContext();
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Id = "p1",
                    Name = "Acme",
                    BaseUrl = "http://127.0.0.1:1/v1",
                    DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
                    Models = [new AiModelConfig { Id = "m1", Model = "x", Name = "some-model" }]
                }
            ],
            ActiveModelId = "m1"
        });

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Create(context).RunAsync(new BridgeConversation("ch1", "chat-1"),
                new BridgeSettings { Mode = ChatMode.Chat }, Message(), Deny, English, null, CancellationToken.None));

        StringAssert.StartsWith(error.Message, "Acme / some-model:");
    }
}

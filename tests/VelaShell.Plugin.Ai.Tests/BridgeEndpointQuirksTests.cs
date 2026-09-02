using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 机器人这条路发出去的请求,得和聊天面板那条<b>守同样的端点规矩</b>。
/// </summary>
/// <remarks>
/// 这是线上撞到的 bug 的形状:同一个订阅型供应商(ChatGPT 的 Codex 后端),
/// 聊天面板里一切正常,飞书群里却每轮都 400
/// <c>{"detail":"System messages are not allowed"}</c> ——
/// 因为端点怪癖的处理只写在 <c>ChatPanelView</c> 里,而 <c>BridgeAgentRunner</c>
/// 是<b>另一条发送路径</b>,它只调了 ApplyReasoning 就把请求发出去了。
/// <para>
/// 所以这里不去断言"某个方法被调用过"(那种测试改个实现就废),而是
/// <b>让 runner 真的把请求发到本地假端点上,抓报文来看</b>。
/// 以后再加第三条发送路径、或再加一条新怪癖,漏了就会在这儿红。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class BridgeEndpointQuirksTests
{
    private static readonly Loc English = new("en");

    private static Task<bool> Deny(ApprovalRequest _) => Task.FromResult(false);

    private static InboundMessage Message(string text = "服务器磁盘满了")
        => new("ch1", "chat-1", false, "u1", "Ann", text, "m1", true);

    /// <summary>照目录里 Codex 那条建供应商,只把端点换成本地假的。</summary>
    private static AiProvider CodexPointedAt(string baseUrl)
    {
        AiProvider provider = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        provider.BaseUrl = baseUrl;
        provider.Auth = AuthMethod.ApiKey; // 免得走登录那条路;要测的是<b>发出去的报文</b>
        provider.OAuth = null;
        return provider;
    }

    /// <summary>跑一个回合,把 runner 真正发出去的请求体抓回来。</summary>
    private static async Task<string> WireBodyAsync(BridgeSettings bridge)
    {
        using var context = new TestPluginContext();
        using var stub = new SseStub("", jsonContent: "{}");
        var store = new AiSettingsStore(context);
        AiProvider provider = CodexPointedAt(stub.BaseUrl);
        await store.SaveAsync(new AiSettings
        {
            Providers = [provider],
            ActiveModelId = provider.Models[0].Id
        });

        var runner = new BridgeAgentRunner(context, store, new ChatHistoryStore(context), new McpManager(context));
        try
        {
            await runner.RunAsync(new BridgeConversation("ch1", "chat-1"), bridge, Message(),
                Deny, English, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // 假端点回的不是 Responses 报文,解析失败无所谓 —— 只关心发出去的是什么
        }
        return await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Codex 后端<b>不收 system 角色</b>。系统提示词得改走 Responses 自己的
    /// <c>instructions</c> 字段 —— 内容一个字不少,只是换了个位置。
    /// </summary>
    [TestMethod]
    public async Task BridgeTurn_SendsNoSystemMessageToAnEndpointThatRefusesThem()
    {
        string body = await WireBodyAsync(new BridgeSettings());

        Assert.DoesNotContain("\"system\"", body,
            $"这一家会为此整轮 400,面板里却好好的 —— 实际报文:{body}");
        Assert.Contains("\"instructions\"", body, "系统提示词不能就这么丢了,它该落在 instructions 上");
    }

    /// <summary>
    /// 同一条路上的另一半:这一家不认的参数也得摘掉。
    /// </summary>
    /// <remarks>
    /// 上一轮踩坑的记录是:先修好 system 消息,下一条请求换来
    /// <c>{"detail":"Unsupported parameter: max_output_tokens"}</c> —— 它一次只肯告诉你一个。
    /// 两半一起钉住,免得再来一轮一来一回。
    /// </remarks>
    [TestMethod]
    public async Task BridgeTurn_DropsTheParametersThisEndpointRejects()
    {
        string body = await WireBodyAsync(new BridgeSettings());

        Assert.DoesNotContain("max_output_tokens", body, $"目录里已写明这一家不认它。实际报文:{body}");
        Assert.DoesNotContain("\"store\":true", body, "订阅型的私有后端不给第三方做服务端响应存储");
    }

    /// <summary>
    /// Agent 模式也得守同样的规矩 —— 群里默认就是 Agent,只测纯对话等于没测。
    /// </summary>
    [TestMethod]
    public async Task BridgeTurn_InAgentMode_StillHonoursTheEndpointQuirks()
    {
        string body = await WireBodyAsync(new BridgeSettings { Mode = ChatMode.Agent });

        Assert.DoesNotContain("\"system\"", body, $"实际报文:{body}");
        Assert.DoesNotContain("max_output_tokens", body, $"实际报文:{body}");
    }

    /// <summary>
    /// 怪癖要<b>请求时从目录读</b>,不能吃建供应商时的快照。
    /// </summary>
    /// <remarks>
    /// 这条上过一次当:规则曾被快照进用户保存的供应商里,于是新加的规则永远到不了
    /// <b>已经连上的</b>用户那儿 —— 代码改了三轮,用户那边一直是同一句 400。
    /// </remarks>
    [TestMethod]
    public void Quirks_AreReadFromTheCatalogue_NotFromWhateverGotSaved()
    {
        AiProvider stale = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        // 模拟一份"登录那天存下来的"旧配置:三项怪癖全是出厂默认
        stale.AllowSystemMessages = true;
        stale.StoreResponses = true;
        stale.UnsupportedParameters = "";

        EndpointQuirks quirks = EndpointQuirks.Of(stale);

        Assert.IsFalse(quirks.AllowSystemMessages, "该以目录为准,而不是用户库里那份旧快照");
        Assert.IsFalse(quirks.StoreResponses);
        Assert.Contains("max_output_tokens", quirks.UnsupportedParameters);
    }
}

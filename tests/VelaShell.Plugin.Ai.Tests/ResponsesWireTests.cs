using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// OpenAI Responses 协议下,请求体到底长什么样 —— 走真 SDK 打到本地假端点,抓报文看。
/// </summary>
/// <remarks>
/// 起因:ChatGPT 的 Codex 后端回 <c>{"detail":"System messages are not allowed"}</c>。
/// 也就是说系统提示词<b>不能以 system 消息的身份</b>发出去,得走 Responses 自己的
/// <c>instructions</c> 字段。而"<c>ChatOptions.Instructions</c> 到底映射成哪一个"
/// 只能抓包确认 —— 猜错的话提示词会<b>静默丢失</b>,模型行为变了却没有任何报错。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ResponsesWireTests
{
    private static IChatClient Client(string baseUrl)
    {
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
        return new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
            .GetResponsesClient()
            .AsIChatClient("m");
#pragma warning restore OPENAI001
    }

    /// <summary>把消息发出去并抓回请求体(假端点回的不是 Responses 报文,解析失败无所谓)。</summary>
    private static async Task<string> WireBodyAsync(List<ChatMessage> messages, ChatOptions options)
    {
        using var stub = new SseStub("", jsonContent: "{}");
        try
        {
            await Client(stub.BaseUrl).GetResponseAsync(messages, options);
        }
        catch (Exception)
        {
            // 只关心发出去的是什么,回来的能不能解析不重要
        }
        return await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Instructions_LandInTheirOwnFieldAndNotAsASystemMessage()
    {
        string body = await WireBodyAsync(
            [new ChatMessage(ChatRole.User, "你好")],
            new ChatOptions { Instructions = "INSTRUCTIONS_MARKER" });

        Assert.Contains("INSTRUCTIONS_MARKER", body, $"提示词不见了。实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"没走 instructions 字段。实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body, $"不该出现 system 角色。实际请求体:{body}");
    }

    [TestMethod]
    public async Task ASystemMessage_DoesShowUpAsARoleOnTheWire()
    {
        // 这条是"为什么必须改"的证据:照原样发,报文里就是有 system 角色,Codex 后端会拒
        string body = await WireBodyAsync(
            [new ChatMessage(ChatRole.System, "SYSTEM_MARKER"), new ChatMessage(ChatRole.User, "你好")],
            new ChatOptions());

        Assert.Contains("SYSTEM_MARKER", body);
        Assert.Contains("\"system\"", body, $"实际请求体:{body}");
    }

    /// <summary>
    /// 真机上那个 400 的修法:把系统提示词从消息列表挪进 <c>instructions</c>,
    /// 报文里就不再有 system 角色,而提示词一个字都没少。
    /// </summary>
    [TestMethod]
    public async Task MovingTheSystemPromptOut_RemovesTheRoleButKeepsTheText()
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "SYSTEM_MARKER"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        var options = new ChatOptions
        {
            Instructions = VelaShell.Plugin.Ai.Chat.ContextBuilder.MoveSystemPromptOut(messages)
        };

        string body = await WireBodyAsync(messages, options);

        Assert.Contains("SYSTEM_MARKER", body, $"提示词丢了。实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body,
            $"还带着 system 角色,Codex 后端会回 System messages are not allowed。实际请求体:{body}");
    }

    [TestMethod]
    public void MoveSystemPromptOut_OnlyTakesTheLeadingOne()
    {
        // 装配出来的列表里 system 必定在最前;后面若还有,那是历史带来的,不该被悄悄吞掉
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "头一条"),
            new ChatMessage(ChatRole.User, "问题"),
            new ChatMessage(ChatRole.System, "历史里的")
        ];

        string? moved = VelaShell.Plugin.Ai.Chat.ContextBuilder.MoveSystemPromptOut(messages);

        Assert.AreEqual("头一条", moved);
        Assert.HasCount(2, messages);
        Assert.AreEqual(ChatRole.System, messages[1].Role);
    }

    [TestMethod]
    public void MoveSystemPromptOut_LeavesAListThatDoesNotStartWithSystemAlone()
    {
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "问题")];

        Assert.IsNull(VelaShell.Plugin.Ai.Chat.ContextBuilder.MoveSystemPromptOut(messages));
        Assert.HasCount(1, messages);
    }

    /// <summary>
    /// Codex 后端接受的<b>全部</b>顶层字段,取自它自己的请求结构
    /// (<c>openai/codex</c> 的 <c>codex-rs/core/src/client.rs</c>,<c>ResponsesApiRequest</c>)。
    /// </summary>
    private static readonly HashSet<string> CodexAcceptedFields =
    [
        with(StringComparer.Ordinal),
        "model", "instructions", "input", "tools", "tool_choice", "parallel_tool_calls",
        "reasoning", "store", "stream", "stream_options", "include", "service_tier",
        "prompt_cache_key", "text", "client_metadata", "access_programs"
    ];

    /// <summary>
    /// 发出去的报文里<b>不能有</b>它清单之外的字段。
    /// </summary>
    /// <remarks>
    /// 这条是为了<b>不再靠用户一轮一个 400 去试</b>而存在的:那个后端多收到一个不认的字段
    /// 就整轮拒,而且一次只报一个名字。把"它到底收哪些"钉成清单,以后 SDK 或适配器
    /// 多发了什么,这里当场就红,不用等真机。
    /// </remarks>
    [TestMethod]
    public async Task TheCodexShapedRequest_SendsNothingOutsideWhatThatBackendAccepts()
    {
        AiProvider codex = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        var resolved = new ResolvedModel(codex, codex.Models[0]);
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "系统提示词"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        // 照 ChatPanelView 那一段原样组装,包括它默认会设的那些
        var options = new ChatOptions
        {
            MaxOutputTokens = resolved.MaxTokens,
            Temperature = resolved.Temperature,
            TopP = resolved.TopP
        };
        AiSettingsStore.ApplyReasoning(options, resolved);
        AiSettingsStore.ApplyEndpointQuirks(options, resolved);
        if (!EndpointQuirks.Of(codex).AllowSystemMessages)
        {
            options.Instructions = VelaShell.Plugin.Ai.Chat.ContextBuilder.MoveSystemPromptOut(messages);
        }

        string body = await WireBodyAsync(messages, options);

        using var document = JsonDocument.Parse(body);
        List<string> unexpected = [.. document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !CodexAcceptedFields.Contains(name))];
        Assert.IsEmpty(unexpected,
            $"这些字段那个后端不认,发出去就是 400:{string.Join(", ", unexpected)}。完整请求体:{body}");
    }

    /// <summary>
    /// Codex 那条路上发出去的报文,必须<b>同时</b>满足后端的三条脾气:
    /// 带 <c>store:false</c>、没有 system 角色、没有 <c>max_output_tokens</c>。
    /// 真机上这三条是一轮一个 400 挖出来的,合在一条测试里守住。
    /// </summary>
    [TestMethod]
    public async Task TheCodexShapedRequest_SatisfiesEveryKnownRestriction()
    {
        AiProvider codex = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        var resolved = new ResolvedModel(codex, codex.Models[0]);
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "SYSTEM_MARKER"),
            new ChatMessage(ChatRole.User, "你好")
        ];
        var options = new ChatOptions { MaxOutputTokens = resolved.MaxTokens, Temperature = 0.7f };
        AiSettingsStore.ApplyReasoning(options, resolved);
        AiSettingsStore.ApplyEndpointQuirks(options, resolved);
        if (!codex.AllowSystemMessages)
        {
            options.Instructions = VelaShell.Plugin.Ai.Chat.ContextBuilder.MoveSystemPromptOut(messages);
        }

        string body = await WireBodyAsync(messages, options);

        Assert.Contains("\"store\":false", body, $"实际请求体:{body}");
        Assert.DoesNotContain("\"system\"", body, $"实际请求体:{body}");
        Assert.DoesNotContain("max_output_tokens", body, $"实际请求体:{body}");
        Assert.DoesNotContain("temperature", body, $"实际请求体:{body}");
        // 提示词一个字都没少,只是换了位置
        Assert.Contains("SYSTEM_MARKER", body, $"实际请求体:{body}");
        Assert.Contains("\"instructions\"", body, $"实际请求体:{body}");
    }
}

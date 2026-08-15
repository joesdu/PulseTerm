using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// "OpenAI 兼容"这条线上思考字段的两种写法,都得能显示出来。
/// 用本地假端点喂真实的 SSE,走的是插件运行时那条真链路(OpenAI SDK → M.E.AI 适配器 → 增量)。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ReasoningPeekTests
{
    private static async Task<List<ChatResponseUpdate>> StreamAsync(string sse)
    {
        using var stub = new SseStub(sse);
        var openAi = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri(stub.BaseUrl) });
        IChatClient client = openAi.GetChatClient("m").AsIChatClient();
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("hi"))
        {
            updates.Add(update);
        }
        return updates;
    }

    /// <summary>
    /// DeepSeek 一系用 <c>delta.reasoning_content</c>,M.E.AI 的适配器认识它 ——
    /// 直接就是 <see cref="TextReasoningContent" />,兜底逻辑不该插手。
    /// </summary>
    [TestMethod]
    public async Task ReasoningContent_IsMappedByTheAdapter_AndNeedsNoFallback()
    {
        List<ChatResponseUpdate> updates = await StreamAsync("""
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"先看看 PS1"}}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]


        """);

        ChatResponseUpdate reasoningUpdate = updates.First(u => u.Contents.OfType<TextReasoningContent>().Any());
        Assert.AreEqual("先看看 PS1", reasoningUpdate.Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.IsFalse(ReasoningPeek.IsBlank(reasoningUpdate), "这一帧已经有内容了,不该再去翻原始报文");
    }

    /// <summary>
    /// OpenRouter 一系用 <c>delta.reasoning</c>,适配器不认 —— 那一帧解析出来一个内容都没有,
    /// 思考就此丢失。兜底逻辑必须从原始报文里把它捞回来。
    /// </summary>
    [TestMethod]
    public async Task PlainReasoningField_IsLostByTheAdapter_ButRecoveredFromTheRawUpdate()
    {
        List<ChatResponseUpdate> updates = await StreamAsync("""
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning":"先看看 PS1"}}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"这是答案"}}]}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]


        """);

        Assert.IsEmpty(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().ToList(),
            "前提:适配器确实认不出这个字段(它要是哪天认了,兜底就可以撤)");

        List<string> recovered = [];
        foreach (ChatResponseUpdate update in updates)
        {
            if (ReasoningPeek.IsBlank(update) && ReasoningPeek.TryRead(update.RawRepresentation, out string text))
            {
                recovered.Add(text);
            }
        }
        Assert.AreSequenceEqual(["先看看 PS1"], recovered);

        // 正文那一帧不是空帧,不会被当成思考重复渲染
        ChatResponseUpdate answer = updates.First(u => u.Contents.OfType<TextContent>().Any(c => c.Text.Length > 0));
        Assert.IsFalse(ReasoningPeek.IsBlank(answer));
    }

    [TestMethod]
    public void TryRead_OnSomethingThatIsNotAClientModel_JustSaysNo()
    {
        Assert.IsFalse(ReasoningPeek.TryRead(null, out _));
        Assert.IsFalse(ReasoningPeek.TryRead("not a model", out _));
        Assert.IsFalse(ReasoningPeek.TryRead(new object(), out _));
    }
}

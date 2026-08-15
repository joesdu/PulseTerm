using Anthropic;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// Anthropic 的提示词缓存断点。除了断点打在哪,还盯住两件容易出事的:
/// 标记确实随请求发到了线上,以及跨轮复用的内容对象不会一路攒断点(协议上限 4 个)。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PromptCacheTests
{
    private const string MarkerKey = "anthropic:cache_control";

    private static ChatMessage Msg(ChatRole role, string text) => new(role, [new TextContent(text)]);

    private static bool IsMarked(ChatMessage message)
        => message.Contents.Any(c => c.AdditionalProperties?.ContainsKey(MarkerKey) == true);

    private static int MarkerCount(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).Count(c => c.AdditionalProperties?.ContainsKey(MarkerKey) == true);

    [TestMethod]
    public void Apply_MarksTheSystemPrompt_AndTheLastMessage()
    {
        List<ChatMessage> messages =
        [
            Msg(ChatRole.System, "你是助手"),
            Msg(ChatRole.User, "第一个问题"),
            Msg(ChatRole.Assistant, "第一个回答"),
            Msg(ChatRole.User, "第二个问题")
        ];

        PromptCache.Apply(messages);

        Assert.IsTrue(IsMarked(messages[0]), "系统提示词是最稳定的一截,值得一个断点");
        Assert.IsFalse(IsMarked(messages[1]));
        Assert.IsFalse(IsMarked(messages[2]));
        Assert.IsTrue(IsMarked(messages[3]), "滚动断点落在末条消息:这一轮写,下一轮整段前缀命中");
        Assert.AreEqual(2, MarkerCount(messages), "协议最多 4 个断点,别乱花");
    }

    /// <summary>
    /// 历史里的内容对象是跨轮复用的。不清旧断点的话,几轮下来就会撞上
    /// "最多 4 个 cache_control 块"的硬限制,请求直接被服务端拒掉。
    /// </summary>
    [TestMethod]
    public void Apply_AcrossManyTurns_NeverAccumulatesMarkers()
    {
        List<ChatMessage> history = [Msg(ChatRole.System, "你是助手")];
        for (int turn = 0; turn < 8; turn++)
        {
            history.Add(Msg(ChatRole.User, $"问题 {turn}"));
            PromptCache.Apply(history);
            Assert.AreEqual(2, MarkerCount(history), $"第 {turn} 轮后仍应只有两个断点");
            history.Add(Msg(ChatRole.Assistant, $"回答 {turn}"));
        }
    }

    [TestMethod]
    public void Clear_RemovesEveryMarker()
    {
        List<ChatMessage> messages = [Msg(ChatRole.System, "你是助手"), Msg(ChatRole.User, "问题")];
        PromptCache.Apply(messages);
        Assert.AreEqual(2, MarkerCount(messages));

        PromptCache.Clear(messages);

        Assert.AreEqual(0, MarkerCount(messages), "关掉缓存后不能留下残标记");
    }

    /// <summary>思考块不支持 cache_control(SDK 明说),断点要落到它前面的块上。</summary>
    [TestMethod]
    public void Mark_SkipsReasoningContent()
    {
        var answer = new TextContent("答案");
        var thinking = new TextReasoningContent("想了想");
        List<ChatMessage> messages = [new(ChatRole.Assistant, [answer, thinking])];

        PromptCache.Apply(messages);

        Assert.IsTrue(answer.AdditionalProperties?.ContainsKey(MarkerKey));
        Assert.AreNotEqual(true, thinking.AdditionalProperties?.ContainsKey(MarkerKey));
    }

    /// <summary>
    /// 最要紧的一条:标记得真的变成线上的 <c>cache_control</c> 字段。
    /// 走真 SDK 打到本地假端点,抓请求体看。
    /// </summary>
    [TestMethod]
    public async Task Markers_ReachTheWire_AsCacheControlBlocks()
    {
        using var stub = new SseStub("""
        event: message_start
        data: {"type":"message_start","message":{"id":"m1","type":"message","role":"assistant","model":"m","content":[],"stop_reason":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: message_stop
        data: {"type":"message_stop"}


        """);

        List<ChatMessage> messages =
        [
            Msg(ChatRole.System, "你是助手"),
            Msg(ChatRole.User, "第一个问题"),
            Msg(ChatRole.Assistant, "第一个回答"),
            Msg(ChatRole.User, "第二个问题")
        ];
        PromptCache.Apply(messages);

        var anthropic = new AnthropicClient { BaseUrl = stub.BaseUrl, ApiKey = "k" };
        await foreach (ChatResponseUpdate _ in anthropic.AsIChatClient("m", 4096).GetStreamingResponseAsync(messages))
        {
        }

        string body = await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, body.Split("\"cache_control\"").Length - 1,
            $"系统提示词与末条消息各一个 cache_control。实际请求体:{body}");
        Assert.Contains("\"cache_control\":{\"type\":\"ephemeral\"}", body);
    }
}

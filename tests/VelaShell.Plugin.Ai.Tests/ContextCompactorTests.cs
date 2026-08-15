using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 上下文压缩:何时触发、折到哪儿、以及折完之后请求里到底装了什么。
/// 前两件是纯函数;最后一件走真 SDK 打到本地假端点,确认摘要真的替代了早期原文。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ContextCompactorTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    /// <summary>约 n 个 token 的 ASCII 文本(估算是 4 字符 1 token)。</summary>
    private static string Bulk(int tokens) => new('x', tokens * 4);

    private static List<ChatMessage> Conversation(int turns, int tokensEach)
    {
        var history = new List<ChatMessage>();
        for (int i = 0; i < turns; i++)
        {
            history.Add(User($"问 {i} {Bulk(tokensEach)}"));
            history.Add(Assistant($"答 {i} {Bulk(tokensEach)}"));
        }
        return history;
    }

    [TestMethod]
    public void ShouldCompact_OnlyOnceTheWindowIsNearlyFull()
    {
        List<ChatMessage> small = Conversation(6, 20);
        List<ChatMessage> big = Conversation(20, 300);

        Assert.IsFalse(ContextCompactor.ShouldCompact(small, 0, "", windowTokens: 100_000, reserveTokens: 4_000),
            "还早得很就不该动");
        Assert.IsTrue(ContextCompactor.ShouldCompact(big, 0, "", windowTokens: 8_000, reserveTokens: 1_000),
            "快撑满了就该压");
    }

    /// <summary>窗口未知(填 0)时什么都别做 —— 没有分母,谈不上"快满了"。</summary>
    [TestMethod]
    public void ShouldCompact_DoesNothingWithoutAKnownWindow()
        => Assert.IsFalse(ContextCompactor.ShouldCompact(Conversation(40, 500), 0, "", 0, 0));

    /// <summary>对话还很短时别压:折掉一两条既省不下什么,又白花一次请求。</summary>
    [TestMethod]
    public void ShouldCompact_SkipsShortConversations()
        => Assert.IsFalse(ContextCompactor.ShouldCompact(Conversation(2, 5_000), 0, "", 1_000, 100));

    [TestMethod]
    public void PlanCutPoint_KeepsRecentTurnsVerbatim_AndCutsOnAUserMessage()
    {
        List<ChatMessage> history = Conversation(20, 300);

        int cut = ContextCompactor.PlanCutPoint(history, 0, windowTokens: 8_000, reserveTokens: 1_000);

        Assert.IsGreaterThan(0, cut, "总得折掉一些,否则压了等于没压");
        Assert.IsLessThan(history.Count, cut, "近几轮必须留原文");
        Assert.AreEqual(ChatRole.User, history[cut].Role, "切口要落在用户消息上,不能把一轮拦腰截断");
    }

    /// <summary>第二次压缩从上一次的终点接着往后折,不会把已折过的再折一遍。</summary>
    [TestMethod]
    public void PlanCutPoint_RollsForwardFromThePreviousCut()
    {
        List<ChatMessage> history = Conversation(20, 300);
        int first = ContextCompactor.PlanCutPoint(history, 0, 8_000, 1_000);

        int second = ContextCompactor.PlanCutPoint(history, first, 8_000, 1_000);

        Assert.IsGreaterThanOrEqualTo(first, second);
    }

    /// <summary>
    /// 折完之后:请求里应当只剩「系统提示词 + 摘要 + 近几轮原文」,
    /// 早期那些原文不能再出现 —— 否则压缩一点作用都没有。
    /// </summary>
    [TestMethod]
    public void Build_WithASummary_ReplacesTheEarlyMessages()
    {
        List<ChatMessage> history =
        [
            User("最早的问题:磁盘满了"),
            Assistant("最早的回答:先看 df"),
            User("后来的问题"),
            Assistant("后来的回答"),
            User("刚问的")
        ];

        RequestContext request = ContextBuilder.Build("你是助手", history, windowTokens: 0, reserveTokens: 0,
            summary: "【摘要】用户在排查磁盘告警,已确认 /dev/sda1 98%。", summarizedThrough: 2);

        string all = string.Join("\n", request.Messages.Select(m => m.Text));
        Assert.Contains("【摘要】", all, "摘要要进请求");
        Assert.IsFalse(all.Contains("最早的回答", StringComparison.Ordinal), "被折进摘要的原文不该再发一遍");
        Assert.Contains("刚问的", all, "近几轮仍是原文");
        Assert.AreEqual(ChatRole.System, request.Messages[0].Role);
        Assert.AreEqual(ChatRole.User, request.Messages[1].Role, "摘要以 user 身份打头,各协议都吃得下");
    }

    /// <summary>压缩这一问走的是真 SDK:非流式、不带工具,拿回来的文本就是新摘要。</summary>
    [TestMethod]
    public async Task CompactAsync_AsksTheModel_AndReturnsTheDigest()
    {
        using var stub = new SseStub("", jsonContent: "【摘要】用户在排查 web-01 的磁盘告警,已确认 /dev/sda1 用满 98%,尚未定位大文件。");
        var openAi = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri(stub.BaseUrl) });
        List<ChatMessage> history = Conversation(10, 100);

        CompactionResult? result = await ContextCompactor.CompactAsync(
            openAi.GetChatClient("m").AsIChatClient(), history, 0, "", cut: 6, "zh-Hans", CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.Contains("/dev/sda1", result.Value.Summary);
        Assert.AreEqual(6, result.Value.Through);
        Assert.AreEqual(6, result.Value.FoldedMessages);

        // 送出去的提示词里要带上被折的那段原文,以及"用用户语言写"的要求
        string body = await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("zh-Hans", body);
        Assert.Contains("conversation to fold in", body);
    }

    /// <summary>模型返回空的时候当作没压成功,让调用方回退到"按窗口裁剪"。</summary>
    [TestMethod]
    public async Task CompactAsync_WithAnEmptyReply_ReportsFailure()
    {
        using var stub = new SseStub("", jsonContent: "   ");
        var openAi = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions { Endpoint = new Uri(stub.BaseUrl) });

        CompactionResult? result = await ContextCompactor.CompactAsync(
            openAi.GetChatClient("m").AsIChatClient(), Conversation(10, 100), 0, "", 6, "en", CancellationToken.None);

        Assert.IsNull(result);
    }
}

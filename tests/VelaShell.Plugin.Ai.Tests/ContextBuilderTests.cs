using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 请求上下文的装配:窗口裁剪与相邻同角色合并。
/// 这两件事以前埋在面板的发送方法里、只能靠起 headless 窗口间接摸,现在是纯函数。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ContextBuilderTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    /// <summary>约 n 个 token 的 ASCII 文本(估算是 4 字符 1 token)。</summary>
    private static string Bulk(int tokens) => new('x', tokens * 4);

    [TestMethod]
    public void Build_AlwaysPutsTheSystemPromptFirst()
    {
        RequestContext context = ContextBuilder.Build("你是助手", [User("问题")], windowTokens: 0, reserveTokens: 0);

        Assert.AreEqual(ChatRole.System, context.Messages[0].Role);
        Assert.AreEqual("你是助手", context.Messages[0].Text);
        Assert.AreEqual(0, context.DroppedMessages);
    }

    /// <summary>窗口未知(填 0)时不许擅自丢用户的上下文 —— 宁可让服务端报超长。</summary>
    [TestMethod]
    public void Build_WithUnknownWindow_KeepsEverything()
    {
        List<ChatMessage> history = [.. Enumerable.Range(0, 40).Select(i => User($"第 {i} 条 {Bulk(500)}"))];

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 0, reserveTokens: 0);

        Assert.AreEqual(0, context.DroppedMessages);
    }

    [TestMethod]
    public void Build_WhenHistoryExceedsTheWindow_DropsOldestAndReportsHowMany()
    {
        List<ChatMessage> history = [];
        for (int i = 0; i < 20; i++)
        {
            history.Add(User($"问 {i} {Bulk(200)}"));
            history.Add(Assistant($"答 {i} {Bulk(200)}"));
        }

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 4000, reserveTokens: 1000);

        Assert.IsGreaterThan(0, context.DroppedMessages, "装不下就该裁");
        Assert.IsLessThan(4000, context.EstimatedTokens, "裁完要真的落进窗口");
        // 最后一轮永远保得住,否则等于把用户刚问的问题也丢了
        Assert.AreEqual(history[^1].Text, context.Messages[^1].Text);
    }

    /// <summary>
    /// 只在用户消息处下刀:从 assistant / 工具结果中间切,会留下没有来由的半截上下文,
    /// 更糟的是把工具调用和它的结果拆开。
    /// </summary>
    [TestMethod]
    public void Build_CutsOnlyAtUserTurns()
    {
        List<ChatMessage> history = [];
        for (int i = 0; i < 20; i++)
        {
            history.Add(User($"问 {i} {Bulk(200)}"));
            history.Add(Assistant($"答 {i} {Bulk(200)}"));
        }

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 4000, reserveTokens: 1000);

        Assert.AreEqual(ChatRole.User, context.Messages[1].Role, "系统提示词之后必须从一条用户消息开始");
    }

    /// <summary>
    /// 取消一轮后历史里会留下没有回复的 user 消息,下一轮就是两条挨着的 user。
    /// 实测 Anthropic 适配器不合并、原样发两条,而协议要求角色交替 —— 这里必须并起来。
    /// </summary>
    [TestMethod]
    public void Build_MergesAdjacentSameRoleMessages()
    {
        List<ChatMessage> history =
        [
            User("被取消的那条"),
            User("重新问一遍"),
            Assistant("回答"),
            User("追问")
        ];

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 0, reserveTokens: 0);

        Assert.AreSequenceEqual(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            [.. context.Messages.Select(m => m.Role)],
            "相邻的两条 user 要并成一条,角色必须交替");
        Assert.Contains("被取消的那条", context.Messages[1].Text);
        Assert.Contains("重新问一遍", context.Messages[1].Text);
    }

    /// <summary>合并不能就地改历史里的对象 —— 那会把用户的原始记录改花。</summary>
    [TestMethod]
    public void Build_DoesNotMutateTheCallersHistory()
    {
        ChatMessage first = User("第一条");
        List<ChatMessage> history = [first, User("第二条")];

        ContextBuilder.Build("s", history, windowTokens: 0, reserveTokens: 0);

        Assert.HasCount(2, history, "历史条数不能变");
        Assert.AreEqual("第一条", first.Text, "历史里的消息内容不能被改写");
    }

    [TestMethod]
    public void Estimate_CountsWideCharactersHeavierThanAscii()
    {
        // 同样 12 个字符:ASCII 约 3 token,中文约 8 token
        Assert.IsLessThan(ContextBuilder.Estimate("你好世界你好世界你好世界"), ContextBuilder.Estimate("abcdefghijkl"));
    }

    [TestMethod]
    public void Estimate_IncludesToolCallsAndResults()
    {
        var call = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "run_command", new Dictionary<string, object?> { ["command"] = Bulk(50) })]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", Bulk(100))]);

        Assert.IsGreaterThan(40, ContextBuilder.Estimate(call), "工具调用的参数要算进去");
        Assert.IsGreaterThan(90, ContextBuilder.Estimate(result), "工具结果往往是最占地方的那部分");
    }

    private static ChatMessage Call(string id, string bulk = "") =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, "run_command",
            new Dictionary<string, object?> { ["command"] = "uptime" + bulk })]);

    private static ChatMessage Result(string id, string bulk = "") =>
        new(ChatRole.Tool, [new FunctionResultContent(id, "up 42 days" + bulk)]);

    private static List<AIContent> AllContents(RequestContext context)
        => [.. context.Messages.SelectMany(m => m.Contents)];

    /// <summary>
    /// 结果落单:裁剪顶到 <c>AlwaysKeep</c> 硬底线时,切点会停在半轮中间,
    /// 把工具调用切掉却留下它的结果。发出去 OpenAI Responses 直接回
    /// <c>400 No tool call found for tool output with call_id …</c>(用户环境实际撞到过)。
    /// </summary>
    [TestMethod]
    public void Build_WhenTrimmingCutsAToolCallLoose_DropsTheOrphanedResultToo()
    {
        // 前面塞满,逼裁剪一直切到硬底线;末尾四条正好把 call 与 result 劈开
        List<ChatMessage> history =
        [
            User(Bulk(4000)), Assistant(Bulk(4000)), User(Bulk(4000)),
            Call("call_00_orphan", Bulk(1000)),
            Result("call_00_orphan"), Assistant("已经查过了"), User("那再看看磁盘"), Assistant("好")
        ];

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 2000, reserveTokens: 200);

        Assert.IsGreaterThan(0, context.DroppedMessages, "这个用例的前提就是真的裁掉了东西");
        Assert.IsEmpty(AllContents(context).OfType<FunctionResultContent>(),
            "调用被切走了,它的结果不能单独留下");
    }

    /// <summary>
    /// 调用落单:模型发起调用后用户按了停止,工具从未执行 —— 半截回复是有意留在历史里的。
    /// 下一轮不能把这个没有结果的 tool_use 发出去(Anthropic 会回
    /// <c>tool_use ids must have corresponding tool_result</c>)。
    /// </summary>
    [TestMethod]
    public void Build_DropsAToolCallThatNeverGotItsResult()
    {
        List<ChatMessage> history =
        [
            User("看看负载"),
            new(ChatRole.Assistant, [new TextContent("我查一下"), new FunctionCallContent("c1", "run_command", null)]),
            User("算了,直接说结论")
        ];

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 0, reserveTokens: 0);

        Assert.IsEmpty(AllContents(context).OfType<FunctionCallContent>(), "没有结果的调用不能发出去");
        Assert.Contains(c => c is TextContent { Text: "我查一下" }, AllContents(context),
            "同一条里的正文要留着 —— 只摘掉落单的那半");
    }

    /// <summary>配对齐全就一个字节都不动。</summary>
    [TestMethod]
    public void Build_KeepsCompleteToolRoundTripsIntact()
    {
        List<ChatMessage> history = [User("看看负载"), Call("c1"), Result("c1"), Assistant("42 天")];

        RequestContext context = ContextBuilder.Build("s", history, windowTokens: 0, reserveTokens: 0);

        Assert.HasCount(1, AllContents(context).OfType<FunctionCallContent>());
        Assert.HasCount(1, AllContents(context).OfType<FunctionResultContent>());
    }
}

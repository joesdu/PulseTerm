using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 回复的附加信息(思考/工具/模型/耗时)与"编辑后重写会话"这两件事的存取。
/// 两者是耦合的:重写必须保住 seq,否则附加信息就对不上原来那条消息了。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ChatHistoryMetaTests
{
    private static async Task<(ChatHistoryStore Store, string Id, DateTimeOffset Created)> NewStoreAsync(
        TestPluginContext context)
    {
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        return (store, ChatHistoryStore.NewConversationId(), DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task Meta_RoundTrips_WithTheAssistantMessageItBelongsTo()
    {
        using var context = new TestPluginContext();
        (ChatHistoryStore store, string id, DateTimeOffset created) = await NewStoreAsync(context);

        await store.AppendAsync(id, created, 0, "user", "磁盘满了怎么查?");
        await store.AppendAsync(id, created, 1, "assistant", "先看 df -h。");
        await store.AppendMetaAsync(id, 1, new ChatTurnMeta(
            "claude-opus-5", 12_300, "先确认是 inode 还是块用满", 4_200,
            [new ChatToolCall("run_command", "{\"command\":\"df -h\"}", "/dev/sda1 98% /")]));

        IReadOnlyList<ChatEntry> entries = await store.LoadAsync(id);

        Assert.HasCount(2, entries);
        Assert.IsNull(entries[0].Meta, "用户消息没有附加信息");
        ChatTurnMeta meta = entries[1].Meta!;
        Assert.AreEqual("claude-opus-5", meta.Model);
        Assert.AreEqual(12_300, meta.ElapsedMs);
        Assert.AreEqual(4_200, meta.ThinkingMs);
        StringAssert.Contains(meta.Thinking, "inode");
        Assert.HasCount(1, meta.Tools!);
        Assert.AreEqual("run_command", meta.Tools![0].Name);
        StringAssert.Contains(meta.Tools[0].Result, "98%");
    }

    /// <summary>老会话(那时还没有 chat_meta)照样能读出来,只是没有附加信息。</summary>
    [TestMethod]
    public async Task Load_WithoutAnyMeta_StillReturnsMessages()
    {
        using var context = new TestPluginContext();
        (ChatHistoryStore store, string id, DateTimeOffset created) = await NewStoreAsync(context);
        await store.AppendAsync(id, created, 0, "user", "问题");
        await store.AppendAsync(id, created, 1, "assistant", "回答");

        IReadOnlyList<ChatEntry> entries = await store.LoadAsync(id);

        Assert.HasCount(2, entries);
        Assert.IsNull(entries[1].Meta);
    }

    /// <summary>
    /// 编辑/删除某条之后要整段重写。重写必须<b>沿用原来的 seq</b> ——
    /// 附加信息是按 conv+seq 挂的,序号一变,幸存消息的思考与工具调用就全丢了。
    /// </summary>
    [TestMethod]
    public async Task Rewrite_KeepsSequences_SoSurvivingMetaStillMatches()
    {
        using var context = new TestPluginContext();
        (ChatHistoryStore store, string id, DateTimeOffset created) = await NewStoreAsync(context);
        await store.AppendAsync(id, created, 0, "user", "第一问");
        await store.AppendAsync(id, created, 1, "assistant", "第一答");
        await store.AppendMetaAsync(id, 1, new ChatTurnMeta("m1", 100, "想了想", 50));
        await store.AppendAsync(id, created, 2, "user", "第二问");
        await store.AppendAsync(id, created, 3, "assistant", "第二答");
        await store.AppendMetaAsync(id, 3, new ChatTurnMeta("m2", 200));

        // 砍掉"第二问"及其之后
        await store.RewriteAsync(id, created, [(0, "user", "第一问"), (1, "assistant", "第一答")]);

        IReadOnlyList<ChatEntry> entries = await store.LoadAsync(id);
        Assert.HasCount(2, entries);
        Assert.AreEqual("第一答", entries[1].Text);
        Assert.AreEqual("m1", entries[1].Meta?.Model, "幸存那条的附加信息不能因为重写而丢");
        StringAssert.Contains(entries[1].Meta!.Thinking, "想了想");
    }

    [TestMethod]
    public async Task Rename_ChangesTheTitleInTheList()
    {
        using var context = new TestPluginContext();
        (ChatHistoryStore store, string id, DateTimeOffset created) = await NewStoreAsync(context);
        await store.AppendAsync(id, created, 0, "user", "原来的标题会取这句");

        await store.RenameAsync(id, "排查磁盘告警", created, 1);

        IReadOnlyList<ChatSessionSummary> sessions = await store.ListSessionsAsync();
        Assert.AreEqual("排查磁盘告警", sessions.Single(s => s.Id == id).Title);
    }

    [TestMethod]
    public async Task Delete_TakesTheMetaWithIt()
    {
        using var context = new TestPluginContext();
        (ChatHistoryStore store, string id, DateTimeOffset created) = await NewStoreAsync(context);
        await store.AppendAsync(id, created, 0, "assistant", "回答");
        await store.AppendMetaAsync(id, 0, new ChatTurnMeta("m", 1));

        await store.DeleteAsync(id);
        // 同一个 id 再写一条同序号的消息,不该捡到上一次的附加信息
        await store.AppendAsync(id, created, 0, "assistant", "新的回答");

        Assert.IsNull((await store.LoadAsync(id))[0].Meta, "删会话要连附加信息一起删干净");
    }
}

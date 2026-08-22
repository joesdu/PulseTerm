using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>会话历史(落时序库)的持久化语义:摘要唯一、加载顺序、删除与输入回溯。</summary>
[TestClass]
public sealed class ChatHistoryStoreTests
{
    private static async Task<ChatHistoryStore> OpenAsync(TestPluginContext context)
    {
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        Assert.IsTrue(store.IsAvailable);
        return store;
    }

    private static async Task WriteTurnAsync(ChatHistoryStore store, string conversation, DateTimeOffset createdAt,
        int firstSequence, string question, string answer)
    {
        await store.AppendAsync(conversation, createdAt, firstSequence, "user", question);
        await store.AppendAsync(conversation, createdAt, firstSequence + 1, "assistant", answer);
    }

    [TestMethod]
    public async Task AppendAndLoad_RoundTripsInOrder()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        string id = ChatHistoryStore.NewConversationId();
        DateTimeOffset created = DateTimeOffset.UtcNow;

        await WriteTurnAsync(store, id, created, 0, "如何看磁盘占用?", "用 df -h。");
        await WriteTurnAsync(store, id, created, 2, "那 inode 呢?", "df -i。");

        IReadOnlyList<ChatEntry> entries = await store.LoadAsync(id);

        Assert.HasCount(4, entries);
        Assert.AreEqual("user", entries[0].Role);
        Assert.AreEqual("如何看磁盘占用?", entries[0].Text);
        Assert.AreEqual("df -i。", entries[3].Text);
    }

    [TestMethod]
    public async Task ListSessions_KeepsOneSummaryPerConversation_NewestFirst()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-10);
        string older = ChatHistoryStore.NewConversationId();
        string newer = ChatHistoryStore.NewConversationId();

        await WriteTurnAsync(store, older, created, 0, "第一个会话", "答一");
        await WriteTurnAsync(store, newer, created.AddMinutes(1), 0, "第二个会话", "答二");
        await store.AppendAsync(older, created, 2, "user", "继续问"); // 老会话被追问 → 回到最前

        IReadOnlyList<ChatSessionSummary> sessions = await store.ListSessionsAsync();

        Assert.HasCount(2, sessions, "每个会话只应有一条摘要(同序列同时间戳 = 覆盖)");
        Assert.AreEqual(older, sessions[0].Id, "最后更新的会话排在最前");
        Assert.AreEqual("第一个会话", sessions[0].Title, "标题取首条用户消息,不随后续消息改写");
        Assert.AreEqual(3, sessions[0].MessageCount);
        Assert.AreEqual(created, sessions[0].CreatedAt);
    }

    [TestMethod]
    public async Task Delete_RemovesMessagesAndSummary_LeavesOthers()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        DateTimeOffset created = DateTimeOffset.UtcNow;
        string doomed = ChatHistoryStore.NewConversationId();
        string kept = ChatHistoryStore.NewConversationId();
        await WriteTurnAsync(store, doomed, created, 0, "删我", "好");
        await WriteTurnAsync(store, kept, created.AddSeconds(1), 0, "留我", "好");

        await store.DeleteAsync(doomed);

        Assert.IsEmpty(await store.LoadAsync(doomed));
        Assert.HasCount(2, await store.LoadAsync(kept));
        IReadOnlyList<ChatSessionSummary> sessions = await store.ListSessionsAsync();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(kept, sessions[0].Id);
    }

    [TestMethod]
    public async Task Clear_EmptiesEverything()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        string id = ChatHistoryStore.NewConversationId();
        await WriteTurnAsync(store, id, DateTimeOffset.UtcNow, 0, "问", "答");

        await store.ClearAsync();

        Assert.IsEmpty(await store.ListSessionsAsync());
        Assert.IsEmpty(await store.LoadAsync(id));
    }

    [TestMethod]
    public async Task RecentUserInputs_AreNewestFirstDeduplicatedAndUserOnly()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        DateTimeOffset created = DateTimeOffset.UtcNow;
        string id = ChatHistoryStore.NewConversationId();
        await WriteTurnAsync(store, id, created, 0, "第一条", "助手回复");
        await WriteTurnAsync(store, id, created, 2, "第二条", "助手回复");
        await store.AppendAsync(id, created, 4, "user", "第一条"); // 重复内容只保留最近一次

        IReadOnlyList<string> inputs = await store.RecentUserInputsAsync();

        Assert.AreSequenceEqual(["第一条", "第二条"], inputs);
    }

    [TestMethod]
    public async Task LongMessage_IsTruncatedNotRejected()
    {
        using var context = new TestPluginContext();
        ChatHistoryStore store = await OpenAsync(context);
        string id = ChatHistoryStore.NewConversationId();

        await store.AppendAsync(id, DateTimeOffset.UtcNow, 0, "user", new('x', ChatHistoryStore.MaxMessageChars * 2));

        IReadOnlyList<ChatEntry> entries = await store.LoadAsync(id);
        Assert.HasCount(1, entries);
        Assert.AreEqual(ChatHistoryStore.MaxMessageChars + 1, entries[0].Text.Length, "超长正文按上限截断并加省略号");
    }

    [TestMethod]
    public async Task TimeSeriesUnavailable_DegradesQuietly()
    {
        using var context = new TestPluginContext { TimeSeries = new UnavailableTimeSeries() };
        var store = new ChatHistoryStore(context);

        await store.InitAsync();

        Assert.IsFalse(store.IsAvailable);
        await store.AppendAsync("c1", DateTimeOffset.UtcNow, 0, "user", "写不进去也不能崩");
        Assert.IsEmpty(await store.ListSessionsAsync());
        Assert.IsEmpty(await store.RecentUserInputsAsync());
    }

    /// <summary>没有数据库的宿主上的时序能力(与 PluginManager 的退化实现同语义)。</summary>
    private sealed class UnavailableTimeSeries : PluginSdk.TimeSeries.ITimeSeriesApi
    {
        public Task<PluginSdk.TimeSeries.ITimeSeries> OpenAsync(
            PluginSdk.TimeSeries.TimeSeriesDefinition definition, CancellationToken cancellationToken = default)
            => Task.FromException<PluginSdk.TimeSeries.ITimeSeries>(
                new InvalidOperationException("Time series capability is unavailable in this host."));

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> DropAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}

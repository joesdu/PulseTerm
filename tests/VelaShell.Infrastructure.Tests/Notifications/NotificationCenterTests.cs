using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Notifications;

namespace VelaShell.Infrastructure.Tests.Notifications;

/// <summary>
/// 消息中心:去重、已读状态、过期清理、上限与持久化。
/// 这些行为串起来才成立 —— 尤其"每次启动都重投同一条更新提醒"不能把读过的又点亮。
/// </summary>
[TestClass]
[TestCategory("Notifications")]
public class NotificationCenterTests
{
    private static NotificationItem Item(string id, DateTime? published = null, DateTime? expires = null) =>
        new()
        {
            Id = id,
            Kind = NotificationKind.News,
            Title = $"消息 {id}",
            PublishedAt = published ?? DateTime.UtcNow,
            ExpiresAt = expires
        };

    /// <summary>投递后进列表,未读数跟着涨,并触发一次变更通知。</summary>
    [TestMethod]
    public async Task Publish_AddsItemsAndRaisesChanged()
    {
        var center = new NotificationCenter();
        int changes = 0;
        center.Changed += () => Interlocked.Increment(ref changes);

        await center.PublishAsync([Item("a"), Item("b")]);

        Assert.HasCount(2, center.Items);
        Assert.AreEqual(2, center.UnreadCount);
        Assert.AreEqual(1, changes, "一批投递只该触发一次变更。");
    }

    /// <summary>
    /// **同 id 重复投递会被跳过,而且保住已读状态。** 每次启动都重投一条
    /// "有新版本"时,覆盖会把用户已经读过的又变回未读 —— 那样铃铛就永远消不掉红点。
    /// </summary>
    [TestMethod]
    public async Task Publish_IsIdempotent_AndPreservesReadState()
    {
        var center = new NotificationCenter();
        await center.PublishAsync([Item("update:1.4.0")]);
        await center.MarkReadAsync("update:1.4.0");
        Assert.AreEqual(0, center.UnreadCount);

        await center.PublishAsync([Item("update:1.4.0")]);

        Assert.HasCount(1, center.Items);
        Assert.AreEqual(0, center.UnreadCount, "重投同一条不该把它变回未读。");
    }

    /// <summary>列表按发布时间倒序,新的在上面。</summary>
    [TestMethod]
    public async Task Publish_SortsNewestFirst()
    {
        var center = new NotificationCenter();
        DateTime now = DateTime.UtcNow;

        await center.PublishAsync([
            Item("old", now.AddDays(-3)),
            Item("new", now),
            Item("mid", now.AddDays(-1))
        ]);

        Assert.AreSequenceEqual(["new", "mid", "old"], [.. center.Items.Select(item => item.Id)]);
    }

    /// <summary>过期条目在投递时就被剔除。</summary>
    [TestMethod]
    public async Task Publish_DropsExpiredItems()
    {
        var center = new NotificationCenter();

        await center.PublishAsync([
            Item("expired", DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1)),
            Item("live", DateTime.UtcNow, DateTime.UtcNow.AddDays(1))
        ]);

        Assert.HasCount(1, center.Items);
        Assert.AreEqual("live", center.Items[0].Id);
    }

    /// <summary>超出上限时丢最旧的 —— 消息中心不是归档系统。</summary>
    [TestMethod]
    public async Task Publish_EnforcesCap_DroppingOldest()
    {
        var center = new NotificationCenter();
        DateTime baseline = DateTime.UtcNow.AddDays(-1);

        await center.PublishAsync(Enumerable.Range(0, NotificationCenter.MaxItems + 20)
            .Select(i => Item($"n{i}", baseline.AddMinutes(i))));

        Assert.HasCount(NotificationCenter.MaxItems, center.Items);
        Assert.DoesNotContain(item => item.Id == "n0", center.Items, "最旧的那条应被截掉。");
        Assert.Contains(item => item.Id == $"n{NotificationCenter.MaxItems + 19}", center.Items, "最新的那条应留下。");
    }

    /// <summary>全部已读把未读清零;删除与清空按预期收缩列表。</summary>
    [TestMethod]
    public async Task MarkAllRead_Remove_And_Clear()
    {
        var center = new NotificationCenter();
        await center.PublishAsync([Item("a"), Item("b"), Item("c")]);

        await center.MarkAllReadAsync();
        Assert.AreEqual(0, center.UnreadCount);

        await center.RemoveAsync("b");
        Assert.HasCount(2, center.Items);
        Assert.DoesNotContain(item => item.Id == "b", center.Items);

        await center.ClearAsync();
        Assert.IsEmpty(center.Items);
    }

    /// <summary>没有实际变化的操作不触发变更通知(避免界面白刷)。</summary>
    [TestMethod]
    public async Task NoOpMutations_DoNotRaiseChanged()
    {
        var center = new NotificationCenter();
        await center.PublishAsync([Item("a")]);
        await center.MarkReadAsync("a");
        int changes = 0;
        center.Changed += () => Interlocked.Increment(ref changes);

        await center.MarkReadAsync("a");            // 已经是已读
        await center.MarkReadAsync("不存在");        // 没这条
        await center.RemoveAsync("也不存在");
        await center.PublishAsync([Item("a")]);     // 重复 id

        Assert.AreEqual(0, changes);
    }

    /// <summary>消息跨重启留存:一条公告关掉应用第二天再看仍然成立。</summary>
    [TestMethod]
    public async Task Load_RestoresPersistedItemsAndReadState()
    {
        var store = new InMemoryDataStore();
        var first = new NotificationCenter(store);
        await first.PublishAsync([Item("kept"), Item("read-me")]);
        await first.MarkReadAsync("read-me");

        var second = new NotificationCenter(store);
        await second.LoadAsync();

        Assert.HasCount(2, second.Items);
        Assert.AreEqual(1, second.UnreadCount, "已读状态要一起恢复。");
    }

    /// <summary>载入时顺手清掉上次运行留下、如今已经过期的条目。</summary>
    [TestMethod]
    public async Task Load_PrunesExpiredItems()
    {
        var store = new InMemoryDataStore();
        var first = new NotificationCenter(store);
        await first.PublishAsync([
            Item("live", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)),
            Item("soon", DateTime.UtcNow, DateTime.UtcNow.AddMilliseconds(80))
        ]);
        await Task.Delay(150);

        var second = new NotificationCenter(store);
        await second.LoadAsync();

        Assert.HasCount(1, second.Items);
        Assert.AreEqual("live", second.Items[0].Id);
    }

    /// <summary>存储读不出来(损坏/旧格式)时当作没有历史消息,不该把应用拖崩。</summary>
    [TestMethod]
    public async Task Load_SurvivesBrokenStore()
    {
        var center = new NotificationCenter(new ThrowingDataStore());

        await center.LoadAsync();

        Assert.IsEmpty(center.Items);
    }

    /// <summary>落盘失败不影响本次运行里看到的消息。</summary>
    [TestMethod]
    public async Task Publish_SurvivesFailingStore()
    {
        var center = new NotificationCenter(new ThrowingDataStore());

        await center.PublishAsync([Item("a")]);

        Assert.HasCount(1, center.Items);
    }

    /// <summary>把文档留在内存里的最小存储,用来验证"跨进程"的往返。</summary>
    private sealed class InMemoryDataStore : IAppDataStore
    {
        private readonly Dictionary<string, object> _documents = [];

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_documents.GetValueOrDefault($"{collection}/{id}") as T);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_documents.Where(pair => pair.Key.StartsWith($"{collection}/", StringComparison.Ordinal))
                                      .Select(pair => pair.Value)
                                      .OfType<T>()
                                      .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            // 存快照的副本:真实存储是序列化落盘,拿到的绝不会是同一个对象实例。
            _documents[$"{collection}/{id}"] = value is List<NotificationItem> items
                                                   ? items.Select(Clone).ToList()
                                                   : value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _documents.Remove($"{collection}/{id}");
            return Task.CompletedTask;
        }

        private static NotificationItem Clone(NotificationItem item) =>
            new()
            {
                Id = item.Id,
                Kind = item.Kind,
                Severity = item.Severity,
                Title = item.Title,
                Body = item.Body,
                PublishedAt = item.PublishedAt,
                ExpiresAt = item.ExpiresAt,
                IsRead = item.IsRead,
                Link = item.Link
            };
    }

    /// <summary>读写都抛的存储,验证消息中心不被存储故障带倒。</summary>
    private sealed class ThrowingDataStore : IAppDataStore
    {
        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            throw new InvalidOperationException("store is broken");

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            throw new InvalidOperationException("store is broken");

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class =>
            throw new InvalidOperationException("store is broken");

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store is broken");
    }
}

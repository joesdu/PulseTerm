using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 插件私有持久化:收藏(<c>Storage</c>)与控制台命令历史(<c>TimeSeries</c>)。
/// <para>
/// 重点在**降级**:无 DB 的宿主上 <c>TimeSeries.OpenAsync</c> 会抛,
/// 那时收藏与历史只在本次会话内有效,而不是让面板打不开。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisStoreTests
{
    [TestMethod]
    public async Task Favorites_RoundTripPerConnection()
    {
        // 同一个键名在两台服务器上是两件事 —— 按连接分开存。
        using var context = new TestPluginContext { PluginId = "velashell.redis" };
        var store = new RedisStore(context);

        await store.SaveFavoritesAsync("redis.example:6379", ["user:1", "lock:a"]);
        await store.SaveFavoritesAsync("10.0.0.2:6379", ["other:1"]);

        CollectionAssert.AreEqual(new[] { "user:1", "lock:a" },
            (await store.LoadFavoritesAsync("redis.example:6379")).ToArray());
        CollectionAssert.AreEqual(new[] { "other:1" },
            (await store.LoadFavoritesAsync("10.0.0.2:6379")).ToArray());
    }

    [TestMethod]
    public async Task Favorites_UnknownConnection_IsEmptyNotNull()
    {
        using var context = new TestPluginContext();
        var store = new RedisStore(context);

        Assert.IsEmpty(await store.LoadFavoritesAsync("never:6379"));
    }

    [TestMethod]
    public async Task History_RoundTripsInChronologicalOrder()
    {
        // ↑ 的语义是"往更早翻",所以读回来必须是旧的在前。
        using var context = new TestPluginContext();
        var store = new RedisStore(context);

        await store.AppendHistoryAsync("host:6379", "PING");
        await store.AppendHistoryAsync("host:6379", "GET a");
        await store.AppendHistoryAsync("host:6379", "GET b");

        CollectionAssert.AreEqual(
            new[] { "PING", "GET a", "GET b" },
            (await store.LoadHistoryAsync("host:6379")).ToArray());
    }

    [TestMethod]
    public async Task History_IsScopedPerConnection()
    {
        using var context = new TestPluginContext();
        var store = new RedisStore(context);

        await store.AppendHistoryAsync("a:6379", "PING");
        await store.AppendHistoryAsync("b:6379", "INFO");

        CollectionAssert.AreEqual(new[] { "PING" }, (await store.LoadHistoryAsync("a:6379")).ToArray());
        CollectionAssert.AreEqual(new[] { "INFO" }, (await store.LoadHistoryAsync("b:6379")).ToArray());
    }

    [TestMethod]
    public async Task History_ConsecutiveWrites_AreNotCollapsedIntoOne()
    {
        // 同序列同毫秒会被时序库覆盖,而连着敲两条命令是常事 ——
        // 这正是 TimeSeriesClock 存在的理由。
        using var context = new TestPluginContext();
        var store = new RedisStore(context);

        for (int i = 0; i < 20; i++)
        {
            await store.AppendHistoryAsync("host:6379", $"GET key{i}");
        }

        Assert.HasCount(20, await store.LoadHistoryAsync("host:6379"));
    }

    [TestMethod]
    public async Task History_WithoutATimeSeriesBackend_DegradesSilently()
    {
        // 无 DB 的宿主:OpenAsync 抛 —— 历史不持久化,但**不该冒到调用方**。
        using var context = new TestPluginContext { TimeSeries = new UnavailableTimeSeriesStub() };
        var store = new RedisStore(context);

        await store.AppendHistoryAsync("host:6379", "PING");

        Assert.IsEmpty(await store.LoadHistoryAsync("host:6379"));
        Assert.Contains(
            entry =>
                entry.Message.Contains("not persisted", StringComparison.OrdinalIgnoreCase), context.CollectingLog.Entries,
            "降级要留一条可查的日志。");
    }

    [TestMethod]
    public async Task History_UnavailableBackend_IsProbedOnlyOnce()
    {
        // 每敲一条命令都重试一遍只会往日志里灌噪音。
        var stub = new UnavailableTimeSeriesStub();
        using var context = new TestPluginContext { TimeSeries = stub };
        var store = new RedisStore(context);

        await store.AppendHistoryAsync("host:6379", "A");
        await store.AppendHistoryAsync("host:6379", "B");
        await store.LoadHistoryAsync("host:6379");

        Assert.AreEqual(1, stub.OpenAttempts);
    }

    /// <summary>模拟无 DB 宿主:<c>OpenAsync</c> 一律抛(与真实宿主同口径)。</summary>
    private sealed class UnavailableTimeSeriesStub : PluginSdk.TimeSeries.ITimeSeriesApi
    {
        public int OpenAttempts { get; private set; }

        public Task<PluginSdk.TimeSeries.ITimeSeries> OpenAsync(
            PluginSdk.TimeSeries.TimeSeriesDefinition definition,
            CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Time series storage is unavailable in this host.");
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> DropAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}

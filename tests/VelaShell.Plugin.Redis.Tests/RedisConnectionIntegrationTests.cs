using StackExchange.Redis;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 打真实 Redis 的集成测试:连接 → 探测 → 游标扫描 → 批量取类型 → 逐类型读值。
/// <para>
/// 按仓库惯例**按环境早退跳过**:本机没有 <c>127.0.0.1:6379</c> 时报 Inconclusive 而不是失败。
/// 用一个独立的库(<see cref="Database" />)与带随机后缀的键前缀,不碰任何既有数据;
/// 收尾只删自己造的键 —— **绝不 FLUSHDB**,那正是本插件在护栏里明令要过手打确认的操作。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisConnectionIntegrationTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 6379;
    private const int Database = 9;

    private static string _prefix = "";
    private static RedisConnection? _connection;

    private static RedisSettings Settings(int scanCount = 50) =>
        RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "it",
            Host = Host,
            Port = Port,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = Database.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["scanCount"] = scanCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["clientName"] = "velashell-tests"
            }
        });

    [ClassInitialize]
    public static async Task SeedAsync(TestContext _)
    {
        _prefix = $"velashell-it-{Guid.NewGuid():N}";
        try
        {
            _connection = await RedisConnection.ConnectAsync(Host, Port, "", "", Settings());
        }
        catch (Exception)
        {
            _connection = null;
            return;
        }
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        IDatabase db = mux.GetDatabase(Database);
        await db.StringSetAsync($"{_prefix}:user:1:name", "张三");
        await db.StringSetAsync($"{_prefix}:user:2:name", "李四", TimeSpan.FromMinutes(30));
        await db.HashSetAsync($"{_prefix}:user:1:profile",
            [new HashEntry("name", "张三"), new HashEntry("age", "32")]);
        await db.ListRightPushAsync($"{_prefix}:queue", ["a", "b", "c"]);
        await db.SetAddAsync($"{_prefix}:tags", ["vip", "beta"]);
        await db.SortedSetAddAsync($"{_prefix}:board", [new SortedSetEntry("alice", 128), new SortedSetEntry("bob", 64)]);
        // 二进制键:非法 UTF-8,用来证明扫描与显示这一路不会把它改坏。
        await db.StringSetAsync((RedisKey)System.Text.Encoding.UTF8.GetBytes($"{_prefix}:bin:").Concat(new byte[] { 0xC3, 0x28 }).ToArray(), "raw");
        await mux.CloseAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_connection is null)
        {
            return;
        }
        try
        {
            using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
                new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
            IDatabase db = mux.GetDatabase(Database);
            IServer server = mux.GetServer(Host, Port);
            // 只删自己造的键。用 SCAN 找它们 —— 连清理脚本也不许用 KEYS。
            await foreach (RedisKey key in server.KeysAsync(Database, $"{_prefix}*", pageSize: 100))
            {
                await db.KeyDeleteAsync(key);
            }
            await mux.CloseAsync();
        }
        finally
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private static RedisConnection Require()
    {
        if (_connection is null)
        {
            Assert.Inconclusive($"没有可用的 Redis({Host}:{Port}),跳过集成测试。");
        }
        return _connection!;
    }

    [TestMethod]
    public void Connect_ProbesServerInfo()
    {
        RedisConnection connection = Require();

        Assert.IsTrue(connection.IsConnected);
        Assert.AreNotEqual("?", connection.Info.Version, "版本探测失败会让状态条显示一个问号。");
        Assert.IsTrue(connection.Info.Protocol is "RESP2" or "RESP3");
        Assert.IsGreaterThanOrEqualTo(1, connection.Info.Databases);
        Assert.AreEqual(Database, connection.Database);
    }

    [TestMethod]
    public async Task PingAsync_ReturnsARoundTrip()
    {
        TimeSpan rtt = await Require().PingAsync();

        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, rtt);
    }

    [TestMethod]
    public async Task DatabaseSizeAsync_CountsSeededKeys()
    {
        long size = await Require().DatabaseSizeAsync();

        Assert.IsGreaterThanOrEqualTo(7, size, $"至少应能数到造出来的 7 个键,实际 {size}。");
    }

    [TestMethod]
    public async Task ScanAsync_WalksTheCursorToZeroAndFindsEverySeededKey()
    {
        // 这一条是"进度必须诚实"那条纪律的证据:一直扫到游标归零才算全部,
        // 期间允许出现空页,也允许出现重复键(调用方按键名去重)。
        RedisConnection connection = Require();
        var found = new HashSet<RedisKeyName>();
        string cursor = "0";
        int rounds = 0;
        do
        {
            RedisScanPage page = await connection.ScanAsync(cursor, $"{_prefix}*", type: null);
            cursor = page.Cursor;
            foreach (RedisKeyName key in page.Keys)
            {
                found.Add(key);
            }
            rounds++;
            Assert.IsLessThan(500, rounds, "游标没有收敛,扫描逻辑有问题。");
        }
        while (cursor is not "0");

        Assert.HasCount(7, found, "七个键都要被扫到,一个不多一个不少。");
        Assert.Contains(k => k.Text == $"{_prefix}:user:1:profile", found);
    }

    /// <summary>
    /// 类型过滤必须**在任何服务器版本上**都真的生效:6.0+ 走服务端的 <c>SCAN TYPE</c>,
    /// 更老的版本退回客户端批量 <c>TYPE</c> 再收窄。界面上写着"hash"就只能列出 hash ——
    /// 一边显示类型过滤一边给出所有类型的键,是最坏的一种坏。
    /// </summary>
    [TestMethod]
    public async Task ScanAsync_WithTypeFilter_OnlyReturnsThatType()
    {
        RedisConnection connection = Require();
        var found = new List<RedisKeyName>();
        string cursor = "0";
        do
        {
            RedisScanPage page = await connection.ScanAsync(cursor, $"{_prefix}*", type: "hash");
            cursor = page.Cursor;
            found.AddRange(page.Keys);
        }
        while (cursor is not "0");

        Assert.HasCount(1, found);
        Assert.AreEqual($"{_prefix}:user:1:profile", found[0].Text);
    }

    [TestMethod]
    public async Task ScanAsync_PreservesBinaryKeysExactly()
    {
        // 非法 UTF-8 的键必须原样带回来:界面显示转义形式,操作用原始字节。
        RedisConnection connection = Require();
        byte[] expected = [.. System.Text.Encoding.UTF8.GetBytes($"{_prefix}:bin:"), 0xC3, 0x28];
        var found = new List<RedisKeyName>();
        string cursor = "0";
        do
        {
            RedisScanPage page = await connection.ScanAsync(cursor, $"{_prefix}:bin:*", type: null);
            cursor = page.Cursor;
            found.AddRange(page.Keys);
        }
        while (cursor is not "0");

        RedisKeyName binary = found.Single();
        CollectionAssert.AreEqual(expected, binary.Raw.ToArray());
        Assert.IsFalse(binary.IsUtf8);
        Assert.AreEqual(binary.Display, binary.Text, "非法 UTF-8 的键名回落到转义形式显示。");
    }

    [TestMethod]
    public async Task TypesAsync_ReportsEveryTypeInOneRoundTrip()
    {
        RedisConnection connection = Require();
        RedisKeyName[] keys =
        [
            new($"{_prefix}:user:1:name"),
            new($"{_prefix}:user:1:profile"),
            new($"{_prefix}:queue"),
            new($"{_prefix}:tags"),
            new($"{_prefix}:board"),
            new($"{_prefix}:does-not-exist")
        ];

        IReadOnlyList<string> types = await connection.TypesAsync(keys);

        CollectionAssert.AreEqual(new[] { "string", "hash", "list", "set", "zset", "none" }, types.ToArray());
    }

    [TestMethod]
    public async Task DescribeAsync_ReadsTtlEncodingAndLength()
    {
        RedisConnection connection = Require();

        RedisKeyInfo withTtl = await connection.DescribeAsync(new($"{_prefix}:user:2:name"));
        RedisKeyInfo withoutTtl = await connection.DescribeAsync(new($"{_prefix}:user:1:name"));

        Assert.AreEqual("string", withTtl.Type);
        Assert.IsNotNull(withTtl.Ttl, "设过过期时间的键必须报出 TTL。");
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(30), withTtl.Ttl!.Value);
        Assert.IsNull(withoutTtl.Ttl, "没有过期时间就是 null,而不是 0 或负数。");
        Assert.IsFalse(string.IsNullOrEmpty(withoutTtl.Encoding), "OBJECT ENCODING 应能取到。");
        Assert.AreEqual(6, withoutTtl.Length, "「张三」的 UTF-8 长度是 6 字节。");
    }

    [TestMethod]
    public async Task DescribeAsync_MissingKey_IsGoneNotAnError()
    {
        // 查看期间过期是**正常生命周期**,不是故障 —— 界面就地说明,不弹错误弹窗。
        RedisKeyInfo info = await Require().DescribeAsync(new($"{_prefix}:vanished"));

        Assert.IsTrue(info.IsGone);
        Assert.AreEqual("none", info.Type);
    }

    [TestMethod]
    public async Task DescribeAsync_WithMemory_ReportsAnEstimateOrDegradesCleanly()
    {
        // MEMORY USAGE 是 Redis 4.0 才有的。老服务器上它不是错误而是**空状态**:
        // 取不到就报 -1,界面把那一列留空,而不是弹一句红色的失败。
        RedisConnection connection = Require();

        RedisKeyInfo info = await connection.DescribeAsync(new($"{_prefix}:user:1:profile"), includeMemory: true);

        if (Version.TryParse(connection.Info.Version, out Version? version) && version.Major >= 4)
        {
            Assert.IsGreaterThan(0, info.MemoryBytes, "4.0 及以上应给出一个抽样估计值。");
        }
        else
        {
            Assert.AreEqual(-1, info.MemoryBytes, "拿不到就必须是 -1(未知),不能是 0 —— 0 会被读成「不占内存」。");
        }
    }

    [TestMethod]
    public async Task ReadStringAsync_ReturnsTheExactBytes()
    {
        RedisStringValue value = await Require().ReadStringAsync(new($"{_prefix}:user:1:name"));

        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("张三"), value.Bytes);
        Assert.AreEqual(6, value.TotalLength);
        Assert.IsFalse(value.IsTruncated);
    }

    [TestMethod]
    public async Task ReadElementsAsync_ReadsHashFields()
    {
        RedisElementPage page = await Require().ReadElementsAsync(new($"{_prefix}:user:1:profile"), "hash", "0", 100);

        Assert.AreEqual(2, page.Total);
        Assert.IsTrue(page.IsComplete);
        Assert.AreEqual("张三", page.Rows.Single(r => r.Label == "name").Value);
        Assert.AreEqual("32", page.Rows.Single(r => r.Label == "age").Value);
    }

    [TestMethod]
    public async Task ReadElementsAsync_ReadsListByIndexWindow()
    {
        // 列表没有 SCAN:索引就是它的游标。分两页读,验证游标接得上。
        RedisConnection connection = Require();
        var key = new RedisKeyName($"{_prefix}:queue");

        RedisElementPage first = await connection.ReadElementsAsync(key, "list", "0", 2);
        Assert.AreEqual(3, first.Total);
        Assert.HasCount(2, first.Rows);
        Assert.AreEqual("0", first.Rows[0].Label);
        Assert.AreEqual("a", first.Rows[0].Value);
        Assert.IsFalse(first.IsComplete);

        RedisElementPage second = await connection.ReadElementsAsync(key, "list", first.Cursor, 2);
        Assert.AreEqual("2", second.Rows.Single().Label);
        Assert.AreEqual("c", second.Rows.Single().Value);
        Assert.IsTrue(second.IsComplete);
    }

    [TestMethod]
    public async Task ReadElementsAsync_ReadsSetMembers()
    {
        RedisElementPage page = await Require().ReadElementsAsync(new($"{_prefix}:tags"), "set", "0", 100);

        Assert.AreEqual(2, page.Total);
        CollectionAssert.AreEquivalent(new[] { "vip", "beta" }, page.Rows.Select(r => r.Label).ToArray());
        Assert.IsTrue(page.Rows.All(r => r.Value.Length == 0), "集合只有成员,没有值 —— 值列该是空的。");
    }

    [TestMethod]
    public async Task ReadElementsAsync_ReadsSortedSetScores()
    {
        RedisElementPage page = await Require().ReadElementsAsync(new($"{_prefix}:board"), "zset", "0", 100);

        Assert.AreEqual(2, page.Total);
        Assert.AreEqual(128d, page.Rows.Single(r => r.Label == "alice").Score);
        Assert.AreEqual(64d, page.Rows.Single(r => r.Label == "bob").Score);
    }

    [TestMethod]
    public async Task ReadElementsAsync_UnknownType_ReturnsAnEmptyPage()
    {
        RedisElementPage page = await Require().ReadElementsAsync(new($"{_prefix}:queue"), "bloomfilter", "0", 10);

        Assert.IsEmpty(page.Rows);
        Assert.IsTrue(page.IsComplete);
    }

    [TestMethod]
    public async Task RefreshKeyspaceAsync_ReportsPerDatabaseKeyCounts()
    {
        // 数据库下拉里的键数就来自这里 —— 省掉"逐个库点进去看有没有东西"的盲测。
        RedisConnection connection = Require();

        await connection.RefreshKeyspaceAsync();

        Assert.IsTrue(connection.Info.KeyCountByDatabase.TryGetValue(Database, out long count));
        Assert.IsGreaterThanOrEqualTo(7, count);
    }
}

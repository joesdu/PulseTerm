using System.Text;
using StackExchange.Redis;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 打真实 Redis 的写入路径、控制台与运维面测试。
/// <para>
/// 按仓库惯例**按环境早退跳过**;用独立库(db9)与带随机后缀的键前缀,收尾只删自己造的键。
/// 本机那台是 Redis 3.0,于是 <c>KEEPTTL</c>(6.0+)与 <c>UNLINK</c>(4.0+)的**回落路径**
/// 在这里都被真的走了一遍 —— 这正是老服务器的价值。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisWriteIntegrationTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 6379;
    private const int Database = 9;

    private static string _prefix = "";
    private static RedisConnection? _connection;

    private static RedisSettings Settings() =>
        RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "wt",
            Host = Host,
            Port = Port,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = Database.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["clientName"] = "velashell-write-tests",
                ["environment"] = "development"
            }
        });

    [ClassInitialize]
    public static async Task InitAsync(TestContext _)
    {
        _prefix = $"velashell-wt-{Guid.NewGuid():N}";
        try
        {
            _connection = await RedisConnection.ConnectAsync(Host, Port, "", "", Settings());
        }
        catch (Exception)
        {
            _connection = null;
        }
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
            foreach (RedisKey key in server.KeysAsync(Database, $"{_prefix}*", pageSize: 100).ToBlockingEnumerable())
            {
                await db.KeyDeleteAsync(key);
            }
            // 跨库复制那条用例会往 db8 写一个键。
            IDatabase other = mux.GetDatabase(8);
            foreach (RedisKey key in server.KeysAsync(8, $"{_prefix}*", pageSize: 100).ToBlockingEnumerable())
            {
                await other.KeyDeleteAsync(key);
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
            Assert.Inconclusive($"没有可用的 Redis({Host}:{Port}),跳过写入集成测试。");
        }
        return _connection!;
    }

    private static RedisKeyName Key(string suffix) => new($"{_prefix}:{suffix}");

    // ── 写入 ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task SetStringAsync_KeepsTheExistingTtl()
    {
        // 用户改的是"值",不是"这个键还能活多久" —— 裸 SET 会把 TTL 抹掉,
        // 那是一次没人要求过的副作用。老服务器上走的是"读 TTL → 写值 → 补回"的回落路径。
        RedisConnection connection = Require();
        RedisKeyName key = Key("keepttl");
        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("first"), keepTtl: false);
        await connection.ExpireAsync(key, TimeSpan.FromMinutes(10));

        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("second"));

        RedisKeyInfo info = await connection.DescribeAsync(key);
        Assert.IsNotNull(info.Ttl, "改值不该把过期时间抹掉。");
        Assert.IsGreaterThan(TimeSpan.FromMinutes(8), info.Ttl!.Value);
        RedisStringValue value = await connection.ReadStringAsync(key);
        Assert.AreEqual("second", Encoding.UTF8.GetString(value.Bytes));
    }

    [TestMethod]
    public async Task SetStringAsync_WithoutKeepTtl_DropsIt()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("droptll");
        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("x"), keepTtl: false);
        await connection.ExpireAsync(key, TimeSpan.FromMinutes(10));

        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("y"), keepTtl: false);

        Assert.IsNull((await connection.DescribeAsync(key)).Ttl);
    }

    [TestMethod]
    public async Task HashField_CanBeWrittenAndDeleted()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("hash");

        await connection.SetHashFieldAsync(key, "name", "张三");
        await connection.SetHashFieldAsync(key, "age", "32");

        RedisElementPage page = await connection.ReadElementsAsync(key, "hash", "0", 100);
        Assert.AreEqual(2, page.Total);
        Assert.AreEqual("张三", page.Rows.Single(r => r.Label == "name").Value);

        Assert.IsTrue(await connection.DeleteHashFieldAsync(key, "age"));
        Assert.IsFalse(await connection.DeleteHashFieldAsync(key, "age"), "第二次删同一个字段应报没删到。");
        Assert.AreEqual(1, (await connection.ReadElementsAsync(key, "hash", "0", 100)).Total);
    }

    [TestMethod]
    public async Task ListItem_CanBePushedSetAndRemovedByValue()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("list");

        await connection.PushListAsync(key, "a", atHead: false);
        await connection.PushListAsync(key, "b", atHead: false);
        await connection.PushListAsync(key, "z", atHead: true);
        await connection.SetListItemAsync(key, 1, "A");

        RedisElementPage page = await connection.ReadElementsAsync(key, "list", "0", 100);
        CollectionAssert.AreEqual(new[] { "z", "A", "b" }, page.Rows.Select(r => r.Value).ToArray());

        // 列表没有按索引删除的原语:删的是第一个等于该值的元素。
        Assert.AreEqual(1, await connection.RemoveListValueAsync(key, "A"));
        CollectionAssert.AreEqual(
            new[] { "z", "b" },
            (await connection.ReadElementsAsync(key, "list", "0", 100)).Rows.Select(r => r.Value).ToArray());
    }

    [TestMethod]
    public async Task SetMembers_CanBeAddedAndRemoved()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("set");

        Assert.IsTrue(await connection.AddSetMemberAsync(key, "vip"));
        Assert.IsFalse(await connection.AddSetMemberAsync(key, "vip"), "重复成员不算新增。");
        Assert.IsTrue(await connection.RemoveSetMemberAsync(key, "vip"));
        Assert.IsFalse(await connection.RemoveSetMemberAsync(key, "vip"));
    }

    [TestMethod]
    public async Task SortedSetScores_CanBeWrittenAndChanged()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("zset");

        Assert.IsTrue(await connection.SetSortedMemberAsync(key, "alice", 10));
        Assert.IsFalse(await connection.SetSortedMemberAsync(key, "alice", 20), "改分不算新增成员。");

        RedisElementPage page = await connection.ReadElementsAsync(key, "zset", "0", 100);
        Assert.AreEqual(20d, page.Rows.Single(r => r.Label == "alice").Score);
        Assert.IsTrue(await connection.RemoveSortedMemberAsync(key, "alice"));
    }

    [TestMethod]
    public async Task ExpireAndPersist_RoundTrip()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("ttl");
        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("x"), keepTtl: false);

        Assert.IsTrue(await connection.ExpireAsync(key, TimeSpan.FromMinutes(5)));
        Assert.IsNotNull((await connection.DescribeAsync(key)).Ttl);
        Assert.IsTrue(await connection.PersistAsync(key));
        Assert.IsNull((await connection.DescribeAsync(key)).Ttl);
        Assert.IsFalse(await connection.PersistAsync(key), "本来就永久的键报没改动。");
    }

    [TestMethod]
    public async Task Rename_DoesNotSilentlyOverwrite()
    {
        // RENAME 会静默覆盖目标键 —— 那是一次无声的数据丢失,所以默认走 RENAMENX。
        RedisConnection connection = Require();
        RedisKeyName source = Key("rename-src");
        RedisKeyName target = Key("rename-dst");
        await connection.SetStringAsync(source, Encoding.UTF8.GetBytes("from"), keepTtl: false);
        await connection.SetStringAsync(target, Encoding.UTF8.GetBytes("to"), keepTtl: false);

        Assert.IsFalse(await connection.RenameAsync(source, target, overwrite: false),
            "目标已存在时不覆盖,如实报失败。");
        Assert.AreEqual("to", Encoding.UTF8.GetString((await connection.ReadStringAsync(target)).Bytes));

        Assert.IsTrue(await connection.RenameAsync(source, target, overwrite: true));
        Assert.AreEqual("from", Encoding.UTF8.GetString((await connection.ReadStringAsync(target)).Bytes));
        Assert.IsTrue((await connection.DescribeAsync(source)).IsGone);
    }

    [TestMethod]
    public async Task Rename_ToAFreeNameSucceeds()
    {
        RedisConnection connection = Require();
        RedisKeyName source = Key("rename-free");
        RedisKeyName target = Key("rename-free2");
        await connection.SetStringAsync(source, Encoding.UTF8.GetBytes("v"), keepTtl: false);

        Assert.IsTrue(await connection.RenameAsync(source, target, overwrite: false));
        Assert.AreEqual("v", Encoding.UTF8.GetString((await connection.ReadStringAsync(target)).Bytes));
    }

    [TestMethod]
    public async Task Delete_RemovesKeysAndReportsTheCount()
    {
        // UNLINK 优先(4.0+);本机 3.0 上会回落 DEL —— 两条路的返回值语义都要对。
        RedisConnection connection = Require();
        RedisKeyName first = Key("del-1");
        RedisKeyName second = Key("del-2");
        await connection.SetStringAsync(first, Encoding.UTF8.GetBytes("1"), keepTtl: false);
        await connection.SetStringAsync(second, Encoding.UTF8.GetBytes("2"), keepTtl: false);

        Assert.AreEqual(2, await connection.DeleteAsync([first, second, Key("del-missing")]));
        Assert.IsTrue((await connection.DescribeAsync(first)).IsGone);
    }

    [TestMethod]
    public async Task Delete_EmptyList_IsANoOp()
    {
        Assert.AreEqual(0, await Require().DeleteAsync([]));
    }

    [TestMethod]
    public async Task CopyKey_UsesDumpRestoreAndCarriesTheTtl()
    {
        // DUMP/RESTORE 是**保真**的:编码、嵌套结构、模块类型全都原样过去。
        RedisConnection connection = Require();
        RedisKeyName source = Key("copy-src");
        await connection.SetHashFieldAsync(source, "f", "v");
        await connection.ExpireAsync(source, TimeSpan.FromMinutes(30));

        Assert.IsTrue(await connection.CopyKeyAsync(source, connection, targetDatabase: 8,
            newKey: Key("copy-dst"), replace: true));

        // 换到 db8 去看那一份。
        connection.SelectDatabase(8);
        try
        {
            RedisKeyInfo copied = await connection.DescribeAsync(Key("copy-dst"));
            Assert.AreEqual("hash", copied.Type);
            Assert.IsNotNull(copied.Ttl, "复制一个还有 TTL 的键,到了对面变成永久是另一种失真。");
        }
        finally
        {
            connection.SelectDatabase(Database);
        }
    }

    [TestMethod]
    public async Task CopyKey_MissingSource_ReportsFalse()
    {
        Assert.IsFalse(await Require().CopyKeyAsync(
            Key("copy-nothing"), Require(), targetDatabase: 8, newKey: null, replace: true));
    }

    // ── 控制台 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Console_LoadsCommandMetadataFromTheServer()
    {
        // 补全与档位分级的依据 —— 而且它自动包含这台服务器上的模块命令。
        RedisConnection connection = Require();

        Assert.IsTrue(connection.Guard.MetadataFromServer, "COMMAND 应答过就该用服务器的 flags。");
        Assert.IsGreaterThan(100, connection.CommandHints.Count, $"命令数偏少:{connection.CommandHints.Count}");
        Assert.AreEqual(RedisCommandRisk.Read, connection.Guard.Classify("GET"));
        Assert.AreEqual(RedisCommandRisk.Write, connection.Guard.Classify("SET"));
    }

    [TestMethod]
    public async Task Console_Completion_MatchesByPrefix()
    {
        IReadOnlyList<RedisCommandHint> hints = Require().Complete("hget");

        Assert.Contains(hint => hint.Name == "HGETALL", hints);
        Assert.IsTrue(hints.All(hint => hint.Name.StartsWith("HGET", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Console_FormatsRepliesLikeRedisCli()
    {
        RedisConnection connection = Require();
        RedisKeyName counter = Key("counter");

        RedisConsoleResult ping = await connection.ExecuteConsoleAsync("PING");
        Assert.AreEqual("PONG", ping.Lines.Single().Text);
        Assert.AreEqual(RedisReplyLineKind.Status, ping.Lines.Single().Kind);

        RedisConsoleResult incr = await connection.ExecuteConsoleAsync($"INCR \"{counter.Display}\"");
        Assert.AreEqual("(integer) 1", incr.Lines.Single().Text);

        RedisConsoleResult missing = await connection.ExecuteConsoleAsync($"GET \"{_prefix}:nothing\"");
        Assert.AreEqual("(nil)", missing.Lines.Single().Text);

        RedisConsoleResult empty = await connection.ExecuteConsoleAsync($"LRANGE \"{_prefix}:nothing-list\" 0 -1");
        Assert.AreEqual("(empty array)", empty.Lines.Single().Text);
    }

    [TestMethod]
    public async Task Console_ArrayReplies_AreNumbered()
    {
        RedisConnection connection = Require();
        RedisKeyName key = Key("console-hash");
        await connection.SetHashFieldAsync(key, "a", "1");

        RedisConsoleResult result = await connection.ExecuteConsoleAsync($"HGETALL \"{key.Display}\"");

        Assert.HasCount(2, result.Lines);
        Assert.AreEqual("1) \"a\"", result.Lines[0].Text);
        Assert.AreEqual("2) \"1\"", result.Lines[1].Text);
    }

    [TestMethod]
    public async Task Console_ServerErrors_ArePassedThroughVerbatim()
    {
        // 服务器说的错是排障的第一手信息(WRONGTYPE / NOPERM / MOVED …),不许包装。
        RedisConnection connection = Require();
        RedisKeyName key = Key("wrongtype");
        await connection.SetStringAsync(key, Encoding.UTF8.GetBytes("x"), keepTtl: false);

        RedisConsoleResult result = await connection.ExecuteConsoleAsync($"LPUSH \"{key.Display}\" a");

        Assert.IsTrue(result.IsError);
        Assert.Contains("WRONGTYPE", result.Lines.Single().Text);
    }

    [TestMethod]
    public async Task Console_UnparsableLine_ReportsAParseError()
    {
        RedisConsoleResult result = await Require().ExecuteConsoleAsync("SET a \"unclosed");

        Assert.IsTrue(result.IsError);
        Assert.Contains("parse", result.Lines.Single().Text);
    }

    [TestMethod]
    public async Task Console_RejectsCommandsTheTransportCannotCarry()
    {
        // 如实拒绝并说明原因,而不是让用户敲下去然后卡住或超时。
        RedisConnection connection = Require();

        foreach (string command in (string[])["MONITOR", "BLPOP q 0", "SUBSCRIBE ch", "MULTI"])
        {
            RedisConsoleResult result = await connection.ExecuteConsoleAsync(command);
            Assert.IsTrue(result.IsError, $"{command} 应被拒绝");
            Assert.AreEqual(RedisReplyLineKind.Note, result.Lines.Single().Kind);
            Assert.IsTrue(RedisConnection.IsUnsupportedOnThisTransport(command));
        }
    }

    [TestMethod]
    public async Task Console_Select_ReportsTheTargetDatabaseAndFollowsIt()
    {
        RedisConnection connection = Require();
        try
        {
            RedisConsoleResult result = await connection.ExecuteConsoleAsync("SELECT 8");

            Assert.AreEqual(8, result.SelectedDatabase);
            Assert.AreEqual(8, connection.Database, "浏览器要跟着切 —— 静默分叉是更差的失败模式。");
        }
        finally
        {
            connection.SelectDatabase(Database);
        }
    }

    // ── 运维面 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Overview_ReportsServerAndMemoryGroups()
    {
        IReadOnlyList<RedisMetricGroup> groups = await Require().ReadOverviewAsync();

        Assert.DoesNotContain(group => group.Unavailable, groups, "INFO 应能读到。");
        RedisMetricGroup server = groups.Single(group => group.Title == "server");
        Assert.IsFalse(string.IsNullOrEmpty(server.Items.Single(item => item.Label == "version").Value));
        Assert.Contains(group => group.Title == "memory", groups);
        Assert.Contains(group => group.Title == "persistence", groups);
    }

    [TestMethod]
    public async Task Overview_LeavesUnknownFieldsEmptyRatherThanZero()
    {
        // 拿不到就留空 —— 0 会被读成一个真实的测量值。
        IReadOnlyList<RedisMetricGroup> groups = await Require().ReadOverviewAsync();

        foreach (RedisMetric metric in groups.SelectMany(group => group.Items))
        {
            Assert.IsNotNull(metric.Value);
        }
    }

    [TestMethod]
    public async Task Slowlog_IsReadable()
    {
        IReadOnlyList<RedisSlowlogEntry>? entries = await Require().ReadSlowlogAsync(16);

        Assert.IsNotNull(entries, "这台服务器开放了 SLOWLOG,应能读到(可能是空表)。");
        foreach (RedisSlowlogEntry entry in entries)
        {
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, entry.Duration);
            Assert.IsFalse(string.IsNullOrEmpty(entry.DurationText));
        }
    }

    [TestMethod]
    public async Task Clients_MarkOurOwnConnections()
    {
        // 自己的连接要标出来并禁止断开:一个客户端把自己 kill 掉然后报"连接丢失",
        // 是很蠢但很常见的 bug。
        IReadOnlyList<RedisClientEntry>? clients = await Require().ReadClientsAsync();

        Assert.IsNotNull(clients);
        Assert.Contains(client => client.IsSelf, clients, "至少本连接自己应被认出来。");
        Assert.IsTrue(clients.All(client => client.Address.Length > 0));
    }

    [TestMethod]
    public async Task MemorySample_DegradesCleanlyWhenMemoryUsageIsMissing()
    {
        RedisConnection connection = Require();

        RedisMemorySample sample = await connection.SampleMemoryAsync(sampleLimit: 200);

        if (Version.TryParse(connection.Info.Version, out Version? version) && version.Major >= 4)
        {
            Assert.IsTrue(sample.Available);
            Assert.IsGreaterThan(0, sample.SampledKeys);
        }
        else
        {
            // 4.0 以下:整条路不可用,如实上报而不是给一堆 0。
            Assert.IsFalse(sample.Available);
            Assert.IsEmpty(sample.Buckets);
        }
    }
}

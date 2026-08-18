using System.Globalization;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>概览里的一项指标。</summary>
/// <param name="Label">名称。</param>
/// <param name="Value">值(已格式化);取不到时为空串。</param>
public sealed record RedisMetric(string Label, string Value);

/// <summary>概览里的一组指标。</summary>
/// <param name="Title">分组标题。</param>
/// <param name="Items">指标。</param>
/// <param name="Unavailable">该组数据不可得(服务器禁了对应命令 / 字段)。</param>
public sealed record RedisMetricGroup(string Title, IReadOnlyList<RedisMetric> Items, bool Unavailable = false);

/// <summary>慢日志的一条。</summary>
/// <param name="Id">条目 id。</param>
/// <param name="At">发生时间。</param>
/// <param name="Duration">耗时。</param>
/// <param name="Command">命令(参数过长已截断)。</param>
/// <param name="Client">客户端地址;老服务器不提供时为空串。</param>
public sealed record RedisSlowlogEntry(long Id, DateTimeOffset At, TimeSpan Duration, string Command, string Client)
{
    /// <summary>耗时的显示形式(毫秒,三位小数 —— 慢日志里的差别常在亚毫秒级)。</summary>
    public string DurationText =>
        $"{Duration.TotalMilliseconds.ToString("0.###", CultureInfo.CurrentCulture)} ms";

    /// <summary>发生时间的显示形式。</summary>
    public string TimeText => At.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary><c>CLIENT LIST</c> 的一条。</summary>
/// <param name="Id">连接 id。</param>
/// <param name="Address">地址。</param>
/// <param name="Name">客户端名。</param>
/// <param name="Age">连接时长。</param>
/// <param name="Idle">空闲时长。</param>
/// <param name="Database">所在库。</param>
/// <param name="LastCommand">最近一条命令。</param>
/// <param name="IsSelf">是否是本客户端自己的连接。</param>
public sealed record RedisClientEntry(
    long Id,
    string Address,
    string Name,
    TimeSpan Age,
    TimeSpan Idle,
    int Database,
    string LastCommand,
    bool IsSelf)
{
    /// <summary>连接时长的显示形式。</summary>
    public string AgeText => RedisTtl.Describe(Age);

    /// <summary>空闲时长的显示形式。</summary>
    public string IdleText => RedisTtl.Describe(Idle);

    /// <summary>所在库的显示形式。</summary>
    public string DatabaseText => Database.ToString(CultureInfo.CurrentCulture);
}

/// <summary>内存抽样的一行(按前缀聚合)。</summary>
/// <param name="Prefix">键前缀。</param>
/// <param name="Keys">该前缀下抽到的键数。</param>
/// <param name="Bytes">这些键的内存占用之和(抽样估计)。</param>
public sealed record RedisMemoryBucket(string Prefix, long Keys, long Bytes)
{
    /// <summary>键数的显示形式。</summary>
    public string KeysText => Keys.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>占用的显示形式(自动换单位)。</summary>
    public string BytesText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{Bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}

/// <summary>一次内存抽样的结果。</summary>
/// <param name="Buckets">按占用降序的前缀桶。</param>
/// <param name="TopKeys">占用最大的若干个键。</param>
/// <param name="SampledKeys">抽样键数。</param>
/// <param name="EstimatedTotal"><c>DBSIZE</c> 给出的估计总数;未知为 -1。</param>
/// <param name="Available"><c>MEMORY USAGE</c> 是否可用(4.0+)。</param>
public sealed record RedisMemorySample(
    IReadOnlyList<RedisMemoryBucket> Buckets,
    IReadOnlyList<RedisMemoryBucket> TopKeys,
    long SampledKeys,
    long EstimatedTotal,
    bool Available);

/// <summary>
/// 运维面:概览、慢日志、客户端、订阅、内存抽样。
/// <para>
/// 全部按**能力探测 + 空状态降级**处理:托管 Redis 普遍禁掉
/// <c>CONFIG</c>/<c>CLIENT</c>/<c>SLOWLOG</c>/<c>MEMORY</c>,当成错误会让概览页一打开就一片红。
/// 拿不到就如实说"该服务器未开放 X",而不是报失败。
/// </para>
/// </summary>
internal sealed partial class RedisConnection
{
    private ISubscriber? _subscriber;

    /// <summary>读 <c>INFO</c> 全段并整理成分组指标。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分组指标。</returns>
    public async Task<IReadOnlyList<RedisMetricGroup>> ReadOverviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 老服务器(3.x)**不报错**地把 `INFO everything` 当成一个未知段名,回一份空内容 ——
        // 所以判据必须是"到底解出了几个字段",而不是"结果是不是 null"。
        // 按结果为空回落,是这类"合法但无用的应答"唯一可靠的处理方式。
        Dictionary<string, string> fields = await ReadInfoFieldsAsync("everything").ConfigureAwait(false);
        if (fields.Count == 0)
        {
            fields = await ReadInfoFieldsAsync(section: null).ConfigureAwait(false);
        }
        if (fields.Count == 0)
        {
            return [new("INFO", [], Unavailable: true)];
        }
        return
        [
            new("server",
            [
                new("version", Field(fields, "redis_version", "valkey_version")),
                new("mode", Field(fields, "redis_mode")),
                new("uptime", DescribeSeconds(Field(fields, "uptime_in_seconds"))),
                new("os", Field(fields, "os")),
                new("process id", Field(fields, "process_id"))
            ]),
            new("memory",
            [
                new("used", Field(fields, "used_memory_human")),
                new("rss", Field(fields, "used_memory_rss_human")),
                new("peak", Field(fields, "used_memory_peak_human")),
                new("fragmentation", Field(fields, "mem_fragmentation_ratio")),
                new("maxmemory", Field(fields, "maxmemory_human")),
                new("policy", Field(fields, "maxmemory_policy"))
            ]),
            new("stats",
            [
                new("ops/sec", Field(fields, "instantaneous_ops_per_sec")),
                new("hit rate", HitRate(fields)),
                new("total commands", Field(fields, "total_commands_processed")),
                new("connections", Field(fields, "connected_clients")),
                new("blocked", Field(fields, "blocked_clients")),
                new("evicted keys", Field(fields, "evicted_keys")),
                new("expired keys", Field(fields, "expired_keys")),
                new("rejected connections", Field(fields, "rejected_connections"))
            ]),
            new("persistence",
            [
                new("last save", DescribeUnix(Field(fields, "rdb_last_save_time"))),
                new("changes since save", Field(fields, "rdb_changes_since_last_save")),
                new("last bgsave", Field(fields, "rdb_last_bgsave_status")),
                new("aof enabled", Field(fields, "aof_enabled")),
                new("last aof write", Field(fields, "aof_last_write_status")),
                new("loading", Field(fields, "loading"))
            ]),
            new("replication",
            [
                new("role", Field(fields, "role")),
                new("connected replicas", Field(fields, "connected_slaves")),
                new("master link", Field(fields, "master_link_status")),
                new("master offset", Field(fields, "master_repl_offset")),
                new("replica lag", Field(fields, "slave_repl_offset"))
            ])
        ];
    }

    /// <summary>读慢日志。</summary>
    /// <param name="count">最多取几条。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目;服务器未开放 <c>SLOWLOG</c> 时为 null。</returns>
    public async Task<IReadOnlyList<RedisSlowlogEntry>?> ReadSlowlogAsync(
        int count = 128,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisResult raw = await TryExecuteAsync(Db(), "SLOWLOG", "GET", count).ConfigureAwait(false);
        if (raw.IsNull || raw.Resp2Type != ResultType.Array)
        {
            return null;
        }
        var entries = new List<RedisSlowlogEntry>();
        foreach (RedisResult item in (RedisResult[])raw!)
        {
            if (item.Resp2Type != ResultType.Array)
            {
                continue;
            }
            RedisResult[] parts;
            try
            {
                parts = (RedisResult[])item!;
            }
            catch (InvalidCastException)
            {
                continue;
            }
            // 老服务器(3.x)只有前四个字段;6.x 起多了客户端地址与名字。
            if (parts.Length < 4)
            {
                continue;
            }
            long id = (long?)parts[0] ?? 0;
            long unix = (long?)parts[1] ?? 0;
            long micros = (long?)parts[2] ?? 0;
            string command = JoinArgs(parts[3]);
            string client = parts.Length > 4 ? (string?)parts[4] ?? string.Empty : string.Empty;
            entries.Add(new(
                id,
                DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime(),
                TimeSpan.FromTicks(micros * (TimeSpan.TicksPerMillisecond / 1000)),
                command,
                client));
        }
        return entries;
    }

    /// <summary>清空慢日志。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task ResetSlowlogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Db().ExecuteAsync("SLOWLOG", ["RESET"]).ConfigureAwait(false);
    }

    /// <summary>读 <c>CLIENT LIST</c>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目;服务器未开放 <c>CLIENT</c> 时为 null。</returns>
    public async Task<IReadOnlyList<RedisClientEntry>?> ReadClientsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisResult raw = await TryExecuteAsync(Db(), "CLIENT", "LIST").ConfigureAwait(false);
        string text = AsString(raw);
        if (text.Length == 0)
        {
            return null;
        }
        var entries = new List<RedisClientEntry>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    fields[pair[..equals]] = pair[(equals + 1)..];
                }
            }
            if (fields.Count == 0)
            {
                continue;
            }
            string name = fields.GetValueOrDefault("name", string.Empty);
            entries.Add(new(
                ParseLong(fields.GetValueOrDefault("id")),
                fields.GetValueOrDefault("addr", string.Empty),
                name,
                TimeSpan.FromSeconds(ParseLong(fields.GetValueOrDefault("age"))),
                TimeSpan.FromSeconds(ParseLong(fields.GetValueOrDefault("idle"))),
                (int)ParseLong(fields.GetValueOrDefault("db")),
                fields.GetValueOrDefault("cmd", string.Empty),
                // 靠客户端名认自己:多路复用下我们有两条连接(交互 + 订阅),
                // 认名字才能把它们都标出来 —— 一个客户端把自己 kill 掉然后报"连接丢失",
                // 是很蠢但很常见的 bug。
                string.Equals(name, _settings.ClientName, StringComparison.Ordinal)));
        }
        return entries;
    }

    /// <summary>断开一条客户端连接。</summary>
    /// <param name="clientId">连接 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task KillClientAsync(long clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Db().ExecuteAsync("CLIENT", ["KILL", "ID", clientId]).ConfigureAwait(false);
    }

    /// <summary>
    /// 订阅一个频道或模式。
    /// <para>
    /// 走库的 <c>ISubscriber</c> —— 它内部自带一条专用的订阅连接,所以涌进来的消息
    /// **不会干扰浏览**。这是多路复用模型在这一处恰好帮上忙的地方。
    /// </para>
    /// </summary>
    /// <param name="channel">频道名或模式(含 <c>*</c> 即按模式订阅)。</param>
    /// <param name="onMessage">收到消息时的回调(频道, 载荷);在库的线程上触发。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task SubscribeAsync(
        string channel,
        Action<string, string> onMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        cancellationToken.ThrowIfCancellationRequested();
        _subscriber ??= _mux.GetSubscriber();
        RedisChannel target = channel.Contains('*', StringComparison.Ordinal)
            ? RedisChannel.Pattern(channel)
            : RedisChannel.Literal(channel);
        await _subscriber.SubscribeAsync(target, (actual, message) =>
            onMessage(actual.ToString() ?? channel, message.ToString() ?? string.Empty))
            .ConfigureAwait(false);
    }

    /// <summary>退订。</summary>
    /// <param name="channel">频道名或模式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscriber is null)
        {
            return;
        }
        RedisChannel target = channel.Contains('*', StringComparison.Ordinal)
            ? RedisChannel.Pattern(channel)
            : RedisChannel.Literal(channel);
        await _subscriber.UnsubscribeAsync(target).ConfigureAwait(false);
    }

    /// <summary>
    /// 抽样估计内存占用,按前缀聚合。
    /// <para>
    /// **这是抽样结论,不是全量审计** —— 界面必须把这句话写在页面上而不是藏进文档里。
    /// 做法:<c>SCAN</c> 取若干页键,每页用流水线 <c>MEMORY USAGE</c> 取占用(一批一个往返),
    /// 按键名的首段聚合。填的是 <c>redis-cli --bigkeys</c> 的坑:那个要么阻塞、要么只给类型级汇总。
    /// </para>
    /// </summary>
    /// <param name="sampleLimit">最多抽多少个键。</param>
    /// <param name="progress">进度回调(已抽样键数)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>抽样结果。</returns>
    public async Task<RedisMemorySample> SampleMemoryAsync(
        int sampleLimit,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long estimatedTotal = await DatabaseSizeAsync().ConfigureAwait(false);
        var byPrefix = new Dictionary<string, (long Keys, long Bytes)>(StringComparer.Ordinal);
        var topKeys = new List<RedisMemoryBucket>();
        long sampled = 0;
        string cursor = "0";
        bool available = true;
        IDatabase db = Db();

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisScanPage page = await ScanAsync(cursor, "*", type: null, cancellationToken).ConfigureAwait(false);
            cursor = page.Cursor;
            if (page.Keys.Count == 0)
            {
                continue;
            }
            // 一批一个往返:逐键单发是那个会把"分析一下内存"变成"喝杯咖啡"的写法。
            var pending = new Task<RedisResult>[page.Keys.Count];
            for (int i = 0; i < page.Keys.Count; i++)
            {
                pending[i] = db.ExecuteAsync("MEMORY", ["USAGE", page.Keys[i].ToRedisKey()]);
            }
            for (int i = 0; i < pending.Length; i++)
            {
                long bytes;
                try
                {
                    RedisResult result = await pending[i].ConfigureAwait(false);
                    bytes = result.IsNull ? 0 : (long?)result ?? 0;
                }
                catch (Exception ex) when (IsDeniedOrUnsupported(ex))
                {
                    // MEMORY USAGE 是 4.0 才有的:整条路不可用,如实上报而不是给一堆 0。
                    available = false;
                    return new([], [], sampled, estimatedTotal, Available: false);
                }
                RedisKeyName key = page.Keys[i];
                string prefix = FirstSegment(key.Text);
                (long Keys, long Bytes) = byPrefix.GetValueOrDefault(prefix);
                byPrefix[prefix] = (Keys + 1, Bytes + bytes);
                topKeys.Add(new(key.Text, 1, bytes));
                sampled++;
            }
            progress?.Invoke(sampled);
        }
        while (cursor is not "0" && sampled < sampleLimit);

        return new(
            [.. byPrefix.Select(entry => new RedisMemoryBucket(entry.Key, entry.Value.Keys, entry.Value.Bytes))
                .OrderByDescending(bucket => bucket.Bytes)
                .Take(50)],
            [.. topKeys.OrderByDescending(bucket => bucket.Bytes).Take(50)],
            sampled,
            estimatedTotal,
            available);
    }

    /// <summary>读一段 <c>INFO</c> 并解析成字段表;段名为 null 即读默认段集。</summary>
    private async Task<Dictionary<string, string>> ReadInfoFieldsAsync(string? section)
    {
        RedisResult raw = section is { Length: > 0 }
            ? await TryExecuteAsync(Db(), "INFO", section).ConfigureAwait(false)
            : await TryExecuteAsync(Db(), "INFO").ConfigureAwait(false);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in ParseInfo(AsString(raw)))
        {
            fields[name] = value;
        }
        return fields;
    }

    private string FirstSegment(string keyText)
    {
        string delimiter = _settings.Delimiter;
        if (delimiter.Length == 0)
        {
            return keyText;
        }
        int index = keyText.IndexOf(delimiter, StringComparison.Ordinal);
        return index > 0 ? keyText[..index] : keyText;
    }

    private static string Field(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        foreach (string name in names)
        {
            if (fields.TryGetValue(name, out string? value) && value.Length > 0)
            {
                return value;
            }
        }
        // 拿不到就留空 —— **不能填 0**:0 会被读成一个真实的测量值。
        return string.Empty;
    }

    private static string HitRate(IReadOnlyDictionary<string, string> fields)
    {
        long hits = ParseLong(fields.GetValueOrDefault("keyspace_hits"));
        long misses = ParseLong(fields.GetValueOrDefault("keyspace_misses"));
        long total = hits + misses;
        return total <= 0
            ? string.Empty
            : ((double)hits / total).ToString("P1", CultureInfo.CurrentCulture);
    }

    private static string DescribeSeconds(string raw)
    {
        long seconds = ParseLong(raw);
        return seconds <= 0 ? string.Empty : RedisTtl.Describe(TimeSpan.FromSeconds(seconds));
    }

    private static string DescribeUnix(string raw)
    {
        long unix = ParseLong(raw);
        return unix <= 0
            ? string.Empty
            : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private static long ParseLong(string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    /// <summary>把慢日志里的参数数组拼成一行(过长截断,免得一条 MSET 撑爆整个表格)。</summary>
    private static string JoinArgs(RedisResult args)
    {
        if (args.Resp2Type != ResultType.Array)
        {
            return (string?)args ?? string.Empty;
        }
        RedisValue[] parts;
        try
        {
            parts = (RedisValue[])args!;
        }
        catch (InvalidCastException)
        {
            return string.Empty;
        }
        string joined = string.Join(' ', parts.Select(static part => (string?)part ?? string.Empty));
        return joined.Length <= 300 ? joined : joined[..300] + "…";
    }
}

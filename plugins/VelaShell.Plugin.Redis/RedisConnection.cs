using System.Diagnostics;
using System.Globalization;
using System.Net;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 一条 Redis 连接。对 StackExchange.Redis 的 <see cref="ConnectionMultiplexer" /> 做了一层
/// **面向图形客户端**的收敛:游标式 <c>SCAN</c>(库自带的 <c>Keys()</c> 会把游标藏起来,
/// 而"扫到哪儿了"正是界面必须如实报出的东西)、按类型分页取值、以及能力探测式的降级。
/// <para>
/// 已知边界(由库的多路复用模型决定,见 <c>docs/Redis客户端插件化调研与设计.md</c> §四):
/// <c>MONITOR</c> 与阻塞类命令(<c>BLPOP</c> 等)在多路复用连接上不可用。控制台遇到这两类
/// 命令时如实拒绝并说明原因,不假装能跑。
/// </para>
/// </summary>
internal sealed partial class RedisConnection : IAsyncDisposable
{
    private readonly RedisSettings _settings;
    private readonly ConnectionMultiplexer _mux;
    private int _database;

    /// <summary>
    /// 服务器是否支持 <c>SCAN … TYPE</c>(Redis 6.0+)。null = 还没试过。
    /// <para>
    /// 能力探测优于版本判断:分叉与托管实例的版本号并不可信,而一次语法错误就是确定的答案。
    /// 探测结果决定类型过滤在**服务端**做还是退回客户端做 —— 见 <see cref="ScanAsync" />。
    /// </para>
    /// </summary>
    private bool? _scanTypeSupported;

    private RedisConnection(ConnectionMultiplexer mux, RedisSettings settings, RedisServerInfo info)
    {
        _mux = mux;
        _settings = settings;
        Info = info;
        _database = settings.Database;
        _mux.ConnectionFailed += OnConnectionFailed;
        _mux.ConnectionRestored += OnConnectionRestored;
    }

    /// <summary>连接可用性变化(true = 恢复,false = 断开)。可能在任意线程触发。</summary>
    public event Action<bool>? Availability;

    /// <summary>服务器概况(连接时探测一次)。</summary>
    public RedisServerInfo Info { get; private set; }

    /// <summary>当前数据库(集群下恒为 0)。</summary>
    public int Database => _database;

    /// <summary>连接设置。</summary>
    public RedisSettings Settings => _settings;

    /// <summary>库当前认为连接是通的。</summary>
    public bool IsConnected => _mux.IsConnected;

    /// <summary>
    /// 建立连接并探测服务器概况。
    /// <para>
    /// <c>AbortOnConnectFail = true</c> 是刻意的:桌面客户端的第一次连接**必须**大声失败,
    /// 用户要看到"连不上"的真实原因。设 false 会让 <c>ConnectAsync</c> 成功返回一个
    /// 后台重试中的对象,界面于是画出一个空的键树,然后每个操作各自超时 ——
    /// 那是最难排查的一种坏。
    /// </para>
    /// </summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="user">ACL 用户(空 = default)。</param>
    /// <param name="password">口令(空 = 无认证)。</param>
    /// <param name="settings">连接设置。</param>
    /// <param name="trust">TLS 校验记录器(仅 TLS 时用);null 表示走系统默认校验。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接的连接对象。</returns>
    public static async Task<RedisConnection> ConnectAsync(
        string host,
        int port,
        string user,
        string password,
        RedisSettings settings,
        RedisTlsTrust? trust = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new ConfigurationOptions
        {
            ClientName = settings.ClientName,
            ConnectTimeout = settings.ConnectTimeoutMs,
            SyncTimeout = Math.Max(settings.ConnectTimeoutMs, 5000),
            Ssl = settings.UseTls,
            AbortOnConnectFail = true,
            // INFO / CONFIG / DBSIZE 都被库标为 admin 命令 —— 一个数据库客户端的概览页
            // 正是靠它们撑起来的,不放开就只能显示一片空白。危险命令由插件自己的护栏拦
            // (依据是 COMMAND INFO 的 flags,而不是库的这个总闸)。
            AllowAdmin = true,
            // 显式要 RESP3:map/set/double 等类型在回复里能自证,控制台才渲染得准。
            // 服务器低于 6.0 时库自动回落 RESP2,连接时探测到的真实协议记在 Info 里。
            Protocol = RedisProtocol.Resp3
        };
        options.EndPoints.Add(host, port);
        if (!string.IsNullOrEmpty(user))
        {
            options.User = user;
        }
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }
        if (settings.UseTls)
        {
            // SNI 用用户填的主机名:走 SSH 隧道时连的是 127.0.0.1,证书上却是真实域名。
            options.SslHost = host;
            if (trust is not null)
            {
                // 自签端点:校验失败时只记录不阻塞(理由见 RedisTlsTrust),
                // 由提供方把记录翻成宿主的证书信任提示。
                options.CertificateValidation += trust.Validate;
            }
        }
        if (settings.Deployment == RedisDeployment.Sentinel && !string.IsNullOrWhiteSpace(settings.MasterName))
        {
            // 哨兵:库据此向哨兵问主地址,并在故障切换后重新解析。
            options.ServiceName = settings.MasterName;
        }
        if (settings.SupportsDatabases && settings.Database > 0)
        {
            options.DefaultDatabase = settings.Database;
        }

        ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RedisServerInfo info;
        try
        {
            info = await ProbeAsync(mux, settings).ConfigureAwait(false);
        }
        catch
        {
            await mux.CloseAsync().ConfigureAwait(false);
            mux.Dispose();
            throw;
        }
        var connection = new RedisConnection(mux, settings, info);
        // 护栏的两个开关来自用户配置;分级依据随后由 COMMAND INFO 覆盖内置兜底表。
        connection.Guard.ReadOnly = settings.ReadOnly;
        connection.Guard.LockDestructive = settings.Environment == RedisEnvironment.Production;
        try
        {
            await connection.LoadCommandMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 拿不到命令元数据不该让连接失败:闸门退回内置兜底表(宁可多问一次),
            // 补全退回"只有已知的那些"。这与"没配过不是错误"是同一条准则。
            Trace.WriteLine($"[Redis] Command metadata unavailable: {ex.Message}");
        }
        return connection;
    }

    /// <summary>切换数据库。集群下无此概念,调用被忽略。</summary>
    /// <param name="database">目标库。</param>
    public void SelectDatabase(int database)
    {
        if (_settings.SupportsDatabases)
        {
            _database = Math.Max(0, database);
        }
    }

    /// <summary>重新读一次 <c>INFO keyspace</c>,刷新逐库键数。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task RefreshKeyspaceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<int, long> counts = await ReadKeyspaceAsync(Db()).ConfigureAwait(false);
        Info = Info with { KeyCountByDatabase = counts };
    }

    /// <summary>探活往返延迟。</summary>
    /// <returns>往返时间。</returns>
    public Task<TimeSpan> PingAsync() => Db().PingAsync();

    /// <summary>当前库的键总数(<c>DBSIZE</c>);取不到时返回 -1。</summary>
    /// <returns>键总数,或 -1。</returns>
    public async Task<long> DatabaseSizeAsync()
    {
        long total = 0;
        bool any = false;
        foreach (IServer server in PrimaryServers())
        {
            try
            {
                total += await server.DatabaseSizeAsync(_database).ConfigureAwait(false);
                any = true;
            }
            catch (Exception ex) when (IsDeniedOrUnsupported(ex))
            {
                // 托管实例常禁掉 DBSIZE:那是空状态而不是错误,进度条改为只报"已扫描多少"。
                Trace.WriteLine($"[Redis] DBSIZE unavailable: {ex.Message}");
            }
        }
        return any ? total : -1;
    }

    /// <summary>
    /// 扫一页键。
    /// <para>
    /// 游标是**调用方持有**的字符串:集群模式下形如 <c>&lt;节点序号&gt;|&lt;游标&gt;</c>,
    /// 逐节点扫完再进下一个 —— 集群的 <c>SCAN</c> 是节点局部的,没有全局游标这回事。
    /// 只有最后一个节点的游标也归零时才返回 <c>"0"</c>,因此"扫完了"这句话在两种形态下
    /// 都只在真的扫完时才成立。
    /// </para>
    /// </summary>
    /// <param name="cursor">上一轮返回的游标;首轮传 <c>"0"</c>。</param>
    /// <param name="match">
    /// <c>MATCH</c> 模式(通配)。**服务端过滤**:不能在客户端拉全量再筛,
    /// 那等于把 KEYS 的代价挪到网络上。
    /// </param>
    /// <param name="type">类型过滤(<c>SCAN TYPE</c>,Redis 6.0+);null 表示不过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>一页结果。</returns>
    public async Task<RedisScanPage> ScanAsync(
        string cursor,
        string match,
        string? type,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings.Deployment == RedisDeployment.Cluster)
        {
            RedisScanPage clustered = await ScanClusterAsync(cursor, match, type, cancellationToken).ConfigureAwait(false);
            return await NarrowByTypeAsync(clustered, type, cancellationToken).ConfigureAwait(false);
        }
        (string next, List<RedisKeyName> keys) = await ScanOnceAsync(
            Db(), cursor is { Length: > 0 } ? cursor : "0", match, type).ConfigureAwait(false);
        return await NarrowByTypeAsync(new(next, keys, _settings.ScanCount), type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 服务器不支持 <c>SCAN TYPE</c> 时,在客户端把类型过滤补上。
    /// <para>
    /// **这一步不能省。** 退回不带 <c>TYPE</c> 的扫描后如果就这么把结果交出去,
    /// 界面会一边显示"类型:hash"一边列出所有类型的键 —— 那是最坏的一种坏:
    /// 界面在说谎,而用户没有任何线索。代价是老服务器上每页多一个往返(批量 <c>TYPE</c>),
    /// 换来的是"要 hash 就只给 hash"这句话在任何版本上都成立。
    /// </para>
    /// </summary>
    private async Task<RedisScanPage> NarrowByTypeAsync(
        RedisScanPage page,
        string? type,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(type) || _scanTypeSupported != false || page.Keys.Count == 0)
        {
            return page;
        }
        IReadOnlyList<string> types = await TypesAsync(page.Keys, cancellationToken).ConfigureAwait(false);
        var kept = new List<RedisKeyName>(page.Keys.Count);
        for (int i = 0; i < page.Keys.Count && i < types.Count; i++)
        {
            if (string.Equals(types[i], type, StringComparison.Ordinal))
            {
                kept.Add(page.Keys[i]);
            }
        }
        return page with { Keys = kept };
    }

    /// <summary>
    /// 批量取一页键的 TTL 与规模(键列表的后两列)。
    /// <para>
    /// 与 <see cref="TypesAsync" /> 同一条纪律:<b>N 条命令、一次往返</b>。这里发两组 ——
    /// 一组 <c>PTTL</c>,一组按类型选的长度命令(<c>STRLEN</c>/<c>HLEN</c>/…),
    /// 全部由库流水线打包。所以一页键的完整元数据一共三次往返(类型、TTL+长度),
    /// 与页大小无关。
    /// </para>
    /// <para>
    /// 个别键在这一瞬过期是常态,不是故障:失败的位置回落成"未知"(长度 -1),
    /// 不让一整页的元数据陪葬。
    /// </para>
    /// </summary>
    /// <param name="keys">键。</param>
    /// <param name="types">与 <paramref name="keys" /> 同序的类型名(决定发哪条长度命令)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与入参同序的度量。</returns>
    public async Task<IReadOnlyList<RedisKeyMeasure>> MeasureAsync(
        IReadOnlyList<RedisKeyName> keys,
        IReadOnlyList<string> types,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(types);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return [];
        }
        IDatabase db = Db();
        var ttls = new Task<TimeSpan?>[keys.Count];
        var lengths = new Task<long>[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var redisKey = keys[i].ToRedisKey();
            ttls[i] = db.KeyTimeToLiveAsync(redisKey);
            lengths[i] = LengthAsync(db, redisKey, ParseType(i < types.Count ? types[i] : string.Empty));
        }
        try
        {
            await Task.WhenAll(ttls).ConfigureAwait(false);
            await Task.WhenAll(lengths).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex))
        {
            Trace.WriteLine($"[Redis] Batch measure partially failed: {ex.Message}");
        }
        var measures = new RedisKeyMeasure[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            measures[i] = new(
                ttls[i].IsCompletedSuccessfully ? ttls[i].Result : null,
                lengths[i].IsCompletedSuccessfully ? lengths[i].Result : -1);
        }
        return measures;
    }

    /// <summary>类型名转回库的枚举(<see cref="TypeName" /> 的逆);认不出的一律当未知。</summary>
    private static RedisType ParseType(string name) => name switch
    {
        "string" => RedisType.String,
        "list" => RedisType.List,
        "set" => RedisType.Set,
        "zset" => RedisType.SortedSet,
        "hash" => RedisType.Hash,
        "stream" => RedisType.Stream,
        _ => RedisType.Unknown
    };

    /// <summary>
    /// 批量取一页键的类型。
    /// <para>
    /// 一批一个往返:库会把这些 <c>TYPE</c> 流水线打包发出去,所以这是 <b>N 条命令、
    /// 一次往返</b>,而不是 N 次往返。逐键单发才是那个会把浏览器拖慢十倍的写法。
    /// 服务器支持 <c>SCAN TYPE</c>(6.0+)且用户开了类型过滤时,调用方根本不必来这一趟。
    /// </para>
    /// </summary>
    /// <param name="keys">键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与入参同序的类型名;取不到的位置为空串。</returns>
    public async Task<IReadOnlyList<string>> TypesAsync(
        IReadOnlyList<RedisKeyName> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return [];
        }
        IDatabase db = Db();
        var pending = new Task<RedisType>[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            pending[i] = db.KeyTypeAsync(keys[i].ToRedisKey());
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex))
        {
            // 个别键在这一瞬过期是常态,不该让整页的类型都变成未知。
            Trace.WriteLine($"[Redis] Batch TYPE partially failed: {ex.Message}");
        }
        string[] types = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            types[i] = pending[i].IsCompletedSuccessfully ? TypeName(pending[i].Result) : string.Empty;
        }
        return types;
    }

    /// <summary>取键的元信息(类型 / TTL / 编码 / 长度,可选内存占用)。</summary>
    /// <param name="key">键名。</param>
    /// <param name="includeMemory">是否取 <c>MEMORY USAGE</c>(每个键多一条命令,由界面决定)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>元信息。</returns>
    public async Task<RedisKeyInfo> DescribeAsync(
        RedisKeyName key,
        bool includeMemory = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        var redisKey = key.ToRedisKey();
        // 一次流水线打包:类型、TTL、编码三条命令一个往返。
        Task<RedisType> typeTask = db.KeyTypeAsync(redisKey);
        Task<TimeSpan?> ttlTask = db.KeyTimeToLiveAsync(redisKey);
        Task<RedisResult> encodingTask = TryExecuteAsync(db, "OBJECT", "ENCODING", redisKey);
        await Task.WhenAll(typeTask, ttlTask, encodingTask).ConfigureAwait(false);

        RedisType type = typeTask.Result;
        string typeName = TypeName(type);
        if (type == RedisType.None)
        {
            return new(key, "none", null, string.Empty, -1, -1);
        }
        long length = await LengthAsync(db, redisKey, type).ConfigureAwait(false);
        long memory = -1;
        if (includeMemory)
        {
            RedisResult usage = await TryExecuteAsync(db, "MEMORY", "USAGE", redisKey).ConfigureAwait(false);
            memory = usage.IsNull ? -1 : (long?)usage ?? -1;
        }
        return new(key, typeName, ttlTask.Result, AsString(encodingTask.Result), length, memory);
    }

    /// <summary>读字符串值(超过上限只取前 N 字节,并如实报出完整长度)。</summary>
    /// <param name="key">键名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>值与完整长度。</returns>
    public async Task<RedisStringValue> ReadStringAsync(RedisKeyName key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        var redisKey = key.ToRedisKey();
        long total = await db.StringLengthAsync(redisKey).ConfigureAwait(false);
        int limit = _settings.ValuePreviewBytes;
        if (total <= limit)
        {
            RedisValue whole = await db.StringGetAsync(redisKey).ConfigureAwait(false);
            return new((byte[]?)whole ?? [], total);
        }
        // GETRANGE 的区间是闭的,所以上界是 limit-1。整取一个 4MB 的值只为显示前 256KB
        // 是纯粹的浪费 —— 而且那份内存还要在界面里再留一遍。
        RedisValue slice = await db.StringGetRangeAsync(redisKey, 0, limit - 1).ConfigureAwait(false);
        return new((byte[]?)slice ?? [], total);
    }

    /// <summary>
    /// 读集合类值的一页。哈希 / 集合 / 有序集合走各自的 <c>*SCAN</c> 游标,
    /// 列表按索引窗口(<c>LRANGE</c>)—— 列表没有 SCAN,索引本身就是它的游标。
    /// </summary>
    /// <param name="key">键名。</param>
    /// <param name="type">类型名。</param>
    /// <param name="cursor">上一轮游标;首轮传 <c>"0"</c>。</param>
    /// <param name="pageSize">每页行数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>一页行。</returns>
    public async Task<RedisElementPage> ReadElementsAsync(
        RedisKeyName key,
        string type,
        string cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        var redisKey = key.ToRedisKey();
        string start = string.IsNullOrEmpty(cursor) ? "0" : cursor;
        return type switch
        {
            "hash" => await ScanPairsAsync(db, "HSCAN", redisKey, start, pageSize, hasValue: true).ConfigureAwait(false),
            "set" => await ScanPairsAsync(db, "SSCAN", redisKey, start, pageSize, hasValue: false).ConfigureAwait(false),
            "zset" => await ScanPairsAsync(db, "ZSCAN", redisKey, start, pageSize, hasValue: true, isScore: true).ConfigureAwait(false),
            "list" => await ReadListWindowAsync(db, redisKey, start, pageSize).ConfigureAwait(false),
            "stream" => await ReadStreamWindowAsync(db, redisKey, start, pageSize).ConfigureAwait(false),
            _ => new([], "0", -1)
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _mux.ConnectionFailed -= OnConnectionFailed;
        _mux.ConnectionRestored -= OnConnectionRestored;
        try
        {
            await _mux.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 关闭路径不许抛:标签页已经没了,再冒异常只会变成没人处理的 UnobservedTaskException。
            Trace.WriteLine($"[Redis] Close failed: {ex.Message}");
        }
        _mux.Dispose();
    }

    private IDatabase Db() => _mux.GetDatabase(_settings.SupportsDatabases ? _database : 0);

    private IEnumerable<IServer> PrimaryServers()
    {
        foreach (EndPoint endpoint in _mux.GetEndPoints())
        {
            IServer server = _mux.GetServer(endpoint);
            if (server.IsConnected && !server.IsReplica)
            {
                yield return server;
            }
        }
    }

    private async Task<RedisScanPage> ScanClusterAsync(
        string cursor,
        string match,
        string? type,
        CancellationToken cancellationToken)
    {
        List<IServer> servers = [.. PrimaryServers()];
        if (servers.Count == 0)
        {
            return new("0", [], 0);
        }
        (int node, string nodeCursor) = ParseClusterCursor(cursor);
        var keys = new List<RedisKeyName>();
        while (node < servers.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string next, List<RedisKeyName> page) = await ScanNodeAsync(
                servers[node], nodeCursor, match, cancellationToken).ConfigureAwait(false);
            keys.AddRange(page);
            if (next is not "0")
            {
                return new(FormatClusterCursor(node, next), keys, _settings.ScanCount);
            }
            // 这个节点扫完了 → 从下一个节点的游标 0 接着来。
            node++;
            nodeCursor = "0";
            if (keys.Count > 0)
            {
                break;
            }
            // 这一轮什么都没拿到:继续往下一个节点走,免得界面上出现连续多次"点了没反应"。
        }
        return node >= servers.Count
            ? new("0", keys, _settings.ScanCount)
            : new(FormatClusterCursor(node, "0"), keys, _settings.ScanCount);
    }

    /// <summary>
    /// 在**指定节点**上扫一页(集群模式用)。
    /// <para>
    /// 这里不能走 <c>IServer.ExecuteAsync("SCAN", …)</c> —— 那条路不带库号,服务器/库会直接回
    /// <c>A target database is required for SCAN</c>(真机上就是这么炸的:键树一片空白,
    /// 状态条里一句红字)。<c>IServer.KeysAsync</c> 是库为"在这个节点上 SCAN"提供的正路:
    /// 它显式接收 database 与 cursor,枚举器本身实现 <c>IScanningCursor</c>,游标照样拿得到。
    /// </para>
    /// <para>
    /// 代价:这条路**没有** <c>TYPE</c> 选项。所以集群模式一律把类型过滤降级到客户端
    /// (<see cref="NarrowByTypeAsync" />),口径与老服务器上的回落完全一致。
    /// </para>
    /// </summary>
    private async Task<(string Cursor, List<RedisKeyName> Keys)> ScanNodeAsync(
        IServer server,
        string cursor,
        string match,
        CancellationToken cancellationToken)
    {
        // 集群只有 db0;仍显式传,免得库回到"没有目标库"那条错误路径上。
        int database = _settings.SupportsDatabases ? _database : 0;
        long start = long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
            ? parsed
            : 0;
        // 集群路径拿不到 SCAN TYPE,把类型过滤交给客户端收窄。
        _scanTypeSupported = false;

        var keys = new List<RedisKeyName>();
        long next = 0;
        IAsyncEnumerable<RedisKey> source = server.KeysAsync(
            database,
            string.IsNullOrEmpty(match) ? "*" : match,
            pageSize: _settings.ScanCount,
            cursor: start);
        await using IAsyncEnumerator<RedisKey> enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (keys.Count < _settings.ScanCount && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            if ((byte[]?)enumerator.Current is { } raw)
            {
                keys.Add(new(raw));
            }
            // 游标要在每次推进后读:枚举器换页时它才更新,而我们要的是"下一次从哪儿接着扫"。
            next = (enumerator as IScanningCursor)?.Cursor ?? 0;
        }
        // 枚举提前结束 = 这个节点扫完了。凑满一页时按当前游标续扫 —— 可能与上一页有重叠,
        // 而 SCAN 本身在 rehash 期间也会返回重复键,调用方按键名去重,两者是同一条兜底。
        bool exhausted = keys.Count < _settings.ScanCount;
        return (exhausted ? "0" : next.ToString(CultureInfo.InvariantCulture), keys);
    }

    /// <summary>
    /// 在当前库上扫一页(单机 / 哨兵)。走 <c>IDatabase.ExecuteAsync</c> ——
    /// 它知道自己是哪个库,因此 <c>SCAN</c> 与 <c>SCAN TYPE</c> 都能原样发出去。
    /// 集群的按节点扫描见 <see cref="ScanNodeAsync" />。
    /// </summary>
    private async Task<(string Cursor, List<RedisKeyName> Keys)> ScanOnceAsync(
        IDatabase db,
        string cursor,
        string match,
        string? type)
    {
        var args = new List<object> { cursor };
        if (!string.IsNullOrEmpty(match))
        {
            args.Add("MATCH");
            args.Add(match);
        }
        args.Add("COUNT");
        args.Add(_settings.ScanCount);
        if (!string.IsNullOrEmpty(type))
        {
            args.Add("TYPE");
            args.Add(type);
        }

        RedisResult result;
        try
        {
            result = await db.ExecuteAsync("SCAN", args).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (!string.IsNullOrEmpty(type) && IsSyntaxError(ex))
        {
            // SCAN TYPE 是 Redis 6.0 才有的。老服务器回一句语法错误 —— 那不该让浏览器瘫掉:
            // 记下"不支持"(能力探测,一次即够),退回不带 TYPE 的扫描,
            // 类型过滤随后由 NarrowByTypeAsync 在客户端补上。
            _scanTypeSupported = false;
            return await ScanOnceAsync(db, cursor, match, type: null).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(type))
        {
            // 带 TYPE 的这一次没有报语法错误 → 服务端支持,客户端不必再补一遍过滤。
            _scanTypeSupported = true;
        }
        var keys = new List<RedisKeyName>();
        if (result.IsNull || result.Resp2Type != ResultType.Array)
        {
            return ("0", keys);
        }
        var parts = (RedisResult[])result!;
        if (parts.Length < 2)
        {
            return ("0", keys);
        }
        string next = (string?)parts[0] ?? "0";
        foreach (RedisValue value in (RedisValue[])parts[1]!)
        {
            if ((byte[]?)value is { } raw)
            {
                keys.Add(new(raw));
            }
        }
        return (next, keys);
    }

    private async Task<RedisElementPage> ScanPairsAsync(
        IDatabase db,
        string command,
        RedisKey key,
        string cursor,
        int pageSize,
        bool hasValue,
        bool isScore = false)
    {
        RedisResult result = await db.ExecuteAsync(command, [key, cursor, "COUNT", pageSize]).ConfigureAwait(false);
        var rows = new List<RedisElement>();
        string next = "0";
        if (!result.IsNull && result.Resp2Type == ResultType.Array)
        {
            var parts = (RedisResult[])result!;
            if (parts.Length >= 2)
            {
                next = (string?)parts[0] ?? "0";
                var flat = (RedisValue[])parts[1]!;
                int step = hasValue ? 2 : 1;
                for (int i = 0; i + step - 1 < flat.Length; i += step)
                {
                    string label = Display(flat[i]);
                    if (!hasValue)
                    {
                        rows.Add(new(label, string.Empty));
                        continue;
                    }
                    string raw = Display(flat[i + 1]);
                    rows.Add(isScore && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double score)
                        ? new(label, raw, score)
                        : new(label, raw));
                }
            }
        }
        long total = await LengthAsync(db, key, command switch
        {
            "HSCAN" => RedisType.Hash,
            "SSCAN" => RedisType.Set,
            _ => RedisType.SortedSet
        }).ConfigureAwait(false);
        return new(rows, next, total);
    }

    private static async Task<RedisElementPage> ReadListWindowAsync(IDatabase db, RedisKey key, string cursor, int pageSize)
    {
        // 列表没有 SCAN:索引就是它的游标。这里把"下一个起始索引"塞回 cursor 字段,
        // 调用方因此对两种分页方式无感。
        long start = long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
        long total = await db.ListLengthAsync(key).ConfigureAwait(false);
        RedisValue[] window = await db.ListRangeAsync(key, start, start + pageSize - 1).ConfigureAwait(false);
        var rows = new List<RedisElement>(window.Length);
        for (int i = 0; i < window.Length; i++)
        {
            rows.Add(new((start + i).ToString(CultureInfo.InvariantCulture), Display(window[i])));
        }
        long next = start + window.Length;
        return new(rows, next >= total || window.Length == 0 ? "0" : next.ToString(CultureInfo.InvariantCulture), total);
    }

    private static async Task<RedisElementPage> ReadStreamWindowAsync(IDatabase db, RedisKey key, string cursor, int pageSize)
    {
        string from = cursor is "0" or "" ? "-" : cursor;
        StreamEntry[] entries = await db.StreamRangeAsync(key, from, "+", pageSize).ConfigureAwait(false);
        long total = await db.StreamLengthAsync(key).ConfigureAwait(false);
        var rows = new List<RedisElement>(entries.Length);
        foreach (StreamEntry entry in entries)
        {
            // 条目的字段对摊平成一行 name=value 展示:流的详情页(消费组、pending)是后续里程碑,
            // 这里先让用户看得见内容而不是一片空白。
            string payload = string.Join("  ", entry.Values.Select(pair => $"{Display(pair.Name)}={Display(pair.Value)}"));
            rows.Add(new(entry.Id.ToString() ?? string.Empty, payload));
        }
        // 下一页从最后一条的 id 之后开始:XRANGE 的区间是闭的,用 ( 前缀取开区间。
        string next = entries.Length < pageSize || entries.Length == 0
            ? "0"
            : $"({entries[^1].Id}";
        return new(rows, next, total);
    }

    private static async Task<long> LengthAsync(IDatabase db, RedisKey key, RedisType type)
    {
        try
        {
            return type switch
            {
                RedisType.String => await db.StringLengthAsync(key).ConfigureAwait(false),
                RedisType.List => await db.ListLengthAsync(key).ConfigureAwait(false),
                RedisType.Set => await db.SetLengthAsync(key).ConfigureAwait(false),
                RedisType.SortedSet => await db.SortedSetLengthAsync(key).ConfigureAwait(false),
                RedisType.Hash => await db.HashLengthAsync(key).ConfigureAwait(false),
                RedisType.Stream => await db.StreamLengthAsync(key).ConfigureAwait(false),
                _ => -1
            };
        }
        catch (Exception ex) when (ex is RedisServerException or RedisTimeoutException)
        {
            // 键刚好在这一瞬过期,或类型与判断不符(并发改写):长度未知不是故障。
            return -1;
        }
    }

    private static async Task<RedisServerInfo> ProbeAsync(ConnectionMultiplexer mux, RedisSettings settings)
    {
        IDatabase db = mux.GetDatabase(settings.SupportsDatabases ? settings.Database : 0);
        string version = "";
        string flavor = "redis";
        string mode = settings.Deployment == RedisDeployment.Cluster ? "cluster" : "standalone";
        RedisResult info = await TryExecuteAsync(db, "INFO", "server").ConfigureAwait(false);
        foreach ((string name, string value) in ParseInfo(AsString(info)))
        {
            switch (name)
            {
                case "redis_version": version = value; break;
                case "valkey_version": version = value; flavor = "valkey"; break;
                case "server_name": flavor = value; break;
                case "redis_mode": mode = value; break;
            }
        }

        // 数据库个数:CONFIG 在托管实例上常被禁 —— 那时按 16 画并**标注未确认**,
        // 而不是假装知道。能力探测优于版本判断(见设计文档 §5.3)。
        int databases = 16;
        bool confirmed = false;
        if (settings.SupportsDatabases)
        {
            RedisResult config = await TryExecuteAsync(db, "CONFIG", "GET", "databases").ConfigureAwait(false);
            if (!config.IsNull && config.Resp2Type == ResultType.Array)
            {
                var pairs = (RedisValue[])config!;
                // 显式转成字符串再解析:RedisValue 同时能隐式转 string 与 ReadOnlySpan<byte>,
                // 直接传进 int.TryParse 是二义调用。
                if (pairs.Length >= 2
                    && int.TryParse((string?)pairs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed > 0)
                {
                    databases = parsed;
                    confirmed = true;
                }
            }
        }
        else
        {
            databases = 1;
            confirmed = true;
        }

        return new(
            string.IsNullOrEmpty(version) ? "?" : version,
            flavor,
            mode,
            await DetectProtocolAsync(db).ConfigureAwait(false),
            databases,
            confirmed,
            await ReadKeyspaceAsync(db).ConfigureAwait(false));
    }

    /// <summary>
    /// 探测实际协商到的协议。裸 <c>HELLO</c>(无参)在 Redis 6.0+ 回一份含 <c>proto</c> 的映射;
    /// 更老的服务器直接报未知命令 —— 那本身就是"只有 RESP2"的证据。
    /// </summary>
    private static async Task<string> DetectProtocolAsync(IDatabase db)
    {
        RedisResult hello = await TryExecuteAsync(db, "HELLO").ConfigureAwait(false);
        if (hello.IsNull)
        {
            return "RESP2";
        }
        try
        {
            var parts = (RedisResult[])hello!;
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                if (string.Equals((string?)parts[i], "proto", StringComparison.Ordinal))
                {
                    return (long?)parts[i + 1] == 3 ? "RESP3" : "RESP2";
                }
            }
        }
        catch (InvalidCastException)
        {
            // RESP3 的 map 回复在库里不是数组形状。走到这儿说明确实握上了 RESP3。
            return "RESP3";
        }
        return "RESP2";
    }

    private static async Task<Dictionary<int, long>> ReadKeyspaceAsync(IDatabase db)
    {
        var counts = new Dictionary<int, long>();
        RedisResult info = await TryExecuteAsync(db, "INFO", "keyspace").ConfigureAwait(false);
        foreach ((string name, string value) in ParseInfo(AsString(info)))
        {
            // 形如 db0:keys=1200,expires=3,avg_ttl=0
            if (!name.StartsWith("db", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(2), out int index))
            {
                continue;
            }
            foreach (string part in value.Split(','))
            {
                if (part.StartsWith("keys=", StringComparison.Ordinal)
                    && long.TryParse(part.AsSpan(5), out long keys))
                {
                    counts[index] = keys;
                }
            }
        }
        return counts;
    }

    private static IEnumerable<(string Name, string Value)> ParseInfo(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }
            int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                yield return (trimmed[..colon], trimmed[(colon + 1)..]);
            }
        }
    }

    /// <summary>
    /// 执行一条命令,把"被禁用 / 无权限 / 不支持"一律吞成空结果。
    /// <para>
    /// 这不是偷懒:托管 Redis 禁掉 <c>CONFIG</c>/<c>OBJECT</c>/<c>MEMORY</c> 是常态,
    /// 当成错误会让概览页一打开就一片红。**"没配过"与"不支持"是空状态,不是错误** ——
    /// 与 S3 插件对未配置的桶能力用的是同一条准则。
    /// </para>
    /// </summary>
    private static async Task<RedisResult> TryExecuteAsync(IDatabase db, string command, params object[] args)
    {
        try
        {
            return await db.ExecuteAsync(command, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex))
        {
            Trace.WriteLine($"[Redis] '{command}' unavailable: {ex.Message}");
            return RedisResult.Create(RedisValue.Null);
        }
    }

    private static bool IsDeniedOrUnsupported(Exception ex) =>
        ex is RedisServerException or RedisCommandException or RedisTimeoutException;

    private static bool IsSyntaxError(RedisServerException ex) =>
        ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("wrong number of arguments", StringComparison.OrdinalIgnoreCase);

    private static string AsString(RedisResult result) => result.IsNull ? string.Empty : (string?)result ?? string.Empty;

    /// <summary>把一个可能是二进制的值转成可显示文本(与键名同一条转义规矩)。</summary>
    private static string Display(RedisValue value) =>
        (byte[]?)value is { } raw ? new RedisKeyName(raw).Text : string.Empty;

    private static string TypeName(RedisType type) => type switch
    {
        RedisType.String => "string",
        RedisType.List => "list",
        RedisType.Set => "set",
        RedisType.SortedSet => "zset",
        RedisType.Hash => "hash",
        RedisType.Stream => "stream",
        RedisType.None => "none",
        _ => type.ToString().ToLowerInvariant()
    };

    private static (int Node, string Cursor) ParseClusterCursor(string cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor is "0")
        {
            return (0, "0");
        }
        int bar = cursor.IndexOf('|', StringComparison.Ordinal);
        return bar > 0 && int.TryParse(cursor.AsSpan(0, bar), out int node)
            ? (node, cursor[(bar + 1)..])
            : (0, cursor);
    }

    private static string FormatClusterCursor(int node, string cursor) =>
        string.Create(CultureInfo.InvariantCulture, $"{node}|{cursor}");

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e) => Availability?.Invoke(false);

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e) => Availability?.Invoke(true);
}

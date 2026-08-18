using System.Globalization;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 写入路径。每个方法都对应界面上一个具体动作,**不提供泛化的"执行任意写命令"** ——
/// 那条路留给控制台,而控制台有自己的闸门(<see cref="RedisCommandGuard" />)。
/// <para>
/// 纪律:删除一律优先 <c>UNLINK</c>(4.0+,异步释放不阻塞实例),不支持时才回落 <c>DEL</c>;
/// 重命名不静默覆盖(<c>RENAMENX</c> 失败即如实上报,由界面问用户)。
/// </para>
/// </summary>
internal sealed partial class RedisConnection
{
    /// <summary>是否支持 <c>UNLINK</c>(Redis 4.0+)。null = 还没试过。</summary>
    private bool? _unlinkSupported;

    /// <summary>写入字符串值(整体覆盖)。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">新值(原始字节)。</param>
    /// <param name="keepTtl">
    /// 是否保留原有过期时间。**默认保留**:用户改的是"值",不是"这个键还能活多久" ——
    /// 裸 <c>SET</c> 会把 TTL 抹掉,那是一次没人要求过的副作用。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task SetStringAsync(
        RedisKeyName key,
        byte[] value,
        bool keepTtl = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        if (!keepTtl)
        {
            await db.StringSetAsync(key.ToRedisKey(), value).ConfigureAwait(false);
            return;
        }
        try
        {
            // KEEPTTL 是 Redis 6.0 的选项;老服务器上退回"读 TTL → 写值 → 补回 TTL"。
            await db.ExecuteAsync("SET", [key.ToRedisKey(), (RedisValue)value, "KEEPTTL"]).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (IsSyntaxError(ex))
        {
            TimeSpan? ttl = await db.KeyTimeToLiveAsync(key.ToRedisKey()).ConfigureAwait(false);
            await db.StringSetAsync(key.ToRedisKey(), value).ConfigureAwait(false);
            if (ttl is { } remaining && remaining > TimeSpan.Zero)
            {
                await db.KeyExpireAsync(key.ToRedisKey(), remaining).ConfigureAwait(false);
            }
        }
    }

    /// <summary>写入哈希字段(新增或覆盖)。</summary>
    /// <param name="key">键。</param>
    /// <param name="field">字段名。</param>
    /// <param name="value">值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task SetHashFieldAsync(RedisKeyName key, string field, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().HashSetAsync(key.ToRedisKey(), field, value);
    }

    /// <summary>删除哈希字段。</summary>
    /// <param name="key">键。</param>
    /// <param name="field">字段名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的删掉了一个字段。</returns>
    public Task<bool> DeleteHashFieldAsync(RedisKeyName key, string field, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().HashDeleteAsync(key.ToRedisKey(), field);
    }

    /// <summary>按索引改写列表元素。</summary>
    /// <param name="key">键。</param>
    /// <param name="index">索引。</param>
    /// <param name="value">新值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task SetListItemAsync(RedisKeyName key, long index, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().ListSetByIndexAsync(key.ToRedisKey(), index, value);
    }

    /// <summary>
    /// 按值删除列表元素(<c>LREM count=1</c>)。
    /// <para>
    /// **列表没有"按索引删除"的原语**,这是 Redis 的事实而不是本实现的限制。
    /// 界面必须如实说明:删的是"第一个等于这个值的元素",而不是"第 N 个元素"。
    /// </para>
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">要删除的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删掉的元素个数。</returns>
    public Task<long> RemoveListValueAsync(RedisKeyName key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().ListRemoveAsync(key.ToRedisKey(), value, count: 1);
    }

    /// <summary>向列表一端追加元素。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <param name="atHead">true = 左端(<c>LPUSH</c>),false = 右端(<c>RPUSH</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作后的列表长度。</returns>
    public Task<long> PushListAsync(RedisKeyName key, string value, bool atHead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return atHead
            ? Db().ListLeftPushAsync(key.ToRedisKey(), value)
            : Db().ListRightPushAsync(key.ToRedisKey(), value);
    }

    /// <summary>向集合加入成员。</summary>
    /// <param name="key">键。</param>
    /// <param name="member">成员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否是新成员。</returns>
    public Task<bool> AddSetMemberAsync(RedisKeyName key, string member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().SetAddAsync(key.ToRedisKey(), member);
    }

    /// <summary>从集合移除成员。</summary>
    /// <param name="key">键。</param>
    /// <param name="member">成员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的移除了。</returns>
    public Task<bool> RemoveSetMemberAsync(RedisKeyName key, string member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().SetRemoveAsync(key.ToRedisKey(), member);
    }

    /// <summary>写入有序集合成员的分值(新增或改分)。</summary>
    /// <param name="key">键。</param>
    /// <param name="member">成员。</param>
    /// <param name="score">分值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否是新成员。</returns>
    public Task<bool> SetSortedMemberAsync(RedisKeyName key, string member, double score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().SortedSetAddAsync(key.ToRedisKey(), member, score);
    }

    /// <summary>从有序集合移除成员。</summary>
    /// <param name="key">键。</param>
    /// <param name="member">成员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的移除了。</returns>
    public Task<bool> RemoveSortedMemberAsync(RedisKeyName key, string member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().SortedSetRemoveAsync(key.ToRedisKey(), member);
    }

    /// <summary>设置过期时间。</summary>
    /// <param name="key">键。</param>
    /// <param name="ttl">存活时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>键是否存在(不存在时 Redis 什么也不做)。</returns>
    public Task<bool> ExpireAsync(RedisKeyName key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().KeyExpireAsync(key.ToRedisKey(), ttl);
    }

    /// <summary>去掉过期时间(<c>PERSIST</c>)。</summary>
    /// <param name="key">键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的去掉了(键本来就永久时为 false)。</returns>
    public Task<bool> PersistAsync(RedisKeyName key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Db().KeyPersistAsync(key.ToRedisKey());
    }

    /// <summary>
    /// 重命名。
    /// <para>
    /// <c>overwrite = false</c> 时用 <c>RENAMENX</c>:**<c>RENAME</c> 会静默覆盖目标键**,
    /// 那是一次无声的数据丢失。界面应当先试不覆盖,失败了再问用户要不要覆盖。
    /// </para>
    /// </summary>
    /// <param name="key">原键名。</param>
    /// <param name="newKey">新键名。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否重命名成功(false = 目标已存在且不允许覆盖)。</returns>
    public async Task<bool> RenameAsync(
        RedisKeyName key,
        RedisKeyName newKey,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(newKey);
        cancellationToken.ThrowIfCancellationRequested();
        return await Db().KeyRenameAsync(
            key.ToRedisKey(),
            newKey.ToRedisKey(),
            overwrite ? When.Always : When.NotExists).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除一批键。
    /// <para>
    /// 优先 <c>UNLINK</c>(4.0+):删一个百万元素的集合时 <c>DEL</c> 会**阻塞整个实例**,
    /// 而 <c>UNLINK</c> 把释放交给后台线程。老服务器上回落 <c>DEL</c>,并只在那时才有阻塞风险。
    /// </para>
    /// </summary>
    /// <param name="keys">要删的键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>真的被删掉的键数。</returns>
    public async Task<long> DeleteAsync(IReadOnlyList<RedisKeyName> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return 0;
        }
        IDatabase db = Db();
        RedisKey[] redisKeys = [.. keys.Select(key => key.ToRedisKey())];
        // 库的 KeyDelete 发的是 DEL,拿不到 UNLINK —— 所以这里显式走 Execute。
        if (_unlinkSupported != false)
        {
            try
            {
                long removed = await CountKeysAsync(db, "UNLINK", redisKeys).ConfigureAwait(false);
                _unlinkSupported = true;
                return removed;
            }
            catch (RedisServerException ex) when (IsUnknownCommand(ex) || IsSyntaxError(ex))
            {
                _unlinkSupported = false;
            }
        }
        return await CountKeysAsync(db, "DEL", redisKeys).ConfigureAwait(false);
    }

    /// <summary>把一批键交给 <c>UNLINK</c>/<c>DEL</c>,返回服务器报的删除计数。</summary>
    private static async Task<long> CountKeysAsync(IDatabase db, string command, RedisKey[] keys)
    {
        // 集群下多键命令要按槽分组;这里逐键发出(仍是流水线,一批一个往返),
        // 顺带让"跨槽"这个限制根本不会出现。
        long total = 0;
        var pending = new Task<RedisResult>[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            pending[i] = db.ExecuteAsync(command, [keys[i]]);
        }
        foreach (Task<RedisResult> task in pending)
        {
            RedisResult result = await task.ConfigureAwait(false);
            total += result.IsNull ? 0 : (long?)result ?? 0;
        }
        return total;
    }

    /// <summary>
    /// 用 <c>DUMP</c> + <c>RESTORE</c> 把一个键复制到另一个库或另一条连接。
    /// <para>
    /// 为什么是 <c>DUMP</c>/<c>RESTORE</c> 而不是逐类型重建:它是**保真**的 ——
    /// 编码、嵌套结构、模块类型全都原样过去,而逐类型重建会在每种边界上各丢一点东西。
    /// </para>
    /// </summary>
    /// <param name="key">要复制的键。</param>
    /// <param name="target">目标连接(可以是自己,配合 <paramref name="targetDatabase" /> 跨库)。</param>
    /// <param name="targetDatabase">目标库。</param>
    /// <param name="newKey">目标键名;null 表示同名。</param>
    /// <param name="replace">目标已存在时是否覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否复制成功(false = 源键不存在)。</returns>
    public async Task<bool> CopyKeyAsync(
        RedisKeyName key,
        RedisConnection target,
        int targetDatabase,
        RedisKeyName? newKey,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? payload = await Db().KeyDumpAsync(key.ToRedisKey()).ConfigureAwait(false);
        if (payload is null)
        {
            return false;
        }
        // TTL 也要跟过去:复制一个"还有十分钟就没了"的键,到了对面变成永久是另一种失真。
        TimeSpan? ttl = await Db().KeyTimeToLiveAsync(key.ToRedisKey()).ConfigureAwait(false);
        IDatabase destination = target._mux.GetDatabase(target._settings.SupportsDatabases ? targetDatabase : 0);
        var destinationKey = (newKey ?? key).ToRedisKey();
        var args = new List<object>
        {
            destinationKey,
            ttl is { } remaining ? (long)remaining.TotalMilliseconds : 0L,
            (RedisValue)payload
        };
        if (replace)
        {
            args.Add("REPLACE");
        }
        await destination.ExecuteAsync("RESTORE", args).ConfigureAwait(false);
        return true;
    }

    /// <summary>库返回的"未知命令"错误(用于能力探测式回落)。</summary>
    private static bool IsUnknownCommand(RedisServerException ex) =>
        ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase);

    /// <summary>把一个可能是二进制的值转成 <c>SET</c> 能接的载荷。</summary>
    internal static RedisValue ToValue(byte[] bytes) => bytes;

    /// <summary>格式化分值(不变文化,免得某些语言把小数点写成逗号)。</summary>
    internal static string FormatScore(double score) => score.ToString("0.############", CultureInfo.InvariantCulture);
}

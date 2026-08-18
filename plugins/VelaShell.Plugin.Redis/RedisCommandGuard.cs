namespace VelaShell.Plugin.Redis;

/// <summary>命令的误伤成本档位。护栏强度按它分级。</summary>
public enum RedisCommandRisk
{
    /// <summary>只读:随时可跑。</summary>
    Read,

    /// <summary>写:只读模式下拦住。</summary>
    Write,

    /// <summary>危:逐次确认(改配置、杀连接、切主从、MONITOR、DEBUG…)。</summary>
    Dangerous,

    /// <summary>毁:要求手打确认串,生产标记下默认整条禁用(清库、关服)。</summary>
    Destructive
}

/// <summary>一次命令闸门的判定结果。</summary>
/// <param name="Risk">档位。</param>
/// <param name="Allowed">是否放行(false = 被只读模式或生产锁拦住)。</param>
/// <param name="NeedsConfirmation">是否需要弹窗确认。</param>
/// <param name="NeedsTypedConfirmation">是否要求手打确认串。</param>
/// <param name="Reason">被拦时给用户的原因;放行时为空串。</param>
public readonly record struct RedisCommandVerdict(
    RedisCommandRisk Risk,
    bool Allowed,
    bool NeedsConfirmation,
    bool NeedsTypedConfirmation,
    string Reason = "");

/// <summary>
/// 命令闸门。
/// <para>
/// **判定依据是服务器自己给的 <c>COMMAND INFO</c> flags,不是插件里手写的黑名单** ——
/// 服务器说它是写命令,它就是写命令。手写名单必然漏:模块命令(<c>JSON.SET</c>、<c>TS.ADD</c>)、
/// 新版本新增的命令、以及各家分叉自带的命令,一条都不在任何人的名单里。
/// </para>
/// <para>
/// 只有"毁"这一档按名字定:<c>FLUSHDB</c>/<c>FLUSHALL</c>/<c>SHUTDOWN</c> 在 flags 上与普通写命令
/// 没有区别,而它们的误伤成本完全不是一回事。这是刻意的例外,不是偷懒。
/// </para>
/// </summary>
internal sealed class RedisCommandGuard
{
    /// <summary>"毁"档:名字定死。它们在 <c>COMMAND INFO</c> 的 flags 上与普通写命令无异。</summary>
    private static readonly HashSet<string> Destructive =
    [
with(StringComparer.OrdinalIgnoreCase),         "FLUSHALL", "FLUSHDB", "SHUTDOWN"
    ];

    /// <summary>
    /// "危"档的补充:这些命令的 flags 里不一定带 <c>admin</c>,但后果需要用户过一次脑子。
    /// </summary>
    private static readonly HashSet<string> Dangerous =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "MONITOR", "DEBUG", "CONFIG", "CLIENT", "REPLICAOF", "SLAVEOF", "ACL",
        "SCRIPT", "FUNCTION", "CLUSTER", "FAILOVER", "SWAPDB", "MIGRATE", "RESET"
    ];

    /// <summary>
    /// <c>COMMAND INFO</c> 拿不到时的兜底写命令表(常见的那几十条)。
    /// <para>
    /// 兜底表**只用于判"是不是写"**,而且宁可多判不可少判 —— 少判一条就是只读模式漏掉一次写入。
    /// 真正的权威永远是服务器的 flags。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> FallbackWrites =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "SET", "SETNX", "SETEX", "PSETEX", "SETRANGE", "APPEND", "GETSET", "GETDEL", "GETEX",
        "INCR", "INCRBY", "INCRBYFLOAT", "DECR", "DECRBY",
        "DEL", "UNLINK", "EXPIRE", "PEXPIRE", "EXPIREAT", "PEXPIREAT", "PERSIST",
        "RENAME", "RENAMENX", "MOVE", "COPY", "RESTORE", "DUMP",
        "HSET", "HSETNX", "HDEL", "HINCRBY", "HINCRBYFLOAT", "HEXPIRE", "HPERSIST",
        "LPUSH", "LPUSHX", "RPUSH", "RPUSHX", "LPOP", "RPOP", "LSET", "LINSERT", "LREM", "LTRIM",
        "LMOVE", "RPOPLPUSH", "BLPOP", "BRPOP", "BLMOVE",
        "SADD", "SREM", "SPOP", "SMOVE", "SINTERSTORE", "SUNIONSTORE", "SDIFFSTORE",
        "ZADD", "ZREM", "ZINCRBY", "ZPOPMIN", "ZPOPMAX", "ZREMRANGEBYSCORE",
        "ZREMRANGEBYRANK", "ZREMRANGEBYLEX", "ZRANGESTORE", "ZUNIONSTORE", "ZINTERSTORE",
        "XADD", "XDEL", "XTRIM", "XACK", "XGROUP", "XCLAIM", "XAUTOCLAIM", "XSETID",
        "SETBIT", "BITOP", "BITFIELD", "PFADD", "PFMERGE",
        "GEOADD", "GEORADIUSBYMEMBER", "GEOSEARCHSTORE",
        "EVAL", "EVALSHA", "FCALL",
        "JSON.SET", "JSON.DEL", "JSON.ARRAPPEND", "JSON.NUMINCRBY", "JSON.STRAPPEND",
        "TS.ADD", "TS.CREATE", "TS.INCRBY", "TS.DECRBY", "TS.DEL"
    ];

    private readonly Dictionary<string, RedisCommandRisk> _byName = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>只读模式(拦住一切写命令)。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>生产标记(把"毁"档整条禁用,而不只是要求确认)。</summary>
    public bool LockDestructive { get; set; }

    /// <summary>
    /// 命令元数据是否来自服务器。为 false 表示走的是兜底表 ——
    /// 界面应当据此在闸门提示里说清"分级依据是内置表",不假装是服务器说的。
    /// </summary>
    public bool MetadataFromServer { get; private set; }

    /// <summary>
    /// 装入 <c>COMMAND INFO</c> 的结果。
    /// </summary>
    /// <param name="flagsByCommand">命令名 → flags 集合。</param>
    public void LoadServerMetadata(IReadOnlyDictionary<string, IReadOnlyList<string>> flagsByCommand)
    {
        ArgumentNullException.ThrowIfNull(flagsByCommand);
        _byName.Clear();
        foreach ((string name, IReadOnlyList<string> flags) in flagsByCommand)
        {
            bool write = flags.Any(f => f.Equals("write", StringComparison.OrdinalIgnoreCase));
            bool admin = flags.Any(f => f.Equals("admin", StringComparison.OrdinalIgnoreCase));
            _byName[name] = Destructive.Contains(name)
                ? RedisCommandRisk.Destructive
                : admin || Dangerous.Contains(name)
                    ? RedisCommandRisk.Dangerous
                    : write
                        ? RedisCommandRisk.Write
                        : RedisCommandRisk.Read;
        }
        MetadataFromServer = _byName.Count > 0;
    }

    /// <summary>给一条命令定档。未知命令一律按"写"处理 —— 宁可多问一次,不可放过一次写入。</summary>
    /// <param name="command">命令名(取第一个词;子命令不影响定档)。</param>
    /// <returns>档位。</returns>
    public RedisCommandRisk Classify(string? command)
    {
        string name = Normalize(command);
        if (name.Length == 0)
        {
            return RedisCommandRisk.Read;
        }
        if (Destructive.Contains(name))
        {
            return RedisCommandRisk.Destructive;
        }
        if (_byName.TryGetValue(name, out RedisCommandRisk known))
        {
            return known;
        }
        if (Dangerous.Contains(name))
        {
            return RedisCommandRisk.Dangerous;
        }
        if (FallbackWrites.Contains(name))
        {
            return RedisCommandRisk.Write;
        }
        // 走到这里的是"服务器元数据里没有、兜底表里也没有"的命令:要么是打错的,
        // 要么是模块/分叉新增的。两种情况都按写处理 —— **未知不等于安全**。
        return RedisCommandRisk.Write;
    }

    /// <summary>判定一条命令能不能跑、要不要确认。</summary>
    /// <param name="command">命令名。</param>
    /// <returns>判定结果。</returns>
    public RedisCommandVerdict Evaluate(string? command)
    {
        RedisCommandRisk risk = Classify(command);
        if (risk == RedisCommandRisk.Read)
        {
            return new(risk, Allowed: true, NeedsConfirmation: false, NeedsTypedConfirmation: false);
        }
        if (ReadOnly)
        {
            // 只读模式拦住的一律给出"为什么 + 怎么解除",而不是灰一个按钮了事。
            return new(risk, Allowed: false, NeedsConfirmation: false, NeedsTypedConfirmation: false, "readonly");
        }
        return risk switch
        {
            RedisCommandRisk.Destructive when LockDestructive =>
                new(risk, Allowed: false, NeedsConfirmation: false, NeedsTypedConfirmation: false, "production-locked"),
            RedisCommandRisk.Destructive =>
                new(risk, Allowed: true, NeedsConfirmation: true, NeedsTypedConfirmation: true),
            RedisCommandRisk.Dangerous =>
                new(risk, Allowed: true, NeedsConfirmation: true, NeedsTypedConfirmation: false),
            _ => new(risk, Allowed: true, NeedsConfirmation: false, NeedsTypedConfirmation: false)
        };
    }

    /// <summary>取一条命令行的命令名(第一个词,去引号)。</summary>
    /// <param name="command">命令或整行命令。</param>
    /// <returns>规范化的命令名。</returns>
    public static string Normalize(string? command)
    {
        string text = (command ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }
        int space = text.IndexOfAny([' ', '\t']);
        string head = space > 0 ? text[..space] : text;
        return head.Trim('"', '\'').ToUpperInvariant();
    }
}

namespace VelaShell.Plugin.Redis;

/// <summary>一页 <c>SCAN</c> 结果。</summary>
/// <param name="Cursor">下一轮的游标;<b>"0" 才表示扫完了</b>,其余一切措辞只能是"已扫描到"。</param>
/// <param name="Keys">这一页的键(可能为空 —— SCAN 允许连续多轮返回空页)。</param>
/// <param name="Scanned">本轮服务端实际遍历过的槽位数(用 <c>COUNT</c> 近似,仅供进度显示)。</param>
public sealed record RedisScanPage(string Cursor, IReadOnlyList<RedisKeyName> Keys, int Scanned)
{
    /// <summary>游标是否已归零(唯一可以说"这就是全部"的依据)。</summary>
    public bool IsComplete => Cursor is "0";
}

/// <summary>键的元信息(列表行与详情头共用)。</summary>
/// <param name="Key">键名。</param>
/// <param name="Type">Redis 类型名(<c>string</c>/<c>hash</c>/…;取不到时为空串)。</param>
/// <param name="Ttl">剩余存活时间;<see langword="null" /> 表示永不过期。</param>
/// <param name="Encoding"><c>OBJECT ENCODING</c>;服务器禁用 OBJECT 时为空串。</param>
/// <param name="Length">元素个数 / 字符串字节数;未知时为 -1。</param>
/// <param name="MemoryBytes"><c>MEMORY USAGE</c> 的**抽样估计**;未取或不可用时为 -1。</param>
public sealed record RedisKeyInfo(
    RedisKeyName Key,
    string Type,
    TimeSpan? Ttl,
    string Encoding,
    long Length,
    long MemoryBytes)
{
    /// <summary>键已不存在(查看期间过期或被删)。</summary>
    public bool IsGone => string.IsNullOrEmpty(Type) || Type is "none";
}

/// <summary>
/// 列表一行所需的两项度量:TTL 与规模。
/// <para>
/// 刻意**不含** <c>MEMORY USAGE</c>:它在服务器上是抽样遍历,对大键并不便宜,
/// 而且 Redis 4.0 以下压根没有这条命令。列表要的是"这个键有多大"的**量级感**,
/// 用 <c>STRLEN</c>/<c>HLEN</c>/<c>LLEN</c>/<c>SCARD</c>/<c>ZCARD</c>/<c>XLEN</c> 就够 ——
/// 它们全是 O(1)。真要看字节数,详情页那一栏才去问 <c>MEMORY USAGE</c>。
/// </para>
/// </summary>
/// <param name="Ttl">剩余存活时间;<see langword="null" /> 表示永不过期。</param>
/// <param name="Length">元素个数 / 字符串字节数;未知时为 -1。</param>
public readonly record struct RedisKeyMeasure(TimeSpan? Ttl, long Length);

/// <summary>字符串值的一段预览。</summary>
/// <param name="Bytes">取到的字节(可能是被截断的前 N 字节)。</param>
/// <param name="TotalLength">服务端的完整长度。</param>
public sealed record RedisStringValue(byte[] Bytes, long TotalLength)
{
    /// <summary>是否被截断(界面必须如实标注,并把编辑器置只读)。</summary>
    public bool IsTruncated => Bytes.LongLength < TotalLength;
}

/// <summary>集合类值的一页(哈希字段 / 列表元素 / 集合成员 / 有序集合成员)。</summary>
/// <param name="Rows">这一页的行。</param>
/// <param name="Cursor">下一轮游标("0" = 读完);列表按索引分页时为空。</param>
/// <param name="Total">服务端报告的总数;未知时为 -1。</param>
public sealed record RedisElementPage(IReadOnlyList<RedisElement> Rows, string Cursor, long Total)
{
    /// <summary>是否已读完。</summary>
    public bool IsComplete => string.IsNullOrEmpty(Cursor) || Cursor is "0";
}

/// <summary>集合类值里的一行。三列语义按类型复用,避免为每种类型各造一个模型。</summary>
/// <param name="Label">字段名 / 索引 / 成员。</param>
/// <param name="Value">值;集合(set)只有成员没有值时为空串。</param>
/// <param name="Score">有序集合的分值;其余类型为 <see langword="null" />。</param>
public sealed record RedisElement(string Label, string Value, double? Score = null);

/// <summary>服务器概况(文档头与状态条用)。</summary>
/// <param name="Version">服务器版本(<c>redis_version</c> 或分叉自己的版本)。</param>
/// <param name="Flavor">发行标识(<c>redis</c> / <c>valkey</c> / 其它);无法判断时为 <c>redis</c>。</param>
/// <param name="Mode">运行模式(<c>standalone</c> / <c>cluster</c> / <c>sentinel</c>)。</param>
/// <param name="Protocol">实际协商到的协议(<c>RESP2</c> / <c>RESP3</c>)。</param>
/// <param name="Databases">数据库个数;拿不到 <c>CONFIG</c> 时为 16 并由 <paramref name="DatabasesConfirmed" /> 标注。</param>
/// <param name="DatabasesConfirmed">数据库个数是否由服务器确认(而非按默认值猜的)。</param>
/// <param name="KeyCountByDatabase">逐库键数(来自 <c>INFO keyspace</c>,缺席的库即为空)。</param>
public sealed record RedisServerInfo(
    string Version,
    string Flavor,
    string Mode,
    string Protocol,
    int Databases,
    bool DatabasesConfirmed,
    IReadOnlyDictionary<int, long> KeyCountByDatabase);

namespace VelaShell.PluginSdk.TimeSeries;

/// <summary>时序字段的取值类型(与宿主时序引擎的列类型一一对应)。</summary>
public enum TimeSeriesValueKind
{
    /// <summary>文本。</summary>
    Text,

    /// <summary>64 位整数。</summary>
    Integer,

    /// <summary>双精度浮点。</summary>
    Number,

    /// <summary>布尔。</summary>
    Flag
}

/// <summary>时序列的角色。</summary>
public enum TimeSeriesColumnRole
{
    /// <summary>
    /// 标签(索引维度):同一 measurement 内「标签取值组合」即一条序列,
    /// 查询按标签过滤最快。标签值一律为字符串,不要放高基数内容(如整段文本)。
    /// </summary>
    Tag,

    /// <summary>字段(数据):按时间存储的实际取值。</summary>
    Field
}

/// <summary>
/// 一个时序字段值(带类型的联合体)。用工厂方法构造:
/// <c>TimeSeriesValue.FromText("x")</c> / <c>FromInteger(1)</c> / <c>FromNumber(1.5)</c> / <c>FromFlag(true)</c>。
/// </summary>
public readonly record struct TimeSeriesValue
{
    /// <summary>取值类型。</summary>
    public TimeSeriesValueKind Kind { get; init; }

    /// <summary>文本载荷(<see cref="Kind" /> 为 <see cref="TimeSeriesValueKind.Text" /> 时有效)。</summary>
    public string? Text { get; init; }

    /// <summary>整数载荷。</summary>
    public long Integer { get; init; }

    /// <summary>浮点载荷。</summary>
    public double Number { get; init; }

    /// <summary>布尔载荷。</summary>
    public bool Flag { get; init; }

    /// <summary>构造文本值(null 视为空串)。</summary>
    public static TimeSeriesValue FromText(string? value) => new() { Kind = TimeSeriesValueKind.Text, Text = value ?? "" };

    /// <summary>构造整数值。</summary>
    public static TimeSeriesValue FromInteger(long value) => new() { Kind = TimeSeriesValueKind.Integer, Integer = value };

    /// <summary>构造浮点值。</summary>
    public static TimeSeriesValue FromNumber(double value) => new() { Kind = TimeSeriesValueKind.Number, Number = value };

    /// <summary>构造布尔值。</summary>
    public static TimeSeriesValue FromFlag(bool value) => new() { Kind = TimeSeriesValueKind.Flag, Flag = value };

    /// <summary>按当前类型转成可读文本(读取任意列时的通用出口)。</summary>
    public string AsText() => Kind switch
    {
        TimeSeriesValueKind.Text => Text ?? "",
        TimeSeriesValueKind.Integer => Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TimeSeriesValueKind.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Flag ? "true" : "false"
    };

    /// <summary>取整数(文本按不变区域解析;解析失败返回 <paramref name="fallback" />)。</summary>
    public long AsInteger(long fallback = 0) => Kind switch
    {
        TimeSeriesValueKind.Integer => Integer,
        TimeSeriesValueKind.Number => (long)Number,
        TimeSeriesValueKind.Flag => Flag ? 1 : 0,
        _ => long.TryParse(Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback
    };

    /// <summary>取布尔(非布尔列按「整数非零 / 文本 true」判定)。</summary>
    public bool AsFlag() => Kind switch
    {
        TimeSeriesValueKind.Flag => Flag,
        TimeSeriesValueKind.Integer => Integer != 0,
        TimeSeriesValueKind.Number => Number != 0,
        _ => bool.TryParse(Text, out bool parsed) && parsed
    };
}

/// <summary>时序 measurement 的一列定义。</summary>
/// <param name="Name">列名(<c>[a-z][a-z0-9_]*</c>,长度 ≤ 40)。</param>
/// <param name="Role">列角色(标签或字段)。</param>
/// <param name="Kind">取值类型;标签列恒为 <see cref="TimeSeriesValueKind.Text" />。</param>
public sealed record TimeSeriesColumn(string Name, TimeSeriesColumnRole Role, TimeSeriesValueKind Kind)
{
    /// <summary>标签列的便捷构造。</summary>
    public static TimeSeriesColumn Tag(string name) => new(name, TimeSeriesColumnRole.Tag, TimeSeriesValueKind.Text);

    /// <summary>字段列的便捷构造。</summary>
    public static TimeSeriesColumn Field(string name, TimeSeriesValueKind kind) => new(name, TimeSeriesColumnRole.Field, kind);
}

/// <summary>
/// 时序 measurement 的定义(首次 <see cref="ITimeSeriesApi.OpenAsync" /> 时按此建表;
/// 已存在则沿用既有 schema,定义变化不会自动迁移 —— 加列请改用新名字)。
/// </summary>
/// <param name="Name">插件内的 measurement 名(<c>[a-z][a-z0-9_]*</c>,长度 ≤ 40)。宿主会自动加插件命名空间前缀,插件之间互不可见。</param>
/// <param name="Columns">列定义(至少 1 个字段列;标签 + 字段合计 ≤ 32)。</param>
public sealed record TimeSeriesDefinition(string Name, IReadOnlyList<TimeSeriesColumn> Columns);

/// <summary>
/// 一个时序数据点。
/// <para>
/// 关键语义:同一序列(measurement + 完全相同的标签组合)内,<b>时间戳唯一</b> ——
/// 同毫秒重复写入会<b>覆盖</b>前一个点,而非追加。高频写入请用
/// <see cref="TimeSeriesClock" /> 生成严格递增的时间戳。
/// </para>
/// </summary>
/// <param name="Timestamp">时间戳(毫秒精度,存储为 UTC)。</param>
/// <param name="Tags">标签取值(键须为定义中的标签列)。</param>
/// <param name="Fields">字段取值(键须为定义中的字段列;缺省的字段读出为 null)。</param>
public sealed record TimeSeriesPoint(
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, TimeSeriesValue> Fields)
{
    /// <summary>取字段值;不存在时返回 <see langword="null" />。</summary>
    public TimeSeriesValue? Field(string name) => Fields.TryGetValue(name, out TimeSeriesValue value) ? value : null;

    /// <summary>取字段的文本形式;不存在时返回空串。</summary>
    public string Text(string name) => Field(name)?.AsText() ?? "";

    /// <summary>取字段的整数形式;不存在时返回 <paramref name="fallback" />。</summary>
    public long Integer(string name, long fallback = 0) => Field(name)?.AsInteger(fallback) ?? fallback;

    /// <summary>取标签值;不存在时返回空串。</summary>
    public string Tag(string name) => Tags.TryGetValue(name, out string? value) ? value : "";
}

/// <summary>时序查询条件(全部可选;默认取最近 200 个点,时间倒序)。</summary>
public sealed record TimeSeriesQuery
{
    /// <summary>标签过滤(全部条件与关系);null 或空 = 不过滤。</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>时间下界(含)。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>时间上界(含)。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>返回上限(1 ≤ n ≤ <see cref="TimeSeriesLimits.MaxQueryLimit" />,越界由宿主钳制)。</summary>
    public int Limit { get; init; } = 200;

    /// <summary>是否时间倒序(默认 true = 最新在前)。</summary>
    public bool Descending { get; init; } = true;
}

/// <summary>
/// 时序能力的配额与命名约束(宿主按同一套规则校验;越界抛
/// <see cref="ArgumentException" /> 或 <see cref="InvalidOperationException" />)。
/// </summary>
public static class TimeSeriesLimits
{
    /// <summary>每个插件可创建的 measurement 数上限。</summary>
    public const int MaxSeriesPerPlugin = 8;

    /// <summary>单个 measurement 的列数上限(标签 + 字段)。</summary>
    public const int MaxColumns = 32;

    /// <summary>名称(measurement / 列)的长度上限。</summary>
    public const int MaxNameLength = 40;

    /// <summary>标签值的长度上限(标签是索引维度,不放长文本)。</summary>
    public const int MaxTagValueLength = 200;

    /// <summary>单个文本字段值的长度上限(字符)。</summary>
    public const int MaxTextFieldLength = 1024 * 1024;

    /// <summary>单次批量写入的点数上限。</summary>
    public const int MaxWriteBatch = 1000;

    /// <summary>单次查询的返回条数上限。</summary>
    public const int MaxQueryLimit = 5000;
}

/// <summary>
/// 严格递增的时间戳分配器:同一序列内同毫秒的多个点会互相覆盖,
/// 用它取时间戳可保证「不丢点、且顺序即写入顺序」。线程安全。
/// </summary>
public sealed class TimeSeriesClock
{
    private long _lastMs;

    /// <summary>取下一个严格大于上次返回值的时间戳(通常即当前时刻)。</summary>
    public DateTimeOffset Next()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long assigned;
        while (true)
        {
            long last = Interlocked.Read(ref _lastMs);
            assigned = now > last ? now : last + 1;
            if (Interlocked.CompareExchange(ref _lastMs, assigned, last) == last)
            {
                break;
            }
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(assigned);
    }

    /// <summary>把水位对齐到已知的最后一个时间戳(加载历史后调用,避免新点覆盖旧点)。</summary>
    public void Observe(DateTimeOffset timestamp)
    {
        long ms = timestamp.ToUnixTimeMilliseconds();
        while (true)
        {
            long last = Interlocked.Read(ref _lastMs);
            if (ms <= last || Interlocked.CompareExchange(ref _lastMs, ms, last) == last)
            {
                return;
            }
        }
    }
}

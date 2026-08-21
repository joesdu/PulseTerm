using System.Data.Common;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>结果集里一列的元信息。</summary>
/// <param name="Name">列名。</param>
/// <param name="ClrTypeName">驱动报的 CLR 类型名。</param>
/// <param name="ProviderTypeName">驱动报的数据源类型名(<b>不足以还原真实 DDL 类型</b>,见备注)。</param>
internal sealed record SqlResultColumn(string Name, string ClrTypeName, string ProviderTypeName);

/// <summary>
/// 一格的值。
/// <para>
/// <b>为什么不直接放 <c>object?</c></b>:结果网格必须把 NULL、空串、二进制、超长文本
/// **四者分开显示**——把它们混为一谈是数据工具的原罪(设计文档 §7.3)。
/// 这四种状态在 <c>IDataReader</c> 层面天然可区分,但一旦装箱成 <c>object?</c> 再往上传,
/// "NULL"和"字符串 null"、"空串"和"没取到"就分不开了。
/// </para>
/// </summary>
internal readonly record struct SqlCell
{
    private SqlCell(SqlCellKind kind, string? text, byte[]? bytes, long fullLength, string? error)
    {
        Kind = kind;
        Text = text;
        Bytes = bytes;
        FullLength = fullLength;
        Error = error;
    }

    /// <summary>值的种类。</summary>
    public SqlCellKind Kind { get; }

    /// <summary>文本形态(已按需截断);二进制与 NULL 时为 <see langword="null" />。</summary>
    public string? Text { get; }

    /// <summary>二进制前若干字节;非二进制时为 <see langword="null" />。</summary>
    public byte[]? Bytes { get; }

    /// <summary>原值的完整长度(字符数或字节数);未截断时等于实际长度。</summary>
    public long FullLength { get; }

    /// <summary>读这一格失败时的原因;成功时为 <see langword="null" />。</summary>
    public string? Error { get; }

    /// <summary>值被截断了(界面要标出来,并提供"看全文")。</summary>
    public bool IsTruncated => Kind is SqlCellKind.Text or SqlCellKind.Binary
                               && FullLength > (Text?.Length ?? Bytes?.Length ?? 0);

    /// <summary>数据库 NULL。</summary>
    public static SqlCell Null() => new(SqlCellKind.Null, null, null, 0, null);

    /// <summary>文本(可能已截断)。</summary>
    /// <param name="text">文本。</param>
    /// <param name="fullLength">完整长度。</param>
    /// <returns>单元格。</returns>
    public static SqlCell FromText(string text, long fullLength) =>
        new(SqlCellKind.Text, text, null, fullLength, null);

    /// <summary>二进制(可能已截断)。</summary>
    /// <param name="bytes">前若干字节。</param>
    /// <param name="fullLength">完整字节数。</param>
    /// <returns>单元格。</returns>
    public static SqlCell FromBinary(byte[] bytes, long fullLength) =>
        new(SqlCellKind.Binary, null, bytes, fullLength, null);

    /// <summary>
    /// 读这一格失败。
    /// <para>
    /// **一格读失败不能让整页失败**(§7.8)。实测 PG 上 <c>infinity</c> 时间戳、<c>numeric NaN</c>、
    /// 超 <c>decimal</c> 范围、未知 OID 的枚举都会抛 —— 用户只是想看看这张表。
    /// </para>
    /// </summary>
    /// <param name="reason">原因。</param>
    /// <returns>单元格。</returns>
    public static SqlCell FromError(string reason) => new(SqlCellKind.Error, null, null, 0, reason);
}

/// <summary>单元格的种类。</summary>
internal enum SqlCellKind
{
    /// <summary>数据库 NULL(界面显示灰斜体 NULL)。</summary>
    Null,

    /// <summary>文本(含数字、日期等一切可文本化的值)。</summary>
    Text,

    /// <summary>二进制(界面显示 <c>0x…(1.2 KB)</c>)。</summary>
    Binary,

    /// <summary>读取失败(界面显示 <c>&lt;不可映射: 原因&gt;</c>,而不是让整页炸)。</summary>
    Error
}

/// <summary>一个结果集。</summary>
/// <param name="Columns">列。</param>
/// <param name="Rows">行。</param>
/// <param name="Truncated">是否因为达到取数上限而截断(界面要给"再取 200 / 全部取")。</param>
/// <param name="ElapsedMs">耗时。</param>
internal sealed record SqlResultSet(
    IReadOnlyList<SqlResultColumn> Columns,
    IReadOnlyList<SqlCell[]> Rows,
    bool Truncated,
    long ElapsedMs)
{
    /// <summary>
    /// 这一份数据是**降级取回来的**:第一次按二进制格式取失败了,执行层用全文本格式重跑了一次。
    /// <para>
    /// 目前只有 PostgreSQL 会走到(<c>aclitem</c> 那一类只有文本表示的类型,见
    /// <c>SqlExecutor.ShouldRefetchAsText</c>)。打这个标记不是为了好看:
    /// 降级之后驱动对<b>每一列</b>都报 <c>string</c>,于是 <c>bytea</c> 列会以
    /// <c>\x0102…</c> 的文本形态落进 <see cref="SqlCellKind.Text" />,
    /// 而不是网格里那个 <c>0x…</c> 的二进制标记 —— 用户看到的"类型"和真实 DDL 不是一回事了。
    /// 界面据此给一句说明(文案键 <c>Sql_TextFallback</c>),否则这是一次**静默的失真**。
    /// </para>
    /// </summary>
    public bool TextFallback { get; init; }
}

/// <summary>一条语句的执行结果:要么是结果集,要么是影响行数,要么是错误。</summary>
/// <param name="Statement">语句。</param>
/// <param name="ResultSets">结果集(可能多个)。</param>
/// <param name="RecordsAffected">影响行数;查询时为 -1。</param>
/// <param name="ElapsedMs">耗时。</param>
/// <param name="Error">错误(已翻译);成功时为 <see langword="null" />。</param>
/// <param name="ErrorLine">错误在**用户原文**里的行号(1 起);拿不到时为 <see langword="null" />。</param>
/// <param name="ErrorColumn">错误在用户原文里的列号(1 起);拿不到时为 <see langword="null" />。</param>
internal sealed record SqlStatementResult(
    SqlStatement Statement,
    IReadOnlyList<SqlResultSet> ResultSets,
    int RecordsAffected,
    long ElapsedMs,
    Exception? Error = null,
    int? ErrorLine = null,
    int? ErrorColumn = null)
{
    /// <summary>成功了没有。</summary>
    public bool Succeeded => Error is null;

    /// <summary>
    /// 这条语句是**降级重跑之后**才成功的(见 <see cref="SqlResultSet.TextFallback" />)。
    /// <para>
    /// 单独在语句这一层也留一份,是因为一条语句可能一个结果集都没有(纯 DML),
    /// 而批量执行的状态栏要在"整批"这一层告诉用户"这一批里有降级发生"。
    /// </para>
    /// </summary>
    public bool TextFallback { get; init; }
}

/// <summary>取数选项。</summary>
/// <param name="MaxRows">最多取多少行(默认 200 —— "默认全取是新手工具才干的事")。</param>
/// <param name="MaxTextLength">单格文本最多取多少字符;超出截断并标注。</param>
/// <param name="MaxBinaryLength">单格二进制最多取多少字节。</param>
internal sealed record SqlFetchOptions(int MaxRows = 200, int MaxTextLength = 4096, int MaxBinaryLength = 256)
{
    /// <summary>默认选项。</summary>
    public static SqlFetchOptions Default { get; } = new();

    /// <summary>
    /// 能不能用 <c>GetChars</c> / <c>GetBytes</c> 分块读。
    /// <para>
    /// <b>SQLite 上必须关掉,而且理由是"不关会把整个进程打死"。</b>
    /// <c>Microsoft.Data.Sqlite</c> 的 <c>GetChars</c> 内部走 <c>GetStream</c>,
    /// 后者要调原生的 <c>sqlite3_table_column_metadata</c> 去问"这一列属于哪张表的哪一列" ——
    /// 而<b>表达式列没有表</b>(<c>EXPLAIN QUERY PLAN</c> 的每一列、<c>select 1+1</c>、
    /// 任何聚合或函数结果都是),这一问就是一个 <c>0xC0000005</c> 访问冲突。
    /// </para>
    /// <para>
    /// <b>这个坑 <c>try/catch</c> 接不住</b>:访问冲突不是异常,它直接带走进程 ——
    /// 表现是用户在 SQLite 上点一下"计划",整个 VelaShell 没了。
    /// 下面那个 <c>catch (InvalidCastException)</c> 的回落对它完全无效。
    /// </para>
    /// <para>
    /// 关掉之后走"整取再截断",代价可控:SQLite 是**进程内**的库,
    /// 分块读本来就省不下网络传输,省下的只是一次托管堆分配。
    /// </para>
    /// </summary>
    public bool AllowChunkedReads { get; init; } = true;
}

/// <summary>从 <see cref="DbDataReader" /> 读一页的读取器。</summary>
internal static class SqlResultReader
{
    /// <summary>
    /// 读一个结果集(最多 <see cref="SqlFetchOptions.MaxRows" /> 行)。
    /// <para>
    /// <b>超长文本必须截断</b>:实测 200 行 × 1MB 文本用 <c>GetDataTable</c> 要 +400 MB 托管堆,
    /// 而默认 <c>CommandBehavior</c> 下 <c>GetChars</c> **救不了**(它照样先缓冲整列)。
    /// 所以这里用 <c>SequentialAccess</c> + <c>GetChars</c> —— 实测分配从 4,196,888 字节降到 2,808(1495 倍)。
    /// 真正的护栏还是服务端截断(§7.3),但那要改用户的 SQL,只能在"我们替他生成的 SQL"上做。
    /// </para>
    /// </summary>
    /// <param name="reader">读取器(须以 <c>SequentialAccess</c> 打开)。</param>
    /// <param name="options">取数选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果集。</returns>
    public static async Task<SqlResultSet> ReadAsync(
        DbDataReader reader,
        SqlFetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var start = System.Diagnostics.Stopwatch.StartNew();
        int fieldCount = reader.FieldCount;
        List<SqlResultColumn> columns = new(fieldCount);
        for (int i = 0; i < fieldCount; i++)
        {
            columns.Add(new(
                reader.GetName(i),
                SafeType(() => reader.GetFieldType(i)?.Name ?? ""),
                SafeType(() => reader.GetDataTypeName(i))));
        }

        List<SqlCell[]> rows = [];
        bool truncated = false;
        while (rows.Count < options.MaxRows)
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            var row = new SqlCell[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                row[i] = ReadCell(reader, i, options);
            }
            rows.Add(row);
        }
        // 还有下一行 = 我们是因为上限停的,不是因为读完了。界面据此给"再取 200 / 全部取"。
        if (rows.Count >= options.MaxRows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            truncated = true;
        }
        start.Stop();
        return new(columns, rows, truncated, start.ElapsedMilliseconds);
    }

    private static SqlCell ReadCell(DbDataReader reader, int ordinal, SqlFetchOptions options)
    {
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return SqlCell.Null();
            }
            Type type = reader.GetFieldType(ordinal);
            if (type == typeof(byte[]))
            {
                return ReadBinary(reader, ordinal, options);
            }
            if (type == typeof(string))
            {
                return ReadText(reader, ordinal, options);
            }
            // 其余类型(数字、日期、Guid、IPAddress、DateOnly/TimeOnly…)一律文本化。
            // 注意 Npgsql 10 起 date → DateOnly、time → TimeOnly、cidr → IPNetwork(§3.8),
            // ToString() 对它们都给得出人能看的形态。
            object value = reader.GetValue(ordinal);
            string text = value?.ToString() ?? "";
            return SqlCell.FromText(text, text.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not OutOfMemoryException
                                       and not StackOverflowException)
        {
            // 这一条就是 §7.8 的"单元格级容错"。PG 上 infinity 时间戳、numeric NaN、
            // 超 decimal 范围、未知 OID 的枚举都会走到这里 —— 用户只是想看看这张表,
            // 不该因为一格读不出来就整页失败。
            return SqlCell.FromError(ex.Message);
        }
    }

    private static SqlCell ReadText(DbDataReader reader, int ordinal, SqlFetchOptions options)
    {
        if (!options.AllowChunkedReads)
        {
            // 见 SqlFetchOptions.AllowChunkedReads:这条路径上 GetChars 会打死进程。
            string value = reader.GetString(ordinal);
            return SqlCell.FromText(Cut(value, options.MaxTextLength), value.Length);
        }
        var buffer = new char[options.MaxTextLength];
        long read;
        try
        {
            read = reader.GetChars(ordinal, 0, buffer, 0, buffer.Length);
        }
        catch (InvalidCastException)
        {
            // 有的驱动对某些列不支持 GetChars,退回整取(这类列通常本来就不长)。
            string whole = reader.GetString(ordinal);
            return SqlCell.FromText(Cut(whole, options.MaxTextLength), whole.Length);
        }
        // GetChars 拿不到"完整长度",只能报"至少这么长"。
        // 读满缓冲 = 后面还有,界面据此显示截断标记。
        long full = read < buffer.Length ? read : read + 1;
        return SqlCell.FromText(new(buffer, 0, (int)read), full);
    }

    private static SqlCell ReadBinary(DbDataReader reader, int ordinal, SqlFetchOptions options)
    {
        if (!options.AllowChunkedReads)
        {
            // 同上:GetBytes 走的是同一个 GetStream。
            var value = (byte[])reader.GetValue(ordinal);
            return SqlCell.FromBinary(value[..Math.Min(value.Length, options.MaxBinaryLength)], value.Length);
        }
        var buffer = new byte[options.MaxBinaryLength];
        long read;
        try
        {
            read = reader.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
        }
        catch (InvalidCastException)
        {
            var whole = (byte[])reader.GetValue(ordinal);
            return SqlCell.FromBinary(whole[..Math.Min(whole.Length, options.MaxBinaryLength)], whole.Length);
        }
        long full = read < buffer.Length ? read : read + 1;
        return SqlCell.FromBinary(buffer[..(int)read], full);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static string SafeType(Func<string> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 某些驱动对某些列拿不到类型名 —— 空着比让整个查询失败好。
            return "";
        }
    }
}

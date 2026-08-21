using System.Data;
using System.Data.Common;
using System.Globalization;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// 方言包的公共脚手架:参数化查询、行读取、标识符转义。
/// <para>
/// 各方言包**只写 SQL 与映射**,连接、参数、读取这些方言无关的部分一次写完。
/// </para>
/// </summary>
internal abstract class DialectPackBase : IDialectPack
{
    /// <inheritdoc />
    public abstract SqlDialect Dialect { get; }

    /// <inheritdoc />
    public abstract bool HasSchemas { get; }

    /// <inheritdoc />
    public abstract bool HasDatabases { get; }

    /// <inheritdoc />
    /// <remarks>
    /// 默认 <see langword="true" />(一条连接查得全)。**只有目录表按库分家的方言要覆盖成 false**
    /// —— 目前是 PostgreSQL 与 SQL Server。写成"默认能跨"是刻意的:
    /// 不能跨是少数派,而且一旦漏覆盖,表现是"树上全是空库",在真机上一眼就看得见;
    /// 反过来默认写 false 的话,漏覆盖的方言会白开一堆连接,而它照样能用 —— 那种错没人会发现。
    /// </remarks>
    public virtual bool MetadataSpansCatalogs => true;

    /// <inheritdoc />
    public virtual bool HasRoutines => false;

    /// <inheritdoc />
    public virtual bool HasSequences => false;

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<SqlObject>> ListSchemasAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<SqlObject>> ListRelationsAsync(DbConnection connection, string schema, CancellationToken cancellationToken);

    /// <inheritdoc />
    /// <remarks>没有例程概念的方言(SQLite)就用这份空实现,不必写一条查不到东西的 SQL。</remarks>
    public virtual Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlObject>>([]);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<SqlObject>> ListSequencesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlObject>>([]);

    /// <inheritdoc />
    public abstract Task<SqlTableSchema> DescribeAsync(DbConnection connection, SqlObject target, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract string ApplyPaging(string innerSql, int offset, int limit);

    /// <inheritdoc />
    public virtual string? EstimateRowCountSql(SqlObject target) => null;

    /// <inheritdoc />
    /// <remarks>默认没有 —— MySQL 的 schema 就是库(树已经把库名标出来了)、
    /// Oracle 的当前 schema 就是登录名、SQLite 压根没有这一级。只有 PG 与 SQL Server 要覆盖。</remarks>
    public virtual string? CurrentSchemaSql => null;

    /// <inheritdoc />
    public virtual string? SessionIdSql => null;

    /// <inheritdoc />
    public virtual string? CancelSessionSql(string sessionId) => null;

    /// <inheritdoc />
    public virtual string? ShowCreateSql(SqlObject target) => null;

    /// <inheritdoc />
    public virtual string? ExplainSql(string innerSql, bool analyze) => null;

    /// <inheritdoc />
    public virtual string? SessionListSql => null;

    /// <inheritdoc />
    public virtual string? LockListSql => null;

    /// <inheritdoc />
    public virtual IReadOnlyList<string> CommonTypes => [];

    /// <inheritdoc />
    public virtual string? AddColumnDdl(SqlObject target, SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        // 各方言的 ADD COLUMN 语法高度一致,差异在类型名与"要不要 COLUMN 关键字"上。
        // 默认给出通行写法,方言包按需覆盖。
        return $"ALTER TABLE {QuoteQualified(target)} ADD COLUMN {QuoteIdentifier(column.Name)} {column.DataType}"
               + (column.IsNullable ? "" : " NOT NULL")
               + (string.IsNullOrEmpty(column.DefaultValue) ? "" : $" DEFAULT {column.DefaultValue}");
    }

    /// <inheritdoc />
    public virtual string? DropColumnDdl(SqlObject target, string columnName) =>
        $"ALTER TABLE {QuoteQualified(target)} DROP COLUMN {QuoteIdentifier(columnName)}";

    /// <inheritdoc />
    public virtual string? CreateIndexDdl(SqlObject target, string indexName, IReadOnlyList<string> columns, bool unique)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            return null;
        }
        string cols = string.Join(", ", columns.Select(QuoteIdentifier));
        return $"CREATE {(unique ? "UNIQUE " : "")}INDEX {QuoteIdentifier(indexName)} ON {QuoteQualified(target)} ({cols})";
    }

    /// <inheritdoc />
    public virtual string? DropIndexDdl(SqlObject target, string indexName) =>
        $"DROP INDEX {QuoteIdentifier(indexName)}";

    /// <summary>标识符的定界符(左、右)。</summary>
    protected abstract (char Open, char Close) Delimiters { get; }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        (char open, char close) = Delimiters;
        // 定界符加倍是所有方言的通行转义。**这一步不能省** ——
        // 标识符里含定界符时不转义就是可执行的注入(§5.4.4 实测能删表)。
        string escaped = identifier.Replace(close.ToString(), $"{close}{close}", StringComparison.Ordinal);
        return $"{open}{escaped}{close}";
    }

    /// <summary>带 schema 的限定名,两段都转义。</summary>
    /// <param name="target">目标对象。</param>
    /// <returns>可拼进 SQL 的限定名。</returns>
    public string QuoteQualified(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.IsNullOrEmpty(target.Schema)
            ? QuoteIdentifier(target.Name)
            : $"{QuoteIdentifier(target.Schema)}.{QuoteIdentifier(target.Name)}";
    }

    /// <summary>跑一条参数化查询,把每行交给映射函数。</summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="sql">SQL。</param>
    /// <param name="map">行映射。</param>
    /// <param name="parameters">参数(按 <c>@p0</c>、<c>@p1</c>… 顺序绑定,PG 上也用 <c>@</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="prepare">
    /// 发出去之前给方言包一次改 <see cref="DbCommand" /> 的机会。
    /// <para>
    /// 开这个口子只为一件真事:**ODP.NET 的 <c>InitialLONGFetchSize</c> 默认是 0**,
    /// 不设它,<c>ALL_TAB_COLUMNS.DATA_DEFAULT</c>(<c>LONG</c> 类型)读回来是空串而不是默认值 ——
    /// 而这个属性只在 <c>OracleCommand</c> 上,脚手架手里只有 <see cref="DbCommand" />。
    /// SQL 侧没有绕法(<c>LONG</c> 进不了 <c>SUBSTR</c> / <c>TO_CHAR</c> / <c>CAST</c>)。
    /// </para>
    /// </param>
    /// <returns>结果列表。</returns>
    protected static async Task<List<T>> QueryAsync<T>(
        DbConnection connection,
        string sql,
        Func<DbDataReader, T> map,
        object?[]? parameters,
        CancellationToken cancellationToken,
        Action<DbCommand>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(map);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        prepare?.Invoke(command);
        for (int i = 0; i < (parameters?.Length ?? 0); i++)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{i.ToString(CultureInfo.InvariantCulture)}";
            parameter.Value = parameters![i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        List<T> results = [];
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }
        return results;
    }

    /// <summary>读一个字符串列;NULL 与越界都回落空串(元数据查询里空串比异常有用)。</summary>
    /// <param name="reader">读取器。</param>
    /// <param name="ordinal">列序号。</param>
    /// <returns>字符串。</returns>
    protected static string Str(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? "" : (reader.GetValue(ordinal)?.ToString() ?? "");
    }

    /// <summary>读一个可空字符串列(要区分"没有默认值"与"默认值是空串"时用它)。</summary>
    /// <param name="reader">读取器。</param>
    /// <param name="ordinal">列序号。</param>
    /// <returns>字符串或 <see langword="null" />。</returns>
    protected static string? StrOrNull(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();
    }

    /// <summary>读一个布尔列。各方言的"真"长得都不一样(1 / 't' / 'YES' / true),一并认。</summary>
    /// <param name="reader">读取器。</param>
    /// <param name="ordinal">列序号。</param>
    /// <returns>布尔值。</returns>
    protected static bool Bool(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }
        object value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            string s => s is "1" or "t" or "T" or "y" or "Y"
                        || s.Equals("YES", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
        };
    }

    /// <summary>读一个可空长整型列。</summary>
    /// <param name="reader">读取器。</param>
    /// <param name="ordinal">列序号。</param>
    /// <returns>长整型或 <see langword="null" />。</returns>
    protected static long? LongOrNull(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>读一个整型列。</summary>
    /// <param name="reader">读取器。</param>
    /// <param name="ordinal">列序号。</param>
    /// <returns>整型。</returns>
    protected static int Int(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把"每列一行"的查询结果按索引名/约束名归并成"每个索引一条(含有序列表)"。
    /// 四个方言的索引与外键查询都是这个形状,归并逻辑写一遍就够。
    /// </summary>
    /// <typeparam name="TKey">分组键。</typeparam>
    /// <typeparam name="TRow">行类型。</typeparam>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <param name="rows">行。</param>
    /// <param name="key">取分组键。</param>
    /// <param name="build">按组构造结果。</param>
    /// <returns>归并后的结果。</returns>
    protected static List<TResult> Fold<TKey, TRow, TResult>(
        IEnumerable<TRow> rows,
        Func<TRow, TKey> key,
        Func<TKey, IReadOnlyList<TRow>, TResult> build)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(build);
        return [.. rows.GroupBy(key).Select(g => build(g.Key, [.. g]))];
    }
}

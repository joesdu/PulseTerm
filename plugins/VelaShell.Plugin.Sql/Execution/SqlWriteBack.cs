using System.Data.Common;
using System.Globalization;
using System.Text;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>网格里改过的一格。</summary>
/// <param name="RowIndex">行号(结果集内)。</param>
/// <param name="ColumnName">列名。</param>
/// <param name="OriginalValue">原值(用于乐观并发);NULL 时为 <see langword="null" />。</param>
/// <param name="NewValue">新值;设成 NULL 时为 <see langword="null" />。</param>
internal sealed record SqlPendingEdit(int RowIndex, string ColumnName, string? OriginalValue, string? NewValue);

/// <summary>一条待执行的写回语句 + 它的参数。</summary>
/// <param name="Sql">参数化 SQL。</param>
/// <param name="Parameters">参数(按 <c>@p0</c>… 顺序)。</param>
/// <param name="Preview">给人看的预览(值已内联)。</param>
internal sealed record SqlWriteStatement(string Sql, IReadOnlyList<object?> Parameters, string Preview);

/// <summary>写回时为什么不能编辑。</summary>
/// <param name="Editable">能不能编辑。</param>
/// <param name="ReasonKey">不能编辑时的文案键。</param>
/// <param name="KeyColumns">用来定位一行的列。</param>
internal sealed record SqlEditability(bool Editable, string ReasonKey, IReadOnlyList<string> KeyColumns);

/// <summary>
/// 结果网格的写回。
/// <para>
/// <b>这里不用 SqlSugar 的字典 CRUD,而是自己拼参数化 SQL。</b> 理由是实测的三条
/// (设计文档 §5.4.3):<c>Updateable(字典)</c> 忘写 <c>WhereColumns</c> **不报错、直接生成全表 UPDATE**;
/// <c>AS(表名)</c> 这条路**没有任何转义**(实测能删表);而 PG 上还要求"SqlSugar 先手"才不炸日期。
/// 一条 UPDATE 而已,自己拼比防着这三样便宜。
/// </para>
/// <para>
/// 定位一行的顺序:主键 → 唯一索引 → 都没有就**只读**。绝不做"按全列匹配"的默认行为 ——
/// 那在有重复行的表上会一次改掉多行,而用户以为自己只改了一格。
/// </para>
/// </summary>
internal static class SqlWriteBack
{
    /// <summary>判断一张表能不能就地编辑。</summary>
    /// <param name="schema">表结构。</param>
    /// <param name="resultColumns">结果集里实际有哪些列(定位列必须都在里面)。</param>
    /// <returns>可编辑性。</returns>
    public static SqlEditability Judge(SqlTableSchema? schema, IReadOnlyList<string> resultColumns)
    {
        ArgumentNullException.ThrowIfNull(resultColumns);
        if (schema is null)
        {
            // 结果不是来自单表(自由查询、JOIN、聚合)—— 我们不知道该往哪张表写。
            return new(false, "Sql_GridReadOnlyNotATable", []);
        }
        if (!schema.TryGetRowKey(out IReadOnlyList<string> keyColumns, out string reason))
        {
            return new(false, reason, []);
        }
        // 定位列必须都在结果集里,否则拼出来的 WHERE 是残缺的。
        var present = new HashSet<string>(resultColumns, StringComparer.OrdinalIgnoreCase);
        return keyColumns.All(present.Contains)
            ? new(true, "", keyColumns)
            : new(false, "Sql_GridReadOnlyKeyNotSelected", []);
    }

    /// <summary>
    /// 把一批改动编译成 UPDATE 语句(每行一条)。
    /// <para>
    /// <b>WHERE 里除了定位列还带上被改列的原值</b> —— 这是乐观并发:
    /// 别人在你打开网格之后改过这一格的话,影响行数会是 0,我们就能如实告诉用户"这行被别人改了",
    /// 而不是把他的值悄悄盖上去。
    /// </para>
    /// </summary>
    /// <param name="pack">方言包(负责标识符转义)。</param>
    /// <param name="target">目标表。</param>
    /// <param name="schema">表结构。</param>
    /// <param name="keyColumns">定位列。</param>
    /// <param name="edits">改动。</param>
    /// <param name="rowValue">取某行某列的原值。</param>
    /// <returns>逐行的写回语句。</returns>
    public static IReadOnlyList<SqlWriteStatement> BuildUpdates(
        IDialectPack pack,
        SqlObject target,
        SqlTableSchema schema,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<SqlPendingEdit> edits,
        Func<int, string, string?> rowValue)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(rowValue);

        var writable = new HashSet<string>(
            schema.WritableColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        List<SqlWriteStatement> statements = [];
        foreach (IGrouping<int, SqlPendingEdit> row in edits.GroupBy(e => e.RowIndex))
        {
            // 生成列不能写 —— 带上它 MySQL 直接报
            // "The value specified for generated column ... is not allowed"(实测)。
            SqlPendingEdit[] changes = [.. row.Where(e => writable.Contains(e.ColumnName))];
            if (changes.Length == 0)
            {
                continue;
            }

            List<object?> parameters = [];
            var sql = new StringBuilder();
            var preview = new StringBuilder();
            string table = pack is DialectPackBase basePack ? basePack.QuoteQualified(target) : pack.QuoteIdentifier(target.Name);

            sql.Append("UPDATE ").Append(table).Append(" SET ");
            preview.Append("UPDATE ").Append(table).Append(" SET ");
            for (int i = 0; i < changes.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                    preview.Append(", ");
                }
                string column = pack.QuoteIdentifier(changes[i].ColumnName);
                sql.Append(column).Append(" = @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
                preview.Append(column).Append(" = ").Append(Literal(changes[i].NewValue));
                parameters.Add(changes[i].NewValue);
            }

            sql.Append(" WHERE ");
            preview.Append(" WHERE ");
            bool first = true;
            foreach (string key in keyColumns)
            {
                Append(sql, preview, pack, key, rowValue(row.Key, key), parameters, ref first);
            }
            // 乐观并发:被改列的原值也进 WHERE。
            foreach (SqlPendingEdit change in changes)
            {
                Append(sql, preview, pack, change.ColumnName, change.OriginalValue, parameters, ref first);
            }

            statements.Add(new(sql.ToString(), parameters, preview.ToString()));
        }
        return statements;
    }

    private static void Append(
        StringBuilder sql,
        StringBuilder preview,
        IDialectPack pack,
        string column,
        string? value,
        List<object?> parameters,
        ref bool first)
    {
        if (!first)
        {
            sql.Append(" AND ");
            preview.Append(" AND ");
        }
        first = false;
        string quoted = pack.QuoteIdentifier(column);
        if (value is null)
        {
            // NULL 不能用 `= @p`,那永远不成立 —— 一格原本是 NULL 的行会因此永远匹配不上。
            sql.Append(quoted).Append(" IS NULL");
            preview.Append(quoted).Append(" IS NULL");
            return;
        }
        sql.Append(quoted).Append(" = @p").Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
        preview.Append(quoted).Append(" = ").Append(Literal(value));
        parameters.Add(value);
    }

    /// <summary>预览用的字面量。**只给人看**,真发出去的是参数化版本。</summary>
    private static string Literal(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>执行一批写回。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="statements">语句。</param>
    /// <param name="commandTimeoutSeconds">语句超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>每条语句的影响行数。</returns>
    public static async Task<IReadOnlyList<int>> ApplyAsync(
        DbConnection connection,
        IReadOnlyList<SqlWriteStatement> statements,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(statements);

        List<int> affected = [];
        foreach (SqlWriteStatement statement in statements)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = statement.Sql;
            command.CommandTimeout = commandTimeoutSeconds;
            for (int i = 0; i < statement.Parameters.Count; i++)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{i.ToString(CultureInfo.InvariantCulture)}";
                parameter.Value = statement.Parameters[i] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            affected.Add(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }
        return affected;
    }
}

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>
/// 把驱动异常里的位置信息换算回**用户原文**里的行列(设计文档 §7.4)。
/// <para>
/// 三个方言给的原料形状完全不同,而且各有一个陷阱:
/// <list type="bullet">
///   <item><b>PostgreSQL</b> 给 <c>Position</c>(1 起的**字符**偏移,不是字节;中文注释与 CRLF 都不会让它偏),
///         但它相对的是"改写后的、单条"语句 —— 直接拿去数用户输入会指错行。
///         我们逐条发送、且不做参数改写,所以它相对的就是这一条语句的文本,
///         再加上这条语句的起始行就对了。</item>
///   <item><b>SQL Server</b> 给 <c>LineNumber</c>,相对**发出去的那一批**;
///         我们一次发一条,所以要把这条语句的起始行加回去。
///         但它**不保证指向出错 token 那一行**(同一个 207 在 select 列表里报 token 行、
///         在 where 子句里报语句起始行),所以对 SQL Server 只承诺**定位到语句**,不承诺列。</item>
///   <item><b>MySQL</b> 没有结构化位置,但语法错的 Message 里带 <c>at line N</c>,可以正则抠出来。</item>
///   <item><b>SQLite</b> 什么都没有。</item>
/// </list>
/// </para>
/// </summary>
internal static class SqlErrorLocator
{
    private static readonly Regex MySqlLine = new(@"at line (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>定位。</summary>
    /// <param name="error">驱动异常(未翻译的原始异常)。</param>
    /// <param name="statement">出错的那条语句。</param>
    /// <param name="dialect">方言。</param>
    /// <returns>用户原文里的行列;拿不到时两者都是 <see langword="null" />。</returns>
    public static (int? Line, int? Column) Locate(Exception? error, SqlStatement statement, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (error is null)
        {
            return (null, null);
        }

        return dialect switch
        {
            SqlDialect.PostgreSql => FromPostgres(error, statement),
            SqlDialect.SqlServer => FromSqlServer(error, statement),
            SqlDialect.MySql => FromMySql(error, statement),
            // SQLite 的异常里没有任何位置信息 —— 如实返回"不知道",
            // 界面就只高亮整条语句,而不是瞎指一行。
            _ => (null, null)
        };
    }

    private static (int?, int?) FromPostgres(Exception error, SqlStatement statement)
    {
        Exception? pg = Find(error, "Npgsql.PostgresException");
        if (pg is null || ReadInt(pg, "Position") is not { } position || position <= 0)
        {
            return (null, null);
        }
        // Position 是 1 起的字符偏移。换算成语句内的行列,再叠加语句在整段文本里的起点。
        (int lineInStatement, int columnInStatement) = OffsetToLineColumn(statement.Text, position - 1);
        int line = statement.StartLine + lineInStatement - 1;
        int column = lineInStatement == 1 ? statement.StartColumn + columnInStatement - 1 : columnInStatement;
        return (line, column);
    }

    private static (int?, int?) FromSqlServer(Exception error, SqlStatement statement)
    {
        Exception? sql = Find(error, "Microsoft.Data.SqlClient.SqlException");
        if (sql is null || ReadInt(sql, "LineNumber") is not { } lineNumber || lineNumber <= 0)
        {
            return (null, null);
        }
        // LineNumber 相对我们发出去的那一条语句(1 起)。不承诺列 —— 它不保证指向出错 token。
        return (statement.StartLine + lineNumber - 1, null);
    }

    private static (int?, int?) FromMySql(Exception error, SqlStatement statement)
    {
        Exception? my = Find(error, "MySqlConnector.MySqlException") ?? error;
        Match match = MySqlLine.Match(my.Message ?? "");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
        {
            return (null, null);
        }
        return (statement.StartLine + line - 1, null);
    }

    /// <summary>把语句内的字符偏移换算成行列(都是 1 起)。<c>\r</c> 算一个字符,与 PG 的口径一致。</summary>
    internal static (int Line, int Column) OffsetToLineColumn(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        int line = 1;
        int column = 1;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    private static Exception? Find(Exception ex, string fullName)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().FullName, fullName, StringComparison.Ordinal))
            {
                return current;
            }
        }
        return null;
    }

    private static int? ReadInt(Exception ex, string property)
    {
        object? value = ex.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}

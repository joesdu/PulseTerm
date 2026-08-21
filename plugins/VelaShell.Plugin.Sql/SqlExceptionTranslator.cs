using System.Collections;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 驱动异常 → SDK 的 <c>Protocol*</c> 四类。判据全部来自真机取得的错误码(设计文档 §5.3),
/// 每一条都有实测出处。
/// <para>
/// <b>为什么全程用反射读驱动异常的属性,而不直接引类型</b>:
/// ① PG 侧是硬纪律 —— 插件永不直接引用 Npgsql 类型,否则会抢在 SqlSugar 之前触发它的静态初始化,
///    把写入路径打死(见 <see cref="SqlSugarGate" /> 纪律二);
/// ② 顺带的好处是连 MySQL 时不会把 SqlClient 拖进来。
/// </para>
/// <para>
/// <b>两条不要按类型/文案匹配的警告</b>(实测):同一个错误,Npgsql 6.x/7.x 抛
/// <c>InvalidCastException</c>、8.0 起改抛 <c>ArgumentException</c>;文案还会随服务器语言变。
/// 所以判据一律建在**错误码**上,并给每条配快照测试。
/// </para>
/// </summary>
internal static class SqlExceptionTranslator
{
    /// <summary>翻译一个连接期/执行期异常。认不出的**原样返回**——把可读信息埋进"未知错误"只会让排障更难。</summary>
    /// <param name="ex">原始异常。</param>
    /// <param name="dialect">方言。</param>
    /// <param name="endpoint">端点描述(用于文案)。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>翻译后的异常。</returns>
    public static Exception Translate(Exception ex, SqlDialect dialect, string endpoint, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(loc);

        if (ex is OperationCanceledException)
        {
            return ex;
        }

        // SqlSugar 在"打开连接"这一步会把驱动异常整个吞掉(SqlSugarException 且 InnerException 为 null),
        // 错误码只剩在中文文案里,MSSQL 侧连 18456 都没了 —— 所以本插件一律自己 Open。
        // 万一还是走到了这条路(比如 SqlSugar 内部某处先开了连接),如实说明拿不到错误码,
        // **绝不去解析它的 Message 文本**:那是本地化的,服务器语言变了就失效。
        if (IsSqlSugarWrapper(ex))
        {
            return new ProtocolConnectionException(loc.Format("Sql_ConnectFailedOpaque", endpoint, Describe(ex)), ex);
        }

        Exception? translated = dialect switch
        {
            SqlDialect.PostgreSql => TranslatePostgres(ex, endpoint, loc),
            SqlDialect.SqlServer => TranslateSqlServer(ex, endpoint, loc),
            SqlDialect.MySql => TranslateMySql(ex, endpoint, loc),
            SqlDialect.Sqlite => TranslateSqlite(ex, loc),
            _ => null
        };
        return translated ?? TranslateTransport(ex, endpoint, loc) ?? ex;
    }

    /// <summary>
    /// 判决结果。**判据与异常构造分开**,是为了让顺序规则可以单测 ——
    /// 驱动的异常类型都是 sealed 且没有公开构造,拿真异常来测就只能连真库,
    /// 而"4060 要排在 18456 前面"这种规则恰恰必须在没有服务器时也守得住。
    /// </summary>
    internal enum SqlFailureKind
    {
        /// <summary>认不出来。</summary>
        Unknown,

        /// <summary>认证失败(宿主会重弹登录框)。</summary>
        Authentication,

        /// <summary>连不上 / 传输层失败。</summary>
        Connection,

        /// <summary>库打不开(不是认证失败——报错了会白弹一次登录框)。</summary>
        DatabaseMissing
    }

    /// <summary>PostgreSQL 判据(纯函数,便于单测)。</summary>
    /// <param name="sqlState">`PostgresException.SqlState`。</param>
    /// <returns>判决。</returns>
    internal static SqlFailureKind DecidePostgres(string? sqlState)
    {
        string state = sqlState ?? "";
        // 28P01 = 密码错 / 角色不存在(Severity=FATAL, Routine=auth_failed)。整个 28 类都是认证域。
        if (state.StartsWith("28", StringComparison.Ordinal))
        {
            return SqlFailureKind.Authentication;
        }
        // 3D000(库不存在)**不是**认证失败 —— 用户该改的是"数据库"那一栏。
        // 42501(权限不足)同理,但它是普通业务错误,原文透出即可,不在这里拦。
        return state is "3D000" ? SqlFailureKind.DatabaseMissing : SqlFailureKind.Unknown;
    }

    /// <summary>SQL Server 判据(纯函数)。<b>顺序不能换</b>。</summary>
    /// <param name="number">`SqlException.Number`。</param>
    /// <param name="errorClass">`SqlException.Class`。</param>
    /// <param name="errorNumbers">`SqlException.Errors` 里的全部错误号。</param>
    /// <returns>判决。</returns>
    internal static SqlFailureKind DecideSqlServer(int number, int errorClass, IReadOnlyList<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        // 「库打不开」(4060) 的 Errors 集合里**同时含 18456** —— 先判 18456 会把它误报成密码错,
        // 白弹一次登录框,而用户真正该改的是"数据库"那一栏。这一条实测逐位复现,顺序是判据的一部分。
        if (errorNumbers.Contains(4060))
        {
            return SqlFailureKind.DatabaseMissing;
        }
        if (number == 18456 || errorNumbers.Contains(18456))
        {
            return SqlFailureKind.Authentication;
        }
        // Class 20 一律算传输层(号随传输层变:258 / 233 / -1983577849 …)。
        // 判据不依赖具体号,正是为了在真 TCP 实例上也成立(本轮只有 LocalDB)。
        return errorClass == 20 ? SqlFailureKind.Connection : SqlFailureKind.Unknown;
    }

    /// <summary>MySQL 判据(纯函数)。</summary>
    /// <param name="number">`MySqlException.Number`。</param>
    /// <returns>判决。</returns>
    internal static SqlFailureKind DecideMySql(int number) => number switch
    {
        1045 => SqlFailureKind.Authentication,
        // 1042 是个大杂烩:端口不通 / DNS 失败 / 连接超时 / 证书不受信全共用它,
        // 只能靠 InnerException 二次分流 —— 但无论分到哪一档,它都属于"连不上"。
        1042 or 0 => SqlFailureKind.Connection,
        _ => SqlFailureKind.Unknown
    };

    private static Exception? TranslatePostgres(Exception ex, string endpoint, Loc loc) =>
        FindByTypeName(ex, "Npgsql.PostgresException") is { } pg
            ? Materialize(DecidePostgres(ReadString(pg, "SqlState")), pg, ex, endpoint, loc)
            : null;

    private static Exception? TranslateSqlServer(Exception ex, string endpoint, Loc loc) =>
        FindByTypeName(ex, "Microsoft.Data.SqlClient.SqlException") is { } sql
            ? Materialize(
                DecideSqlServer(ReadInt(sql, "Number") ?? 0, ReadInt(sql, "Class") ?? 0, ReadErrorNumbers(sql)),
                sql, ex, endpoint, loc)
            : null;

    private static Exception? TranslateMySql(Exception ex, string endpoint, Loc loc) =>
        FindByTypeName(ex, "MySqlConnector.MySqlException") is { } my
            ? Materialize(DecideMySql(ReadInt(my, "Number") ?? 0), my, ex, endpoint, loc)
            : null;

    /// <summary>判决 → SDK 异常。</summary>
    private static Exception? Materialize(
        SqlFailureKind kind,
        Exception driverException,
        Exception original,
        string endpoint,
        Loc loc) => kind switch
        {
            SqlFailureKind.Authentication =>
                new ProtocolAuthenticationException(loc.Format("Sql_AuthFailed", Describe(driverException))),
            SqlFailureKind.DatabaseMissing =>
                new ProtocolConnectionException(loc.Format("Sql_DatabaseMissing", Describe(driverException)), original),
            SqlFailureKind.Connection =>
                new ProtocolConnectionException(
                    loc.Format("Sql_ConnectFailed", endpoint, Describe(driverException)), original),
            _ => null
        };

    /// <summary>SQLite:文件型,没有认证,失败基本都是路径/权限/文件损坏。</summary>
    private static Exception? TranslateSqlite(Exception ex, Loc loc) =>
        FindByTypeName(ex, "Microsoft.Data.Sqlite.SqliteException") is { } lite
            ? new ProtocolConnectionException(loc.Format("Sql_SqliteOpenFailed", Describe(lite)), ex)
            : ex is (UnauthorizedAccessException or IOException)
                ? new ProtocolConnectionException(loc.Format("Sql_SqliteOpenFailed", Describe(ex)), ex)
                : null;

    /// <summary>方言无关的传输层失败(端口不通、主机不存在、连接超时)。</summary>
    private static Exception? TranslateTransport(Exception ex, string endpoint, Loc loc)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException or TimeoutException)
            {
                return new ProtocolConnectionException(loc.Format("Sql_ConnectFailed", endpoint, Describe(current)), ex);
            }
        }
        return null;
    }

    /// <summary>SqlSugar 自己包出来的异常(它把真实装载/连接失败吞了)。</summary>
    private static bool IsSqlSugarWrapper(Exception ex) =>
        string.Equals(ex.GetType().FullName, "SqlSugar.SqlSugarException", StringComparison.Ordinal);

    /// <summary>
    /// provider 装不出来时 SqlSugar 抛的也是 <c>SqlSugarException: Not Found ….dll</c> ——
    /// <b>不是</b> <c>FileNotFoundException</c>,而且那个 dll 名可能是别人的(静态污染,§3.3)。
    /// 所以这一条单独给出口:翻成"该方言未内置",文案走插件自带的映射表,永不透传原文。
    /// </summary>
    /// <param name="ex">异常。</param>
    /// <param name="dialect">方言。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>翻译后的异常;不是这一类时返回 <see langword="null" />。</returns>
    public static Exception? TranslateProviderMissing(Exception ex, SqlDialect dialect, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(loc);
        bool missing =
            (IsSqlSugarWrapper(ex) && ex.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            || ex is FileNotFoundException;
        return missing
            ? new ProtocolUnsupportedException(loc.Format("Sql_DialectNotBundled", SqlDialects.Of(dialect).DisplayName))
            : null;
    }

    private static Exception? FindByTypeName(Exception ex, string fullName)
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

    private static string? ReadString(Exception ex, string property) =>
        ex.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex) as string;

    private static int? ReadInt(Exception ex, string property)
    {
        object? value = ex.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary><c>SqlException.Errors</c> 里的全部错误号(4060 那条判据要看整个集合,不能只看 Number)。</summary>
    private static IReadOnlyList<int> ReadErrorNumbers(Exception ex)
    {
        if (ex.GetType().GetProperty("Errors", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex)
            is not IEnumerable errors)
        {
            return [];
        }
        List<int> numbers = [];
        foreach (object? error in errors)
        {
            if (error is null)
            {
                continue;
            }
            object? number = error.GetType()
                .GetProperty("Number", BindingFlags.Public | BindingFlags.Instance)?.GetValue(error);
            if (number is not null)
            {
                numbers.Add(Convert.ToInt32(number, CultureInfo.InvariantCulture));
            }
        }
        return numbers;
    }

    private static string Describe(Exception ex) => ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
}

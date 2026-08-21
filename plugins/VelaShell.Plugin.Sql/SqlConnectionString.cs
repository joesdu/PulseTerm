using System.Data.Common;
using System.Globalization;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 连接串装配。每个方言一份,键名与档位翻译全部按实测来 —— 这一层的错基本都是静默的:
/// 键名写错驱动直接忽略(超时不生效),档位翻错要么连不上、要么以为加密了其实没有。
/// <para>
/// 用 <see cref="DbConnectionStringBuilder" />(BCL,与驱动无关)负责转义,
/// 而**不用各驱动自己的 Builder** —— PG 侧尤其不能碰,理由见 <see cref="SqlSugarGate" /> 的纪律二:
/// 插件永不直接引用 Npgsql 类型。
/// </para>
/// </summary>
internal static class SqlConnectionString
{
    /// <summary>装配一条连接串。</summary>
    /// <param name="settings">设置。</param>
    /// <param name="host">主机(走隧道时已是宿主给的本地端点);SQLite 上是文件路径。</param>
    /// <param name="port">端口(同上)。</param>
    /// <param name="username">用户名。</param>
    /// <param name="password">口令。</param>
    /// <returns>连接串。</returns>
    public static string Build(SqlSettings settings, string host, int port, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Dialect switch
        {
            SqlDialect.MySql => MySql(settings, host, port, username, password),
            SqlDialect.PostgreSql => PostgreSql(settings, host, port, username, password),
            SqlDialect.SqlServer => SqlServer(settings, host, port, username, password),
            SqlDialect.Oracle => Oracle(settings, host, port, username, password),
            SqlDialect.Sqlite => Sqlite(settings),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Dialect, "未登记的方言。")
        };
    }

    private static string MySql(SqlSettings s, string host, int port, string user, string password)
    {
        var b = new DbConnectionStringBuilder
        {
            ["Server"] = host,
            ["Port"] = Num(port),
            ["Uid"] = user,
            ["Pwd"] = password,
            // 实测:驼峰无空格的 connectTimeout 驱动**不认**(3ms 就被拒);
            // 认的是 "Connect Timeout" / "Connection Timeout" / "ConnectionTimeout"。
            ["Connect Timeout"] = Num(s.ConnectTimeoutSeconds),
            ["SslMode"] = s.SslMode switch
            {
                SqlSslMode.Disabled => "None",
                SqlSslMode.Required => "Required",
                SqlSslMode.VerifyCa => "VerifyCA",
                _ => "Preferred"
            },
            // **管理工具必须关掉它。** 驱动默认 true:任何 TINYINT(1) 列被当 bool 读出来,
            // 值 42 渲染成 True —— 那是数据失真,不是显示偏好。
            ["TreatTinyAsBoolean"] = "false",
            // 不开的话用户手敲的 `SET @x := 1; SELECT @x` 直接报错,
            // 而且报的是"参数未定义"这种把人往参数化上引的误导消息。
            ["AllowUserVariables"] = s.MySqlAllowUserVariables ? "true" : "false"
        };
        if (!string.IsNullOrWhiteSpace(s.Database))
        {
            b["Database"] = s.Database;
        }
        if (s.MySqlZeroDate == MySqlZeroDatePolicy.Convert)
        {
            b["ConvertZeroDateTime"] = "true";
        }
        // 注意这里**没有 CharSet**:实测该键已被驱动标为 Obsolete 并完全忽略,
        // 会话字符集恒为 utf8mb4。写进去只会让人以为它起作用。
        return b.ConnectionString;
    }

    private static string PostgreSql(SqlSettings s, string host, int port, string user, string password)
    {
        var b = new DbConnectionStringBuilder
        {
            ["Host"] = host,
            ["Port"] = Num(port),
            ["Username"] = user,
            ["Password"] = password,
            ["Timeout"] = Num(s.ConnectTimeoutSeconds),
            ["SSL Mode"] = s.SslMode switch
            {
                SqlSslMode.Disabled => "Disable",
                SqlSslMode.Required => "Require",
                // Npgsql 5.0.18 只有 Disable/Prefer/Require —— VerifyCA 是升到 10.0.3 才有的档位。
                // 这也是 csproj 里显式覆盖 Npgsql 版本的理由之一。
                SqlSslMode.VerifyCa => "VerifyCA",
                _ => "Prefer"
            },
            // 默认被 Npgsql 抹掉,加上才拿得到 "Key (id)=(1) already exists." ——
            // 约束冲突提示里最有用的就是这一行(§7.8)。
            ["Include Error Detail"] = "true"
        };
        if (!string.IsNullOrWhiteSpace(s.Database))
        {
            b["Database"] = s.Database;
        }
        if (!string.IsNullOrWhiteSpace(s.Schema))
        {
            // **这是 DbMaintenance 能看到自定义 schema 的唯一开关**(§3.5 实测):
            // 不设它,GetTableInfoList 只列 public,传 "app.t" 一律返回空且不抛异常。
            b["Search Path"] = s.Schema;
        }
        return b.ConnectionString;
    }

    private static string SqlServer(SqlSettings s, string host, int port, string user, string password)
    {
        var b = new DbConnectionStringBuilder
        {
            // LocalDB 与命名实例走 `(localdb)\Name` / `HOST\INSTANCE` 这种形态,此时不该再拼端口。
            ["Server"] = host.Contains('\\', StringComparison.Ordinal) || host.StartsWith("(local", StringComparison.OrdinalIgnoreCase)
                ? host
                : $"{host},{Num(port)}",
            ["Connect Timeout"] = Num(s.ConnectTimeoutSeconds),
            ["Encrypt"] = s.SslMode == SqlSslMode.Disabled ? "false" : "true",
            ["TrustServerCertificate"] = s.SslMode == SqlSslMode.VerifyCa ? "false" : "true",
            // 驱动默认会**静默重连**被掐断的空闲连接(ConnectRetryCount 默认 1),
            // 于是"连接断了"在 SQL Server 上多数时候用户根本看不见。
            // 关掉它,重连提示由插件自己掌控(§5.2)。
            ["ConnectRetryCount"] = "0"
        };
        if (string.IsNullOrWhiteSpace(user))
        {
            b["Integrated Security"] = "true";
        }
        else
        {
            b["User ID"] = user;
            b["Password"] = password;
        }
        if (!string.IsNullOrWhiteSpace(s.Database))
        {
            b["Database"] = s.Database;
        }
        return b.ConnectionString;
    }

    private static string Oracle(SqlSettings s, string host, int port, string user, string password)
    {
        // Oracle 本轮**没有真机**(见设计文档附录 B),下面这份装配是按官方文档写的,
        // 尚未验证。第一次接上真 Oracle 时这里是首要复核点。
        string service = string.IsNullOrWhiteSpace(s.OracleServiceName) ? s.Database : s.OracleServiceName;
        var b = new DbConnectionStringBuilder
        {
            ["Data Source"] =
                $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={Num(port)}))(CONNECT_DATA=(SERVICE_NAME={service})))",
            ["User Id"] = user,
            ["Password"] = password,
            ["Connection Timeout"] = Num(s.ConnectTimeoutSeconds)
        };
        if (!string.Equals(s.OracleConnectAs, "NORMAL", StringComparison.OrdinalIgnoreCase))
        {
            b["DBA Privilege"] = s.OracleConnectAs;
        }
        return b.ConnectionString;
    }

    private static string Sqlite(SqlSettings s)
    {
        var b = new DbConnectionStringBuilder { ["Data Source"] = s.Database };
        if (s.SqliteReadOnlyOpen)
        {
            b["Mode"] = "ReadOnly";
        }
        return b.ConnectionString;
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}

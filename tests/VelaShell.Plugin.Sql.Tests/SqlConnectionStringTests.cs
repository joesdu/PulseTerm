using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 连接串装配。这一层的错基本都是**静默**的:键名写错驱动直接忽略(超时不生效),
/// 档位翻错要么连不上、要么以为加密了其实没有。每条断言都对着调研阶段的一个实测结论。
/// </summary>
[TestClass]
public sealed class SqlConnectionStringTests
{
    /// <summary>
    /// §5.1 实测:驼峰无空格的 <c>connectTimeout</c> 驱动**不认**(3 ms 就被拒),
    /// 认的是带空格的 <c>Connect Timeout</c>。这条错了的表现是"超时设了没用"。
    /// </summary>
    [TestMethod]
    public void MySQL_用驱动认得的超时键名()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.MySql, new() { ["connectTimeout"] = "42" });

        Assert.IsTrue(built.ContainsKey("Connect Timeout"), "必须用带空格的键名,驼峰那个驱动不认。");
        Assert.AreEqual("42", built["Connect Timeout"].ToString());
    }

    /// <summary>
    /// §5.1 实测:<c>TreatTinyAsBoolean</c> 默认 true,于是任何 <c>TINYINT(1)</c> 列被当 bool 读出来,
    /// **值 42 渲染成 True**。对管理工具这是数据失真,必须关掉。
    /// </summary>
    [TestMethod]
    public void MySQL_关掉把tinyint当布尔()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.MySql, []);

        Assert.AreEqual("false", built["TreatTinyAsBoolean"].ToString(),
            "不关掉的话 tinyint(1) 里的 42 会显示成 True。");
    }

    /// <summary>
    /// §5.1 实测:不开 <c>AllowUserVariables</c>,用户手敲的 <c>SET @x := 1</c> 直接报错,
    /// 而且报的是"参数未定义"这种把人往参数化上引的误导消息。
    /// </summary>
    [TestMethod]
    public void MySQL_默认允许用户变量()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.MySql, []);

        Assert.AreEqual("true", built["AllowUserVariables"].ToString());
    }

    /// <summary>
    /// §5.1 实测:MySqlConnector 已把 <c>CharacterSet</c> 标为 Obsolete 并完全忽略,
    /// 会话字符集恒为 utf8mb4。连接串里塞它只会让人以为它起作用。
    /// </summary>
    [TestMethod]
    public void MySQL_不写无效的字符集键()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.MySql, []);

        Assert.IsFalse(built.ContainsKey("CharSet"));
        Assert.IsFalse(built.ContainsKey("CharacterSet"));
    }

    /// <summary>
    /// §7.8 实测:PG 的 <c>Detail</c> 默认被 Npgsql 抹掉,加上 <c>Include Error Detail</c>
    /// 才拿得到 <c>Key (id)=(1) already exists.</c> —— 约束冲突提示里最有用的就是这一行。
    /// </summary>
    [TestMethod]
    public void PostgreSQL_打开错误详情()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.PostgreSql, []);

        Assert.AreEqual("true", built["Include Error Detail"].ToString());
    }

    /// <summary>
    /// §3.5 实测:连接串的 <c>Search Path</c> 是 <c>DbMaintenance</c> 能看见自定义 schema 的
    /// **唯一**开关 —— 不设它,传 <c>"app.t"</c> 一律返回空且不抛异常。
    /// </summary>
    [TestMethod]
    public void PostgreSQL_schema写进SearchPath()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.PostgreSql, new() { ["schema"] = "app" });

        Assert.AreEqual("app", built["Search Path"].ToString());
    }

    /// <summary>
    /// §5.2 实测:SqlClient 默认会**静默重连**被掐断的空闲连接(<c>ConnectRetryCount</c> 默认 1),
    /// 于是"连接断了"在 SQL Server 上多数时候用户根本看不见。关掉它由插件接管重连提示。
    /// </summary>
    [TestMethod]
    public void SQLServer_关掉驱动的静默重连()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.SqlServer, []);

        Assert.AreEqual("0", built["ConnectRetryCount"].ToString());
    }

    /// <summary>LocalDB 与命名实例形态下不该再拼端口,否则连不上。</summary>
    [TestMethod]
    public void SQLServer_命名实例不拼端口()
    {
        DbConnectionStringBuilder named = Build(SqlDialect.SqlServer, [], host: @"(localdb)\VelaSpike");
        Assert.AreEqual(@"(localdb)\VelaSpike", named["Server"].ToString());

        DbConnectionStringBuilder plain = Build(SqlDialect.SqlServer, [], host: "db.example.com", port: 1433);
        Assert.AreEqual("db.example.com,1433", plain["Server"].ToString());
    }

    /// <summary>没有用户名时走集成认证,而不是发一个空账号上去。</summary>
    [TestMethod]
    public void SQLServer_无用户名时走集成认证()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.SqlServer, [], username: "");

        Assert.AreEqual("true", built["Integrated Security"].ToString());
        Assert.IsFalse(built.ContainsKey("User ID"));
    }

    /// <summary>TLS 四档在各方言上的翻译。档位翻错的后果是"以为加密了其实没有"。</summary>
    [TestMethod]
    public void TLS档位_逐方言翻译正确()
    {
        Assert.AreEqual("None", Build(SqlDialect.MySql, Ssl("Disabled"))["SslMode"].ToString());
        Assert.AreEqual("Required", Build(SqlDialect.MySql, Ssl("Required"))["SslMode"].ToString());
        Assert.AreEqual("VerifyCA", Build(SqlDialect.MySql, Ssl("VerifyCa"))["SslMode"].ToString());

        Assert.AreEqual("Disable", Build(SqlDialect.PostgreSql, Ssl("Disabled"))["SSL Mode"].ToString());
        Assert.AreEqual("Prefer", Build(SqlDialect.PostgreSql, Ssl("Preferred"))["SSL Mode"].ToString());

        // SQL Server 用 Encrypt + TrustServerCertificate 两个开关表达同一件事。
        DbConnectionStringBuilder verify = Build(SqlDialect.SqlServer, Ssl("VerifyCa"));
        Assert.AreEqual("true", verify["Encrypt"].ToString());
        Assert.AreEqual("false", verify["TrustServerCertificate"].ToString());
    }

    /// <summary>SQLite 的只读打开:不改文件、也不生成 -wal/-shm。</summary>
    [TestMethod]
    public void SQLite_只读打开()
    {
        var settings = SqlSettings.From(
            new WorkspaceConnectRequest
            {
                SessionId = "t",
                Host = @"C:\data\app.db",
                Port = 1,
                Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["sqliteReadOnlyOpen"] = "true" }
            },
            SqlDialect.Sqlite);

        var built = new DbConnectionStringBuilder
        {
            ConnectionString = SqlConnectionString.Build(settings, @"C:\data\app.db", 1, "", "")
        };

        Assert.AreEqual(@"C:\data\app.db", built["Data Source"].ToString());
        Assert.AreEqual("ReadOnly", built["Mode"].ToString());
    }

    /// <summary>值里的分号与引号必须被正确转义,否则密码带分号就会连不上(而且报错莫名其妙)。</summary>
    [TestMethod]
    public void 口令里的分号被正确转义()
    {
        DbConnectionStringBuilder built = Build(SqlDialect.MySql, [], password: "p;a\"ss'word");

        Assert.AreEqual("p;a\"ss'word", built["Pwd"].ToString());
    }

    private static Dictionary<string, string> Ssl(string mode) =>
        new(StringComparer.Ordinal) { ["ssl"] = mode };

    private static DbConnectionStringBuilder Build(
        SqlDialect dialect,
        Dictionary<string, string> settings,
        string host = "127.0.0.1",
        int port = 1234,
        string username = "u",
        string password = "p")
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "t",
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Settings = settings
        };
        SqlSettings parsed = SqlSettings.From(request, dialect);
        return new() { ConnectionString = SqlConnectionString.Build(parsed, host, port, username, password) };
    }
}

using VelaShell.Plugin.Sql;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 打真实服务器的连通性集成测试 —— <b>M0 的验收标准</b>:连得上、探得活、重连得回来、关得干净。
/// <para>
/// 按仓库惯例**按环境早退跳过**:本机没有对应服务器时报 Inconclusive 而不是失败。
/// 全程只发只读语句,不建表、不写数据 —— 这几台是调研期留下的临时实例,
/// 但"集成测试不改别人的库"这条纪律与它是不是临时的无关。
/// </para>
/// <para>
/// 起库脚本见 docs/SqlSugar数据库管理插件调研与设计.md 附录 A。
/// 环境变量可覆盖:<c>VELASQL_PG</c> / <c>VELASQL_MYSQL</c> / <c>VELASQL_MSSQL</c>(值为 host:port 或实例名)。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlConnectivityIntegrationTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>SQLite 是唯一一个**永远**该通过的:它进程内跑,没有任何外部依赖。</summary>
    [TestMethod]
    public async Task SQLite_连得上并且探得活()
    {
        string file = Path.Combine(Path.GetTempPath(), $"velasql-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlConnection connection = await OpenAsync(SqlDialect.Sqlite, file, 1, "", "");

            int latency = await connection.PingAsync();

            Assert.IsTrue(latency >= 0);
            Assert.AreEqual(SqlDialect.Sqlite, connection.Dialect);
            // 顺带守住 csproj 里那次 SQLitePCLRaw 升版:原生库换版本是运行期才暴露的那类改动,
            // 这条用例通过就说明 e_sqlite3 3.0.x 与 Microsoft.Data.Sqlite 10.0.9 配得上。
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>SQLite 只读打开:文件不存在时应当如实失败,而不是**悄悄建一个空库**。</summary>
    [TestMethod]
    public async Task SQLite_只读打开不存在的文件时如实失败()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"velasql-missing-{Guid.NewGuid():N}.db");

        await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(async () =>
        {
            await using SqlConnection _ = await OpenAsync(
                SqlDialect.Sqlite, missing, 1, "", "",
                new() { ["sqliteReadOnlyOpen"] = "true" });
        });

        Assert.IsFalse(File.Exists(missing), "只读打开失败之后不该在磁盘上留下一个空库。");
    }

    /// <summary>PostgreSQL 18.1(调研期起的临时集群,端口 55432)。</summary>
    [TestMethod]
    public async Task PostgreSQL_连得上并且探得活()
    {
        (string host, int port) = Endpoint("VELASQL_PG", "127.0.0.1", 55432);
        await using SqlConnection? connection = await TryOpenAsync(
            SqlDialect.PostgreSql, host, port, "postgres", "velaspike",
            new() { ["database"] = "postgres" });
        if (connection is null)
        {
            Assert.Inconclusive($"没有可用的 PostgreSQL({host}:{port});起库脚本见设计文档附录 A。");
            return;
        }

        int latency = await connection.PingAsync();

        Assert.IsTrue(latency >= 0);
        StringAssert.StartsWith(connection.Info.ServerVersion, "1", "PG 18.x 的版本号应以 1 开头。");
    }

    /// <summary>MySQL 8.4(podman 容器,端口 13306)。</summary>
    [TestMethod]
    public async Task MySQL_连得上并且探得活()
    {
        (string host, int port) = Endpoint("VELASQL_MYSQL", "127.0.0.1", 13306);
        await using SqlConnection? connection = await TryOpenAsync(
            SqlDialect.MySql, host, port, "root", "velaspike", []);
        if (connection is null)
        {
            Assert.Inconclusive($"没有可用的 MySQL({host}:{port});起库脚本见设计文档附录 A。");
            return;
        }

        int latency = await connection.PingAsync();

        Assert.IsTrue(latency >= 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(connection.Info.ServerVersion));
    }

    /// <summary>SQL Server(LocalDB 实例 VelaSpike)。</summary>
    [TestMethod]
    public async Task SQLServer_连得上并且探得活()
    {
        string instance = System.Environment.GetEnvironmentVariable("VELASQL_MSSQL") ?? @"(localdb)\VelaSpike";
        await using SqlConnection? connection = await TryOpenAsync(
            SqlDialect.SqlServer, instance, 1433, "", "", new() { ["database"] = "master" });
        if (connection is null)
        {
            Assert.Inconclusive($"没有可用的 SQL Server({instance});起库脚本见设计文档附录 A。");
            return;
        }

        int latency = await connection.PingAsync();

        Assert.IsTrue(latency >= 0);
    }

    /// <summary>
    /// 重连:关掉再打开,同一条连接对象继续可用。这是宿主标签页上"重连"按钮的落点。
    /// </summary>
    [TestMethod]
    public async Task 重连之后连接仍然可用()
    {
        string file = Path.Combine(Path.GetTempPath(), $"velasql-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlConnection connection = await OpenAsync(SqlDialect.Sqlite, file, 1, "", "");
            _ = await connection.PingAsync();

            await connection.ReopenAsync();

            int latency = await connection.PingAsync();
            Assert.IsTrue(latency >= 0, "重连之后必须还能发语句。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// 认证失败要翻成 <see cref="ProtocolAuthenticationException" />,宿主才会**重弹登录框**。
    /// 翻成"连接失败"的代价是:用户去查网络、查防火墙、查跳板机,而其实只是密码打错了。
    /// </summary>
    [TestMethod]
    public async Task 密码错要翻成认证失败而不是连接失败()
    {
        (string host, int port) = Endpoint("VELASQL_PG", "127.0.0.1", 55432);
        if (await TryOpenAsync(SqlDialect.PostgreSql, host, port, "postgres", "velaspike", []) is not { } probe)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL。");
            return;
        }
        await probe.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ProtocolAuthenticationException>(async () =>
        {
            await using SqlConnection _ = await OpenAsync(
                SqlDialect.PostgreSql, host, port, "postgres", "definitely-not-the-password");
        });
    }

    /// <summary>连不上的端点要翻成 <see cref="ProtocolConnectionException" />(而不是原样漏出驱动异常)。</summary>
    [TestMethod]
    public async Task 端口不通要翻成连接失败()
    {
        await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(async () =>
        {
            await using SqlConnection _ = await OpenAsync(
                SqlDialect.PostgreSql, "127.0.0.1", 59999, "u", "p",
                new() { ["connectTimeout"] = "2" });
        });
    }

    private static async Task<SqlConnection> OpenAsync(
        SqlDialect dialect,
        string host,
        int port,
        string username,
        string password,
        Dictionary<string, string>? settings = null)
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "it",
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Settings = settings ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
        return await SqlConnection.ConnectAsync(
            SqlSettings.From(request, dialect), host, port, username, password, Localization, onSql: null);
    }

    private static async Task<SqlConnection?> TryOpenAsync(
        SqlDialect dialect,
        string host,
        int port,
        string username,
        string password,
        Dictionary<string, string>? settings)
    {
        try
        {
            return await OpenAsync(dialect, host, port, username, password, settings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string Host, int Port) Endpoint(string variable, string defaultHost, int defaultPort)
    {
        string? configured = System.Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return (defaultHost, defaultPort);
        }
        string[] parts = configured.Split(':', 2);
        return parts.Length == 2 && int.TryParse(parts[1], out int port) ? (parts[0], port) : (configured, defaultPort);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉不该让测试失败。
        }
    }
}

using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// **端到端验收**:开一条真会话 → 装载对象树 → 执行查询 → 读结果 → 取消长查询。
/// <para>
/// 前面那些测试各自证明一块零件是对的;这一组证明它们**接起来能用** ——
/// 而"能用"正是这个插件唯一有意义的验收口径。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlEndToEndTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>SQLite:全链路。它没有外部依赖,所以**永远**该通过。</summary>
    [TestMethod]
    public async Task SQLite_全链路()
    {
        string file = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenSessionAsync(SqlDialect.Sqlite, file, 1, "", "", []);
            await SeedAsync(session, """
                create table orders(
                  id integer primary key autoincrement,
                  name text not null,
                  amount numeric(12,2),
                  memo text,
                  blob_col blob
                )
                """);
            await SeedAsync(session, """
                insert into orders(name, amount, memo, blob_col) values
                  ('张三', 39.90, null, x'01020304'),
                  ('李四', 12.00, '', null)
                """);

            // ① 对象树装得出来。
            var tree = new Ui.SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(tree.Roots.Count > 0, "SQLite 没有库也没有 schema,根应当直接是分类节点。");

            Ui.SqlTreeNode tables = tree.Roots[0];
            await tables.LoadAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(tables.Children.Any(c => c.Title == "orders"), "对象树里要看得见 orders。");

            // ② 列展开:类型原文、主键、自增都要在标题里。
            Ui.SqlTreeNode ordersNode = tables.Children.First(c => c.Title == "orders");
            await ordersNode.LoadAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(ordersNode.Children.Any(c => c.Title.Contains("PK", StringComparison.Ordinal)));

            // ③ 执行一条查询,并检查**四类值都分得开** —— 这是结果网格的核心承诺。
            var executor = new SqlExecutor(session.Dialect, session.Pack);
            IReadOnlyList<SqlStatement> statements =
                SqlStatementSplitter.Split("select id, name, memo, blob_col from orders order by id", session.Dialect);
            IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                session.Metadata.Raw, statements, SqlFetchOptions.Default, 30, null,
                TestContext.CancellationTokenSource.Token);

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded, results[0].Error?.Message);
            SqlResultSet set = results[0].ResultSets[0];
            Assert.AreEqual(2, set.Rows.Count);
            Assert.AreEqual(4, set.Columns.Count);

            // 第 1 行:memo 是 NULL,blob 是二进制。
            Assert.AreEqual(SqlCellKind.Null, set.Rows[0][2].Kind, "NULL 必须是 NULL,不是空串。");
            Assert.AreEqual(SqlCellKind.Binary, set.Rows[0][3].Kind);
            // 第 2 行:memo 是空串,blob 是 NULL —— 与上一行正好互换,混了就看得出来。
            Assert.AreEqual(SqlCellKind.Text, set.Rows[1][2].Kind);
            Assert.AreEqual(0, set.Rows[1][2].Text!.Length, "空串必须与 NULL 分得开。");
            Assert.AreEqual(SqlCellKind.Null, set.Rows[1][3].Kind);

            // ④ 显示层也要分得开(这是"数据工具的原罪"那条纪律的落点)。
            var row0 = new Ui.SqlGridRow(set.Rows[0], Localization);
            var row1 = new Ui.SqlGridRow(set.Rows[1], Localization);
            Assert.AreEqual("NULL", row0[2].Text);
            Assert.AreEqual(Ui.SqlCellStyle.Null, row0[2].Style);
            Assert.AreEqual("''", row1[2].Text);
            Assert.AreEqual(Ui.SqlCellStyle.Empty, row1[2].Style);
            StringAssert.StartsWith(row0[3].Text, "0x");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>执行失败时要给出**用户原文里的行号**,而不是只丢一句错误。</summary>
    [TestMethod]
    public async Task 语法错误_定位到用户原文的那一行()
    {
        string file = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenSessionAsync(SqlDialect.Sqlite, file, 1, "", "", []);
            var executor = new SqlExecutor(session.Dialect, session.Pack);
            // 第 1 条好的,第 2 条坏的 —— 失败的必须是第 2 条,而且要停在那里。
            IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(
                "select 1;\nselect * from no_such_table;\nselect 3", session.Dialect);

            IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                session.Metadata.Raw, statements, SqlFetchOptions.Default, 30, null,
                TestContext.CancellationTokenSource.Token);

            Assert.AreEqual(2, results.Count, "任一条失败即停 —— 第 3 条不该被执行。");
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsFalse(results[1].Succeeded);
            Assert.AreEqual(2, results[1].Statement.StartLine, "出错的是第 2 行那条语句。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// <b>取消一条跑飞的查询。</b> 这是整份调研里最贵的一段结论,也是最该有端到端验收的一条。
    /// <para>
    /// SQLite 上走的是 <c>raw.sqlite3_interrupt</c> —— ADO.NET 门面的 <c>Cancel()</c> 是空方法体,
    /// 实测按它取消会跑满 144 秒。这条用例就是守着那条路不被"简化"掉。
    /// </para>
    /// <para>
    /// 这条用例第一次写的时候还逮到了一个真 bug:<c>Microsoft.Data.Sqlite</c> 的
    /// <c>ExecuteReaderAsync</c> 是**同步套壳**,于是 <c>await ExecuteAsync(...)</c> 会在调用线程上
    /// 同步跑完 —— 在 UI 线程上就是整个窗口冻住、连取消按钮都点不到。
    /// <see cref="SqlExecutor" /> 因此改成一律跳到后台线程。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task SQLite_取消跑飞的查询()
    {
        string file = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenSessionAsync(SqlDialect.Sqlite, file, 1, "", "", []);
            var executor = new SqlExecutor(session.Dialect, session.Pack);
            // 一条要跑很久的递归 CTE。
            IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(
                "with recursive c(x) as (select 1 union all select x+1 from c where x < 200000000) select count(*) from c",
                session.Dialect);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task<IReadOnlyList<SqlStatementResult>> run = executor.ExecuteAsync(
                session.Metadata.Raw, statements, SqlFetchOptions.Default, 300, null, CancellationToken.None);

            // 等它真的跑起来,再取消。
            for (int i = 0; i < 100 && !executor.IsRunning; i++)
            {
                await Task.Delay(20);
            }
            Assert.IsTrue(executor.IsRunning, "查询没跑起来,后面的取消就不算数。");

            SqlCancelStage stage = await executor.CancelAsync(session.ProbeConnection, "", _ => { });
            IReadOnlyList<SqlStatementResult> results = await run;
            stopwatch.Stop();

            Assert.AreEqual(SqlCancelStage.DriverCancel, stage, "SQLite 应当在第一档就被 sqlite3_interrupt 打断。");
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
                $"取消应当在秒级生效,实际用了 {stopwatch.Elapsed.TotalSeconds:F1} 秒。");
            Assert.IsFalse(results[0].Succeeded, "被取消的查询不该报成功。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>PostgreSQL:对象树 + 查询 + 服务端分页的全链路。</summary>
    [TestMethod]
    public async Task PostgreSQL_全链路()
    {
        SqlSession? session = await TryOpenSessionAsync(
            SqlDialect.PostgreSql, "127.0.0.1", 55432, "postgres", "velaspike",
            new() { ["database"] = "pack_verify", ["schema"] = "public" });
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL。");
            return;
        }
        await using (session)
        {
            var tree = new Ui.SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(tree.Roots.Count > 0, "PG 有库这一级,根应当是数据库节点。");

            // 服务端分页:**用方言包拼,不用 SqlSugar 的 ToPageList** ——
            // 实测后者在 SQL Server 上只要用户 SQL 带 ORDER BY 就直接报错(§7.3)。
            string paged = session.Pack.ApplyPaging("select generate_series(1, 1000) as n order by n", 10, 5);
            var executor = new SqlExecutor(session.Dialect, session.Pack);
            IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                session.Metadata.Raw,
                SqlStatementSplitter.Split(paged, session.Dialect),
                SqlFetchOptions.Default, 30, null,
                TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(results[0].Succeeded, results[0].Error?.Message);
            SqlResultSet set = results[0].ResultSets[0];
            Assert.AreEqual(5, set.Rows.Count, "分页要正好取 5 行。");
            Assert.AreEqual("11", set.Rows[0][0].Text, "跳过 10 行之后应当从 11 开始。");

            // 会话 id 是旁路取消那一档的前提。
            string sessionId = await executor.ReadSessionIdAsync(
                session.Metadata.Raw, TestContext.CancellationTokenSource.Token);
            Assert.IsFalse(string.IsNullOrEmpty(sessionId), "PG 的会话 id 必须拿得到,否则旁路取消这一档就废了。");
        }
    }

    /// <summary>MySQL:对象树 + 查询的全链路。</summary>
    [TestMethod]
    public async Task MySQL_全链路()
    {
        SqlSession? session = await TryOpenSessionAsync(
            SqlDialect.MySql, "127.0.0.1", 13306, "root", "velaspike",
            new() { ["database"] = "pack_verify" });
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 MySQL。");
            return;
        }
        await using (session)
        {
            var tree = new Ui.SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(tree.Roots.Any(r => r.Title == "pack_verify"), "对象树里要看得见自己的库。");

            var executor = new SqlExecutor(session.Dialect, session.Pack);
            IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                session.Metadata.Raw,
                SqlStatementSplitter.Split("select 1 as a, null as b, '' as c", session.Dialect),
                SqlFetchOptions.Default, 30, null,
                TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(results[0].Succeeded, results[0].Error?.Message);
            SqlResultSet set = results[0].ResultSets[0];
            Assert.AreEqual(SqlCellKind.Null, set.Rows[0][1].Kind);
            Assert.AreEqual(SqlCellKind.Text, set.Rows[0][2].Kind);
            Assert.AreEqual(0, set.Rows[0][2].Text!.Length);
        }
    }

    /// <summary>SQL Server:对象树 + 查询 + 服务端分页的全链路。</summary>
    [TestMethod]
    public async Task SQLServer_全链路()
    {
        SqlSession? session = await TryOpenSessionAsync(
            SqlDialect.SqlServer, @"(localdb)\VelaSpike", 1433, "", "",
            new() { ["database"] = "master", ["schema"] = "dbo" });
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 SQL Server。");
            return;
        }
        await using (session)
        {
            var tree = new Ui.SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(TestContext.CancellationTokenSource.Token);
            Assert.IsTrue(tree.Roots.Count > 0, "SQL Server 有库这一级。");

            // OFFSET/FETCH 要求有 ORDER BY —— 方言包负责兜底(并在注释里写明它不保证顺序)。
            //
            // ⚠ 这里刻意**不用 UNION** 的查询:兜底加的是 `ORDER BY (SELECT NULL)`,
            // 而 SQL Server 对 UNION/INTERSECT/EXCEPT 的结果要求 ORDER BY 的项出现在 select 列表里,
            // 于是那条兜底会被 Msg 104 打回。这是分页兜底的一处真实边界 ——
            // 用户写 UNION 查询时必须自己带 ORDER BY,报错响亮且指得明白,不会静默出错行。
            string paged = session.Pack.ApplyPaging("select n from (values (1),(2),(3)) as v(n)", 1, 2);
            var executor = new SqlExecutor(session.Dialect, session.Pack);
            IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                session.Metadata.Raw,
                SqlStatementSplitter.Split(paged, session.Dialect),
                SqlFetchOptions.Default, 30, null,
                TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(results[0].Succeeded, results[0].Error?.Message);
            Assert.AreEqual(2, results[0].ResultSets[0].Rows.Count, "分页要正好取 2 行。");

            string sessionId = await executor.ReadSessionIdAsync(
                session.Metadata.Raw, TestContext.CancellationTokenSource.Token);
            Assert.IsFalse(string.IsNullOrEmpty(sessionId), "@@SPID 必须拿得到,旁路取消要用它。");
        }
    }

    /// <summary>
    /// <b>v1 承诺的五种方言都要有方言包。</b> 少一份的表现是"那个方言连得上但没有对象树",
    /// 而那种半残状态最容易被当成 bug 报上来。
    /// </summary>
    [TestMethod]
    public void 五种一等公民方言都有方言包()
    {
        foreach (SqlDialectInfo info in SqlDialects.All)
        {
            Assert.IsTrue(DialectPacks.Has(info.Dialect), $"{info.DisplayName} 还没有方言包。");
            IDialectPack pack = DialectPacks.For(info.Dialect);
            Assert.AreEqual(info.Dialect, pack.Dialect, "方言包报的方言必须与登记表一致。");
            // 转义与分页是每个包都必须给出的两件事,顺手验一下它们不是空实现。
            Assert.AreNotEqual("x", pack.QuoteIdentifier("x"), "标识符必须加定界符。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(pack.ApplyPaging("select 1", 0, 10)));
        }
    }

    /// <summary>
    /// 一条会话要开出**三根**连接(元数据 / 探针 / 查询)。
    /// 探针那根同时是旁路取消通道,少了它取消阶梯就只剩一档。
    /// </summary>
    [TestMethod]
    public async Task 会话开出独立的元数据与查询连接()
    {
        string file = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenSessionAsync(SqlDialect.Sqlite, file, 1, "", "", []);
            Assert.IsNotNull(session.Metadata);
            Assert.IsNotNull(session.Probe, "探针连接开不出来的话,旁路取消那一档就没有通道了。");

            SqlConnection query = await session.OpenQueryConnectionAsync(
                "", TestContext.CancellationTokenSource.Token);
            Assert.AreNotSame(session.Metadata.Raw, query.Raw, "查询连接必须独占 —— 取消要拿到它自己的 DbCommand。");
            await session.CloseQueryConnectionAsync(query);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static async Task SeedAsync(SqlSession session, string sql)
    {
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlSession> OpenSessionAsync(
        SqlDialect dialect, string host, int port, string user, string password, Dictionary<string, string> settings) =>
        await SqlSession.OpenAsync(
            new WorkspaceConnectRequest
            {
                SessionId = "e2e",
                Host = host,
                Port = port,
                Username = user,
                Password = password,
                Settings = settings,
                DisplayName = "e2e"
            },
            dialect,
            Localization);

    private static async Task<SqlSession?> TryOpenSessionAsync(
        SqlDialect dialect, string host, int port, string user, string password, Dictionary<string, string> settings)
    {
        try
        {
            return await OpenSessionAsync(dialect, host, port, user, password, settings);
        }
        catch (Exception)
        {
            return null;
        }
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

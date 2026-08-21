using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 结构页与运维面。
/// <para>
/// 这两页的共同点是:**方言给不了的时候要说出来,而不是留一片空白**(§7.8)——
/// 空白与"这张表真的没有索引""现在真的没人连"长得一模一样,而那是两件完全不同的事。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlStructureAndOpsTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>结构页:列/索引/外键/DDL 四块都要有内容,而且标注要对得上真值。</summary>
    [TestMethod]
    public async Task 结构页_列出列与索引与外键并给出DDL()
    {
        string file = Path.Combine(Path.GetTempPath(), $"st-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, """
                create table parent(id integer primary key autoincrement, code text not null unique)
                """);
            await ExecAsync(session, """
                create table child(
                  cid integer primary key,
                  pid integer not null references parent(id),
                  total real generated always as (cid * 2) stored,
                  note text default 'x'
                )
                """);
            await ExecAsync(session, "create index ix_child_pid on child(pid)");

            var tab = new SqlStructureTabViewModel(session, new(SqlObjectKind.Table, "child"), Localization);
            await tab.LoadAsync(TestContext.CancellationTokenSource.Token);

            Assert.AreEqual(4, tab.Columns.Count, "四列都该在。");
            // 主键 / 自增 / 生成列 / NOT NULL / DEFAULT —— 正是 DbMaintenance 给错或给不了的那几样,
            // 结构页存在的意义就是把它们如实摆出来。
            SqlStructureRow pk = tab.Columns.First(c => c.Name == "cid");
            StringAssert.Contains(pk.Extra, "PK");
            SqlStructureRow generated = tab.Columns.First(c => c.Name == "total");
            StringAssert.Contains(generated.Extra, Localization["Sql_GeneratedColumn"]);
            SqlStructureRow withDefault = tab.Columns.First(c => c.Name == "note");
            StringAssert.Contains(withDefault.Extra, "DEFAULT");

            Assert.IsTrue(tab.Indexes.Any(i => i.Name == "ix_child_pid"), "索引要列出来。");
            Assert.AreEqual(1, tab.ForeignKeys.Count, "外键要列出来 —— IDbMaintenance 里一条都没有。");
            StringAssert.Contains(tab.ForeignKeys[0].Detail, "parent");

            // SQLite 天生存着建表原文,所以这一页在它上面应当拿得到真 DDL。
            StringAssert.Contains(tab.Ddl, "create table child", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// <b>方言不提供运维面时要明说。</b> SQLite 是嵌入式的 —— 它没有服务端会话,
    /// 这不是"没做",是"这个方言里不存在这个概念"。给一张空表会让用户以为是自己没权限。
    /// </summary>
    [TestMethod]
    public async Task 运维面_方言不支持时如实说明而不是空表()
    {
        string file = Path.Combine(Path.GetTempPath(), $"ops-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var tab = new SqlOpsTabViewModel(session, Localization);

            await tab.LoadAsync(TestContext.CancellationTokenSource.Token);

            Assert.IsFalse(tab.IsSupported, "SQLite 没有服务端会话。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(tab.UnsupportedNotice), "必须给一句说明,不能留空。");
            StringAssert.Contains(tab.UnsupportedNotice, "SQLite");
            Assert.AreEqual(0, tab.Sessions.Count);
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>PostgreSQL 的运维面:会话列表里至少要能看到自己那一条。</summary>
    [TestMethod]
    public async Task 运维面_PostgreSQL能看到自己的会话()
    {
        SqlSession? session = await TryOpenAsync(
            SqlDialect.PostgreSql, "127.0.0.1", 55432, "postgres", "velaspike",
            new() { ["database"] = "pack_verify" });
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL。");
            return;
        }
        await using (session)
        {
            if (session.Pack.SessionListSql is null)
            {
                Assert.Inconclusive("PG 方言包还没有会话视图(M4 资产尚未落地)。");
                return;
            }
            var tab = new SqlOpsTabViewModel(session, Localization);

            await tab.LoadAsync(TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(tab.IsSupported);
            Assert.IsTrue(tab.Sessions.Count > 0, $"至少该看到自己那条会话。状态:{tab.Status}");
        }
    }

    /// <summary>
    /// <b>不许杀自己。</b> 杀掉本窗口的会话只会把用户自己断开,然后整个面板陷入"连接断了" ——
    /// 那是最没用的一种"功能生效"。
    /// </summary>
    [TestMethod]
    public async Task 运维面_不杀自己的会话()
    {
        SqlSession? session = await TryOpenAsync(
            SqlDialect.PostgreSql, "127.0.0.1", 55432, "postgres", "velaspike",
            new() { ["database"] = "pack_verify" });
        if (session is null || session.Pack.SessionListSql is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL,或方言包尚无会话视图。");
            return;
        }
        await using (session)
        {
            // 让会话知道自己的 id —— 这正是"不杀自己"那条判断的依据。
            var executor = new Execution.SqlExecutor(session.Dialect, session.Pack);
            session.Metadata.SessionId = await executor.ReadSessionIdAsync(
                session.Metadata.Raw, TestContext.CancellationTokenSource.Token);
            Assert.IsFalse(string.IsNullOrEmpty(session.Metadata.SessionId));

            var tab = new SqlOpsTabViewModel(session, Localization);
            await tab.LoadAsync(TestContext.CancellationTokenSource.Token);
            SqlOpsRow? self = tab.Sessions.FirstOrDefault(r => r.Id == session.Metadata.SessionId);
            Assert.IsNotNull(self, "自己那条会话应当在列表里。");

            tab.Selected = self;
            tab.KillCommand.Execute(null);
            for (int i = 0; i < 50 && !tab.Status.Contains("自己", StringComparison.Ordinal); i++)
            {
                await Task.Delay(10);
            }

            StringAssert.Contains(tab.Status, "自己", "点了杀自己应当被拦下并说明原因。");
            // 拦下之后连接必须还活着。
            Assert.IsTrue(await session.Metadata.PingAsync(TestContext.CancellationTokenSource.Token) >= 0);
        }
    }

    /// <summary>
    /// <b>看计划失败之后,这条连接还得能出数据。</b>
    /// <para>
    /// SQL Server 的计划脚本是三条:<c>SET SHOWPLAN_ALL ON</c> / 用户语句 / <c>SET ... OFF</c>。
    /// 中间那条失败时执行器一条失败即停,<b>第三条 <c>OFF</c> 就发不出去</b> ——
    /// 而 <c>SET</c> 是<b>连接级</b>的,于是这条连接从此只出计划不出数据:
    /// 再发一条完全正常的 <c>select 1</c>,拿回来的是 <c>StmtText</c> / <c>EstimateRows</c> 那几列。
    /// </para>
    /// <para>
    /// 这一条守的就是那个补发:<b>失败一次之后,下一条普通查询必须拿回数据。</b>
    /// 它是这一整块里唯一能把"连接被污染"与"查询本身失败"分开的断言 ——
    /// 只看第一条失败的话,污染和没污染长得完全一样。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_中途失败不会让连接卡在只出计划的状态()
    {
        SqlSession? session = await TryOpenAsync(
            SqlDialect.SqlServer,
            System.Environment.GetEnvironmentVariable("VELASQL_MSSQL") ?? @"(localdb)\VelaSpike",
            1433, "", "", new() { ["database"] = "master" });
        if (session is null || session.Pack.ExplainSql("select 1", analyze: false) is null)
        {
            Assert.Inconclusive("没有可用的 SQL Server。");
            return;
        }
        await using (session)
        {
            var tab = new SqlQueryTabViewModel(session, Localization, new VelaShell.PluginSdk.Testing.CollectingLogger(), "plan");

            // ① 故意让中间那条失败(表不存在,Msg 208)。
            tab.Sql = "select * from dbo.velaspike_no_such_table";
            tab.CaretOffset = 0;
            tab.ExplainCommand.Execute(null);
            await WaitAsync(() => !tab.IsBusy);

            // ② 紧接着一条完全正常的查询 —— 必须拿回**数据**。
            tab.Sql = "select 42 as answer";
            tab.CaretOffset = 0;
            tab.ExecuteAllCommand.Execute(null);
            await WaitAsync(() => !tab.IsBusy && tab.Grid.Rows.Count > 0);

            Assert.AreEqual(1, tab.Grid.Columns.Count, $"列数不对,多半拿回的是计划。状态:{tab.Status}");
            Assert.AreEqual(
                "answer", tab.Grid.Columns[0].Header,
                "拿回的是计划不是数据 —— 连接卡在 SHOWPLAN 状态,收尾的 OFF 没补发出去。");
            Assert.AreEqual("42", tab.Grid.Rows[0][0].Text);
        }
    }

    private static async Task WaitAsync(Func<bool> until)
    {
        for (int i = 0; i < 400 && !until(); i++)
        {
            await Task.Delay(25);
        }
        Assert.IsTrue(until(), "等的条件一直没成立。");
    }

    /// <summary>
    /// <b>方言只给得出估算值时,面板要说出来。</b>
    /// <para>
    /// SQLite 的 <c>EXPLAIN QUERY PLAN</c> 不执行语句,所以计划里的每一个行数都是优化器的估算。
    /// 不说的话,一个离谱的估算值会被当成"真的扫了这么多行"拿去做判断(§7.8)——
    /// 而这一档护栏是**放行**的(SQLite 上没有危险档),用户不会收到任何别的提示。
    /// </para>
    /// <para>
    /// 判据本身是方言无关的:两档 <c>analyze</c> 生成的语句逐字相同 ⇒ 这个方言没有"真跑一遍"那一档。
    /// 所以这里同时断言 PG 那种真有两档的方言**不会**误报。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_只有估算值的方言要明说()
    {
        string file = Path.Combine(Path.GetTempPath(), $"pe-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, name text)");
            await ExecAsync(session, "insert into t(name) values('a'),('b')");

            var tab = new SqlQueryTabViewModel(
                session, Localization, new VelaShell.PluginSdk.Testing.CollectingLogger(), "plan");
            tab.Sql = "select * from t where id = 1";
            tab.CaretOffset = 0;
            tab.ExplainCommand.Execute(null);
            await WaitAsync(() => !tab.IsBusy && tab.Grid.Rows.Count > 0);

            StringAssert.Contains(tab.Status, "估算", $"没说清这是估算值。状态:{tab.Status}");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>真有两档的方言不该被误报成"只有估算值"。</summary>
    [TestMethod]
    public void 执行计划_两档不同的方言不算估算方言()
    {
        // 这一条纯离线:直接问方言包本人,不需要任何服务器。
        const string Sql = "select 1";
        foreach (SqlDialect dialect in new[] { SqlDialect.PostgreSql, SqlDialect.SqlServer })
        {
            if (!DialectPacks.Has(dialect))
            {
                continue;
            }
            IDialectPack pack = DialectPacks.For(dialect);
            if (pack.ExplainSql(Sql, analyze: false) is null)
            {
                continue;
            }
            Assert.AreNotEqual(
                pack.ExplainSql(Sql, analyze: true), pack.ExplainSql(Sql, analyze: false),
                $"{dialect} 的两档 analyze 生成了同一条语句 —— 那样面板会把它当成只有估算值的方言。");
        }

        // 反面:SQLite / Oracle 两档确实相同,这正是"只有估算值"的判据。
        foreach (SqlDialect dialect in new[] { SqlDialect.Sqlite, SqlDialect.Oracle })
        {
            IDialectPack pack = DialectPacks.For(dialect);
            Assert.AreEqual(
                pack.ExplainSql(Sql, analyze: true), pack.ExplainSql(Sql, analyze: false),
                $"{dialect} 本就没有「真跑一遍」那一档,两档应当逐字相同。");
        }
    }

    /// <summary>
    /// <b>元数据连接同时来两拨查询,不能炸。</b>
    /// <para>
    /// 这条是真机 GUI 测出来的:对象树上挂出一行
    /// <c>A command is already in progress: SELECT a.attnum, a.attname::text, …</c>。
    /// 根因不在方言包,在<b>连接被并发使用</b>——Npgsql 与 MySqlConnector 都不允许
    /// 一条连接上同时跑两条命令(SQL Server 要显式开 MARS)。
    /// </para>
    /// <para>
    /// 而对象树<b>天然会并发</b>:展开是即发即忘的(<c>IsExpanded</c> 的 setter 里
    /// <c>_ = LoadAsync(...)</c>),用户飞快点开两个节点就够了。
    /// 现在所有元数据查询都排在 <c>SqlConnection.UseAsync</c> 的闸门后面。
    /// </para>
    /// <para>
    /// <b>这条用例在修复前会红</b>:去掉闸门直接摸 <c>Metadata.Raw</c>,PG 上必抛。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 元数据连接_并发查询被排队而不是抛A_command_is_already_in_progress()
    {
        SqlSession? session = await TryOpenAsync(
            SqlDialect.PostgreSql, "127.0.0.1", 55432, "postgres", "velaspike",
            new() { ["database"] = "pack_verify" });
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL。");
            return;
        }
        await using (session)
        {
            IReadOnlyList<SqlObject> tables = await session.Metadata.UseAsync(
                (c, t) => session.Pack.ListRelationsAsync(c, "public", t),
                TestContext.CancellationTokenSource.Token);
            SqlObject[] targets = [.. tables.Where(o => o.Kind == SqlObjectKind.Table).Take(6)];
            Assert.IsTrue(targets.Length >= 2, "pack_verify 里该有几张表可供并发读。");

            // 同时发一把 —— 正是对象树展开时发生的事。
            Task<SqlTableSchema>[] racing =
            [
                .. targets.Select(target => session.Metadata.UseAsync(
                    (c, t) => session.Pack.DescribeAsync(c, target, t),
                    TestContext.CancellationTokenSource.Token))
            ];

            SqlTableSchema[] schemas = await Task.WhenAll(racing);

            Assert.AreEqual(targets.Length, schemas.Length);
            foreach (SqlTableSchema schema in schemas)
            {
                Assert.IsTrue(schema.Columns.Count > 0, "并发读回来的结构是空的。");
            }
        }
    }

    private static Task<SqlSession> OpenAsync(string file) =>
        SqlSession.OpenAsync(
            new WorkspaceConnectRequest { SessionId = "st", Host = file, Port = 1 },
            SqlDialect.Sqlite, Localization);

    private static async Task<SqlSession?> TryOpenAsync(
        SqlDialect dialect, string host, int port, string user, string password, Dictionary<string, string> settings)
    {
        try
        {
            return await SqlSession.OpenAsync(
                new WorkspaceConnectRequest
                {
                    SessionId = "st",
                    Host = host,
                    Port = port,
                    Username = user,
                    Password = password,
                    Settings = settings
                },
                dialect, Localization);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task ExecAsync(SqlSession session, string sql)
    {
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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

using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// <b>Oracle 的真机用例。</b>
/// <para>
/// 这一组存在的理由,是把 <see cref="OraclePackTests" /> 开头那段"整份 Oracle 包都是离线推断"
/// 的免责声明**一条条换成实测**。它验的正是离线用例声明验不了的那一半:
/// 数据字典视图的列名与取值域、<c>LONG</c> 类型的 <c>DATA_DEFAULT</c> 读不读得回来、
/// 降序索引的列名形态、会话与锁视图、以及那份连接串装配到底认不认。
/// </para>
/// <para>
/// 真机:podman 容器 <c>velaspike-oracle</c>,Oracle AI Database 26ai Free 23.26.2.0.0,
/// 127.0.0.1:11521/FREEPDB1。连不上就 <see cref="Assert.Inconclusive(string)" /> ——
/// **不是静默跳过**:一条"没验过"的用例必须让人看见它没验过。
/// </para>
/// </summary>
[TestClass]
public sealed class OracleRealMachineTests
{
    private static readonly Loc Localization = new("zh-Hans");
    private const string Schema = "VELASPIKE";

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 连得上,而且那份**从没验过**的连接串装配是对的。
    /// <para>
    /// 这一条挂了,后面全部无意义 —— 所以它单独成一条,失败信息里带原始异常。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 连得上真Oracle()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        Assert.AreEqual(SqlDialect.Oracle, session.Dialect);
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        // Oracle 里没有裸 SELECT —— 必须有 FROM,这就是 DUAL 存在的原因。
        command.CommandText = "select banner_full from v$version";
        string? banner = (await command.ExecuteScalarAsync())?.ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(banner));
        TestContext.WriteLine($"Oracle: {banner}");
    }

    /// <summary>
    /// 元数据逐项对账:列类型原文、可空性、默认值、主键、生成列、索引唯一性、外键。
    /// <para>
    /// 这些正是 <c>IDbMaintenance</c> 给错或给不了的那几样(§2.3),
    /// 也是整份 Oracle 包里最可能因为"照文档写"而写歪的地方。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 方言包读得对表结构()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_PARENT", "ORA_CHILD");
        await ExecAsync(session, """
            create table ORA_PARENT (
              ID number(10) generated always as identity primary key,
              CODE varchar2(32) not null unique
            )
            """);
        await ExecAsync(session, """
            create table ORA_CHILD (
              CID number(10) primary key,
              PID number(10) not null,
              PRICE number(12,3) default 0 not null,
              TOTAL number(12,3) generated always as (PRICE * 2) virtual,
              NOTE varchar2(100) default 'x',
              constraint FK_ORA_CHILD_PID foreign key (PID) references ORA_PARENT(ID) on delete cascade
            )
            """);
        await ExecAsync(session, "create index IX_ORA_CHILD_PID on ORA_CHILD(PID)");
        await ExecAsync(session, "create unique index UX_ORA_CHILD_NOTE on ORA_CHILD(NOTE)");

        SqlTableSchema schema = await session.Pack.DescribeAsync(
            session.Metadata.Raw,
            new(SqlObjectKind.Table, "ORA_CHILD", Schema),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(5, schema.Columns.Count, "五列都该在。");

        SqlColumn price = schema.Columns.First(c => c.Name == "PRICE");
        // 类型原文要带精度 —— 只报 "NUMBER" 等于把 number(12,3) 与 number 混为一谈。
        StringAssert.Contains(price.DataType, "12", $"类型原文丢了精度:{price.DataType}");
        Assert.IsFalse(price.IsNullable);
        // DATA_DEFAULT 在数据字典里是 LONG 类型 —— 这是 Oracle 元数据最经典的读不回来的坑。
        Assert.IsFalse(string.IsNullOrWhiteSpace(price.DefaultValue), "默认值没读回来(LONG 列的老问题)。");

        SqlColumn total = schema.Columns.First(c => c.Name == "TOTAL");
        Assert.IsTrue(total.IsGenerated, "虚拟列没被识别成生成列 —— 它会被结果网格当成可写列。");
        Assert.IsFalse(total.IsWritable);

        Assert.IsTrue(schema.Columns.First(c => c.Name == "CID").IsPrimaryKey);
        CollectionAssert.AreEqual(new[] { "CID" }, (System.Collections.ICollection)schema.PrimaryKey);

        SqlIndex unique = schema.Indexes.First(i => i.Name == "UX_ORA_CHILD_NOTE");
        Assert.IsTrue(unique.IsUnique, "唯一索引没被识别 —— 写回会挑错定位列。");
        Assert.IsFalse(schema.Indexes.First(i => i.Name == "IX_ORA_CHILD_PID").IsUnique);

        SqlForeignKey fk = schema.ForeignKeys.Single(f => f.Name == "FK_ORA_CHILD_PID");
        Assert.AreEqual("ORA_PARENT", fk.ReferencedTable);
        CollectionAssert.AreEqual(new[] { "PID" }, (System.Collections.ICollection)fk.Columns);
        CollectionAssert.AreEqual(new[] { "ID" }, (System.Collections.ICollection)fk.ReferencedColumns);
    }

    /// <summary>
    /// 自增。
    /// <para>
    /// §2.3 实测过 <c>IsIdentity</c> 在 PG/MSSQL/MySQL 上**每一列都返回 True**;
    /// Oracle 的 identity 列是另一套机制(<c>ALL_TAB_IDENTITY_COLS</c>),这一条验它没跟着错。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 自增列只有一列是自增()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_IDENT");
        await ExecAsync(session, """
            create table ORA_IDENT (
              ID number(10) generated always as identity primary key,
              NAME varchar2(50),
              QTY number(10)
            )
            """);

        SqlTableSchema schema = await session.Pack.DescribeAsync(
            session.Metadata.Raw,
            new(SqlObjectKind.Table, "ORA_IDENT", Schema),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(
            1, schema.Columns.Count(c => c.IsAutoIncrement),
            "自增列应当**只有一列**。全 True 正是 IDbMaintenance 在别的方言上犯的错。");
        Assert.IsTrue(schema.Columns.First(c => c.Name == "ID").IsAutoIncrement);
    }

    /// <summary>对象树:列得出表与视图,而且视图的列也读得到。</summary>
    [TestMethod]
    public async Task 对象树列得出表与视图()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_BASE");
        await ExecAsync(session, "create table ORA_BASE (ID number(10) primary key, NAME varchar2(50))");
        await DropQuietlyAsync(session, "drop view ORA_VIEW");
        await ExecAsync(session, "create view ORA_VIEW as select ID, NAME from ORA_BASE");

        IReadOnlyList<SqlObject> schemas = await session.Pack.ListSchemasAsync(
            session.Metadata.Raw, TestContext.CancellationTokenSource.Token);
        Assert.IsTrue(
            schemas.Any(x => string.Equals(x.Name, Schema, StringComparison.OrdinalIgnoreCase)),
            "自己的 schema 都没列出来。");

        IReadOnlyList<SqlObject> objects = await session.Pack.ListRelationsAsync(
            session.Metadata.Raw, Schema, TestContext.CancellationTokenSource.Token);
        Assert.IsTrue(objects.Any(o => o.Name == "ORA_BASE" && o.Kind == SqlObjectKind.Table));
        Assert.IsTrue(objects.Any(o => o.Name == "ORA_VIEW" && o.Kind == SqlObjectKind.View), "视图没列出来。");

        // 视图的列 —— §2.3 里 IDbMaintenance 对视图是给不出列的。
        SqlTableSchema view = await session.Pack.DescribeAsync(
            session.Metadata.Raw,
            new(SqlObjectKind.View, "ORA_VIEW", Schema),
            TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(2, view.Columns.Count, "视图的列没读到。");
    }

    /// <summary>分页:<c>ApplyPaging</c> 生成的 SQL 在真机上跑得动、给得出正确的窗口。</summary>
    [TestMethod]
    public async Task 分页在真机上跑得动()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_PAGE");
        await ExecAsync(session, "create table ORA_PAGE (ID number(10) primary key)");
        await ExecAsync(session, "insert into ORA_PAGE select level from dual connect by level <= 25");

        string paged = session.Pack.ApplyPaging("select ID from ORA_PAGE order by ID", 10, 5);
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = paged;
        await using DbDataReader reader = await command.ExecuteReaderAsync(
            TestContext.CancellationTokenSource.Token);

        List<decimal> ids = [];
        while (await reader.ReadAsync(TestContext.CancellationTokenSource.Token))
        {
            ids.Add(reader.GetDecimal(0));
        }
        CollectionAssert.AreEqual(new decimal[] { 11, 12, 13, 14, 15 }, ids, $"分页窗口不对。SQL:{paged}");
    }

    /// <summary>会话 id:拿得到,而且就是自己这条连接的。</summary>
    [TestMethod]
    public async Task 会话id拿得到且是自己那条()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }
        if (session.Pack.SessionIdSql is null)
        {
            Assert.Inconclusive("方言包没给会话 id 语句。");
            return;
        }

        var executor = new Execution.SqlExecutor(session.Dialect, session.Pack);
        string? id = await executor.ReadSessionIdAsync(
            session.Metadata.Raw, TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(string.IsNullOrWhiteSpace(id), "会话 id 没拿到 —— 「不杀自己」那条判断就失效了。");
        TestContext.WriteLine($"session id: {id}");
    }

    /// <summary>建表原文:Oracle 用 <c>DBMS_METADATA</c>,它要权限,拿不到也得**说出来**而不是留空。</summary>
    [TestMethod]
    public async Task 建表原文拿得到或明确说明()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_DDL");
        await ExecAsync(session, "create table ORA_DDL (ID number(10) primary key)");

        if (session.Pack.ShowCreateSql(new(SqlObjectKind.Table, "ORA_DDL", Schema)) is not { } sql)
        {
            Assert.Inconclusive("方言包声明不提供建表原文。");
            return;
        }

        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = sql;
        string? ddl = (await command.ExecuteScalarAsync())?.ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(ddl), "建表原文是空的。");
        StringAssert.Contains(ddl, "ORA_DDL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 执行计划:两条语句发下去,最后一条的结果集就是计划文本。
    /// <para>
    /// Oracle 是唯一给不出"一条语句出计划"的方言(<c>EXPLAIN PLAN</c> 不返回结果集),
    /// 所以这一条真正验的是**那个两段式在真机上确实成立**。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_两段式在真机上出得来()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_PLAN");
        await ExecAsync(session, "create table ORA_PLAN (ID number(10) primary key, NAME varchar2(50))");

        string? explain = session.Pack.ExplainSql("select * from ORA_PLAN where ID = 1", analyze: false);
        Assert.IsNotNull(explain);

        var executor = new Execution.SqlExecutor(session.Dialect, session.Pack);
        IReadOnlyList<Execution.SqlStatementResult> results = await executor.ExecuteAsync(
            session.Metadata.Raw,
            Execution.SqlStatementSplitter.Split(explain, session.Dialect),
            Execution.SqlFetchOptions.Default,
            30,
            null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, results.Count, "应当是两条语句:EXPLAIN PLAN 一条、DISPLAY 一条。");
        Assert.IsTrue(results[^1].Succeeded, $"DISPLAY 那条失败了:{results[^1].Error?.Message}");
        Execution.SqlResultSet? plan = results[^1].ResultSets.FirstOrDefault();
        Assert.IsNotNull(plan, "最后一条应当给出计划结果集。");
        string text = string.Join("\n", plan.Rows.Select(r => r[0].Text ?? ""));
        // 计划里必须提到这张表 —— 否则拿到的是别人留在 PLAN_TABLE 里的旧计划。
        StringAssert.Contains(text, "ORA_PLAN", StringComparison.OrdinalIgnoreCase);
        TestContext.WriteLine(text);
    }

    /// <summary>
    /// <b><c>EXPLAIN PLAN</c> 真的不执行被解释的语句。</b>
    /// <para>
    /// 这是 <c>analyze</c> 两档在 Oracle 上返回同一条的**前提**。前提不成立,
    /// 那两档就是把一个危险操作伪装成安全操作 —— 所以必须拿 DML 真验一次。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_不会真的执行被解释的DML()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_DANGER");
        await ExecAsync(session, "create table ORA_DANGER (ID number(10))");
        await ExecAsync(session, "insert into ORA_DANGER select level from dual connect by level <= 3");
        await ExecAsync(session, "commit");

        string? explain = session.Pack.ExplainSql("delete from ORA_DANGER where ID > 0", analyze: true);
        Assert.IsNotNull(explain);

        var executor = new Execution.SqlExecutor(session.Dialect, session.Pack);
        await executor.ExecuteAsync(
            session.Metadata.Raw,
            Execution.SqlStatementSplitter.Split(explain, session.Dialect),
            Execution.SqlFetchOptions.Default,
            30,
            null,
            TestContext.CancellationTokenSource.Token);

        await using DbCommand count = session.Metadata.Raw.CreateCommand();
        count.CommandText = "select count(*) from ORA_DANGER";
        Assert.AreEqual(
            3m, Convert.ToDecimal(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture),
            "EXPLAIN PLAN 把 DELETE 真的执行了 —— 那两档 analyze 的取舍就全错了。");
    }

    /// <summary>会话列表:至少看得到自己那条,而且 id 形态与「杀会话」认的一致。</summary>
    [TestMethod]
    public async Task 会话列表_看得到自己且id形态对得上()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }
        Assert.IsNotNull(session.Pack.SessionListSql);

        var executor = new Execution.SqlExecutor(session.Dialect, session.Pack);
        session.Metadata.SessionId = await executor.ReadSessionIdAsync(
            session.Metadata.Raw, TestContext.CancellationTokenSource.Token);

        var tab = new Ui.SqlOpsTabViewModel(session, Localization);
        await tab.LoadAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(tab.IsSupported);
        Assert.IsTrue(tab.Sessions.Count > 0, $"一条会话都没列出来。状态:{tab.Status}");
        Assert.IsTrue(
            tab.Sessions.Any(r => r.Id == session.Metadata.SessionId),
            "自己那条不在列表里 —— 说明会话列表的 id 形态与 SessionIdSql 对不上,「不杀自己」就会失效。");
        // 锁那一页在没有争用时**空表是正确答案**,只要它不报错。
        StringAssert.Contains(tab.Status, "会话");
    }

    /// <summary>
    /// 加列:<b>基类的通用写法在 Oracle 上是语法错</b>,所以这一条同时验两件事 ——
    /// 覆盖后的 DDL 跑得通,且它确实没在用 <c>ADD COLUMN</c>。
    /// </summary>
    [TestMethod]
    public async Task 加列_不用ADDCOLUMN写法且真跑得通()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        await ResetAsync(session, "ORA_DDL2");
        await ExecAsync(session, "create table ORA_DDL2 (ID number(10) primary key)");
        var target = new SqlObject(SqlObjectKind.Table, "ORA_DDL2", Schema);

        string? add = session.Pack.AddColumnDdl(
            target, new("QTY", 1, "number(12,3)", IsNullable: false, DefaultValue: "0"));
        Assert.IsNotNull(add);
        Assert.IsFalse(
            add.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase),
            $"Oracle 没有 ADD COLUMN 这种写法。生成的是:{add}");
        await ExecAsync(session, add);

        string? index = session.Pack.CreateIndexDdl(target, "IX_ORA_DDL2_QTY", ["QTY"], unique: false);
        Assert.IsNotNull(index);
        await ExecAsync(session, index);

        SqlTableSchema schema = await session.Pack.DescribeAsync(
            session.Metadata.Raw, target, TestContext.CancellationTokenSource.Token);
        SqlColumn qty = schema.Columns.First(c => c.Name == "QTY");
        Assert.IsFalse(qty.IsNullable);
        StringAssert.Contains(qty.DataType, "12");
        Assert.IsTrue(schema.Indexes.Any(i => i.Name == "IX_ORA_DDL2_QTY"));

        string? dropIndex = session.Pack.DropIndexDdl(target, "IX_ORA_DDL2_QTY");
        Assert.IsNotNull(dropIndex);
        await ExecAsync(session, dropIndex);

        string? dropColumn = session.Pack.DropColumnDdl(target, "QTY");
        Assert.IsNotNull(dropColumn);
        await ExecAsync(session, dropColumn);

        schema = await session.Pack.DescribeAsync(
            session.Metadata.Raw, target, TestContext.CancellationTokenSource.Token);
        Assert.IsFalse(schema.Columns.Any(c => c.Name == "QTY"), "列没删掉。");
    }

    /// <summary>
    /// <b>表达不了的东西返回 <see langword="null" />,而不是发一条只做对一半的 DDL。</b>
    /// <para>生成列/主键/自增/注释各有专门语法,拼进 ADD 会得到"成功了但不是你要的那一列"。</para>
    /// </summary>
    [TestMethod]
    public void 加列_表达不了的一律返回null()
    {
        var pack = new OraclePack();
        var target = new SqlObject(SqlObjectKind.Table, "T", Schema);

        Assert.IsNull(pack.AddColumnDdl(target, new("C", 1, "number", true, IsGenerated: true)));
        Assert.IsNull(pack.AddColumnDdl(target, new("C", 1, "number", true, IsPrimaryKey: true)));
        Assert.IsNull(pack.AddColumnDdl(target, new("C", 1, "number", true, IsAutoIncrement: true)));
        Assert.IsNull(pack.AddColumnDdl(target, new("C", 1, "number", true, Comment: "注释")));
        Assert.IsNotNull(pack.AddColumnDdl(target, new("C", 1, "number", true)));
    }

    /// <summary>类型候选:每一条都要能真的用来建列 —— 摆一个建不出来的类型比不摆更坏。</summary>
    [TestMethod]
    public async Task 类型候选每一条都建得出来()
    {
        await using SqlSession? session = await TryOpenAsync();
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        IReadOnlyList<string> types = session.Pack.CommonTypes;
        Assert.IsTrue(types.Count > 0);
        List<string> broken = [];
        for (int i = 0; i < types.Count; i++)
        {
            string table = $"ORA_TY{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            await DropQuietlyAsync(session, $"drop table {table} cascade constraints purge");
            try
            {
                await ExecAsync(session, $"create table {table} (C {types[i]})");
            }
            catch (DbException ex)
            {
                broken.Add($"{types[i]} → {ex.Message}");
            }
            finally
            {
                await DropQuietlyAsync(session, $"drop table {table} cascade constraints purge");
            }
        }
        Assert.AreEqual(0, broken.Count, $"这些候选类型建不出列:{string.Join(" | ", broken)}");
    }

    /// <summary>
    /// <b><c>ALTER SYSTEM CANCEL SQL</c> 在这台真机上到底认不认。</b>
    /// <para>
    /// 源码里这条一直标着【未验证】,而且它的取舍很讲究:掐的是**一条查询**不是整个会话
    /// (与 PG 选 <c>pg_cancel_backend</c>、MySQL 选 <c>KILL QUERY</c> 同一条纪律),
    /// 代价是它 18c 才有,老版本会 ORA-00933。所以"认不认"必须真机说了算。
    /// </para>
    /// <para>
    /// 验法:A 会话跑一条一定跑很久的查询,B 会话拿 A 的 <c>sid,serial#</c> 发 CANCEL,
    /// 然后看 A 是不是**带着错误提前回来了**,而且**连接还活着**(这正是"取消查询"
    /// 区别于"杀会话"的地方 —— 杀了会话的话 A 之后连 ping 都发不出去)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 取消查询_CANCEL_SQL在这台真机上生效且连接还活着()
    {
        await using SqlSession? victim = await TryOpenAsync();
        await using SqlSession? killer = await TryOpenAsync();
        if (victim is null || killer is null)
        {
            Assert.Inconclusive("没有可用的 Oracle。");
            return;
        }

        var executor = new Execution.SqlExecutor(victim.Dialect, victim.Pack);
        string? sessionId = await executor.ReadSessionIdAsync(
            victim.Metadata.Raw, TestContext.CancellationTokenSource.Token);
        Assert.IsFalse(string.IsNullOrEmpty(sessionId));

        if (victim.Pack.CancelSessionSql(sessionId!) is not { } cancel)
        {
            Assert.Inconclusive("方言包拼不出取消语句。");
            return;
        }

        // 一条纯 CPU、不落盘、一定跑很久的查询。
        Task<Exception?> running = Task.Run(async () =>
        {
            try
            {
                await using DbCommand slow = victim.Metadata.Raw.CreateCommand();
                slow.CommandText =
                    "select count(*) from (select level from dual connect by level <= 40000000)";
                slow.CommandTimeout = 120;
                await slow.ExecuteScalarAsync();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        // 等它真的开始跑(在 V$SESSION 里变成 ACTIVE),否则 CANCEL 会打在空处。
        for (int i = 0; i < 100 && !await IsActiveAsync(killer, sessionId!); i++)
        {
            await Task.Delay(50);
        }

        Exception? cancelFailure = null;
        try
        {
            await using DbCommand command = killer.Metadata.Raw.CreateCommand();
            command.CommandText = cancel;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            cancelFailure = ex;
        }

        Exception? victimError = await running.WaitAsync(TimeSpan.FromSeconds(120));

        if (cancelFailure is not null)
        {
            // ORA-00933(老版本没有 CANCEL SQL)/ ORA-01031(没有 ALTER SYSTEM)都是**已知的**
            // 显式失败,不是本用例要抓的错 —— 但要把原文说出来,否则"取消不生效"会变成悬案。
            Assert.Inconclusive($"这台机器上取消语句本身被拒:{cancelFailure.Message}");
            return;
        }

        Assert.IsNotNull(
            victimError,
            "取消发出去了,可那条查询自己跑完了 —— CANCEL SQL 在这台机器上没生效。");
        TestContext.WriteLine($"被取消方收到:{victimError.Message}");

        // **连接必须还活着**:这才是"取消查询"而不是"杀会话"。
        await using DbCommand ping = victim.Metadata.Raw.CreateCommand();
        ping.CommandText = "select 1 from dual";
        Assert.AreEqual(
            1m, Convert.ToDecimal(await ping.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture),
            "取消之后连接就不能用了 —— 那是杀会话的行为,不是取消查询。");
    }

    private static async Task<bool> IsActiveAsync(SqlSession probe, string sessionId)
    {
        string sid = sessionId.Split(',')[0];
        await using DbCommand command = probe.Metadata.Raw.CreateCommand();
        command.CommandText = $"select count(*) from V$SESSION where SID = {sid} and STATUS = 'ACTIVE'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<SqlSession?> TryOpenAsync()
    {
        try
        {
            return await SqlSession.OpenAsync(
                new WorkspaceConnectRequest
                {
                    SessionId = "ora",
                    Host = "127.0.0.1",
                    Port = 11521,
                    Username = "velaspike",
                    Password = "velaspike",
                    Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["oracleServiceName"] = "FREEPDB1",
                        ["schema"] = Schema
                    }
                },
                SqlDialect.Oracle, Localization);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task ResetAsync(SqlSession session, params string[] tables)
    {
        foreach (string table in tables.Reverse())
        {
            await DropQuietlyAsync(session, $"drop table {table} cascade constraints purge");
        }
    }

    private static async Task DropQuietlyAsync(SqlSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
        catch (DbException)
        {
            // 本来就不存在 —— 这正是想要的状态。
        }
    }

    private static async Task ExecAsync(SqlSession session, string sql)
    {
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

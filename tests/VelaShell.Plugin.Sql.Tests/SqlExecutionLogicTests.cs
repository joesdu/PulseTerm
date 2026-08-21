using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Plugin.Sql.Execution;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 语句切分、护栏分级、错误定位这三块纯逻辑。
/// <para>它们错了都是**静默的**:切错会把半条语句发出去,护栏判错会让危险语句直接落到生产库上。</para>
/// </summary>
[TestClass]
public sealed class SqlExecutionLogicTests
{
    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ───────────────────────── 切分 ─────────────────────────

    /// <summary>基本切分与起始行 —— 起始行是错误定位算法的地基。</summary>
    [TestMethod]
    public void 切分_记下每条语句的起始行()
    {
        const string sql = "select 1;\nselect 2;\n\nselect 3";

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(sql, SqlDialect.PostgreSql);

        Assert.AreEqual(3, statements.Count);
        Assert.AreEqual(1, statements[0].StartLine);
        Assert.AreEqual(2, statements[1].StartLine);
        Assert.AreEqual(4, statements[2].StartLine);
        Assert.AreEqual("select 3", statements[2].Text);
    }

    /// <summary>
    /// **字符串里的分号不能切**。切错的后果不是报错,是把半条语句发出去 ——
    /// 而那半条可能正好是一条能跑通但语义完全不同的 SQL。
    /// </summary>
    [TestMethod]
    public void 切分_不切字符串里的分号()
    {
        const string sql = "select 'a;b' as x, 'it''s; fine' as y";

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(sql, SqlDialect.PostgreSql);

        Assert.AreEqual(1, statements.Count);
        StringAssert.Contains(statements[0].Text, "a;b");
    }

    /// <summary>注释里的分号同理。</summary>
    [TestMethod]
    public void 切分_不切注释里的分号()
    {
        const string sql = "select 1 -- 见 §3; §4\n; select 2 /* 这里也有; */";

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(sql, SqlDialect.PostgreSql);

        Assert.AreEqual(2, statements.Count);
    }

    /// <summary>PG 的美元引用:函数体里全是分号,不认它就切得稀碎。</summary>
    [TestMethod]
    public void 切分_认PG的美元引用()
    {
        const string sql = """
            create function f() returns int as $$
            begin
              perform 1;
              return 2;
            end;
            $$ language plpgsql;
            select 9
            """;

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(sql, SqlDialect.PostgreSql);

        Assert.AreEqual(2, statements.Count, "函数体里的三个分号不该把它切开。");
        StringAssert.Contains(statements[0].Text, "language plpgsql");
        Assert.AreEqual("select 9", statements[1].Text);
    }

    /// <summary>各方言的定界符:MySQL 反引号、SQL Server 方括号。</summary>
    [TestMethod]
    public void 切分_认各方言的标识符定界符()
    {
        Assert.AreEqual(1, SqlStatementSplitter.Split("select `a;b` from t", SqlDialect.MySql).Count);
        Assert.AreEqual(1, SqlStatementSplitter.Split("select [a;b] from t", SqlDialect.SqlServer).Count);
        Assert.AreEqual(1, SqlStatementSplitter.Split("select \"a;b\" from t", SqlDialect.PostgreSql).Count);
    }

    /// <summary><c>Ctrl+Enter</c> 执行光标所在语句:光标停在分号后面时取前一条(那是刚敲完的那条)。</summary>
    [TestMethod]
    public void 切分_按光标取当前语句()
    {
        const string sql = "select 1;\nselect 2;\nselect 3";

        SqlStatement? first = SqlStatementSplitter.StatementAt(sql, SqlDialect.PostgreSql, 3);
        SqlStatement? second = SqlStatementSplitter.StatementAt(sql, SqlDialect.PostgreSql, 14);

        Assert.AreEqual("select 1", first?.Text);
        Assert.AreEqual("select 2", second?.Text);
    }

    // ───────────────────────── 护栏 ─────────────────────────

    /// <summary>
    /// <b>无 WHERE 的 UPDATE/DELETE 是红档。</b> 这是整套护栏最重要的一条:
    /// 它与 DROP 的区别只是"看起来无害",后果是一样的。
    /// </summary>
    [TestMethod]
    public void 护栏_无WHERE的写操作是红档()
    {
        SqlVerdict noWhere = SqlGuard.Judge("delete from orders", SqlEnvironment.Development, false, SqlDialect.MySql);
        SqlVerdict withWhere = SqlGuard.Judge("delete from orders where id = 1", SqlEnvironment.Development, false, SqlDialect.MySql);

        Assert.AreEqual(SqlRisk.Red, noWhere.Risk);
        Assert.IsTrue(noWhere.RequiresConfirmation, "红档在任何环境下都要确认。");
        Assert.AreEqual(SqlRisk.Yellow, withWhere.Risk);
        Assert.IsFalse(withWhere.RequiresConfirmation, "开发环境的有界写不该弹框 —— 弹多了用户就会无脑点确定。");
    }

    /// <summary>
    /// <b>字符串里的 where 不算 where。</b> 不挖空字面量的话,
    /// <c>delete from t where note = 'no where clause'</c> 与
    /// <c>delete from t /* where */</c> 会被判反,护栏就废了。
    /// </summary>
    [TestMethod]
    public void 护栏_字面量与注释里的关键字不算数()
    {
        SqlVerdict fake = SqlGuard.Judge(
            "delete from t -- where id = 1", SqlEnvironment.Development, false, SqlDialect.MySql);
        SqlVerdict real = SqlGuard.Judge(
            "delete from t where note = 'no where clause'", SqlEnvironment.Development, false, SqlDialect.MySql);

        Assert.AreEqual(SqlRisk.Red, fake.Risk, "注释里的 where 不是 where。");
        Assert.AreEqual(SqlRisk.Yellow, real.Risk, "字面量里的 where 不该让真 WHERE 失效。");
    }

    /// <summary>生产环境下黄档也要确认,红档还要手打对象名。</summary>
    [TestMethod]
    public void 护栏_生产环境更严()
    {
        SqlVerdict yellow = SqlGuard.Judge("insert into t values (1)", SqlEnvironment.Production, false, SqlDialect.MySql);
        SqlVerdict red = SqlGuard.Judge("drop table orders", SqlEnvironment.Production, false, SqlDialect.MySql);

        Assert.IsTrue(yellow.RequiresConfirmation);
        Assert.IsTrue(red.RequiresTypedName, "生产环境的红档要用户手打对象名。");
        Assert.AreEqual("ORDERS", red.TargetObject);
    }

    /// <summary>
    /// 只读连接:黄红两档在**发出之前**被拒。
    /// 不是靠数据库权限 —— 用户可能就是用 root 连的。
    /// </summary>
    [TestMethod]
    public void 护栏_只读连接在发出前拒掉写操作()
    {
        SqlVerdict write = SqlGuard.Judge("update t set a = 1 where id = 2", SqlEnvironment.Development, true, SqlDialect.MySql);
        SqlVerdict read = SqlGuard.Judge("select * from t", SqlEnvironment.Development, true, SqlDialect.MySql);

        Assert.IsTrue(write.BlockedByReadOnly);
        Assert.IsFalse(write.CanRunSilently);
        Assert.IsFalse(read.BlockedByReadOnly, "只读连接不该挡住查询。");
        Assert.IsTrue(read.CanRunSilently);
    }

    /// <summary>
    /// <b>PostgreSQL 的「数据修改型 CTE」把写动词藏在括号里,而收尾的是 SELECT。</b>
    /// <para>
    ///     <c>with d as (delete from orders returning id) select count(*) from d</c>
    /// </para>
    /// <para>
    /// 深度 0 上收尾的是 <c>SELECT</c>,所以只看深度 0 会判成绿档 —— 而这条语句**删光整张表**。
    /// 绿档意味着两件事同时失效:只读连接不再拦它;它还会被当成"安全语句"送去跑
    /// <c>EXPLAIN ANALYZE</c>(<c>SqlQueryTabViewModel</c> 只给绿档 analyze),
    /// 于是"我只想看看执行计划"也真删。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 护栏_数据修改型CTE不是查询()
    {
        SqlVerdict verdict = SqlGuard.Judge(
            "with d as (delete from orders returning id) select count(*) from d",
            SqlEnvironment.Development,
            readOnly: true,
            SqlDialect.PostgreSql);

        Assert.AreEqual(SqlRisk.Red, verdict.Risk, "括号里的 DELETE 才是这条语句的真动词。");
        Assert.IsTrue(verdict.BlockedByReadOnly, "只读连接必须在发出之前拒掉它。");
        Assert.IsFalse(verdict.CanRunSilently);
    }

    /// <summary>
    /// <c>UPDATE</c> / <c>INSERT</c> 版本的同一件事 —— 判据不能只对 <c>DELETE</c> 生效。
    /// </summary>
    [TestMethod]
    public void 护栏_数据修改型CTE认得出UPDATE与INSERT()
    {
        foreach (string sql in (string[])
        [
            "with d as (update orders set paid = true returning id) select count(*) from d",
            "with d as (insert into audit(id) select id from orders returning id) select count(*) from d"
        ])
        {
            SqlVerdict verdict = SqlGuard.Judge(sql, SqlEnvironment.Development, readOnly: true, SqlDialect.PostgreSql);
            Assert.AreNotEqual(SqlRisk.Green, verdict.Risk, $"这条会写库,不该是绿档:{sql}");
            Assert.IsTrue(verdict.BlockedByReadOnly, $"只读连接必须拒掉:{sql}");
        }
    }

    /// <summary>
    /// <b><c>EXPLAIN ANALYZE</c> 会真的把语句跑一遍</b>(PG 与 MySQL 都是),对 <c>DELETE</c> 就是真删。
    /// <para>
    /// <c>IDialectPack.ExplainSql</c> 上写着"绿档之外的语句一律不给 analyze",
    /// 而那条纪律只有在护栏把 <c>EXPLAIN ANALYZE</c> 的真动词挖出来之后才成立 ——
    /// 否则 <c>explain analyze delete …</c> 自己就是绿档,闸门等于没有。
    /// </para>
    /// <para>不带 <c>ANALYZE</c> 的 <c>EXPLAIN</c> 只出计划、不执行,仍是绿档。</para>
    /// </summary>
    [TestMethod]
    public void 护栏_ExplainAnalyze按被解释的那条语句定级()
    {
        SqlVerdict analyze = SqlGuard.Judge(
            "explain analyze delete from orders", SqlEnvironment.Development, readOnly: true, SqlDialect.PostgreSql);
        Assert.AreEqual(SqlRisk.Red, analyze.Risk, "ANALYZE 会真跑,所以定级要看 DELETE。");
        Assert.IsTrue(analyze.BlockedByReadOnly);

        // PG 的括号选项写法:ANALYZE 在深度 1,真动词仍在深度 0。
        SqlVerdict parenthesised = SqlGuard.Judge(
            "explain (analyze, buffers) delete from orders",
            SqlEnvironment.Development,
            readOnly: true,
            SqlDialect.PostgreSql);
        Assert.AreEqual(SqlRisk.Red, parenthesised.Risk);
        Assert.IsTrue(parenthesised.BlockedByReadOnly);

        // 不带 ANALYZE 的 EXPLAIN 不执行,照旧绿档 —— 别把一条无害的语句也拦了。
        SqlVerdict plan = SqlGuard.Judge(
            "explain delete from orders", SqlEnvironment.Development, readOnly: true, SqlDialect.PostgreSql);
        Assert.AreEqual(SqlRisk.Green, plan.Risk, "不带 ANALYZE 的 EXPLAIN 只出计划,不该被拦。");
        Assert.IsFalse(plan.BlockedByReadOnly);

        // EXPLAIN ANALYZE SELECT 确实会执行,但执行的是一条查询 —— 仍是绿档。
        SqlVerdict query = SqlGuard.Judge(
            "explain analyze select * from orders", SqlEnvironment.Development, readOnly: true, SqlDialect.PostgreSql);
        Assert.AreEqual(SqlRisk.Green, query.Risk);
    }

    /// <summary>
    /// <c>SELECT … INTO</c> 是写操作,而首词是 <c>SELECT</c>。
    /// <para>
    /// SQL Server / PG 上它**建表**,MySQL 的 <c>INTO OUTFILE</c> 往**服务端磁盘**写文件。
    /// 判成绿档的话,一条"只读"连接就能在服务端建表、落文件。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 护栏_SelectInto是写操作()
    {
        SqlVerdict table = SqlGuard.Judge(
            "select * into orders_copy from orders", SqlEnvironment.Development, readOnly: true, SqlDialect.SqlServer);
        Assert.AreNotEqual(SqlRisk.Green, table.Risk, "SELECT … INTO 会建表。");
        Assert.IsTrue(table.BlockedByReadOnly);

        SqlVerdict outfile = SqlGuard.Judge(
            "select * from orders into outfile '/tmp/x.csv'",
            SqlEnvironment.Development,
            readOnly: true,
            SqlDialect.MySql);
        Assert.AreNotEqual(SqlRisk.Green, outfile.Risk, "INTO OUTFILE 往服务端磁盘写文件。");
        Assert.IsTrue(outfile.BlockedByReadOnly);

        // 普通查询不受影响 —— 这一条防的是上面两条判据把所有 SELECT 都拦掉。
        SqlVerdict plain = SqlGuard.Judge(
            "select id, name from orders where id = 1",
            SqlEnvironment.Development,
            readOnly: true,
            SqlDialect.MySql);
        Assert.AreEqual(SqlRisk.Green, plain.Risk);
        Assert.IsFalse(plain.BlockedByReadOnly);
    }

    /// <summary><c>WITH ... DELETE</c> 的真正动词在 CTE 之后 —— 只看首词会把它判成查询。</summary>
    [TestMethod]
    public void 护栏_穿透CTE找到真正的动词()
    {
        SqlVerdict verdict = SqlGuard.Judge(
            "with doomed as (select id from t) delete from t", SqlEnvironment.Development, false, SqlDialect.PostgreSql);

        Assert.AreEqual(SqlRisk.Red, verdict.Risk);
    }

    /// <summary>
    /// <b>子查询里的 WHERE 不是本语句的 WHERE。</b>
    /// <c>update t set a = (select … where …)</c> 是**无界 UPDATE**——它会改光整张表。
    /// 不按括号深度过滤就会被判成有界,直接放行。
    /// </summary>
    [TestMethod]
    public void 护栏_子查询里的WHERE不算本句有界()
    {
        SqlVerdict unbounded = SqlGuard.Judge(
            "update t set a = (select max(x) from u where y = 1)", SqlEnvironment.Development, false, SqlDialect.MySql);
        SqlVerdict bounded = SqlGuard.Judge(
            "update t set a = (select max(x) from u where y = 1) where id = 9", SqlEnvironment.Development, false, SqlDialect.MySql);

        Assert.AreEqual(SqlRisk.Red, unbounded.Risk, "它没有自己的 WHERE,会改光整张表。");
        Assert.AreEqual(SqlRisk.Yellow, bounded.Risk);
    }

    /// <summary>认不出的语句按黄档 —— 宁可多问一次,也不要把看不懂的语句静默发到生产库。</summary>
    [TestMethod]
    public void 护栏_认不出的语句按写操作对待()
    {
        SqlVerdict verdict = SqlGuard.Judge("vacuum full", SqlEnvironment.Production, false, SqlDialect.PostgreSql);

        Assert.AreEqual(SqlRisk.Yellow, verdict.Risk);
        Assert.IsTrue(verdict.RequiresConfirmation);
    }

    /// <summary>整批取最高危的那一档。</summary>
    [TestMethod]
    public void 护栏_整批取最危险的一条()
    {
        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(
            "select 1; update t set a=1 where id=2; drop table x", SqlDialect.MySql);

        (IReadOnlyList<SqlVerdict> each, SqlVerdict overall) =
            SqlGuard.JudgeBatch(statements, SqlEnvironment.Development, false, SqlDialect.MySql);

        Assert.AreEqual(3, each.Count);
        Assert.AreEqual(SqlRisk.Red, overall.Risk);
        Assert.IsTrue(overall.RequiresConfirmation);
    }

    // ───────────────────────── 错误定位 ─────────────────────────

    /// <summary>偏移换行列:<c>\r</c> 算一个字符(与 PG 的 Position 口径一致)。</summary>
    [TestMethod]
    public void 错误定位_偏移换算行列()
    {
        const string text = "select id\r\n  from t\r\n where x";

        Assert.AreEqual((1, 1), SqlErrorLocator.OffsetToLineColumn(text, 0));
        Assert.AreEqual((2, 3), SqlErrorLocator.OffsetToLineColumn(text, 13));
        Assert.AreEqual((3, 2), SqlErrorLocator.OffsetToLineColumn(text, 22));
    }

    /// <summary>拿不到位置时如实返回"不知道",而不是瞎指一行。</summary>
    [TestMethod]
    public void 错误定位_没有位置信息时不猜()
    {
        var statement = new SqlStatement("select 1", 5, 1, 0);

        (int? line, int? column) = SqlErrorLocator.Locate(new InvalidOperationException("boom"), statement, SqlDialect.Sqlite);

        Assert.IsNull(line);
        Assert.IsNull(column);
    }

    /// <summary>MySQL 的语法错误消息里带 <c>at line N</c>,要能抠出来并叠加语句起始行。</summary>
    [TestMethod]
    public void 错误定位_MySQL从消息里抠行号()
    {
        var statement = new SqlStatement("select\n  bad syntax", StartLine: 10, StartColumn: 1, StartOffset: 0);
        var error = new InvalidOperationException(
            "You have an error in your SQL syntax; check the manual ... at line 2");

        (int? line, _) = SqlErrorLocator.Locate(error, statement, SqlDialect.MySql);

        Assert.AreEqual(11, line, "语句起始行 10 + 句内第 2 行 - 1 = 11。");
    }
    /// <summary>
    /// <b>SQLite 上读表达式列不能把进程打死。</b>
    /// <para>
    /// <c>Microsoft.Data.Sqlite</c> 的 <c>GetChars</c> 内部走 <c>GetStream</c>,后者要调原生的
    /// <c>sqlite3_table_column_metadata</c> 去问"这一列属于哪张表的哪一列"——
    /// 而<b>表达式列没有表</b>(<c>select 1+1</c>、任何函数或聚合结果、
    /// <c>EXPLAIN QUERY PLAN</c> 的每一列都是),这一问就是一个 <c>0xC0000005</c> 访问冲突。
    /// </para>
    /// <para>
    /// <b>这条用例的形态很特别:它断言的是"进程还活着"。</b> 访问冲突不是异常,
    /// <c>try/catch</c> 接不住,测试宿主会**整个崩掉**(<c>测试主机进程崩溃</c>),
    /// 于是失败表现为"测试运行已中止"而不是一条红。真机上的表现是:
    /// 用户在 SQLite 连接上点一下"计划",整个 VelaShell 没了。
    /// </para>
    /// <para>发现它的过程也值得记:走 <c>SqlExecutor</c> 真跑一次 <c>EXPLAIN QUERY PLAN</c> 才炸的——
    /// 之前所有 SQLite 用例读的都是**真表的列**,那条路上 <c>GetChars</c> 是安全的。</para>
    /// </summary>
    [TestMethod]
    public async Task 表达式列_SQLite上读回来不会把进程打死()
    {
        string file = Path.Combine(Path.GetTempPath(), $"expr-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await SqlSession.OpenAsync(
                new WorkspaceConnectRequest { SessionId = "expr", Host = file, Port = 1 },
                SqlDialect.Sqlite, new Loc("zh-Hans"));

            await using (DbCommand seed = session.Metadata.Raw.CreateCommand())
            {
                seed.CommandText = "create table t(id integer primary key, name text)";
                await seed.ExecuteNonQueryAsync();
            }

            var executor = new SqlExecutor(session.Dialect, session.Pack);
            // 每一条都只产出表达式列 —— 一条真表列都没有。
            string[] statements =
            [
                "select 1 + 1",
                "select upper('abc')",
                "select count(*) from t",
                "select group_concat(name) from t",
                "explain query plan select * from t where id = 1"
            ];

            foreach (string sql in statements)
            {
                IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
                    session.Metadata.Raw,
                    SqlStatementSplitter.Split(sql, SqlDialect.Sqlite),
                    SqlFetchOptions.Default,
                    30,
                    null,
                    TestContext.CancellationTokenSource.Token);

                Assert.IsTrue(results[0].Succeeded, $"{sql} → {results[0].Error?.Message}");
                Assert.IsTrue(results[0].ResultSets.Count > 0, $"{sql} 没给出结果集。");
            }

            // 跑到这里就说明进程还活着 —— 这正是本用例要的那个信号。
            // 这里**没有**一句 Assert.IsTrue(true):访问冲突会让测试宿主整个崩掉,
            // 那句话根本没机会执行,写了也只是让分析器报 MSTEST0032(恒真断言)。
            // 真正的判据是"这个方法返回了",外加上面循环里每条语句的两句断言。
        }
        finally
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }

}

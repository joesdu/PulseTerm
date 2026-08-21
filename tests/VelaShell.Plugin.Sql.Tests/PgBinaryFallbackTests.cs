using System.Data.Common;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// <b>PostgreSQL 全文本降级</b>(缺陷 #4)。
/// <para>
/// 真机现象:在 PG 会话里执行 <c>select * from pg_class limit 20</c>,
/// 报 <c>42883: no binary output function available for type aclitem</c>,<b>整个结果集一行都拿不到</b>。
/// 根因是 <c>pg_class.relacl</c> 的类型是 <c>aclitem[]</c>,驱动默认按二进制格式要数据,
/// 而 <c>aclitem</c> 在服务端只有文本表示、没有二进制输出函数,服务端在吐第一行时就 ereport 了。
/// 这个失败发生在"读某一格"<b>之下的一层</b>,所以既有的单元格级容错完全接不住。
/// </para>
/// <para>
/// 这一组分两层:
/// <list type="number">
///   <item><b>判据</b>(离线):只认"二进制输出函数缺席"这一种失败,不认打错的函数名 ——
///         42883 本身是用户敲错函数名的日常错误,只看 SQLSTATE 就重试等于把每一条打错字的
///         语句都发两遍。这一层不需要真机,用一个假的 <see cref="DbException" /> 就钉得死。</item>
///   <item><b>真机</b>:系统表真的查得出来,而且降级重跑<b>不会把写操作做两遍</b> ——
///         后者是这条修复唯一真正危险的地方,必须对着真服务端证。</item>
/// </list>
/// 按仓库惯例:拿不到 PostgreSQL 时 <c>Inconclusive</c> 而不是失败。
/// </para>
/// </summary>
[TestClass]
public sealed class PgBinaryFallbackTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>本组专用的库(与 PostgreSqlOpsTests 同一个,那一组也是现建现用)。</summary>
    private const string Database = "ops_pg";

    /// <summary>写操作那条用例的场地。名字带前缀,免得和别人的探针表重名。</summary>
    private const string ProbeTable = "vela_acl_fallback_probe";

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ═══════════════════════════ 判据(离线) ═══════════════════════════

    /// <summary>
    /// 判据的正例与反例一次钉死。
    /// <para>
    /// 反例里最要紧的是第 2 条:<c>select nosuchfunction(1)</c> 的 SQLSTATE <b>也是 42883</b>
    /// (实测 <c>Routine=ParseFuncOrColumn</c>)。如果判据只看 SQLSTATE,
    /// 每一条打错函数名的语句都会被发两遍 —— 查询无所谓,一条
    /// <c>delete … using nosuchfunction(x)</c> 就不好笑了。
    /// </para>
    /// <para>
    /// 这里用假异常而不是真机异常,是因为要覆盖"服务端消息被 <c>lc_messages</c> 翻译过"
    /// 这一档:那种服务端上 <c>Routine</c> 还在、消息已经不是英文了。
    /// 假异常上没有 <c>Routine</c> 属性,正好走的就是"只剩消息可看"的那条回落路径。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 判据_只认二进制输出函数缺席_不认打错的函数名()
    {
        const string BinaryFailure = "42883: no binary output function available for type aclitem";
        const string TypoFailure = "42883: function nosuchfunction(integer) does not exist";

        Assert.IsTrue(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, new FakeDbException("42883", BinaryFailure)),
            "这就是缺陷 #4 的原貌,必须降级重跑。");

        Assert.IsFalse(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, new FakeDbException("42883", TypoFailure)),
            "打错函数名的 SQLSTATE 也是 42883 —— 只看状态码就重试,等于把每条打错字的语句发两遍。");

        Assert.IsFalse(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, new FakeDbException("42703", BinaryFailure)),
            "SQLSTATE 对不上就不该重跑,哪怕消息看着像。");

        Assert.IsFalse(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.MySql, new FakeDbException("42883", BinaryFailure)),
            "别的驱动没有这个开关,而它们的 42883 含义也不一样 —— 只在 PG 上开。");

        Assert.IsFalse(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, new InvalidOperationException(BinaryFailure)),
            "连 DbException 都不是,说明失败根本没到服务端 —— 重跑救不了。");

        Assert.IsFalse(
            SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, new FakeDbException(null, BinaryFailure)),
            "拿不到 SQLSTATE 时宁可不重跑:重试一条我们没看懂的失败才是真的危险。");
    }

    /// <summary>降级说明这条文案两种语言都得有 —— 少一种,那种语言下用户看到的是键名。</summary>
    [TestMethod]
    public void 文案_降级说明中英双份()
    {
        Assert.IsTrue(Loc.KeysOf(chinese: false).Contains("Sql_TextFallback"), "英文表缺 Sql_TextFallback。");
        Assert.IsTrue(Loc.KeysOf(chinese: true).Contains("Sql_TextFallback"), "中文表缺 Sql_TextFallback。");
        Assert.AreNotEqual(
            new Loc("en")["Sql_TextFallback"],
            new Loc("zh-Hans")["Sql_TextFallback"],
            "两种语言给的是同一句话,说明有一边是照抄的。");
    }

    // ═══════════════════════════ 真机 ═══════════════════════════

    /// <summary>
    /// <b>缺陷 #4 的验收用例</b>:<c>select * from pg_class limit 20</c> 必须出数据。
    /// <para>
    /// 三条断言各钉一个失败模式:① 语句成功(没修之前这里就是 42883);
    /// ② 真的有 20 行(别把"空结果集"当成功);
    /// ③ 打了降级标记(说明我们是走回落救回来的,而不是哪天 PG 悄悄给了 aclitem 二进制输出 ——
    /// 那样这条用例会变绿但修复其实已经没在跑,是最坏的一种"绿")。
    /// </para>
    /// <para>
    /// 顺带钉住降级的代价与边界:<c>relacl</c> 的<b>数据源类型名仍然是真的</b>
    /// (降级只把 CLR 类型统一成 <c>String</c>,不影响 <c>GetDataTypeName</c>),
    /// 而 NULL 与非 NULL 依旧分得开(<c>IsDBNull</c> 不受格式影响)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 真机_pg_class_降级之后查得出来()
    {
        await WithPostgresAsync(async raw =>
        {
            // ① 用户原样的那条形态,先证"查得出来、真有数据"。
            SqlStatementResult plain = await RunAsync(raw, "select * from pg_class limit 20");
            Assert.IsTrue(plain.Succeeded, $"pg_class 必须查得出来,实际:{plain.Error?.Message}");
            Assert.AreEqual(1, plain.ResultSets.Count);
            Assert.AreEqual(20, plain.ResultSets[0].Rows.Count, "limit 20 就该给 20 行 —— 空结果集不算成功。");

            // ② 降级那几条断言换一条**取样确定**的语句。
            //
            // 为什么不能直接用上面那条:服务端只在**真要输出某一行的某一格**时才需要
            // aclitem 的二进制输出函数,所以走不走降级取决于"取到的这 20 行里有没有非空 relacl"。
            // 而不带 ORDER BY 的 limit 20 取的是**物理行序**的前 20 行 —— 别的用例在同一个库里
            // 建删一张表就会把它挪动。实测:并行跑时这条会间歇性地取到 20 行全 NULL,
            // 于是压根不触发降级、TextFallback 断言落空。**那不是修复失效,是取样漂了。**
            //
            // order by (relacl is null) 把非空的排到前面。真机上每个库里
            // relacl 非空的有 206 行(information_schema 那批视图自带 PUBLIC 授权),
            // 所以这 20 行必然全是非空,降级必然触发。
            SqlStatementResult result = await RunAsync(
                raw, "select * from pg_class order by (relacl is null), oid limit 20");

            Assert.IsTrue(result.Succeeded, $"pg_class 必须查得出来,实际:{result.Error?.Message}");
            Assert.AreEqual(1, result.ResultSets.Count);
            SqlResultSet set = result.ResultSets[0];
            Assert.AreEqual(20, set.Rows.Count, "limit 20 就该给 20 行 —— 空结果集不算成功。");
            Assert.IsTrue(result.TextFallback, "这一条本来就该走降级;不走降级说明修复没在生效。");
            Assert.IsTrue(set.TextFallback, "结果集这一层也要带标记,界面才提示得出来。");

            int acl = IndexOf(set, "relacl");
            Assert.AreNotEqual(-1, acl, "pg_class 里应当有 relacl —— 它正是惹祸的那一列。");
            Assert.AreEqual(
                "aclitem[]", set.Columns[acl].ProviderTypeName,
                "降级只统一 CLR 类型,数据源类型名必须还是真的,否则用户连自己在看什么都不知道。");
            Assert.AreEqual(
                "String", set.Columns[acl].ClrTypeName,
                "降级的代价就在这里:所有列的 CLR 类型都变成 String。这条断言就是那句注释的证据。");
            // NULL 判定不受降级影响 —— 这一条要的是"降级之后 NULL 仍然是 NULL,不是空串"。
            // 上面那条语句刻意把非空 relacl 排到前面,所以这里换一张必然有 NULL 的取样来问同一件事:
            // relnamespace 之类恒非空的列没法验 NULL,而 relpartbound 在非分区表上恒为 NULL。
            int partBound = IndexOf(set, "relpartbound");
            Assert.AreNotEqual(-1, partBound, "pg_class 里应当有 relpartbound。");
            Assert.IsTrue(
                set.Rows.All(r => r[partBound].Kind == SqlCellKind.Null),
                "这 20 行都不是分区,relpartbound 必须是 NULL —— 降级之后 NULL 不能变成空串。");
            Assert.IsTrue(
                set.Rows.All(r => r[acl].Kind != SqlCellKind.Null),
                "取样刻意排了非空 relacl 在前,这 20 行不该有 NULL —— 有就是排序没生效,上面的降级断言也就不作数了。");
        });
    }

    /// <summary>
    /// 别的系统表也得能看。
    /// <para>
    /// 这几张里 <c>pg_proc</c> / <c>pg_type</c> / <c>information_schema.columns</c> 在这台真机上
    /// <b>二进制格式就能过</b>(它们的 acl 列前若干行恰好是 NULL —— 服务端只在真要输出
    /// 非 NULL 的 aclitem 时才去找二进制输出函数)。这一点很值得钉:<b>同一条语句会因为数据不同而
    /// 时好时坏</b>,所以判据不能建立在"哪张表危险"上,只能建立在服务端报的错上。
    /// <c>pg_namespace</c> 则和 <c>pg_class</c> 一样必炸(<c>nspacl</c> 通常非空)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 真机_其它系统表也查得出来()
    {
        await WithPostgresAsync(async raw =>
        {
            string[] probes =
            [
                "select * from pg_class limit 20",
                "select * from pg_proc limit 20",
                "select * from pg_type limit 20",
                "select * from pg_namespace limit 20",
                "select * from information_schema.columns limit 20"
            ];
            foreach (string sql in probes)
            {
                SqlStatementResult result = await RunAsync(raw, sql);
                Assert.IsTrue(result.Succeeded, $"{sql} 查不出来:{result.Error?.Message}");
                Assert.AreEqual(1, result.ResultSets.Count, sql);
                Assert.IsTrue(result.ResultSets[0].Rows.Count > 0, $"{sql} 给了个空结果集。");
                TestContext.WriteLine(
                    $"{sql} -> {result.ResultSets[0].Rows.Count} 行,降级={result.TextFallback}");
            }
        });
    }

    /// <summary>
    /// <b>打错函数名不会被拖去重跑。</b>
    /// <para>
    /// 这一条是判据的真机反例:同样是 42883,同样来自 PG,但它不是"二进制输出函数缺席",
    /// 所以必须原样失败。要是这里变成"成功"或者带上降级标记,说明判据放得太宽了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 真机_打错函数名照样失败而且不降级()
    {
        await WithPostgresAsync(async raw =>
        {
            SqlStatementResult result = await RunAsync(raw, "select nosuchfunction(1)");

            Assert.IsFalse(result.Succeeded, "函数不存在就该失败 —— 降级不是用来兜住打字错误的。");
            Assert.IsFalse(result.TextFallback, "不该给它打降级标记。");
            Assert.IsInstanceOfType<DbException>(result.Error, "失败该是服务端报回来的。");
            var db = (DbException)result.Error!;
            Assert.AreEqual("42883", db.SqlState, "它的 SQLSTATE 与 aclitem 那条一模一样 —— 这正是判据要分开的两件事。");
            Assert.IsFalse(
                SqlExecutor.ShouldRefetchAsText(SqlDialect.PostgreSql, db),
                "拿真机异常再问一遍判据:它不该被重跑。");
        });
    }

    /// <summary>
    /// <b>降级重跑不会把写操作做两遍。</b>
    /// <para>
    /// 这是整条修复唯一真正危险的地方,所以要对着真服务端证,而不是推理。
    /// 场景是最刁的那一种:<c>… RETURNING *</c> 而且返回列里有 <c>aclitem[]</c> ——
    /// 它<b>既是写语句、又会命中降级路径</b>。服务端是在输出结果行时才报错的,
    /// 也就是写动作那时候<b>已经跑过了</b>,所以"重跑一次会不会写两遍"是个真问题。
    /// 答案是不会:PG 在语句失败时把该语句的效果整体回滚(autocommit 下是隐式的单语句事务)。
    /// </para>
    /// <para>
    /// <b>先 INSERT 后 DELETE,顺序是有讲究的</b>:验重复执行只能靠 INSERT。
    /// <c>delete … where id &lt;= 3</c> 跑两遍和跑一遍<b>剩下的行数一样</b>(第二遍无事可做),
    /// 所以"剩几行"这个口径对 DELETE 是<b>钝的</b>;<c>insert … returning</c> 跑两遍会实打实多出 3 行。
    /// (这不是推演出来的:反向验证时把重跑改成故意跑两遍,DELETE 那条的行数断言确实没红,
    /// 红的是 <c>RETURNING</c> 给回 0 行那一条。)
    /// DELETE 仍然留着,因为它是这条回落路径上最吓人的那个动词,而且它钉的是另一件事 ——
    /// <c>RETURNING</c> 必须把真删掉的那 3 行原样带回来。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 真机_降级重跑不会把写操作做两遍()
    {
        await WithPostgresAsync(async raw =>
        {
            await ExecAsync(raw, $"drop table if exists {ProbeTable}");
            await ExecAsync(raw, $"create table {ProbeTable}(id int, acl aclitem[])");
            // acl 值直接从系统表借一份现成的,免得手写 aclitem 字面量在不同大版本上写法有出入。
            await ExecAsync(raw, $"""
                insert into {ProbeTable}
                select g, (select relacl from pg_class where relacl is not null limit 1)
                  from generate_series(1, 5) g
                """);
            try
            {
                Assert.AreEqual(5L, await ScalarAsync(raw, $"select count(*) from {ProbeTable}"), "场地没铺好。");

                // ① INSERT … RETURNING:重复执行会留下痕迹,这一条才是"没跑两遍"的证据。
                SqlStatementResult inserted = await RunAsync(raw, $"""
                    insert into {ProbeTable}
                    select g, (select relacl from pg_class where relacl is not null limit 1)
                      from generate_series(6, 8) g
                    returning *
                    """);

                Assert.IsTrue(inserted.Succeeded, $"带 aclitem 的 RETURNING 也该救得回来:{inserted.Error?.Message}");
                Assert.IsTrue(inserted.TextFallback, "这一条必须是走降级成功的,否则这个用例什么也没证。");
                Assert.AreEqual(3, inserted.ResultSets[0].Rows.Count, "RETURNING 该给回新插的那 3 行。");
                Assert.AreEqual(
                    8L, await ScalarAsync(raw, $"select count(*) from {ProbeTable}"),
                    "必须是 8 行:11 行说明降级重跑把 INSERT 做了两遍,5 行说明压根没插进去。");

                // ② DELETE … RETURNING:钉的是"删掉的行原样带得回来"。
                SqlStatementResult deleted = await RunAsync(raw, $"delete from {ProbeTable} where id <= 3 returning *");

                Assert.IsTrue(deleted.Succeeded, $"DELETE … RETURNING 也该救得回来:{deleted.Error?.Message}");
                Assert.IsTrue(deleted.TextFallback, "它同样该是走降级成功的。");
                Assert.AreEqual(
                    3, deleted.ResultSets[0].Rows.Count,
                    "RETURNING 该给回删掉的那 3 行 —— 给 0 行说明第一遍已经删过了(也就是跑了两遍)。");
                Assert.AreEqual(
                    5L, await ScalarAsync(raw, $"select count(*) from {ProbeTable}"),
                    "8 行删 3 行该剩 5 行。");
            }
            finally
            {
                await ExecAsync(raw, $"drop table if exists {ProbeTable}");
            }
        });
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>走真正的执行层跑一条语句(这一组要验的就是执行层,不能绕过它)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>那一条的结果。</returns>
    private async Task<SqlStatementResult> RunAsync(DbConnection connection, string sql)
    {
        var executor = new SqlExecutor(SqlDialect.PostgreSql, new PostgreSqlPack());
        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(sql, SqlDialect.PostgreSql);
        IReadOnlyList<SqlStatementResult> results = await executor.ExecuteAsync(
            connection, statements, SqlFetchOptions.Default, 60, null,
            TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(1, results.Count, "这一组每次只发一条语句。");
        return results[0];
    }

    /// <summary>找一列的序号;没有则 -1。</summary>
    /// <param name="set">结果集。</param>
    /// <param name="name">列名。</param>
    /// <returns>序号。</returns>
    private static int IndexOf(SqlResultSet set, string name)
    {
        for (int i = 0; i < set.Columns.Count; i++)
        {
            if (string.Equals(set.Columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>拿一条连着 <see cref="Database" /> 的连接跑一段;没有 PostgreSQL 就 <c>Inconclusive</c>。</summary>
    /// <param name="body">拿到已打开连接之后要做的事。</param>
    /// <returns>任务。</returns>
    private static async Task WithPostgresAsync(Func<DbConnection, Task> body)
    {
        SqlConnection? bootstrap = await TryOpenAsync("postgres");
        if (bootstrap is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }
        await using (bootstrap)
        {
            object? exists = await ScalarAsync(
                bootstrap.Raw, $"select 1 from pg_catalog.pg_database where datname = '{Database}'");
            if (exists is null)
            {
                // PG 没有 CREATE DATABASE IF NOT EXISTS,而且它不能在事务里跑,只能先查再单独发。
                await ExecAsync(bootstrap.Raw, $"create database {new PostgreSqlPack().QuoteIdentifier(Database)}");
            }
        }

        await using SqlConnection connection = await OpenAsync(Database);
        await body(connection.Raw);
    }

    /// <summary>连一条到指定库。</summary>
    /// <param name="database">库名。</param>
    /// <returns>已打开的连接。</returns>
    private static async Task<SqlConnection> OpenAsync(string database)
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "pg-text-fallback",
            Host = "127.0.0.1",
            Port = 55432,
            Username = "postgres",
            Password = "velaspike",
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = database }
        };
        return await SqlConnection.ConnectAsync(
            SqlSettings.From(request, SqlDialect.PostgreSql),
            "127.0.0.1", 55432, "postgres", "velaspike", Localization, null);
    }

    /// <summary>连不上就返回 <see langword="null" />(按仓库惯例早退跳过)。</summary>
    /// <param name="database">库名。</param>
    /// <returns>连接或 <see langword="null" />。</returns>
    private static async Task<SqlConnection?> TryOpenAsync(string database)
    {
        try
        {
            return await OpenAsync(database);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>铺场地用:发一条语句,失败就让用例失败。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>任务。</returns>
    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>取一个标量(核对行数用,刻意不走执行层 —— 用被测件核对被测件等于没核对)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>标量值。</returns>
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// 一个只带 SQLSTATE 与消息的假 <see cref="DbException" />。
    /// <para>
    /// 判据用的是 <see cref="DbException.SqlState" /> 这个 BCL 门面(执行层不许引用驱动类型),
    /// 所以判据完全测得动,不用把 <c>Npgsql.PostgresException</c> 的构造参数顺序抄进测试里 ——
    /// 那种抄法会在驱动升级时以"编译不过"的形式报复回来。
    /// </para>
    /// <para>
    /// 它<b>没有</b> <c>Routine</c> 属性,于是判据走的是"只剩消息可看"那条回落路径 ——
    /// 那正是 <c>lc_messages</c> 非英文的服务端之外我们唯一能凭的证据。
    /// </para>
    /// </summary>
    /// <param name="sqlState">SQLSTATE;<see langword="null" /> 表示服务端没给。</param>
    /// <param name="message">消息。</param>
    private sealed class FakeDbException(string? sqlState, string message) : DbException(message)
    {
        /// <inheritdoc />
        public override string? SqlState => sqlState;
    }
}

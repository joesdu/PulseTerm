using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// MySQL 方言包的 <b>M4 资产真机验收</b>:执行计划(能力组 7)、运维面(能力组 8)、表设计器 DDL(能力组 5)。
/// <para>
/// 这一组的断言几乎全是"真发给服务端看它认不认",而不是比字符串。理由在 §3.4:
/// 方言资产最容易出的错是<b>语法合法、语义错误</b>,只比文本的测试对这一类完全无感 ——
/// 而 M4 这批资产里正好有两条只有真机才证得了的:
/// <c>EXPLAIN ANALYZE</c> 会不会真把语句跑一遍、锁查询在没人争锁时是不是干净地返回空。
/// </para>
/// <para>
/// 按仓库惯例按环境早退:拿不到 MySQL 时 <c>Inconclusive</c> 而不是失败。
/// 用自己的库 <c>ops_mysql</c>(没有就现建),不去动别的用例的 <c>pack_verify</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class MySqlOpsTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>本组专用的库。与 <c>DialectPackIntegrationTests</c> 的 <c>pack_verify</c> 分开,免得互相踩。</summary>
    private const string Database = "ops_mysql";

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ═══════════════════════════ 执行计划(能力组 7) ═══════════════════════════

    /// <summary>
    /// <c>EXPLAIN</c> 在真机上给得出计划,而且给的是<b>传统 12 列表格</b>那一形态。
    /// <para>
    /// 三条断言分别对着一个失败模式:① 一行都没有(发出去的根本不是计划语句);
    /// ② 只有一列(那是 <c>EXPLAIN ANALYZE</c> / <c>FORMAT=TREE</c> 的形状,渲染代码会认错);
    /// ③ 命中了索引却不说 —— 那样这一栏就白开了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_真机给出传统12列并点出命中的索引()
    {
        await WithMySqlAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists plan_probe");
            await ExecAsync(raw, """
                create table plan_probe(
                  id int not null auto_increment primary key,
                  name varchar(50) null,
                  tag varchar(20) null,
                  key ix_plan_probe_tag (tag))
                """);
            await ExecAsync(raw, "insert into plan_probe(name, tag) values('a', 'x'), ('b', 'y')");

            var pack = new MySqlPack();
            const string Query = "select id, name from plan_probe where tag = 'x'";
            string? explain = pack.ExplainSql(Query, analyze: false);
            Assert.IsNotNull(explain, "MySQL 是有执行计划的,这里不该是 null。");
            Assert.AreEqual($"EXPLAIN {Query}", explain, "计划语句只是给用户 SQL 加个前缀,不套派生表也不改写它。");

            (int columns, List<string> rows) = await ReadGridAsync(raw, explain);
            Assert.AreEqual(12, columns,
                $"EXPLAIN 的传统形态是 12 列(id/select_type/table/.../Extra),实际 {columns} 列 —— "
                + "一列说明拿到的是 TREE 形状,渲染代码会照错的形状画。");
            Assert.IsTrue(rows.Count > 0, "计划一行都没有,说明发出去的根本不是 EXPLAIN。");

            string text = string.Join("\n", rows);
            Assert.IsTrue(
                text.Contains("ix_plan_probe_tag", StringComparison.Ordinal),
                $"等值条件命中了索引,计划里必须点出索引名,实际拿到:\n{text}");

            // 末尾分号要剥掉(编辑器切出来的语句常带终止符)。
            Assert.AreEqual("EXPLAIN select 1", pack.ExplainSql("select 1;  ", analyze: false));
            Assert.AreEqual("EXPLAIN select 1", pack.ExplainSql("select 1 ; ;", analyze: false));
        });
    }

    /// <summary>
    /// <b><c>EXPLAIN ANALYZE</c> 真的把 <c>SELECT</c> 跑完 —— 契约把 analyze 标成危险开关不是虚张声势。</b>
    /// <para>
    /// 证法是让计划本身耗时:<c>SELECT SLEEP(1)</c> 对着一张两行的表,真跑就得两秒。
    /// 同时钉死它的<b>结果形状</b>:一列、树形文本、带 <c>actual time</c>(那正是"真跑过"的痕迹,
    /// 不带 analyze 的 <c>EXPLAIN</c> 里没有这一段)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_analyze档真的把查询跑完()
    {
        await WithMySqlAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists analyze_probe");
            await ExecAsync(raw, "create table analyze_probe(id int not null primary key)");
            await ExecAsync(raw, "insert into analyze_probe(id) values(1), (2)");

            var pack = new MySqlPack();
            string? analyzed = pack.ExplainSql("select sleep(1) from analyze_probe", analyze: true);
            Assert.AreEqual("EXPLAIN ANALYZE select sleep(1) from analyze_probe", analyzed);

            var stopwatch = Stopwatch.StartNew();
            (int columns, List<string> rows) = await ReadGridAsync(raw, analyzed!);
            stopwatch.Stop();

            Assert.AreEqual(1, columns, "EXPLAIN ANALYZE 的结果是一列树形文本,不是 12 列表格。");
            Assert.IsTrue(rows.Count > 0, "一行都没有说明这条根本没跑成。");
            StringAssert.Contains(string.Join("\n", rows), "actual time",
                "树里必须有 actual time —— 那是'真的执行过'留下的痕迹。");
            Assert.IsTrue(stopwatch.ElapsedMilliseconds >= 1500,
                $"两行表上 sleep(1) 应当耗时约 2 秒,实际只用了 {stopwatch.ElapsedMilliseconds} ms —— "
                + "要是它没真跑,契约里'analyze 会真删数据'那条警告就落空了,护栏也会被人当成多余的。");
        });
    }

    /// <summary>
    /// <b>analyze 档绝不静默降级</b>:调用方要 analyze,拿到的就必须是 <c>EXPLAIN ANALYZE</c>。
    /// <para>
    /// 顺带钉死一条 8.4.11 的实测形态,并写清<b>它不能当护栏用</b>:
    /// 这个版本的 <c>EXPLAIN ANALYZE</c> 对 <c>DELETE</c> 只回一行
    /// <c>&lt;not executable by iterator executor&gt;</c>,数据一行没少。
    /// 这条断言要是在更新的服务端上挂了,说明 MySQL 把 DML 接进迭代器执行器了 ——
    /// 那时该改的是 <see cref="MySqlPack.ExplainSql" /> 的注释,而<b>不是</b>把护栏往这边挪:
    /// 护栏一直都该留在调用方,按语句种类判(§7.6)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_analyze不降级_而且DML那条实测形态不能当护栏()
    {
        await WithMySqlAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists analyze_danger");
            await ExecAsync(raw, "create table analyze_danger(id int not null primary key)");
            await ExecAsync(raw, "insert into analyze_danger(id) values(1), (2), (3)");

            var pack = new MySqlPack();
            const string Delete = "delete from analyze_danger where id > 0";
            Assert.AreEqual($"EXPLAIN {Delete}", pack.ExplainSql(Delete, analyze: false));
            Assert.AreEqual($"EXPLAIN ANALYZE {Delete}", pack.ExplainSql(Delete, analyze: true),
                "危险开关不许在方言包里被悄悄拧回去 —— 要拦就由调用方明着拦。");

            (_, List<string> rows) = await ReadGridAsync(raw, pack.ExplainSql(Delete, analyze: true)!);
            Assert.AreEqual(3L, await CountAsync(raw, "analyze_danger"),
                "8.4.11 实测:EXPLAIN ANALYZE 的 DELETE 没有真执行。这条挂了说明服务端版本变了行为,"
                + "去更新 ExplainSql 的注释 —— 护栏仍然留在调用方。");
            StringAssert.Contains(string.Join("\n", rows), "not executable by iterator executor",
                "8.4.11 实测原文。同上:它是版本副产物,不是承诺。");
        });
    }

    // ═══════════════════════════ 运维面(能力组 8) ═══════════════════════════

    /// <summary>
    /// 会话列表在真机上跑得通,而且<b>至少看得见自己那一条</b> —— 列序按契约,调用方是按序号读的。
    /// <para>
    /// 三处容易写错的地方一并钉死:
    /// ① <c>id</c> 必须是 <c>CONNECTION_ID()</c> 那个号(杀会话认的就是它,给成 <c>THREAD_ID</c> 就杀不动);
    /// ② <c>state</c> 空闲连接不能是空格子(那是只取 <c>STATE</c> 的写法留下的洞);
    /// ③ <c>query</c> 在自己这一行上必须就是这条会话列表语句本身。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 会话列表_至少看得见自己那一条_且列序按契约()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            string? sql = pack.SessionListSql;
            Assert.IsNotNull(sql, "MySQL 是有会话视图的,这里不该是 null。");

            string self = (await ScalarAsync(raw, "select connection_id()"))?.ToString() ?? "";
            Assert.IsFalse(string.IsNullOrEmpty(self));

            (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(raw, sql);
            CollectionAssert.AreEqual(
                new[] { "id", "user", "host", "db", "state", "seconds", "query" },
                headers.ToArray(),
                "列名与列序按契约固定 —— SqlOpsTabViewModel 是按序号读的。");

            string[]? mine = rows.Find(r => string.Equals(r[0], self, StringComparison.Ordinal));
            Assert.IsNotNull(mine, $"至少该看到自己那条会话(connection_id={self}),实际拿到 {rows.Count} 行。");
            Assert.AreEqual("root", mine[1], "user 那一格。");
            Assert.AreEqual(Database, mine[3], "db 那一格应当是当前库。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(mine[4]),
                "state 不许是空格子 —— 只取 STATE 的写法会让空闲连接整格为空,那正是要合上 COMMAND 的理由。");
            Assert.IsTrue(long.TryParse(mine[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                $"seconds 必须是个数,实际:{mine[5]}");
            StringAssert.Contains(mine[6], "information_schema.PROCESSLIST",
                "自己这一行的 query 就该是正在跑的这条会话列表语句。");

            // id 必须是 KILL 认的那个号:拿它去生成取消语句应当成立(这里只生成不发)。
            StringAssert.Contains(pack.CancelSessionSql(mine[0]) ?? "", $"KILL QUERY {self}");
        });
    }

    /// <summary>
    /// <b>空闲连接必须看得见,而且 <c>state</c> 说得出 <c>Sleep</c>。</b>
    /// <para>
    /// 这条守的是 <see cref="MySqlPack.SessionListSql" /> 里 <c>COALESCE(NULLIF(STATE,''), COMMAND)</c> 那一格:
    /// 只取 <c>STATE</c> 的写法在这里会给出空白,而"一堆连接挂着不干活"恰恰是运维最想一眼看见的形态。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 会话列表_空闲连接的state说得出Sleep()
    {
        await WithMySqlAsync(async raw =>
        {
            // 另开一条连接,让它什么都不做 —— 它在服务端就是一条 COMMAND='Sleep'、STATE='' 的会话。
            await using SqlConnection idle = await OpenAsync(Database);
            string idleId = (await ScalarAsync(idle.Raw, "select connection_id()"))?.ToString() ?? "";

            (_, List<string[]> rows) = await ReadNamedGridAsync(raw, new MySqlPack().SessionListSql!);
            string[]? row = rows.Find(r => string.Equals(r[0], idleId, StringComparison.Ordinal));
            Assert.IsNotNull(row, $"那条空闲连接(connection_id={idleId})应当在会话列表里。");
            Assert.AreEqual("Sleep", row[4],
                "空闲会话的 STATE 是空串,得回落到 COMMAND 才说得出 Sleep;整格空白等于什么都没告诉用户。");
            Assert.AreEqual("", row[6], "空闲会话没有正在跑的语句,query 该是空的。");
        });
    }

    /// <summary>
    /// <b>没人争锁时锁查询干净地返回空,不报错。</b>
    /// <para>
    /// 这是契约点名的那条要求(performance_schema 关掉时也得如此):
    /// 一条<b>跑得通但空</b>的 SQL,而不是一条会报错的 SQL。顺带钉死列名与列序。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 锁查询_没人争锁时跑得通且为空()
    {
        await WithMySqlAsync(async raw =>
        {
            string? sql = new MySqlPack().LockListSql;
            Assert.IsNotNull(sql, "MySQL 8 是有锁视图的,这里不该是 null。");

            (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(raw, sql);
            CollectionAssert.AreEqual(
                new[] { "blocked_id", "blocking_id", "object", "mode", "query" },
                headers.ToArray(),
                "列名与列序按契约固定。");
            Assert.AreEqual(0, rows.Count, "本用例没有制造锁等待,这里应当是干净的空结果。");

            // 采集面本身也得在:performance_schema 关掉时上面那条照样跑得通(只是空),
            // 而"表根本不存在"是另一回事 —— 那种服务端要走 1109 的翻译路径。
            Assert.AreEqual(3L, Convert.ToInt64(await ScalarAsync(raw, """
                SELECT COUNT(*) FROM information_schema.TABLES
                 WHERE TABLE_SCHEMA = 'performance_schema'
                   AND TABLE_NAME IN ('data_locks', 'data_lock_waits', 'threads')
                """), CultureInfo.InvariantCulture),
                "锁查询要的三张 performance_schema 表必须都在(它们是 MySQL 8.0 起才有的)。");
        });
    }

    /// <summary>
    /// <b>真造一次行锁等待,锁查询要把阻塞链说清楚。</b>
    /// <para>
    /// 这是这一组里最该留着的一条 —— "谁锁了我"是运维排障问得最多的一句,
    /// 而它恰恰是 <c>IDbMaintenance</c> 完全没有的(§2.3)。四处逐一验:
    /// ① 两个 id 是<b>连接 id</b>(与会话列表对得上、<c>KILL</c> 认得),不是 <c>THREAD_ID</c>;
    /// ② <c>object</c> 点得出库.表与索引;③ <c>mode</c> 把冲突两边都说了;
    /// ④ <c>query</c> 是<b>被阻塞方</b>那条语句。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 锁查询_真造一次阻塞_两个id都是连接id()
    {
        await WithMySqlAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists lock_probe");
            await ExecAsync(raw, "create table lock_probe(id int not null primary key, v int null) engine=InnoDB");
            await ExecAsync(raw, "insert into lock_probe(id, v) values(1, 0), (2, 0)");

            await using SqlConnection holder = await OpenAsync(Database);
            await using SqlConnection waiter = await OpenAsync(Database);
            string holderId = (await ScalarAsync(holder.Raw, "select connection_id()"))?.ToString() ?? "";
            string waiterId = (await ScalarAsync(waiter.Raw, "select connection_id()"))?.ToString() ?? "";

            // 持锁方:开事务、改一行、不提交。
            await ExecAsync(holder.Raw, "start transaction");
            await ExecAsync(holder.Raw, "update lock_probe set v = v + 1 where id = 1");

            // 等待方:同一行再改一次 —— 它会挂在服务端。**不 await**,让它在那儿等着。
            await ExecAsync(waiter.Raw, "set innodb_lock_wait_timeout = 30");
            await ExecAsync(waiter.Raw, "start transaction");
            const string Blocked = "update lock_probe set v = v + 100 where id = 1";
            Task blockedUpdate = ExecAsync(waiter.Raw, Blocked);

            try
            {
                string[]? row = await PollForLockAsync(raw, waiterId);
                Assert.IsNotNull(row,
                    "造出了真的行锁等待,锁查询却一行都没给 —— 这一栏存在的全部理由就是这一行。");

                Assert.AreEqual(waiterId, row[0], "blocked_id 必须是被阻塞方的**连接 id**。");
                Assert.AreEqual(holderId, row[1],
                    "blocking_id 必须是持锁方的**连接 id** —— 给成 THREAD_ID 的话它既对不上会话列表,也杀不动。");
                StringAssert.Contains(row[2], $"{Database}.lock_probe", "object 要点得出库与表。");
                StringAssert.Contains(row[2], "PRIMARY", "行锁落在主键索引上,索引名要带出来。");
                StringAssert.Contains(row[3], "RECORD", "mode 要说清是行锁还是表锁。");
                StringAssert.Contains(row[3], "<-", "mode 要把'要的模式'与'持有方的模式'两边都说了。");
                Assert.AreEqual(Blocked, row[4],
                    "query 给的是**被阻塞方**那条语句(持锁方十有八九是开着事务闲着,那一格会是空的)。");

                // 两个 id 拿去会话列表里必须都找得到 —— 这才叫"点出来的 id 能直接用"。
                (_, List<string[]> sessions) = await ReadNamedGridAsync(raw, new MySqlPack().SessionListSql!);
                Assert.IsTrue(sessions.Exists(s => string.Equals(s[0], row[0], StringComparison.Ordinal)));
                Assert.IsTrue(sessions.Exists(s => string.Equals(s[0], row[1], StringComparison.Ordinal)));
            }
            finally
            {
                // 放锁,让等待方跑完,再各自回滚 —— 别把一条挂着的事务留给下一个用例。
                await ExecAsync(holder.Raw, "rollback");
                await blockedUpdate;
                await ExecAsync(waiter.Raw, "rollback");
            }
        });
    }

    /// <summary>
    /// 类型表是<b>静态</b>的:与库里建了什么无关(这正是不能用 <c>GetDbTypes()</c> 的理由,§2.3),
    /// 而且里面不许出现整数显示宽度 —— 那种写法服务端会当场改写掉。
    /// </summary>
    [TestMethod]
    public async Task 类型表_是静态表_且不带整数显示宽度()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            IReadOnlyList<string> before = pack.CommonTypes;
            Assert.IsTrue(before.Count > 0, "类型下拉不能是空的。");

            await ExecAsync(raw, "drop table if exists type_probe");
            await ExecAsync(raw, "create table type_probe(a json, b geometry null, c set('p','q'))");
            CollectionAssert.AreEqual(before.ToArray(), pack.CommonTypes.ToArray(),
                "静态类型表不该跟着库里的表变 —— 会变的那个是 GetDbTypes(),正是本包不用它的原因。");

            IReadOnlyList<string> types = pack.CommonTypes;
            Assert.AreEqual(types.Count, types.Distinct(StringComparer.Ordinal).Count(), "类型表里不该有重复项。");
            Assert.IsFalse(types.Any(string.IsNullOrWhiteSpace), "类型表里不该有空项。");

            // 整数显示宽度:8.0.17 起弃用,建出来括号会被静默丢掉。
            foreach (string integer in new[] { "TINYINT", "SMALLINT", "MEDIUMINT", "INT", "BIGINT" })
            {
                Assert.IsTrue(types.Contains(integer, StringComparer.Ordinal), $"{integer} 应当在类型表里。");
                Assert.IsFalse(
                    types.Any(t => t.StartsWith($"{integer}(", StringComparison.Ordinal)),
                    $"{integer} 不许带显示宽度 —— 服务端会警告 1681 并把括号丢掉,建完类型对不上最像插件坏了。");
            }
            Assert.IsTrue(types.Contains("BOOLEAN", StringComparer.Ordinal),
                "布尔要给 BOOLEAN;它与 TINYINT(1) 建出来一样,但不触发 1681 警告。");

            // 真机反证上面那两条:int(11) 会被改写成 int,而 BOOLEAN 建出来就是 tinyint(1)。
            await ExecAsync(raw, "drop table if exists width_probe");
            await ExecAsync(raw, "create table width_probe(a int(11), b boolean)");
            Assert.AreEqual("int", await ColumnTypeAsync(raw, "width_probe", "a"),
                "int(11) 的括号被服务端静默丢掉了 —— 所以下拉里不能摆这种写法。");
            Assert.AreEqual("tinyint(1)", await ColumnTypeAsync(raw, "width_probe", "b"));

            // 每一项都得真的建得出来:类型表是拿去拼 DDL 的,列一个建不出的写法等于埋雷。
            await ExecAsync(raw, "drop table if exists type_menu");
            await ExecAsync(raw, "create table type_menu(id int not null primary key)");
            var target = new SqlObject(SqlObjectKind.Table, "type_menu", Database);
            for (int i = 0; i < types.Count; i++)
            {
                string ddl = pack.AddColumnDdl(target, new SqlColumn($"c{i}", i + 2, types[i], IsNullable: true))
                             ?? throw new AssertFailedException($"类型 {types[i]} 生成不出加列 DDL。");
                Exception? failure = await CaptureAsync(raw, ddl);
                Assert.IsNull(failure, $"类型表里的 {types[i]} 在真机上建不出来:{failure?.Message}");
            }
        });
    }

    // ═══════════════════════════ 表设计器 DDL(能力组 5) ═══════════════════════════

    /// <summary>
    /// DDL 文本逐字对,而且<b>用户标识符一律走 <c>QuoteIdentifier</c></b> ——
    /// 名字里含反引号时必须加倍,否则就是 §5.4.4 实测能删表的那条路。
    /// </summary>
    [TestMethod]
    public void 表设计器DDL_文本正确且标识符按方言转义()
    {
        var pack = new MySqlPack();
        // MySQL 没有 schema 一级,库名填在 Schema 上(见 MySqlPack 类注释),限定名是 `库`.`表`。
        var target = new SqlObject(SqlObjectKind.Table, "ops`tbl", "ops`db");

        Assert.AreEqual(
            "ALTER TABLE `ops``db`.`ops``tbl` ADD COLUMN `qty``x` int",
            pack.AddColumnDdl(target, new SqlColumn("qty`x", 1, "int", IsNullable: true)));

        Assert.AreEqual(
            "ALTER TABLE `ops``db`.`ops``tbl` ADD COLUMN `qty``x` int NOT NULL DEFAULT 0",
            pack.AddColumnDdl(target, new SqlColumn("qty`x", 1, "int", IsNullable: false, DefaultValue: "0")));

        Assert.AreEqual(
            "ALTER TABLE `ops``db`.`ops``tbl` DROP COLUMN `qty``x`",
            pack.DropColumnDdl(target, "qty`x"));

        Assert.AreEqual(
            "CREATE UNIQUE INDEX `ix``w` ON `ops``db`.`ops``tbl` (`qty``x`, `id`)",
            pack.CreateIndexDdl(target, "ix`w", ["qty`x", "id"], unique: true));

        // **删索引必须带表名** —— 通用写法 DROP INDEX `ix` 在 MySQL 上是语法错,不是"删不掉"。
        Assert.AreEqual(
            "DROP INDEX `ix``w` ON `ops``db`.`ops``tbl`",
            pack.DropIndexDdl(target, "ix`w"));

        // 一列都不给的索引没有意义,基类返回 null,别在这里生成 "()"。
        Assert.IsNull(pack.CreateIndexDdl(target, "ix_empty", [], unique: false));

        // 转义纪律的通用判据(与 DialectPackIntegrationTests 同一条):
        // 内部每个定界符都成对,于是第一个落单的反引号只可能是结尾那个,payload 没法提前收尾。
        string quoted = pack.QuoteIdentifier("orders`; drop table victim--");
        Assert.IsTrue(quoted.StartsWith('`') && quoted.EndsWith('`'));
        Assert.AreEqual(0, quoted[1..^1].Count(c => c == '`') % 2, "内部的定界符必须全部成对(加倍转义)。");
    }

    /// <summary>
    /// <b>四条 DDL 真的在库上跑一遍</b>:加列 → 建索引 → 删索引 → 删列,每一步都回查元数据确认。
    /// <para>
    /// 表名、列名、索引名里都埋了反引号 —— 这是转义的<b>端到端</b>证明:文本比对只能证明"我按规则拼了",
    /// 引擎认不认是另一回事(名字转错时 MySQL 报的是 <c>1146 Table doesn't exist</c>,不是语法错)。
    /// 库名走 <see cref="SqlObject.Schema" />,所以这一轮同时证明了限定名那一段也拼对了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 加列建索引删索引删列_在真库上依次执行成功()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            CancellationToken token = TestContext.CancellationTokenSource.Token;
            const string TableName = "ops`tbl";
            const string ColumnName = "qty`x";
            const string IndexName = "ix`w";
            var target = new SqlObject(SqlObjectKind.Table, TableName, Database);

            // 建表也走 QuoteIdentifier:测试自己也得守"永不裸拼用户标识符"这条纪律。
            await ExecAsync(raw, $"drop table if exists {pack.QuoteIdentifier(TableName)}");
            await ExecAsync(raw, $"""
                create table {pack.QuoteIdentifier(TableName)}(
                  id int not null auto_increment primary key,
                  name varchar(20) null)
                """);
            await ExecAsync(raw, $"insert into {pack.QuoteIdentifier(TableName)}(name) values('a'), ('b')");

            // —— ① 加列(NOT NULL + 常量默认值)。
            string add = pack.AddColumnDdl(target, new SqlColumn(ColumnName, 3, "int", IsNullable: false, DefaultValue: "0"))
                         ?? throw new AssertFailedException("加列 DDL 不该是 null。");
            await ExecAsync(raw, add);

            SqlColumn added = Col(await pack.DescribeAsync(raw, target, token), ColumnName);
            Assert.AreEqual("int", added.DataType);
            Assert.IsFalse(added.IsNullable, "加的是 NOT NULL 列。");
            Assert.AreEqual("0", added.DefaultValue);
            Assert.AreEqual(2L, Convert.ToInt64(await ScalarAsync(
                raw,
                $"select count(*) from {pack.QuoteIdentifier(TableName)} "
                + $"where {pack.QuoteIdentifier(ColumnName)} = 0"), CultureInfo.InvariantCulture),
                "已有的两行都该被默认值填上。");

            // —— ② 建索引(基类的通行写法,MySQL 上逐字成立)。
            string createIndex = pack.CreateIndexDdl(target, IndexName, [ColumnName], unique: false)
                                 ?? throw new AssertFailedException("建索引 DDL 不该是 null。");
            await ExecAsync(raw, createIndex);

            SqlIndex index = Idx(await pack.DescribeAsync(raw, target, token), IndexName);
            Assert.IsFalse(index.IsUnique);
            CollectionAssert.AreEqual(new[] { ColumnName }, index.Columns.ToArray());

            // —— ③ 删索引:**通用写法先证明它在 MySQL 上跑不通**,再用带表名的那条删掉。
            Exception? generic = await CaptureAsync(raw, $"DROP INDEX {pack.QuoteIdentifier(IndexName)}");
            Assert.IsNotNull(generic,
                "不带表名的 DROP INDEX 居然成功了 —— 那 DropIndexDdl 的覆盖就没必要,注释也得改。");
            StringAssert.Contains(generic.Message, "syntax",
                $"MySQL 上它是语法错(解析器还在等 ON),实际:{generic.Message}");

            await ExecAsync(raw, pack.DropIndexDdl(target, IndexName)!);
            Assert.IsFalse(
                (await pack.DescribeAsync(raw, target, token)).Indexes
                    .Any(i => string.Equals(i.Name, IndexName, StringComparison.Ordinal)),
                "索引应当已经没了。");

            // —— ④ 删列。
            await ExecAsync(raw, pack.DropColumnDdl(target, ColumnName)!);
            SqlTableSchema afterDrop = await pack.DescribeAsync(raw, target, token);
            Assert.IsFalse(afterDrop.Columns.Any(c => string.Equals(c.Name, ColumnName, StringComparison.Ordinal)),
                "列应当已经没了。");
            Assert.AreEqual(2, afterDrop.Columns.Count, "只该少那一列,别的列不许受牵连。");
        });
    }

    /// <summary>唯一索引也真跑一遍:唯一性必须落到元数据上(<c>GetIndexList</c> 恰恰是把它丢了的那个)。</summary>
    [TestMethod]
    public async Task 建唯一索引_唯一性落到元数据上()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "uniq_probe", Database);
            await ExecAsync(raw, "drop table if exists uniq_probe");
            await ExecAsync(raw, """
                create table uniq_probe(
                  id int not null auto_increment primary key,
                  code varchar(20) not null,
                  tag varchar(20) not null)
                """);
            await ExecAsync(raw, "insert into uniq_probe(code, tag) values('a', 'x'), ('b', 'x')");

            await ExecAsync(raw, pack.CreateIndexDdl(target, "ux_uniq_probe_code", ["code", "tag"], unique: true)!);

            SqlIndex index = Idx(
                await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token),
                "ux_uniq_probe_code");
            Assert.IsTrue(index.IsUnique, "唯一性不能丢。");
            CollectionAssert.AreEqual(new[] { "code", "tag" }, index.Columns.ToArray(), "复合索引的列序按声明顺序。");

            // 唯一约束真的生效(索引建对了,不只是元数据好看)。
            Assert.IsNotNull(await CaptureAsync(raw, "insert into uniq_probe(code, tag) values('a', 'x')"));
        });
    }

    /// <summary>
    /// <b>列定义里说了、而这条 DDL 表达不了的,一律不生成。</b>
    /// <para>
    /// 四面旗逐一验,并且用真机反证"静默办成别的事"不是假想:把通用写法会生成的那条手工发一次,
    /// 建出来的是个普通列 —— 用户点的是"加一个生成列",拿到的是个什么都不是的列,而且哪儿都不提示。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 加列_表达不了的四样一律不生成DDL()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "flag_probe", Database);
            await ExecAsync(raw, "drop table if exists flag_probe");
            await ExecAsync(raw, "create table flag_probe(id int not null primary key, amount decimal(12,3) not null)");

            Assert.IsNull(
                pack.AddColumnDdl(target, new SqlColumn("g1", 3, "int", IsNullable: true, IsGenerated: true)),
                "拼不出 GENERATED ALWAYS AS (...),就不该生成一条会静默办成别的事的 DDL。");
            Assert.IsNull(
                pack.AddColumnDdl(target, new SqlColumn("p1", 3, "int", IsNullable: false, IsPrimaryKey: true)),
                "主键那面旗同理。");
            Assert.IsNull(
                pack.AddColumnDdl(target, new SqlColumn("a1", 3, "int", IsNullable: false, IsAutoIncrement: true)),
                "自增那面旗同理(而且 MySQL 还要求自增列必须是某个键的第一列)。");
            Assert.IsNull(
                pack.AddColumnDdl(target, new SqlColumn("c1", 3, "int", IsNullable: true, Comment: "金额")),
                "注释拼不出来 —— COMMENT 只接带引号的字面量,而正确转义要先知道服务端的 sql_mode。");

            // 反证 ①:通用写法生成的那条真发出去,建出来是个普通列。
            await ExecAsync(raw, "alter table flag_probe add column `g1` int NULL");
            SqlColumn plain = Col(
                await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token), "g1");
            Assert.IsFalse(plain.IsGenerated, "它建出来是个普通列 —— 正是不能生成这条 DDL 的理由。");
            Assert.IsFalse(plain.IsPrimaryKey);
            Assert.IsFalse(plain.IsAutoIncrement);

            // 反证 ②:注释走本包那招十六进制在 DDL 里是语法错 —— 所以不是"懒得拼",是真拼不出来。
            Exception? hex = await CaptureAsync(
                raw, "alter table flag_probe add column `c1` int NULL COMMENT X'E98791E9A29D'");
            Assert.IsNotNull(hex, "COMMENT 接十六进制字面量居然成功了 —— 那 AddColumnDdl 的注释要改。");
            StringAssert.Contains(hex.Message, "syntax", $"预期是 1064 语法错,实际:{hex.Message}");

            // 剩下的那条来回是通的:DescribeAsync 读回来的原生类型原样拿去加列成立。
            SqlColumn amount = Col(
                await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token), "amount");
            Assert.AreEqual("decimal(12,3)", amount.DataType);
            await ExecAsync(raw, pack.AddColumnDdl(
                target, new SqlColumn("amount_copy", 5, amount.DataType, IsNullable: true))!);
            Assert.AreEqual("decimal(12,3)", await ColumnTypeAsync(raw, "flag_probe", "amount_copy"));
        });
    }

    /// <summary>
    /// <b>删列在 MySQL 上不像 SQLite 那样被拦 —— 它会静默改掉索引。</b>
    /// <para>
    /// 这条把 <see cref="MySqlPack" /> 表设计器那段注释里记的三条形态钉在真机上:
    /// 独占该列的索引<b>一并消失</b>、复合索引<b>少一列继续存在</b>(名字没变、计划变了)、
    /// 被外键引用的列删不掉。表设计器删列前必须先把受影响的索引摆给用户看。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 删列_MySQL会静默改掉索引_而外键会拦下来()
    {
        await WithMySqlAsync(async raw =>
        {
            var pack = new MySqlPack();
            CancellationToken token = TestContext.CancellationTokenSource.Token;
            var target = new SqlObject(SqlObjectKind.Table, "drop_probe", Database);
            await ExecAsync(raw, "drop table if exists drop_child");
            await ExecAsync(raw, "drop table if exists drop_probe");
            await ExecAsync(raw, """
                create table drop_probe(
                  id int not null primary key,
                  a  int null,
                  b  int null,
                  c  int null,
                  key ix_solo (c),
                  key ix_ab (a, b))
                """);

            // —— ① 独占该列的索引:删列时一并消失,一声不吭。
            await ExecAsync(raw, pack.DropColumnDdl(target, "c")!);
            Assert.IsFalse(
                (await pack.DescribeAsync(raw, target, token)).Indexes
                    .Any(i => string.Equals(i.Name, "ix_solo", StringComparison.Ordinal)),
                "MySQL 把只用到这一列的索引一并删了 —— 与 SQLite 拦下来的行为正相反。");

            // —— ② 复合索引:少一列继续存在。索引还在、名字没变,查询计划却变了 —— 这条最阴。
            await ExecAsync(raw, pack.DropColumnDdl(target, "a")!);
            SqlIndex composite = Idx(await pack.DescribeAsync(raw, target, token), "ix_ab");
            CollectionAssert.AreEqual(new[] { "b" }, composite.Columns.ToArray(),
                "复合索引静默少了一列 —— 表设计器删列前应当把受影响的索引列出来。");

            // —— ③ 外键引用着的列:这个会被拦下来,报错原文记在注释里。
            await ExecAsync(raw, "create table drop_child(cid int not null primary key, pid int null, "
                                 + "constraint fk_drop foreign key (pid) references drop_probe(id))");
            Exception? blocked = await CaptureAsync(
                raw, pack.DropColumnDdl(new SqlObject(SqlObjectKind.Table, "drop_child", Database), "pid")!);
            Assert.IsNotNull(blocked, "被外键引用的列不该删得掉。");
            StringAssert.Contains(blocked.Message, "foreign key constraint",
                $"预期 1828 needed in a foreign key constraint,实际:{blocked.Message}");
        });
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>
    /// 拿到一条连着 <see cref="Database" /> 的连接跑一段;没有 MySQL 就 <c>Inconclusive</c>。
    /// <para>库不存在时现建 —— 这一组要能在一台干净的容器上直接跑起来。</para>
    /// </summary>
    /// <param name="body">拿到已打开连接之后要做的事。</param>
    /// <returns>任务。</returns>
    private static async Task WithMySqlAsync(Func<DbConnection, Task> body)
    {
        SqlConnection? bootstrap = await TryOpenAsync("");
        if (bootstrap is null)
        {
            Assert.Inconclusive("没有可用的 MySQL(127.0.0.1:13306)。");
            return;
        }
        await using (bootstrap)
        {
            // 库名是本文件里的常量,不是用户输入;仍然走 QuoteIdentifier,免得将来有人改成变量。
            await ExecAsync(
                bootstrap.Raw,
                $"create database if not exists {new MySqlPack().QuoteIdentifier(Database)} character set utf8mb4");
        }

        await using SqlConnection connection = await OpenAsync(Database);
        await body(connection.Raw);
    }

    /// <summary>连一条到指定库(空串 = 不指定库)。</summary>
    /// <param name="database">库名。</param>
    /// <returns>已打开的连接。</returns>
    private static async Task<SqlConnection> OpenAsync(string database)
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "mysql-ops",
            Host = "127.0.0.1",
            Port = 13306,
            Username = "root",
            Password = "velaspike",
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = database }
        };
        return await SqlConnection.ConnectAsync(
            SqlSettings.From(request, SqlDialect.MySql), "127.0.0.1", 13306, "root", "velaspike", Localization, null);
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

    /// <summary>
    /// 反复查锁表,直到看见 <paramref name="blockedId" /> 那条等待为止。
    /// <para>
    /// 锁等待是<b>异步</b>出现的:发出 UPDATE 到它真的挂在服务端之间有一小段,
    /// 直接查一次十有八九是空的。轮询而不是固定 sleep,是为了让常见情形下用例仍然是快的。
    /// </para>
    /// </summary>
    /// <param name="connection">观察用的连接。</param>
    /// <param name="blockedId">被阻塞方的连接 id。</param>
    /// <returns>那一行;等不到则为 <see langword="null" />。</returns>
    private static async Task<string[]?> PollForLockAsync(DbConnection connection, string blockedId)
    {
        string sql = new MySqlPack().LockListSql!;
        for (int i = 0; i < 100; i++)
        {
            (_, List<string[]> rows) = await ReadNamedGridAsync(connection, sql);
            string[]? row = rows.Find(r => string.Equals(r[0], blockedId, StringComparison.Ordinal));
            if (row is not null)
            {
                return row;
            }
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>发一条语句;失败就让测试失败(这一组要的就是"引擎认不认")。</summary>
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

    /// <summary>发一条<b>预期会失败</b>的语句,把异常带回来(拿不到异常就是断言失败的证据)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>异常;居然成功了则为 <see langword="null" />。</returns>
    private static async Task<Exception?> CaptureAsync(DbConnection connection, string sql)
    {
        try
        {
            await ExecAsync(connection, sql);
            return null;
        }
        catch (DbException ex)
        {
            return ex;
        }
    }

    /// <summary>把一条查询读成"列数 + 每行一段文本"(计划的列名随版本变,按序号读最稳)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>列数与行文本。</returns>
    private static async Task<(int Columns, List<string> Rows)> ReadGridAsync(DbConnection connection, string sql)
    {
        (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(connection, sql);
        return (headers.Count, [.. rows.Select(r => string.Join(" | ", r))]);
    }

    /// <summary>把一条查询读成"列名 + 行"。列名要断言时用它(运维面那两条的列约定就靠它守)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>列名与行。</returns>
    private static async Task<(List<string> Headers, List<string[]> Rows)> ReadNamedGridAsync(
        DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<string> headers = [];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            headers.Add(reader.GetName(i));
        }
        List<string[]> rows = [];
        while (await reader.ReadAsync())
        {
            string[] cells = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            }
            rows.Add(cells);
        }
        return (headers, rows);
    }

    /// <summary>取第一行第一列。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>值。</returns>
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>数一张表有多少行(表名是本文件里的常量,走 <c>QuoteIdentifier</c> 是纪律)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="table">表名。</param>
    /// <returns>行数。</returns>
    private static async Task<long> CountAsync(DbConnection connection, string table) =>
        Convert.ToInt64(
            await ScalarAsync(connection, $"select count(*) from {new MySqlPack().QuoteIdentifier(table)}"),
            CultureInfo.InvariantCulture);

    /// <summary>回查某一列的 <c>COLUMN_TYPE</c> 原文(证明服务端到底把类型存成了什么)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="table">表名。</param>
    /// <param name="column">列名。</param>
    /// <returns>类型原文。</returns>
    private static async Task<string> ColumnTypeAsync(DbConnection connection, string table, string column)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_TYPE FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @p0 AND COLUMN_NAME = @p1
            """;
        DbParameter table_ = command.CreateParameter();
        table_.ParameterName = "@p0";
        table_.Value = table;
        command.Parameters.Add(table_);
        DbParameter column_ = command.CreateParameter();
        column_.ParameterName = "@p1";
        column_.Value = column;
        command.Parameters.Add(column_);
        return (await command.ExecuteScalarAsync())?.ToString() ?? "";
    }

    private static SqlColumn Col(SqlTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
        ?? throw new AssertFailedException(
            $"结果里没有列 {name};实际有:{string.Join(", ", schema.Columns.Select(c => c.Name))}");

    private static SqlIndex Idx(SqlTableSchema schema, string name) =>
        schema.Indexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal))
        ?? throw new AssertFailedException(
            $"结果里没有索引 {name};实际有:{string.Join(", ", schema.Indexes.Select(i => i.Name))}");
}

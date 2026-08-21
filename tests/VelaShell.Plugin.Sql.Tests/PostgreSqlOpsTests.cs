using System.Data.Common;
using System.Globalization;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// PostgreSQL 方言包的 <b>M4 资产真机验收</b>:执行计划(能力组 7)、运维面(能力组 8)、表设计器 DDL(能力组 5)。
/// <para>
/// 这一组的断言几乎全是"真发给服务端看它认不认",而不是比字符串。理由与 MySQL 那一组同一条(§3.4):
/// 方言资产最容易出的错是<b>语法合法、语义错误</b>,只比文本的测试对这一类完全无感。
/// 而 PG 这一批里有四条只有真机才证得了的:
/// ① <c>EXPLAIN ANALYZE</c> 会不会真把 <c>DELETE</c> 跑完(会,而且 PG 没有 MySQL 那种意外护栏);
/// ② 基类的裸名 <c>DROP INDEX</c> 在 PG 上删不到,而 <c>CREATE INDEX</c> 反过来<b>不能</b>加 schema 限定;
/// ③ 会话耗时用 <c>now()</c> 算会在事务里变成<b>负数</b>;
/// ④ 类型下拉里的每一项建完读回来是不是逐字相同(别名与语法糖会在这一步露馅)。
/// </para>
/// <para>
/// 按仓库惯例按环境早退:拿不到 PostgreSQL 时 <c>Inconclusive</c> 而不是失败。
/// 用自己的库 <c>ops_pg</c>(没有就现建),不去动 <c>DialectPackIntegrationTests</c> 的 <c>pack_verify</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class PostgreSqlOpsTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>本组专用的库。与别的用例分开,免得互相踩。</summary>
    private const string Database = "ops_pg";

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ═══════════════════════════ 执行计划(能力组 7) ═══════════════════════════

    /// <summary>
    /// <c>EXPLAIN (FORMAT TEXT)</c> 在真机上给得出计划,而且给的是<b>一列</b>那一形态。
    /// <para>
    /// 三条断言分别对着一个失败模式:① 一行都没有(发出去的根本不是计划语句);
    /// ② 不是一列(PG 只有这一种形状,多一列说明有人把 <c>FORMAT</c> 改成了 JSON/YAML,
    /// 而渲染代码会照错的形状画);③ 命中了索引却不说 —— 那样这一栏就白开了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_真机给出一列计划文本并点出命中的索引()
    {
        await WithPostgresAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists plan_probe cascade");
            await ExecAsync(raw, """
                create table plan_probe(
                  id   int generated always as identity primary key,
                  tag  varchar(20),
                  name text)
                """);
            await ExecAsync(raw, "insert into plan_probe(tag, name) select 'x'||(i%7), 'n'||i from generate_series(1,5000) i");
            await ExecAsync(raw, "create index ix_plan_probe_tag on plan_probe(tag)");
            await ExecAsync(raw, "analyze plan_probe");

            var pack = new PostgreSqlPack();
            const string Query = "select id, name from plan_probe where tag = 'x3'";
            string? explain = pack.ExplainSql(Query, analyze: false);
            Assert.IsNotNull(explain, "PG 是有执行计划的,这里不该是 null。");
            Assert.AreEqual($"EXPLAIN (FORMAT TEXT) {Query}", explain,
                "计划语句只是给用户 SQL 加个前缀,不套派生表也不改写它。");

            (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(raw, explain);
            Assert.AreEqual(1, headers.Count,
                $"PG 的 EXPLAIN 恒为一列,实际 {headers.Count} 列 —— 多一列说明 FORMAT 被改了,渲染代码会照错的形状画。");
            Assert.AreEqual("QUERY PLAN", headers[0], "列名恒为 QUERY PLAN。");
            Assert.IsTrue(rows.Count > 0, "计划一行都没有,说明发出去的根本不是 EXPLAIN。");

            string text = string.Join("\n", rows.Select(r => r[0]));
            StringAssert.Contains(text, "ix_plan_probe_tag",
                $"等值条件命中了索引,计划里必须点出索引名,实际拿到:\n{text}");
            Assert.IsFalse(text.Contains("actual time", StringComparison.Ordinal),
                $"不带 analyze 的计划里不该有 actual time —— 有它就说明这条真跑过了。实际:\n{text}");

            // 末尾分号要剥掉(编辑器切出来的语句常带终止符)。
            Assert.AreEqual("EXPLAIN (FORMAT TEXT) select 1", pack.ExplainSql("select 1;  ", analyze: false));
            Assert.AreEqual("EXPLAIN (FORMAT TEXT) select 1", pack.ExplainSql("select 1 ; ;", analyze: false));
        });
    }

    /// <summary>
    /// <b><c>EXPLAIN ANALYZE</c> 真的把 <c>DELETE</c> 跑完 —— 契约把 analyze 标成危险开关不是虚张声势。</b>
    /// <para>
    /// 这一条是 PG 与 MySQL <b>结论相反</b>的地方,所以必须单独钉死:
    /// MySQL 8.4 上 <c>EXPLAIN ANALYZE DELETE</c> 只回一行 <c>&lt;not executable by iterator executor&gt;</c>
    /// 且数据一行没少(那是它当前版本的副产物,不是承诺);
    /// <b>PG 上它就是老老实实地删</b>。所以护栏(§7.6)在 PG 上是唯一的一道闸。
    /// 这条断言要是哪天挂了(删完还是 10 行),说明 PG 改了 <c>EXPLAIN ANALYZE</c> 的语义 ——
    /// 那时该改的是注释,<b>而不是</b>把护栏往方言包这边挪。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_analyze档真的把DELETE跑完_PG没有MySQL那种意外护栏()
    {
        await WithPostgresAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists analyze_danger cascade");
            await ExecAsync(raw, "create table analyze_danger(id int primary key)");
            await ExecAsync(raw, "insert into analyze_danger select generate_series(1,10)");
            Assert.AreEqual(10L, await CountAsync(raw, "analyze_danger"), "前置条件:10 行。");

            var pack = new PostgreSqlPack();
            const string Delete = "delete from analyze_danger where id > 5";
            Assert.AreEqual($"EXPLAIN (FORMAT TEXT) {Delete}", pack.ExplainSql(Delete, analyze: false));

            string? analyzed = pack.ExplainSql(Delete, analyze: true);
            Assert.AreEqual($"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {Delete}", analyzed,
                "调用方要 analyze,拿到的就必须是 analyze —— 这里绝不静默降级。");

            (_, List<string[]> rows) = await ReadNamedGridAsync(raw, analyzed!);
            StringAssert.Contains(string.Join("\n", rows.Select(r => r[0])), "Delete on analyze_danger",
                "计划里得看得出这是一条删除。");

            Assert.AreEqual(5L, await CountAsync(raw, "analyze_danger"),
                "EXPLAIN ANALYZE 必须真的把这 5 行删掉 —— 它要是没删,契约里那条警告就落空了,"
                + "护栏也会被后来的人当成多余的。");

            // 不带 analyze 的那一档反过来:一行都不能少。
            await ExecAsync(raw, "insert into analyze_danger select generate_series(6,10)");
            await ReadNamedGridAsync(raw, pack.ExplainSql(Delete, analyze: false)!);
            Assert.AreEqual(10L, await CountAsync(raw, "analyze_danger"),
                "静态计划只做优化不执行,行数不能变。");
        });
    }

    /// <summary>
    /// analyze 档带回了<b>真跑过的痕迹</b>(<c>actual time</c>)与<b>缓冲区统计</b>(<c>Buffers:</c>)。
    /// <para>
    /// <c>BUFFERS</c> 是显式写进语句里的,不是靠服务端默认 —— PG 18 起它随 <c>ANALYZE</c> 默认开,
    /// 17 及以前默认关。这条断言守的就是"同一个界面在两台不同版本的服务器上信息量一样"。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_analyze不降级_而且带回缓冲区统计()
    {
        await WithPostgresAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists buffers_probe cascade");
            await ExecAsync(raw, "create table buffers_probe(id int primary key, pad text)");
            await ExecAsync(raw, "insert into buffers_probe select i, repeat('x', 200) from generate_series(1,2000) i");
            await ExecAsync(raw, "analyze buffers_probe");

            var pack = new PostgreSqlPack();
            string? analyzed = pack.ExplainSql("select count(*) from buffers_probe", analyze: true);
            (_, List<string[]> rows) = await ReadNamedGridAsync(raw, analyzed!);
            string text = string.Join("\n", rows.Select(r => r[0]));

            StringAssert.Contains(text, "actual time",
                $"树里必须有 actual time —— 那是'真的执行过'留下的痕迹。实际:\n{text}");
            StringAssert.Contains(text, "Buffers:",
                $"BUFFERS 是写死在语句里的,计划里必须带缓冲区统计。实际:\n{text}");
            StringAssert.Contains(text, "Execution Time",
                $"analyze 档必须报出实际执行耗时。实际:\n{text}");
        });
    }

    /// <summary>
    /// <b>不可优化的语句会被 PG 以语法错打回,而且报错指在用户那条 SQL 上。</b>
    /// <para>
    /// 这条不是"测 PG",是把一条<b>界面必须翻译</b>的形态钉住:用户看到的是"我的 SQL 有语法错误",
    /// 可他那条单独跑起来好好的。按 §7.8 这一类要翻成"这种语句没有执行计划",别把 42601 原样丢出去。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_不可优化的语句以语法错打回_报错指在用户那条SQL上()
    {
        await WithPostgresAsync(async raw =>
        {
            var pack = new PostgreSqlPack();

            Exception? ddl = await CaptureAsync(raw, pack.ExplainSql("create table zzz_no_plan(i int)", analyze: false)!);
            Assert.IsNotNull(ddl, "EXPLAIN 吃不下 CREATE TABLE,这里必须抛。");
            StringAssert.Contains(ddl.Message, "syntax error",
                $"预期 42601 语法错,实际:{ddl.Message}");
            // ::text 不是装饰:Npgsql 5.0.18 读不了裸的 regclass
            // (Reading as 'System.Object' is not supported for fields having DataTypeName 'regclass'),
            // 这也正是 LockListSql 里那一格写成 relation::regclass::text 的原因。
            Assert.IsNull(await ScalarAsync(raw, "select to_regclass('zzz_no_plan')::text"),
                "报的是语法错,那张表就不该被建出来。");

            Exception? vacuum = await CaptureAsync(raw, pack.ExplainSql("vacuum analyze_danger", analyze: false)!);
            Assert.IsNotNull(vacuum, "EXPLAIN 也吃不下 VACUUM。");
            StringAssert.Contains(vacuum.Message, "syntax error",
                $"预期 42601 语法错,实际:{vacuum.Message}");
        });
    }

    // ═══════════════════════════ 运维面(能力组 8) ═══════════════════════════

    /// <summary>
    /// 会话列表的<b>列名与列序严格按契约</b>,而且真机上看得见自己这条连接。
    /// <para>
    /// 调用方是 <c>SequentialAccess</c> 按序号读的,所以列序错了不会报错,只会把用户名画到主机那一栏。
    /// 一并验自己那一行的每一格都对得上:<c>id</c> = <c>pg_backend_pid()</c>、
    /// <c>db</c> = 本库、<c>state</c> 以 <c>active</c> 开头(它正在跑这条查询)、
    /// <c>query</c> 就是这条查询本身。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 会话列表_列名与列序严格按契约_而且看得见自己这条连接()
    {
        await WithPostgresAsync(async raw =>
        {
            var pack = new PostgreSqlPack();
            string sql = pack.SessionListSql!;
            Assert.IsNotNull(sql, "PG 是有会话视图的,这里不该是 null。");

            int self = Convert.ToInt32(await ScalarAsync(raw, pack.SessionIdSql!), CultureInfo.InvariantCulture);
            (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(raw, sql);

            CollectionAssert.AreEqual(
                new[] { "id", "user", "host", "db", "state", "seconds", "query" },
                headers,
                "列名与列序是契约的一部分 —— 调用方按序号读,错序不会报错,只会把用户名画到主机那一栏。");

            string[]? mine = rows.Find(r => r[0] == self.ToString(CultureInfo.InvariantCulture));
            Assert.IsNotNull(mine,
                $"会话列表里必须看得见自己(pid={self}),一共 {rows.Count} 行。看不见说明过滤条件把真会话也滤掉了。");
            Assert.AreEqual(Database, mine[3], "db 这一格是本库。");
            StringAssert.StartsWith(mine[4], "active",
                $"自己正在跑这条查询,state 必须以 active 开头,实际:{mine[4]}");
            StringAssert.Contains(mine[6], "pg_catalog.pg_stat_activity",
                "query 这一格是这条查询本身。");
            Assert.IsTrue(mine[2].Length > 0, "本机 TCP 连上来的,host 不该是空的。");
            Assert.IsTrue(
                double.TryParse(mine[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds),
                $"seconds 必须是个数,实际:{mine[5]}");
            Assert.IsTrue(seconds >= 0, $"自己这条刚开始跑,耗时不该是负的,实际 {seconds}。");
        });
    }

    /// <summary>
    /// <b>后台进程不混进会话列表。</b>
    /// <para>
    /// PG 10 起 <c>pg_stat_activity</c> 把 <c>checkpointer</c> / <c>walwriter</c> / <c>io worker</c>
    /// 这些也列进来了,它们的 user / host / db / state / query <b>全是 NULL</b> ——
    /// 不过滤的话运维面第一栏开箱就是一片空格子。
    /// 断言两头都要有:① 视图里确实有这类行(否则这条测试什么都没证明);
    /// ② 方言包给的列表里一行都没有。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 会话列表_后台进程不混进来()
    {
        await WithPostgresAsync(async raw =>
        {
            long background = Convert.ToInt64(
                await ScalarAsync(raw, "select count(*) from pg_catalog.pg_stat_activity where datname is null"),
                CultureInfo.InvariantCulture);
            Assert.IsTrue(background > 0,
                "这台服务端一个后台进程都没有?那这条用例证明不了任何事 —— 先确认连的是真 PG。");

            (_, List<string[]> rows) = await ReadNamedGridAsync(raw, new PostgreSqlPack().SessionListSql!);
            Assert.IsTrue(rows.Count > 0, "至少该看得见自己这一条。");
            foreach (string[] row in rows)
            {
                Assert.IsTrue(row[3].Length > 0,
                    $"列表里出现了一行没有库的会话(id={row[0]}) —— 那是后台进程,不该混进来。");
            }
        });
    }

    /// <summary>
    /// <b>开着事务时耗时不会变成负数</b> —— 这是 <c>clock_timestamp()</c> 而不是 <c>now()</c> 的理由。
    /// <para>
    /// 先把反例钉死:同一个事务里 <c>now() - query_start</c> 实测给的是 <b>-2 秒</b>
    /// (<c>now()</c> 冻在事务开始那一刻,而 <c>query_start</c> 是 2 秒之后的这条查询)。
    /// 再验方言包给的那一栏是非负的。运维面用的元数据连接完全可能正开着事务,
    /// 那时一整栏负耗时既没法排序也没法读。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 会话列表_开着事务时耗时不会变成负数()
    {
        await WithPostgresAsync(async raw =>
        {
            var pack = new PostgreSqlPack();
            int self = Convert.ToInt32(await ScalarAsync(raw, pack.SessionIdSql!), CultureInfo.InvariantCulture);

            await ExecAsync(raw, "begin");
            try
            {
                await ExecAsync(raw, "select pg_sleep(2)");

                // 反例:契约要点里写的那条 now() 版本,在事务里是负的。
                double nowBased = Convert.ToDouble(
                    await ScalarAsync(
                        raw,
                        "select extract(epoch from now() - query_start) "
                        + "from pg_catalog.pg_stat_activity where pid = pg_backend_pid()"),
                    CultureInfo.InvariantCulture);
                Assert.IsTrue(nowBased < 0,
                    $"前置条件:事务里 now() 是冻住的,now() - query_start 应当是负数,实际 {nowBased}。"
                    + "它要是不再是负的,说明 PG 改了 now() 的语义,那时这条注释与实现都该重看。");

                (_, List<string[]> rows) = await ReadNamedGridAsync(raw, pack.SessionListSql!);
                string[]? mine = rows.Find(r => r[0] == self.ToString(CultureInfo.InvariantCulture));
                Assert.IsNotNull(mine, "开着事务照样该看得见自己。");
                double seconds = double.Parse(mine[5], NumberStyles.Float, CultureInfo.InvariantCulture);
                Assert.IsTrue(seconds >= 0,
                    $"方言包给的耗时必须非负,实际 {seconds} —— 负数说明这条 SQL 用回了 now()。");
            }
            finally
            {
                await ExecAsync(raw, "rollback");
            }
        });
    }

    /// <summary>
    /// <b>没人争锁时锁列表干净地返回空</b>,而不是报错。
    /// <para>
    /// 契约要的正是这个:空表意味着"真的没有阻塞"。这条查询要是在无争用时就跑不通,
    /// 用户永远等不到它在真出事那一刻给出答案。列名一并守住(调用方按序号读)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 锁列表_没人争锁时干净地返回空()
    {
        await WithPostgresAsync(async raw =>
        {
            string sql = new PostgreSqlPack().LockListSql!;
            Assert.IsNotNull(sql, "PG 有 pg_locks + pg_blocking_pids(),这里不该是 null。");

            (List<string> headers, List<string[]> rows) = await ReadNamedGridAsync(raw, sql);
            CollectionAssert.AreEqual(
                new[] { "blocked_id", "blocking_id", "object", "mode", "query" },
                headers,
                "列名与列序是契约的一部分。");
            Assert.AreEqual(0, rows.Count,
                "这一组用例自己没制造争用,锁表该是空的 —— 有行说明这台服务端上另有东西挂着,"
                + "或者这条 SQL 把'持有锁'误当成了'被阻塞'。");
        });
    }

    /// <summary>
    /// <b>真机阻塞链:指认得出持锁方,而且两边的锁模式都在。</b>
    /// <para>
    /// 造一个最常见的形态:两个事务改同一行。断言逐条对着一个失败模式:
    /// ① <c>blocked_id</c> / <c>blocking_id</c> 认反了(那样用户会去杀被害者);
    /// ② 两个 id 不是 pid(拿去会话栏里找不到、拿去取消也取消不了);
    /// ③ <c>mode</c> 只给一边(<c>S</c> 撞 <c>X</c> 与 <c>X</c> 撞 <c>X</c> 是两种事);
    /// ④ <c>query</c> 给成了持锁方的语句(那一格多半是空的,而被阻塞方的语句才是用户在找的)。
    /// </para>
    /// <para>
    /// 顺带把 PG 的一条形态钉住:行锁冲突下 <c>object</c> 是 <c>transaction &lt;xid&gt;</c>,
    /// <b>不是表名</b> —— 界面别指望从这一格读出表来。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 锁列表_真机阻塞链指认得出持锁方与两边的锁模式()
    {
        await WithPostgresAsync(async raw =>
        {
            await ExecAsync(raw, "drop table if exists lock_probe cascade");
            await ExecAsync(raw, "create table lock_probe(id int primary key, v int)");
            await ExecAsync(raw, "insert into lock_probe values(1, 0)");

            var pack = new PostgreSqlPack();
            await using SqlConnection holder = await OpenAsync(Database);
            await using SqlConnection waiter = await OpenAsync(Database);
            int holderPid = Convert.ToInt32(
                await ScalarAsync(holder.Raw, pack.SessionIdSql!), CultureInfo.InvariantCulture);
            int waiterPid = Convert.ToInt32(
                await ScalarAsync(waiter.Raw, pack.SessionIdSql!), CultureInfo.InvariantCulture);

            await ExecAsync(holder.Raw, "begin");
            await ExecAsync(holder.Raw, "update lock_probe set v = v + 1 where id = 1");

            // 这条会挂住,所以**不等它** —— 挂住正是本用例要观察的状态。
            Task blocked = ExecAsync(waiter.Raw, "update lock_probe set v = v + 100 where id = 1");
            string[]? row = null;
            try
            {
                row = await PollForLockAsync(raw, waiterPid.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                await ExecAsync(holder.Raw, "rollback");
                try
                {
                    // 放开之后它就跑完了;这里只是把任务收干净,它的成败不是本用例的结论。
                    await blocked;
                }
                catch (DbException)
                {
                    // 忽略。
                }
            }

            Assert.IsNotNull(row,
                $"等了 10 秒也没在锁表里看见被阻塞的那条(pid={waiterPid}) —— "
                + "要么阻塞没发生,要么 pg_blocking_pids() 那条链没接上。");
            Assert.AreEqual(waiterPid.ToString(CultureInfo.InvariantCulture), row[0], "blocked_id 是被挡住的那个。");
            Assert.AreEqual(holderPid.ToString(CultureInfo.InvariantCulture), row[1],
                "blocking_id 是持锁的那个 —— 认反了用户会去杀被害者。");

            StringAssert.StartsWith(row[2], "transaction ",
                $"PG 的行锁冲突等的是 transactionid 锁,object 该是 'transaction <xid>' 而不是表名,实际:{row[2]}");
            StringAssert.Contains(row[3], "<-",
                $"mode 必须把冲突的两边都说出来,实际:{row[3]}");
            StringAssert.StartsWith(row[3], "ShareLock",
                $"被阻塞方要的是 ShareLock(等对方事务结束),实际:{row[3]}");
            StringAssert.Contains(row[3], "ExclusiveLock",
                $"持锁方持的是它自己事务号上的 ExclusiveLock,实际:{row[3]}");
            StringAssert.Contains(row[4], "v + 100",
                $"query 是**被阻塞方**的语句(这一行的主语是它),实际:{row[4]}");

            // 两个 id 都得是 pid:拿去取消要认得出来。
            Assert.IsNotNull(pack.CancelSessionSql(row[0]), "blocked_id 必须是个能拿去取消的 pid。");
            Assert.IsNotNull(pack.CancelSessionSql(row[1]), "blocking_id 必须是个能拿去取消的 pid。");
        });
    }

    // ═══════════════════════════ 表设计器(能力组 5) ═══════════════════════════

    /// <summary>
    /// <b>基类的通行写法在 PG 上逐条执行得了</b> —— 所以这三格不覆盖是有依据的,不是没写。
    /// <para>
    /// 刻意挑一个<b>自定义 schema</b> 加一个<b>带空格与大写的表名</b>:
    /// 那正是 <c>DbMaintenance</c> 静默不可达、而拼接又必炸的组合(§3.5)。
    /// 建完索引再回查 <c>pg_indexes.schemaname</c>,钉住"索引跟着表走"这一条。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 表设计器_基类的通行写法在PG上逐条执行得了()
    {
        await WithPostgresAsync(async raw =>
        {
            await ResetDesignSchemaAsync(raw);
            var pack = new PostgreSqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "Odd Table", "app");

            await ExecAsync(raw, pack.AddColumnDdl(target, new("qty", 4, "integer", IsNullable: false, DefaultValue: "0"))!);
            await ExecAsync(raw, pack.CreateIndexDdl(target, "ix_ab", ["a", "b"], unique: false)!);
            await ExecAsync(raw, pack.CreateIndexDdl(target, "ux_a", ["a"], unique: true)!);

            SqlTableSchema schema = await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token);
            SqlColumn qty = schema.Columns.Single(c => c.Name == "qty");
            Assert.AreEqual("integer", qty.DataType);
            Assert.IsFalse(qty.IsNullable, "NOT NULL 得真的落下去。");
            Assert.AreEqual("0", qty.DefaultValue, "DEFAULT 得真的落下去。");
            Assert.IsTrue(schema.Indexes.Any(i => i.Name == "ix_ab" && !i.IsUnique));
            Assert.IsTrue(schema.Indexes.Any(i => i.Name == "ux_a" && i.IsUnique));

            // **索引跟着表走**:CREATE INDEX 的索引名是裸的,索引却落在了表所在的 schema 里。
            // 这一条正是 DropIndexDdl 必须补 schema 限定的根据。
            object? where = await ScalarAsync(
                raw, "select schemaname from pg_catalog.pg_indexes where indexname = 'ix_ab'");
            Assert.AreEqual("app", where?.ToString(),
                "索引必须落在表所在的 schema 里 —— 落错地方的话删索引那条限定名就是错的。");

            await ExecAsync(raw, pack.DropColumnDdl(target, "qty")!);
            SqlTableSchema after = await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token);
            Assert.IsFalse(after.Columns.Any(c => c.Name == "qty"), "删列得真的删掉。");
        });
    }

    /// <summary>
    /// <b>删索引必须带 schema 限定:基类的裸名在 PG 上删不到。</b>
    /// <para>
    /// 两头都发一次才算证明:基类那条以 <c>42704 index "ix_ab" does not exist</c> 打回,
    /// 方言包这条成功。少了前一半,这个覆盖看起来就只是"风格偏好"。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 表设计器_删索引必须带schema限定_基类的裸名在PG上删不到()
    {
        await WithPostgresAsync(async raw =>
        {
            await ResetDesignSchemaAsync(raw);
            var pack = new PostgreSqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "Odd Table", "app");
            await ExecAsync(raw, pack.CreateIndexDdl(target, "ix_ab", ["a", "b"], unique: false)!);

            // 基类的写法:裸名,靠 search_path 解析 —— app 不在 search_path 里,于是找不到。
            Exception? bare = await CaptureAsync(raw, "DROP INDEX \"ix_ab\"");
            Assert.IsNotNull(bare, "裸名在 PG 上删不到,这里必须抛。");
            StringAssert.Contains(bare.Message, "does not exist",
                $"预期 42704 index \"ix_ab\" does not exist,实际:{bare.Message}");

            string? qualified = pack.DropIndexDdl(target, "ix_ab");
            Assert.AreEqual("DROP INDEX \"app\".\"ix_ab\"", qualified,
                "限定用的是**表所在的 schema** —— PG 强制索引与表同 schema。");
            await ExecAsync(raw, qualified!);
            Assert.IsNull(
                await ScalarAsync(raw, "select indexname from pg_catalog.pg_indexes where indexname = 'ix_ab'"),
                "限定名这条得真的删掉。");

            // schema 为空(调用方还没拿到 schema 的中间态)时回落到裸名 —— 那时按 search_path 解析是唯一能做的事。
            Assert.AreEqual(
                "DROP INDEX \"ix_public\"",
                pack.DropIndexDdl(new(SqlObjectKind.Table, "t"), "ix_public"));

            // 标识符纪律:名字里带双引号也不能拼出可执行的东西。
            Assert.AreEqual(
                "DROP INDEX \"app\".\"a\"\"b\"",
                pack.DropIndexDdl(target, "a\"b"));
        });
    }

    /// <summary>
    /// <b>建索引反过来:索引名<b>不能</b>加 schema 限定,加了是语法错。</b>
    /// <para>
    /// 这条与上一条是 PG 语法的一正一反,放在一起才说得清为什么
    /// <c>CreateIndexDdl</c> 不覆盖而 <c>DropIndexDdl</c> 非覆盖不可。
    /// 少了这一条,后来的人很容易"顺手把建索引也补上限定",而那会当场炸。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 表设计器_建索引反过来不能给索引名加schema限定()
    {
        await WithPostgresAsync(async raw =>
        {
            await ResetDesignSchemaAsync(raw);
            var pack = new PostgreSqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "Odd Table", "app");

            string? create = pack.CreateIndexDdl(target, "ix_b", ["b"], unique: false);
            Assert.AreEqual("""CREATE INDEX "ix_b" ON "app"."Odd Table" ("b")""", create,
                "索引名是裸的,表名才带限定。");
            await ExecAsync(raw, create!);

            Exception? qualified = await CaptureAsync(
                raw, """CREATE INDEX "app"."ix_bad" ON "app"."Odd Table" ("b")""");
            Assert.IsNotNull(qualified, "给索引名加 schema 限定在 PG 上是语法错,这里必须抛。");
            StringAssert.Contains(qualified.Message, "syntax error",
                $"预期 42601 syntax error at or near \".\",实际:{qualified.Message}");

            // 主键/唯一约束背后的索引删不掉 —— 结构页那颗按钮点在主键上必然是这条错,界面该先拦。
            Exception? constraintBacked = await CaptureAsync(raw, pack.DropIndexDdl(target, "Odd Table_pkey")!);
            Assert.IsNotNull(constraintBacked, "约束背后的索引不该删得掉。");
            StringAssert.Contains(constraintBacked.Message, "constraint",
                $"预期 2BP01 … because constraint … requires it,实际:{constraintBacked.Message}");
        });
    }

    /// <summary>
    /// <b>通用 DDL 表达不了的列定义一律返回 <see langword="null" />,而不是静默办成一个普通列。</b>
    /// <para>
    /// 四种情形各验一次,并且验"普通列照样给得出 DDL" —— 后者是
    /// <c>SqlStructureTabViewModel.CanDesign</c> 的判据,漏了它整页设计器会被关掉。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 表设计器_表达不了的列定义一律返回null而不是静默办成普通列()
    {
        await WithPostgresAsync(async raw =>
        {
            await ResetDesignSchemaAsync(raw);
            var pack = new PostgreSqlPack();
            var target = new SqlObject(SqlObjectKind.Table, "Odd Table", "app");

            Assert.IsNotNull(pack.AddColumnDdl(target, new("plain", 9, "integer", IsNullable: true)),
                "普通列必须给得出 DDL —— CanDesign 就靠这一格,给不出整页设计器会被关掉。");

            Assert.IsNull(pack.AddColumnDdl(target, new("g", 9, "integer", true, IsGenerated: true)),
                "生成列:模型里没有生成表达式,拼出来只会是个普通列。");
            Assert.IsNull(pack.AddColumnDdl(target, new("p", 9, "integer", false, IsPrimaryKey: true)),
                "主键列:拼不出 PRIMARY KEY。");
            Assert.IsNull(pack.AddColumnDdl(target, new("a", 9, "integer", false, IsAutoIncrement: true)),
                "自增列:拼不出 GENERATED BY DEFAULT AS IDENTITY。");
            Assert.IsNull(pack.AddColumnDdl(target, new("c", 9, "integer", true, Comment: "订单号")),
                "带注释:PG 的列注释只能另发一条 COMMENT ON,这一格返回的是一条 DDL。");

            // 上面那四条不是"PG 做不到",而是"这条 DDL 说不出口" —— 各自的正解在真机上都成立,
            // 这里发一遍,免得将来有人以为方言不支持而把契约也改窄了。
            await ExecAsync(raw, """ALTER TABLE "app"."Odd Table" ADD COLUMN "sid" integer GENERATED BY DEFAULT AS IDENTITY""");
            await ExecAsync(raw, """COMMENT ON COLUMN "app"."Odd Table"."sid" IS '订单号'""");
            SqlTableSchema schema = await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token);
            SqlColumn sid = schema.Columns.Single(c => c.Name == "sid");
            Assert.IsTrue(sid.IsAutoIncrement, "IDENTITY 列在 PG 上加得上,只是这条通用 DDL 写不出。");
            Assert.AreEqual("订单号", sid.Comment, "列注释在 PG 上加得上,只是要另发一条语句。");

            // 表里已有行时 NOT NULL 不带 DEFAULT 必然失败 —— 界面该在这两个输入框之间就拦住。
            await ExecAsync(raw, """INSERT INTO "app"."Odd Table"("id") VALUES (1)""");
            Exception? notNull = await CaptureAsync(
                raw, pack.AddColumnDdl(target, new("must", 9, "integer", IsNullable: false))!);
            Assert.IsNotNull(notNull, "非空表上加 NOT NULL 且无默认值必然失败。");
            StringAssert.Contains(notNull.Message, "contains null values",
                $"预期 23502 … contains null values,实际:{notNull.Message}");
        });
    }

    /// <summary>
    /// <b>类型下拉里的每一项都建得出列,而且读回来逐字相同。</b>
    /// <para>
    /// 这一条守的是 <see cref="PostgreSqlPack.CommonTypes" /> 的全部意义:
    /// PG 会把别名当场折算掉(<c>varchar(50)</c> → <c>character varying(50)</c>),
    /// 语法糖更彻底(<c>serial</c> → <c>integer</c> + <c>nextval</c>)。
    /// 下拉里放一个"服务端会当场改写掉"的选项,用户建完回头一看类型对不上,最像插件出了 bug。
    /// 断言用的是 <see cref="PostgreSqlPack.DescribeAsync" /> 读回来的 <c>DataType</c>,
    /// 也就是界面真正会显示的那个值 —— 这一趟来回必须是恒等的。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 类型表_每一项都建得出列而且读回来逐字相同()
    {
        await WithPostgresAsync(async raw =>
        {
            var pack = new PostgreSqlPack();
            IReadOnlyList<string> types = pack.CommonTypes;
            Assert.IsTrue(types.Count > 15, $"类型表太短了({types.Count} 项),下拉里选不到东西。");
            CollectionAssert.AllItemsAreUnique(types.ToArray(), "同一个类型不该出现两次。");

            await ExecAsync(raw, "drop table if exists type_probe cascade");
            await ExecAsync(raw, "create table type_probe(id int primary key)");
            var target = new SqlObject(SqlObjectKind.Table, "type_probe", "public");

            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < types.Count; i++)
            {
                string name = $"c{i.ToString(CultureInfo.InvariantCulture)}";
                string? ddl = pack.AddColumnDdl(target, new(name, i + 2, types[i], IsNullable: true));
                Assert.IsNotNull(ddl, $"类型 {types[i]} 生成不出加列 DDL。");
                Exception? failure = await CaptureAsync(raw, ddl!);
                Assert.IsNull(failure, $"类型下拉里的 {types[i]} 在真机上建不出列:{failure?.Message}");
                expected[name] = types[i];
            }

            SqlTableSchema schema = await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token);
            foreach ((string column, string declared) in expected)
            {
                SqlColumn actual = schema.Columns.Single(c => c.Name == column);
                Assert.AreEqual(declared, actual.DataType,
                    $"下拉里写的是 {declared},建完读回来却是 {actual.DataType} —— "
                    + "服务端把它折算掉了,这一项该换成规范形态(或者根本不该进表)。");
                Assert.IsFalse(actual.IsAutoIncrement,
                    $"{declared} 建出来带了自增语义(serial 那一类) —— 类型表里不该有语法糖。");
                Assert.IsNull(actual.DefaultValue,
                    $"{declared} 建出来自带了默认值 {actual.DefaultValue} —— 那说明它不是个纯类型。");
            }

            // 反例钉死:被刻意排除的两项,排除的理由在真机上成立。
            Assert.IsFalse(types.Contains("serial", StringComparer.Ordinal), "serial 是语法糖,不该进类型表。");
            await ExecAsync(raw, """ALTER TABLE "public"."type_probe" ADD COLUMN "sugar" serial""");
            SqlTableSchema sugared = await pack.DescribeAsync(raw, target, TestContext.CancellationTokenSource.Token);
            SqlColumn sugar = sugared.Columns.Single(c => c.Name == "sugar");
            Assert.AreEqual("integer", sugar.DataType, "serial 存下来的是 integer —— 下拉里摆它就是摆一个会被改写的选项。");
            Assert.IsTrue(sugar.IsAutoIncrement, "而且它悄悄带上了序列默认值。");
        });
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>
    /// 拿到一条连着 <see cref="Database" /> 的连接跑一段;没有 PostgreSQL 就 <c>Inconclusive</c>。
    /// <para>
    /// 库不存在时现建 —— 这一组要能在一台干净的实例上直接跑起来。
    /// PG 没有 <c>CREATE DATABASE IF NOT EXISTS</c>,所以先查 <c>pg_database</c> 再建;
    /// 而且 <c>CREATE DATABASE</c> 不能在事务里跑,只能单独发。
    /// </para>
    /// </summary>
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
                bootstrap.Raw, "select 1 from pg_catalog.pg_database where datname = 'ops_pg'");
            if (exists is null)
            {
                // 库名是本文件里的常量,不是用户输入;仍然走 QuoteIdentifier,免得将来有人改成变量。
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
            SessionId = "pg-ops",
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

    /// <summary>
    /// 把表设计器用例的场地清干净重建。
    /// <para>
    /// 刻意用<b>自定义 schema</b> + <b>带空格与大写的表名</b>:那正是"不转义就必炸、
    /// 而 <c>DbMaintenance</c> 又静默不可达"的组合(§3.5)。<c>search_path</c> 保持默认,
    /// 于是 <c>app</c> 里的东西<b>只能</b>靠限定名够得着 —— 删索引那条覆盖就靠这个前提才证得了。
    /// </para>
    /// </summary>
    /// <param name="connection">连接。</param>
    /// <returns>任务。</returns>
    private static async Task ResetDesignSchemaAsync(DbConnection connection)
    {
        await ExecAsync(connection, "drop schema if exists app cascade");
        await ExecAsync(connection, "create schema app");
        await ExecAsync(connection, """create table app."Odd Table"(id int primary key, a int, b int)""");
    }

    /// <summary>
    /// 反复查锁表,直到看见 <paramref name="blockedId" /> 那条等待为止。
    /// <para>
    /// 锁等待是<b>异步</b>出现的:发出 UPDATE 到它真的挂在服务端之间有一小段,
    /// 直接查一次十有八九是空的。轮询而不是固定 sleep,是为了让常见情形下用例仍然是快的。
    /// </para>
    /// </summary>
    /// <param name="connection">观察用的连接。</param>
    /// <param name="blockedId">被阻塞方的 pid。</param>
    /// <returns>那一行;等不到则为 <see langword="null" />。</returns>
    private static async Task<string[]?> PollForLockAsync(DbConnection connection, string blockedId)
    {
        string sql = new PostgreSqlPack().LockListSql!;
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
    /// <returns>值;没有行或值为 NULL 时是 <see langword="null" />。</returns>
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>数一张表有多少行(表名是本文件里的常量,走 <c>QuoteIdentifier</c> 是纪律)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="table">表名。</param>
    /// <returns>行数。</returns>
    private static async Task<long> CountAsync(DbConnection connection, string table) =>
        Convert.ToInt64(
            await ScalarAsync(connection, $"select count(*) from {new PostgreSqlPack().QuoteIdentifier(table)}"),
            CultureInfo.InvariantCulture);
}

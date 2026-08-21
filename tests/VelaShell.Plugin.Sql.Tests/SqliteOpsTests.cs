using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// SQLite 方言包的 <b>M4 资产真机验收</b>:执行计划(能力组 7)、运维面(能力组 8)、表设计器 DDL(能力组 5)。
/// <para>
/// SQLite 是进程内库,所以这一组**没有"拿不到服务器就跳过"这回事** ——
/// 每个用例自建一个临时 <c>.db</c>,跑完删掉。也正因为它一定跑得起来,
/// 这里的断言都刻意做成"真发给引擎看它认不认",而不是只比字符串:
/// 方言资产最容易出的错是**语法合法、语义错误**(§3.4 里 <c>GetIndexList</c> 返回 <c>"0"</c> 就是那个原型),
/// 只比文本的测试对这一类错误完全无感。
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteOpsTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    // ═══════════════════════════ 执行计划(能力组 7) ═══════════════════════════

    /// <summary>
    /// <c>EXPLAIN QUERY PLAN</c> 在真机上给得出计划行,而且给的是"走没走索引"那一层。
    /// <para>
    /// 两条断言分别对着一个失败模式:① 一行都没有(说明发的语句根本不是计划查询);
    /// ② 内容是 VDBE 字节码(说明误用了 <c>EXPLAIN</c> —— 那个也返回行,而且行更多,
    /// 光看"有没有结果"是分不出来的)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_真机给出的是走没走索引那一层()
    {
        await WithSqliteAsync(async raw =>
        {
            await ExecAsync(raw, "create table plan_probe(id integer primary key, name text, tag text)");
            await ExecAsync(raw, "create index ix_plan_probe_tag on plan_probe(tag)");
            await ExecAsync(raw, "insert into plan_probe(name, tag) values('a', 'x'), ('b', 'y')");

            var pack = new SqlitePack();
            string? explain = pack.ExplainSql("select id, name from plan_probe where tag = 'x'", analyze: false);
            Assert.IsNotNull(explain, "SQLite 是有执行计划的,这里不该是 null。");
            Assert.AreEqual(
                "EXPLAIN QUERY PLAN select id, name from plan_probe where tag = 'x'",
                explain,
                "计划语句只是给用户 SQL 加个前缀,不套派生表也不改写它。");

            List<string> plan = await ReadAllAsync(raw, explain);
            Assert.IsTrue(plan.Count > 0, "计划一行都没有,说明发出去的根本不是 EXPLAIN QUERY PLAN。");
            string text = string.Join("\n", plan);
            Assert.IsTrue(
                text.Contains("SEARCH", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SCAN", StringComparison.OrdinalIgnoreCase),
                $"计划里应当有 SEARCH/SCAN 这一层,实际拿到:\n{text}");
            Assert.IsFalse(
                text.Contains("OpenRead", StringComparison.OrdinalIgnoreCase),
                "出现 VDBE 操作码说明发的是 EXPLAIN 而不是 EXPLAIN QUERY PLAN —— 那是给引擎调试用的,不是给用户看的。");

            // 用了索引就得说出来:这是"执行计划"这一栏存在的全部理由。
            Assert.IsTrue(
                text.Contains("ix_plan_probe_tag", StringComparison.Ordinal),
                $"等值条件命中了索引,计划里必须点出索引名,实际拿到:\n{text}");
        });
    }

    /// <summary>
    /// <b>analyze 两档返回同一条,而且它真的不执行查询。</b>
    /// <para>
    /// 契约把 analyze 标成危险开关,是因为别的方言的 analyze 档会真把语句跑一遍(对 <c>DELETE</c> 就是真删)。
    /// SQLite 没有那种档位,所以两档同一条是**如实**而不是偷懒 —— 这里用一条真 <c>DELETE</c> 来证明:
    /// 拿 analyze=true 生成的语句发出去,表里的行数必须一行不少。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 执行计划_analyze两档同一条_而且它本来就不执行查询()
    {
        await WithSqliteAsync(async raw =>
        {
            await ExecAsync(raw, "create table plan_danger(id integer primary key, name text)");
            await ExecAsync(raw, "insert into plan_danger(name) values('a'), ('b'), ('c')");

            var pack = new SqlitePack();
            const string Delete = "delete from plan_danger where id > 0";
            string? plain = pack.ExplainSql(Delete, analyze: false);
            string? analyzed = pack.ExplainSql(Delete, analyze: true);
            Assert.AreEqual(plain, analyzed, "SQLite 没有 EXPLAIN ANALYZE 这一档,两档必须给同一条。");
            Assert.IsNotNull(analyzed);

            // 真发一次 analyze 档 —— 如果它像别的方言那样"先跑再给计划",这三行就没了。
            _ = await ReadAllAsync(raw, analyzed);
            Assert.AreEqual(3L, Convert.ToInt64(await ScalarAsync(raw, "select count(*) from plan_danger")),
                "EXPLAIN QUERY PLAN 只做 prepare,底下那段程序一步都不该走。");

            // 末尾分号要剥掉(编辑器切出来的语句常带终止符)。
            Assert.AreEqual(
                "EXPLAIN QUERY PLAN select 1",
                pack.ExplainSql("select 1;  ", analyze: false));
        });
    }

    // ═══════════════════════════ 运维面(能力组 8) ═══════════════════════════

    /// <summary>
    /// <b>会话列表与锁列表返回 <see langword="null" /> —— 这是"这个方言里不存在这个概念",不是"还没做"。</b>
    /// <para>
    /// 界面据此显示"该数据库不提供会话列表",而不是画一张空表格(§7.8)。
    /// 这条断言看着像在测 <c>null == null</c>,它守的其实是**将来某个人手痒去凑一条**:
    /// 拿 <c>pragma_database_list</c> 或 <c>PRAGMA lock_status</c> 硬凑出五列来,
    /// 界面就会一本正经地画出一份与"谁在跑什么"毫无关系的表 —— 那正是 §3.4 那条纪律要防的。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 会话与锁_返回null_因为SQLite里没有这两个概念()
    {
        var pack = new SqlitePack();
        Assert.IsNull(pack.SessionListSql, "SQLite 是进程内库,没有服务端会话。");
        Assert.IsNull(pack.LockListSql, "SQLite 的锁是整库粒度的文件锁,没有可查的锁表,也没有阻塞链。");

        // 取消那一路同样不走 SQL(见 SqlitePack.SessionIdSql 的说明:执行层要改走 sqlite3_interrupt)。
        Assert.IsNull(pack.SessionIdSql);
        Assert.IsNull(pack.CancelSessionSql("1"));
    }

    /// <summary>
    /// 类型表是**静态**的:与库里建了什么无关。
    /// <para>
    /// 这正是不能用 <c>GetDbTypes()</c> 的那条理由(§2.3)—— 它返回的是"这个库当前用到了哪些类型",
    /// 建一张表就多几项。所以这里在空库与建过表之后各取一次,断言两次一模一样。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 类型表_是静态表_不随库里建了什么而变()
    {
        await WithSqliteAsync(async raw =>
        {
            var pack = new SqlitePack();
            IReadOnlyList<string> before = pack.CommonTypes;
            Assert.IsTrue(before.Count > 0, "类型下拉不能是空的。");

            await ExecAsync(raw, "create table type_probe(a blob, b real, c varchar(9))");
            CollectionAssert.AreEqual(before.ToArray(), pack.CommonTypes.ToArray(),
                "静态类型表不该跟着库里的表变 —— 会变的那个是 GetDbTypes(),正是本包不用它的原因。");
        });

        IReadOnlyList<string> types = new SqlitePack().CommonTypes;

        // 五种亲和性必须都在,而且它们排在最前(那是最没有歧义的声明写法)。
        string[] affinities = ["INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC"];
        CollectionAssert.AreEqual(affinities, types.Take(5).ToArray(),
            "SQLite 的五种类型亲和性应当排在最前。");

        Assert.AreEqual(types.Count, types.Distinct(StringComparer.Ordinal).Count(), "类型表里不该有重复项。");
        Assert.IsFalse(types.Any(string.IsNullOrWhiteSpace), "类型表里不该有空项。");

        // JSON 是故意不给的:SQLite 没有这个类型,声明成 JSON 会落 NUMERIC 亲和性,
        // 形如 123 的 JSON 文档会被静默转成整数。
        Assert.IsFalse(types.Contains("JSON", StringComparer.OrdinalIgnoreCase),
            "JSON 不是 SQLite 的类型,列进下拉等于摆一个静默丢数据的选项。");
    }

    // ═══════════════════════════ 表设计器 DDL(能力组 5) ═══════════════════════════

    /// <summary>
    /// DDL 文本逐字对,而且**用户标识符一律走 <c>QuoteIdentifier</c>** ——
    /// 名字里含双引号时必须加倍,否则就是 §5.4.4 实测能删表的那条路。
    /// </summary>
    [TestMethod]
    public void 表设计器DDL_文本正确且标识符按方言转义()
    {
        var pack = new SqlitePack();
        var target = new SqlObject(SqlObjectKind.Table, "ops\"tbl");

        Assert.AreEqual(
            "ALTER TABLE \"ops\"\"tbl\" ADD COLUMN \"qty\"\"x\" INTEGER",
            pack.AddColumnDdl(target, new SqlColumn("qty\"x", 1, "INTEGER", IsNullable: true)));

        Assert.AreEqual(
            "ALTER TABLE \"ops\"\"tbl\" ADD COLUMN \"qty\"\"x\" INTEGER NOT NULL DEFAULT 0",
            pack.AddColumnDdl(target, new SqlColumn("qty\"x", 1, "INTEGER", IsNullable: false, DefaultValue: "0")));

        Assert.AreEqual(
            "ALTER TABLE \"ops\"\"tbl\" DROP COLUMN \"qty\"\"x\"",
            pack.DropColumnDdl(target, "qty\"x"));

        Assert.AreEqual(
            "CREATE UNIQUE INDEX \"ix\"\"w\" ON \"ops\"\"tbl\" (\"qty\"\"x\", \"id\")",
            pack.CreateIndexDdl(target, "ix\"w", ["qty\"x", "id"], unique: true));

        Assert.AreEqual(
            "CREATE INDEX \"ix\"\"w\" ON \"ops\"\"tbl\" (\"id\")",
            pack.CreateIndexDdl(target, "ix\"w", ["id"], unique: false));

        // DROP INDEX 不带表名是对的:SQLite 的索引名与表同一个命名空间,是**库级**唯一。
        Assert.AreEqual("DROP INDEX \"ix\"\"w\"", pack.DropIndexDdl(target, "ix\"w"));

        // 一列都不给的索引没有意义,基类返回 null,别在这里生成 "()"。
        Assert.IsNull(pack.CreateIndexDdl(target, "ix_empty", [], unique: false));

        // 转义纪律的通用判据(与 DialectPackIntegrationTests 同一条):
        // 内部每个定界符都成对,于是第一个落单的双引号只可能是结尾那个,payload 没法提前收尾。
        string quoted = pack.QuoteIdentifier("orders\"; drop table victim--");
        Assert.IsTrue(quoted.StartsWith('"') && quoted.EndsWith('"'));
        Assert.AreEqual(0, quoted[1..^1].Count(c => c == '"') % 2, "内部的定界符必须全部成对(加倍转义)。");
    }

    /// <summary>
    /// <b>四条 DDL 真的在库上跑一遍</b>:加列 → 建索引 → 删索引 → 删列,每一步都回查元数据确认。
    /// <para>
    /// 表名与列名里都埋了双引号 —— 这是转义的**端到端**证明:文本比对只能证明"我按规则拼了",
    /// 引擎认不认是另一回事(名字转错时 SQLite 报的是 <c>no such table</c>,不是语法错)。
    /// </para>
    /// <para>
    /// 中间那步"索引还在时删列必须失败"是刻意留的:它证明 <see cref="SqlitePack.DropColumnDdl" />
    /// 注释里说的顺序要求不是想当然,表设计器必须先删索引再删列。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 加列建索引删索引删列_在真库上依次执行成功()
    {
        await WithSqliteAsync(async raw =>
        {
            var pack = new SqlitePack();
            CancellationToken token = TestContext.CancellationTokenSource.Token;
            const string TableName = "ops\"tbl";
            const string ColumnName = "qty\"x";
            const string IndexName = "ix\"w";
            var target = new SqlObject(SqlObjectKind.Table, TableName);

            // 建表也走 QuoteIdentifier:测试自己也得守"永不裸拼用户标识符"这条纪律。
            await ExecAsync(raw, $"create table {pack.QuoteIdentifier(TableName)}(id integer primary key, name text)");
            await ExecAsync(raw, $"insert into {pack.QuoteIdentifier(TableName)}(name) values('a'), ('b')");

            // —— ① 加列(NOT NULL + 常量默认值:SQLite 唯一放行的 NOT NULL 形态)。
            string add = pack.AddColumnDdl(target, new SqlColumn(ColumnName, 3, "INTEGER", IsNullable: false, DefaultValue: "0"))
                         ?? throw new AssertFailedException("加列 DDL 不该是 null。");
            await ExecAsync(raw, add);

            SqlTableSchema afterAdd = await pack.DescribeAsync(raw, target, token);
            SqlColumn added = Col(afterAdd, ColumnName);
            Assert.AreEqual("INTEGER", added.DataType);
            Assert.IsFalse(added.IsNullable, "加的是 NOT NULL 列。");
            Assert.AreEqual("0", added.DefaultValue);
            Assert.AreEqual(2L, Convert.ToInt64(await ScalarAsync(
                raw, $"select count(*) from {pack.QuoteIdentifier(TableName)} where {pack.QuoteIdentifier(ColumnName)} = 0")),
                "已有的两行都该被默认值填上 —— 这正是 SQLite 要求 NOT NULL 必须带默认值的原因。");

            // —— ② 建索引。
            string createIndex = pack.CreateIndexDdl(target, IndexName, [ColumnName], unique: false)
                                 ?? throw new AssertFailedException("建索引 DDL 不该是 null。");
            await ExecAsync(raw, createIndex);

            SqlIndex index = Idx(await pack.DescribeAsync(raw, target, token), IndexName);
            Assert.IsFalse(index.IsUnique);
            CollectionAssert.AreEqual(new[] { ColumnName }, index.Columns.ToArray());

            // —— ③ 索引还在时删列:SQLite 拒绝。顺序要求是真的,不是注释里的想当然。
            Exception? blocked = await CaptureAsync(raw, pack.DropColumnDdl(target, ColumnName)!);
            Assert.IsNotNull(blocked, "列还被索引引用着,DROP COLUMN 必须失败。");
            // 报错原文长得像"索引坏了"(error in index ... after drop column: no such column: ...),
            // 实际是"这一列还被那条索引用着" —— DropColumnDdl 的注释里记的就是这一条。
            Assert.IsTrue(blocked.Message.Contains("after drop column", StringComparison.OrdinalIgnoreCase),
                $"预期是 error in index ... after drop column ...,实际:{blocked.Message}");

            // —— ④ 删索引,再删列。
            await ExecAsync(raw, pack.DropIndexDdl(target, IndexName)!);
            SqlTableSchema afterDropIndex = await pack.DescribeAsync(raw, target, token);
            Assert.IsFalse(afterDropIndex.Indexes.Any(i => string.Equals(i.Name, IndexName, StringComparison.Ordinal)),
                "索引应当已经没了。");

            await ExecAsync(raw, pack.DropColumnDdl(target, ColumnName)!);
            SqlTableSchema afterDropColumn = await pack.DescribeAsync(raw, target, token);
            Assert.IsFalse(afterDropColumn.Columns.Any(c => string.Equals(c.Name, ColumnName, StringComparison.Ordinal)),
                "列应当已经没了。");
            Assert.AreEqual(2, afterDropColumn.Columns.Count, "只该少那一列,别的列不许受牵连。");
        });
    }

    /// <summary>唯一索引也真跑一遍:唯一性必须落到元数据上(<c>GetIndexList</c> 恰恰是把它丢了的那个)。</summary>
    [TestMethod]
    public async Task 建唯一索引_唯一性落到元数据上()
    {
        await WithSqliteAsync(async raw =>
        {
            var pack = new SqlitePack();
            var target = new SqlObject(SqlObjectKind.Table, "uniq_probe");
            await ExecAsync(raw, "create table uniq_probe(id integer primary key, code text, tag text)");
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
    /// <b>SQLite 的 <c>ADD COLUMN</c> 限制只在"表里已经有行"时才生效 —— 所以方言包不能替引擎拦。</b>
    /// <para>
    /// 这条用例是本组里最该留着的一条:它把同一份列定义分别发给**空表**与**有行的表**,
    /// 证明同一条 DDL 在前者上成功、在后者上被拒。方言包只有一份表结构、没有连接,
    /// 判不了表里有没有行;要是照"文档上写着不许"去拦,表设计器就会在空表上拒绝一件合法的事,
    /// 而那种"按不动的按钮"比一条清楚的引擎报错更难查。
    /// </para>
    /// <para>顺带钉死两条真机原文,它们是 <see cref="SqlitePack.AddColumnDdl" /> 注释里那张表的来源。</para>
    /// </summary>
    [TestMethod]
    public async Task 加列_限制只在表里有行时才生效_所以这一层不替引擎拦()
    {
        await WithSqliteAsync(async raw =>
        {
            var pack = new SqlitePack();
            var empty = new SqlObject(SqlObjectKind.Table, "add_empty");
            var filled = new SqlObject(SqlObjectKind.Table, "add_filled");
            await ExecAsync(raw, "create table add_empty(id integer primary key)");
            await ExecAsync(raw, "create table add_filled(id integer primary key)");
            await ExecAsync(raw, "insert into add_filled(id) values(1)");

            // —— ① NOT NULL 且没有默认值:DDL 照发,空表放行、有行的表拒绝。
            var notNull = new SqlColumn("c1", 2, "INTEGER", IsNullable: false);
            Assert.AreEqual(
                "ALTER TABLE \"add_empty\" ADD COLUMN \"c1\" INTEGER NOT NULL",
                pack.AddColumnDdl(empty, notNull));
            await ExecAsync(raw, pack.AddColumnDdl(empty, notNull)!);

            Exception? notNullOnRows = await CaptureAsync(raw, pack.AddColumnDdl(filled, notNull)!);
            Assert.IsNotNull(notNullOnRows, "表里有行时才轮到 SQLite 拒绝。");
            Assert.IsTrue(
                notNullOnRows.Message.Contains("Cannot add a NOT NULL column", StringComparison.Ordinal),
                $"预期原文 Cannot add a NOT NULL column with default value NULL,实际:{notNullOnRows.Message}");

            // —— ② 非常量默认值:同上。CURRENT_TIMESTAMP 在 CREATE TABLE 里一直合法,
            //    在 ADD COLUMN 里只有空表放行。
            var expression = new SqlColumn(
                "c2", 3, "TEXT", IsNullable: true, DefaultValue: "CURRENT_TIMESTAMP", IsDefaultExpression: true);
            await ExecAsync(raw, pack.AddColumnDdl(empty, expression)!);

            Exception? expressionOnRows = await CaptureAsync(raw, pack.AddColumnDdl(filled, expression)!);
            Assert.IsNotNull(expressionOnRows);
            Assert.IsTrue(
                expressionOnRows.Message.Contains("non-constant default", StringComparison.Ordinal),
                $"预期原文 Cannot add a column with non-constant default,实际:{expressionOnRows.Message}");

            // —— ③ 默认值是**原样拼进去**的,所以调用方给的文本必须自己成立:
            //    函数调用当默认值要自己带一层括号,不带就是语法错误。
            //    这一条正好是 DescribeAsync 的反向陷阱 —— SQLite 存回来的原文是剥了外层括号的。
            var bareCall = new SqlColumn("c3", 4, "TEXT", IsNullable: true, DefaultValue: "datetime('now')");
            Assert.IsNotNull(await CaptureAsync(raw, pack.AddColumnDdl(empty, bareCall)!),
                "DEFAULT datetime('now') 是语法错误,要写成 DEFAULT (datetime('now'))。");
            await ExecAsync(raw, pack.AddColumnDdl(
                empty, new SqlColumn("c4", 5, "TEXT", IsNullable: true, DefaultValue: "(datetime('now'))"))!);

            // —— ④ 这一层**只**拦一种情况:列定义里说了、而 ADD COLUMN 的通用写法表达不了的那三样。
            //    通用写法只写得出 列名 + 类型 + NOT NULL + DEFAULT,剩下的旗它一声不吭地丢掉。
            Assert.IsNull(
                pack.AddColumnDdl(empty, new SqlColumn("g1", 6, "INTEGER", IsNullable: true, IsGenerated: true)),
                "拼不出 GENERATED ALWAYS AS (...),就不该生成一条会静默办成别的事的 DDL。");
            Assert.IsNull(
                pack.AddColumnDdl(empty, new SqlColumn("p1", 7, "INTEGER", IsNullable: true, IsPrimaryKey: true)),
                "主键那面旗同理。");
            Assert.IsNull(
                pack.AddColumnDdl(empty, new SqlColumn("a1", 8, "INTEGER", IsNullable: true, IsAutoIncrement: true)),
                "自增那面旗同理(SQLite 的自增就是 INTEGER PRIMARY KEY,加不出来)。");

            // 证明"静默办成别的事"不是假想:把通用写法会生成的那条手工发一次,建出来的是个普通列。
            await ExecAsync(raw, "alter table add_empty add column \"g1\" INTEGER");
            SqlTableSchema schema = await pack.DescribeAsync(raw, empty, TestContext.CancellationTokenSource.Token);
            SqlColumn plain = Col(schema, "g1");
            Assert.IsFalse(plain.IsGenerated, "它建出来是个普通列 —— 正是不能生成这条 DDL 的理由。");
            Assert.IsFalse(plain.IsPrimaryKey);

            // 而主键那面旗**就算表达得出来 SQLite 也照样拒绝**(空表也拒),所以 null 是双重正确的。
            Exception? primaryKey = await CaptureAsync(raw, "alter table add_empty add column \"p1\" INTEGER PRIMARY KEY");
            Assert.IsNotNull(primaryKey);
            Assert.IsTrue(
                primaryKey.Message.Contains("Cannot add a PRIMARY KEY column", StringComparison.Ordinal),
                $"预期原文 Cannot add a PRIMARY KEY column,实际:{primaryKey.Message}");
        });
    }

    /// <summary>
    /// <c>DROP COLUMN</c> 要 SQLite 3.35+(2021-03)。这里把引擎版本断出来 ——
    /// 上面那条删列用例要是在更老的引擎上跑,报的会是一句 <c>near "DROP": syntax error</c>,
    /// 看上去像"列名转义错了",能白查半天。
    /// </summary>
    [TestMethod]
    public async Task 引擎版本_够得上DROP_COLUMN那条线()
    {
        await WithSqliteAsync(async raw =>
        {
            string version = (await ScalarAsync(raw, "select sqlite_version()"))?.ToString() ?? "";
            Assert.IsTrue(Version.TryParse(version, out Version? parsed), $"版本号读不出来:{version}");
            Assert.IsTrue(parsed >= new Version(3, 35, 0),
                $"捆绑的原生库是 {version},低于 DROP COLUMN 要求的 3.35.0。");
        });
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>开一个临时 <c>.db</c> 跑一段,跑完连带文件一起收掉。</summary>
    /// <param name="body">拿到已打开连接之后要做的事。</param>
    /// <returns>任务。</returns>
    private static async Task WithSqliteAsync(Func<DbConnection, Task> body)
    {
        string file = Path.Combine(Path.GetTempPath(), $"sqlite-ops-{Guid.NewGuid():N}.db");
        try
        {
            var request = new WorkspaceConnectRequest
            {
                SessionId = "sqlite-ops",
                Host = file,
                Port = 1,
                Username = "",
                Password = "",
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            await using SqlConnection connection = await SqlConnection.ConnectAsync(
                SqlSettings.From(request, SqlDialect.Sqlite), file, 1, "", "", Localization, null);
            await body(connection.Raw);
        }
        finally
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
                // 临时文件删不掉不该让测试失败(SQLite 的句柄有时会晚一拍才还给系统)。
            }
        }
    }

    /// <summary>发一条语句;失败就让测试失败(这一组要的就是"引擎认不认")。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>任务。</returns>
    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>发一条**预期会失败**的语句,把异常带回来(拿不到异常就是断言失败的证据)。</summary>
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

    /// <summary>把一条查询的所有行读成"每行一段文本"(计划的列名各版本略有出入,按序号读最稳)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>行文本。</returns>
    private static async Task<List<string>> ReadAllAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<string> rows = [];
        while (await reader.ReadAsync())
        {
            string[] cells = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            }
            rows.Add(string.Join(" | ", cells));
        }
        return rows;
    }

    /// <summary>取第一行第一列。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>值。</returns>
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
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

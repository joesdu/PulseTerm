using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 方言包的**真机验收**:对着 MySQL 8.4 / PostgreSQL 18.1 / SQLite 建一张"什么坑都占全"的表,
/// 逐条断言那些 <c>IDbMaintenance</c> 给错的字段现在给对了。
/// <para>
/// 这一组存在的全部理由是设计文档 §2.3 那张表:自增恒 True、生成列没有这个概念、
/// 视图的列返回 0、索引丢唯一性、跨 schema 同名表串列。方言包声称把它们都修好了 ——
/// <b>没有真机断言,这个"声称"一文不值。</b>
/// </para>
/// <para>按仓库惯例按环境早退跳过:拿不到服务器时 <c>Inconclusive</c> 而不是失败。</para>
/// </summary>
[TestClass]
public sealed class DialectPackIntegrationTests
{
    private static readonly Loc Localization = new("zh-Hans");

    // ═══════════════════════════ MySQL ═══════════════════════════

    /// <summary>MySQL:列类型原文、自增、生成列、默认值表达式、索引唯一性、外键、视图的列。</summary>
    [TestMethod]
    public async Task MySQL_元数据逐项对得上真值()
    {
        await using SqlConnection? connection = await TryMySqlAsync();
        if (connection is null)
        {
            Assert.Inconclusive("没有可用的 MySQL(127.0.0.1:13306)。");
            return;
        }
        DbConnection raw = connection.Raw;
        await ExecAsync(raw, "drop view if exists v_kitchen");
        await ExecAsync(raw, "drop table if exists kitchen_child");
        await ExecAsync(raw, "drop table if exists kitchen");
        await ExecAsync(raw, """
            create table kitchen(
              id            bigint unsigned not null auto_increment,
              tenant_id     int not null,
              code          varchar(50) not null comment '业务编码',
              amount        decimal(12,3) null default 0.000,
              status        enum('new','paid') not null default 'new',
              created_at    datetime(3) not null default current_timestamp(3),
              note          text null,
              flag          tinyint(1) not null default 0,
              total_cents   int generated always as (amount * 1000) stored,
              code_upper    varchar(50) generated always as (upper(code)) virtual,
              primary key (id, tenant_id),
              unique key ux_code (code),
              key ix_prefix (note(10)),
              key ix_comp (tenant_id, status)
            ) comment='厨房水槽表'
            """);
        await ExecAsync(raw, """
            create table kitchen_child(
              child_id int not null primary key,
              id       bigint unsigned not null,
              tenant_id int not null,
              constraint fk_child foreign key (id, tenant_id) references kitchen(id, tenant_id) on delete cascade
            )
            """);
        await ExecAsync(raw, "create view v_kitchen as select id, code, amount from kitchen");

        var pack = new MySqlPack();
        SqlTableSchema schema = await pack.DescribeAsync(
            raw, new(SqlObjectKind.Table, "kitchen"), TestContext.CancellationTokenSource.Token);

        // —— 列类型必须是**完整原生形态**。DbMaintenance 的 Length 是从类型名里 SUBSTRING 出来的,
        //    LOB/JSON/enum 恒为 0、datetime(3) 的 3 被当成长度、tinyint(1) 的 1 是显示宽度。
        Assert.AreEqual("varchar(50)", Col(schema, "code").DataType);
        Assert.AreEqual("decimal(12,3)", Col(schema, "amount").DataType);
        Assert.AreEqual("enum('new','paid')", Col(schema, "status").DataType, "enum 的取值列表不能丢。");
        Assert.AreEqual("datetime(3)", Col(schema, "created_at").DataType);
        Assert.AreEqual("bigint unsigned", Col(schema, "id").DataType, "unsigned 要保留。");

        // —— 自增:IsIdentity 在真机上恒 True,这里必须只认真的那一个。
        Assert.IsTrue(Col(schema, "id").IsAutoIncrement);
        Assert.IsFalse(Col(schema, "tenant_id").IsAutoIncrement, "IsIdentity 恒 True 那个坑不能重演。");
        Assert.IsFalse(Col(schema, "code").IsAutoIncrement);

        // —— 生成列:DbColumnInfo 里根本没有这个概念,而带上它写库会直接报错。
        Assert.IsTrue(Col(schema, "total_cents").IsGenerated, "STORED 生成列要认出来。");
        Assert.IsTrue(Col(schema, "code_upper").IsGenerated, "VIRTUAL 生成列也要认出来。");
        Assert.IsFalse(Col(schema, "amount").IsGenerated);
        Assert.IsFalse(schema.WritableColumns.Any(c => c.Name is "total_cents" or "code_upper"),
            "生成列必须被排除在可写列之外 —— 带上它字典 CRUD 会报错。");

        // —— 默认值:表达式与字符串字面量必须分得开。
        Assert.IsTrue(Col(schema, "created_at").IsDefaultExpression, "current_timestamp(3) 是表达式。");
        Assert.IsFalse(Col(schema, "status").IsDefaultExpression, "'new' 是字面量。");

        // —— 复合主键与顺序。
        CollectionAssert.AreEqual(new[] { "id", "tenant_id" }, schema.PrimaryKey.ToArray());

        // —— 索引:唯一性必须带上(GetIndexList 只给名字,而且不去重)。
        SqlIndex unique = Idx(schema, "ux_code");
        Assert.IsTrue(unique.IsUnique);
        CollectionAssert.AreEqual(new[] { "code" }, unique.Columns.ToArray());
        SqlIndex composite = Idx(schema, "ix_comp");
        CollectionAssert.AreEqual(new[] { "tenant_id", "status" }, composite.Columns.ToArray(), "复合索引的列序不能乱。");
        Assert.IsFalse(composite.IsUnique);
        Assert.IsFalse(schema.Indexes.Any(i => i.Name == "ix_comp" && i.Columns.Count == 1),
            "复合索引不能被拆成多条(GetIndexList 就是这么把 7 个索引报成 11 项的)。");

        // —— 外键:IDbMaintenance 里一个都没有。
        SqlForeignKey fk = await FirstForeignKeyAsync(pack, raw, "kitchen_child");
        Assert.AreEqual("kitchen", fk.ReferencedTable);
        CollectionAssert.AreEqual(new[] { "id", "tenant_id" }, fk.Columns.ToArray());

        // —— 视图的列:DbMaintenance 对视图返回 0 列且不抛异常。
        SqlTableSchema view = await pack.DescribeAsync(
            raw, new(SqlObjectKind.View, "v_kitchen"), TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(3, view.Columns.Count, "视图的列必须拿得到。");
    }

    // ═══════════════════════════ PostgreSQL ═══════════════════════════

    /// <summary>PG:跨 schema 同名表不串、物化视图列得出来、两种自增都认得出、内部触发器不混进来。</summary>
    [TestMethod]
    public async Task PostgreSQL_元数据逐项对得上真值()
    {
        await using SqlConnection? connection = await TryPostgresAsync();
        if (connection is null)
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }
        DbConnection raw = connection.Raw;
        await ExecAsync(raw, "drop schema if exists packapp cascade");
        await ExecAsync(raw, "drop materialized view if exists mv_kitchen");
        await ExecAsync(raw, "drop view if exists v_kitchen");
        await ExecAsync(raw, "drop table if exists kitchen_child");
        await ExecAsync(raw, "drop table if exists kitchen");
        await ExecAsync(raw, """
            create table kitchen(
              seq_id     int generated always as identity,
              legacy_id  serial,
              tenant_id  int not null,
              code       varchar(50) not null,
              amount     numeric(12,3),
              status     varchar(20) default 'new',
              created_at timestamptz not null default now(),
              total      numeric generated always as (amount * 2) stored,
              primary key (seq_id, tenant_id)
            )
            """);
        await ExecAsync(raw, "comment on column kitchen.code is '业务编码'");
        await ExecAsync(raw, "create unique index ux_kitchen_code on kitchen(code)");
        await ExecAsync(raw, "create index ix_kitchen_lower on kitchen(lower(code))");
        await ExecAsync(raw, "create index ix_kitchen_part on kitchen(status) where status = 'new'");
        await ExecAsync(raw, """
            create table kitchen_child(
              child_id int primary key,
              seq_id int not null, tenant_id int not null,
              constraint fk_child foreign key (seq_id, tenant_id) references kitchen(seq_id, tenant_id)
            )
            """);
        await ExecAsync(raw, "create view v_kitchen as select seq_id, code from kitchen");
        await ExecAsync(raw, "create materialized view mv_kitchen as select seq_id, amount from kitchen");
        // 同名表放到另一个 schema 且列完全不同 —— 这正是 SqlSugar 会串的地方。
        await ExecAsync(raw, "create schema packapp");
        await ExecAsync(raw, "create table packapp.kitchen(only_here text, another_one int)");

        var pack = new PostgreSqlPack();
        var token = TestContext.CancellationTokenSource.Token;

        SqlTableSchema pub = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "kitchen", "public"), token);
        SqlTableSchema app = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "kitchen", "packapp"), token);

        // —— **跨 schema 同名表不能串**。
        Assert.IsTrue(pub.Columns.Count >= 8);
        Assert.AreEqual(2, app.Columns.Count, "packapp.kitchen 只有 2 列,不该被 public 的列污染。");
        Assert.IsTrue(app.Columns.Any(c => c.Name == "only_here"));
        Assert.IsFalse(pub.Columns.Any(c => c.Name == "only_here"), "反向也不能串。");

        // —— 两种自增写法都要认得出。
        Assert.IsTrue(Col(pub, "seq_id").IsAutoIncrement, "GENERATED ALWAYS AS IDENTITY。");
        Assert.IsTrue(Col(pub, "legacy_id").IsAutoIncrement, "老式 serial 也是自增。");
        Assert.IsFalse(Col(pub, "tenant_id").IsAutoIncrement);

        // —— 生成列。
        Assert.IsTrue(Col(pub, "total").IsGenerated);
        Assert.IsFalse(pub.WritableColumns.Any(c => c.Name == "total"));

        // —— 类型原文与注释。
        StringAssert.Contains(Col(pub, "amount").DataType, "numeric");
        StringAssert.Contains(Col(pub, "code").DataType, "50");
        Assert.AreEqual("业务编码", Col(pub, "code").Comment);

        // —— 默认值:now() 是表达式,'new' 是字面量。
        Assert.IsTrue(Col(pub, "created_at").IsDefaultExpression);
        Assert.IsFalse(Col(pub, "status").IsDefaultExpression);

        // —— 索引:唯一/表达式/部分三种都要在,且唯一性对。
        Assert.IsTrue(Idx(pub, "ux_kitchen_code").IsUnique);
        Assert.IsTrue(pub.Indexes.Any(i => i.Name == "ix_kitchen_lower"), "表达式索引要列出来。");
        Assert.IsTrue(pub.Indexes.Any(i => i.Name == "ix_kitchen_part"), "部分索引要列出来。");

        // —— 视图与物化视图的列都要拿得到;物化视图还要出现在对象清单里。
        SqlTableSchema view = await pack.DescribeAsync(raw, new(SqlObjectKind.View, "v_kitchen", "public"), token);
        Assert.AreEqual(2, view.Columns.Count, "视图的列必须拿得到。");
        SqlTableSchema matview = await pack.DescribeAsync(
            raw, new(SqlObjectKind.MaterializedView, "mv_kitchen", "public"), token);
        Assert.AreEqual(2, matview.Columns.Count, "物化视图的列也要拿得到。");

        IReadOnlyList<SqlObject> relations = await pack.ListRelationsAsync(raw, "public", token);
        Assert.IsTrue(relations.Any(r => r.Name == "mv_kitchen" && r.Kind == SqlObjectKind.MaterializedView),
            "物化视图在 DbMaintenance 里根本不存在 —— 方言包必须把它列出来。");
        Assert.IsTrue(relations.Any(r => r.Name == "v_kitchen" && r.Kind == SqlObjectKind.View));

        // —— 外键。
        SqlTableSchema child = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "kitchen_child", "public"), token);
        Assert.AreEqual(1, child.ForeignKeys.Count);
        Assert.AreEqual("kitchen", child.ForeignKeys[0].ReferencedTable);
    }

    // ═══════════════════════════ SQLite ═══════════════════════════

    /// <summary>SQLite:索引名是**真名**而不是 <c>"0"</c>、唯一性对、自增与生成列认得出、复合主键顺序对。</summary>
    [TestMethod]
    public async Task SQLite_元数据逐项对得上真值()
    {
        string file = Path.Combine(Path.GetTempPath(), $"packlite-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlConnection connection = await OpenAsync(SqlDialect.Sqlite, file, 1, "", "", []);
            DbConnection raw = connection.Raw;
            await ExecAsync(raw, """
                create table kitchen(
                  id integer primary key autoincrement,
                  code varchar(50) not null,
                  amount numeric(12,3),
                  status text default 'new',
                  created_at text default current_timestamp,
                  total real generated always as (amount * 2) stored,
                  code_upper text generated always as (upper(code)) virtual
                )
                """);
            await ExecAsync(raw, "create unique index ux_kitchen_code on kitchen(code)");
            await ExecAsync(raw, "create index ix_kitchen_part on kitchen(status) where status = 'new'");
            await ExecAsync(raw, """
                create table kitchen_child(
                  child_id integer primary key,
                  id integer not null references kitchen(id)
                )
                """);
            await ExecAsync(raw, "create table compo(a int not null, b int not null, primary key(a, b))");
            await ExecAsync(raw, "create view v_kitchen as select id, code from kitchen");

            var pack = new SqlitePack();
            var token = TestContext.CancellationTokenSource.Token;
            SqlTableSchema schema = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "kitchen"), token);

            // —— **索引名必须是真名**。DbMaintenance 在 SQLite 上返回的是 "0"(疑似读了 PRAGMA 的 seq 列),
            //    这是整份调研里"元数据静默说谎"最早的那个例子。
            Assert.IsTrue(schema.Indexes.Any(i => i.Name == "ux_kitchen_code"),
                "索引名必须是 ux_kitchen_code,而不是 \"0\"。");
            Assert.IsFalse(schema.Indexes.Any(i => i.Name is "0" or "1" or "2"),
                "纯数字索引名就是读错了列的信号。");
            Assert.IsTrue(Idx(schema, "ux_kitchen_code").IsUnique);
            CollectionAssert.AreEqual(new[] { "code" }, Idx(schema, "ux_kitchen_code").Columns.ToArray());

            // —— 自增:只有 INTEGER PRIMARY KEY AUTOINCREMENT 才是真自增。
            Assert.IsTrue(Col(schema, "id").IsAutoIncrement);
            Assert.IsFalse(Col(schema, "code").IsAutoIncrement);

            // —— 生成列(PRAGMA table_xinfo 的 hidden 列才看得见)。
            Assert.IsTrue(Col(schema, "total").IsGenerated, "STORED 生成列。");
            Assert.IsTrue(Col(schema, "code_upper").IsGenerated, "VIRTUAL 生成列。");
            Assert.IsFalse(schema.WritableColumns.Any(c => c.Name is "total" or "code_upper"));

            // —— 类型原文(DbMaintenance 的长度恒 0)。
            Assert.AreEqual("varchar(50)", Col(schema, "code").DataType, StringComparer.OrdinalIgnoreCase.Equals(
                Col(schema, "code").DataType, "varchar(50)") ? "" : "声明类型要原样保留。");

            // —— 复合主键顺序(PRAGMA 的 pk 值是主键内序号,不是布尔)。
            SqlTableSchema composite = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "compo"), token);
            CollectionAssert.AreEqual(new[] { "a", "b" }, composite.PrimaryKey.ToArray());

            // —— 外键。
            SqlTableSchema child = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, "kitchen_child"), token);
            Assert.AreEqual(1, child.ForeignKeys.Count);
            Assert.AreEqual("kitchen", child.ForeignKeys[0].ReferencedTable);

            // —— 视图的列。
            SqlTableSchema view = await pack.DescribeAsync(raw, new(SqlObjectKind.View, "v_kitchen"), token);
            Assert.AreEqual(2, view.Columns.Count);
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>标识符转义:标识符里含定界符时必须加倍,否则就是可执行的注入(§5.4.4 实测能删表)。</summary>
    [TestMethod]
    public void 标识符转义_定界符加倍()
    {
        Assert.AreEqual("`a``b`", new MySqlPack().QuoteIdentifier("a`b"));
        Assert.AreEqual("\"a\"\"b\"", new PostgreSqlPack().QuoteIdentifier("a\"b"));
        Assert.AreEqual("\"a\"\"b\"", new SqlitePack().QuoteIdentifier("a\"b"));

        // 这个 payload 在 SqlSugar 的 AS(表名) 那条路上真删过表。
        // 判据不是"结果里不含某个子串"(转义后的 ``; 本来就含 `;),
        // 而是**内部每个定界符都成对** —— 于是第一个落单的反引号只可能是结尾那个,
        // payload 没法提前收尾把后面的语句放出来。
        string quoted = new MySqlPack().QuoteIdentifier("orders`; drop table victim--");
        Assert.IsTrue(quoted.StartsWith('`') && quoted.EndsWith('`'));
        string inner = quoted[1..^1];
        Assert.AreEqual(0, inner.Count(c => c == '`') % 2, "内部的定界符必须全部成对(加倍转义)。");
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    private static SqlColumn Col(SqlTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertFailedException($"结果里没有列 {name};实际有:{string.Join(", ", schema.Columns.Select(c => c.Name))}");

    private static SqlIndex Idx(SqlTableSchema schema, string name) =>
        schema.Indexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertFailedException($"结果里没有索引 {name};实际有:{string.Join(", ", schema.Indexes.Select(i => i.Name))}");

    private static async Task<SqlForeignKey> FirstForeignKeyAsync(IDialectPack pack, DbConnection raw, string table)
    {
        SqlTableSchema schema = await pack.DescribeAsync(raw, new(SqlObjectKind.Table, table), CancellationToken.None);
        return schema.ForeignKeys.Count > 0
            ? schema.ForeignKeys[0]
            : throw new AssertFailedException($"{table} 上一条外键都没查到 —— IDbMaintenance 也是这样,方言包本该修好它。");
    }

    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (DbException)
        {
            // drop if exists 这类清理语句在对象本来就不存在时会抛,忽略即可。
        }
    }

    private static Task<SqlConnection?> TryMySqlAsync() =>
        TryOpenAsync(SqlDialect.MySql, "127.0.0.1", 13306, "root", "velaspike",
            new() { ["database"] = "pack_verify" });

    private static Task<SqlConnection?> TryPostgresAsync() =>
        TryOpenAsync(SqlDialect.PostgreSql, "127.0.0.1", 55432, "postgres", "velaspike",
            new() { ["database"] = "pack_verify" });

    private static async Task<SqlConnection> OpenAsync(
        SqlDialect dialect, string host, int port, string user, string password, Dictionary<string, string> settings)
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "pack-it",
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            Settings = settings
        };
        return await SqlConnection.ConnectAsync(
            SqlSettings.From(request, dialect), host, port, user, password, Localization, null);
    }

    private static async Task<SqlConnection?> TryOpenAsync(
        SqlDialect dialect, string host, int port, string user, string password, Dictionary<string, string> settings)
    {
        try
        {
            return await OpenAsync(dialect, host, port, user, password, settings);
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

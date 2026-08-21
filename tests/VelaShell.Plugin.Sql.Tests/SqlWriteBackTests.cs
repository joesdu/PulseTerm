using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 结果网格的写回。
/// <para>
/// 这是整个插件里**唯一会改用户数据**的一条路径,所以它的每条护栏都要有测试:
/// 定位不到一行就只读、生成列不能带、NULL 要用 <c>IS NULL</c> 而不是 <c>= NULL</c>、
/// 原值进 WHERE 做乐观并发。任何一条漏掉的后果都是"改错了行"或"悄悄盖掉别人的改动"。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlWriteBackTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>有主键 → 可编辑,定位列就是主键。</summary>
    [TestMethod]
    public void 可编辑性_有主键时用主键定位()
    {
        SqlTableSchema schema = Schema(
            [Col("id", pk: true), Col("name")],
            indexes: []);

        SqlEditability verdict = SqlWriteBack.Judge(schema, ["id", "name"]);

        Assert.IsTrue(verdict.Editable);
        CollectionAssert.AreEqual(new[] { "id" }, verdict.KeyColumns.ToArray());
    }

    /// <summary>没有主键但有唯一索引 → 退而求其次用唯一索引。</summary>
    [TestMethod]
    public void 可编辑性_无主键时退到唯一索引()
    {
        SqlTableSchema schema = Schema(
            [Col("code"), Col("name")],
            indexes: [new("ux_code", ["code"], IsUnique: true)]);

        SqlEditability verdict = SqlWriteBack.Judge(schema, ["code", "name"]);

        Assert.IsTrue(verdict.Editable);
        CollectionAssert.AreEqual(new[] { "code" }, verdict.KeyColumns.ToArray());
    }

    /// <summary>
    /// 主键与唯一索引都没有 → **只读**。
    /// 绝不退化成"按全列匹配":那在有重复行的表上会一次改掉多行,而用户以为自己只改了一格。
    /// </summary>
    [TestMethod]
    public void 可编辑性_没有任何唯一定位时只读()
    {
        SqlTableSchema schema = Schema([Col("a"), Col("b")], indexes: []);

        SqlEditability verdict = SqlWriteBack.Judge(schema, ["a", "b"]);

        Assert.IsFalse(verdict.Editable);
        Assert.AreEqual("Sql_GridReadOnlyNoKey", verdict.ReasonKey);
    }

    /// <summary>自由查询(不知道是哪张表)→ 只读。</summary>
    [TestMethod]
    public void 可编辑性_不是单表结果时只读()
    {
        SqlEditability verdict = SqlWriteBack.Judge(null, ["a"]);

        Assert.IsFalse(verdict.Editable);
        Assert.AreEqual("Sql_GridReadOnlyNotATable", verdict.ReasonKey);
    }

    /// <summary>定位列没被 SELECT 出来 → 只读(拼出来的 WHERE 会是残缺的)。</summary>
    [TestMethod]
    public void 可编辑性_定位列不在结果集里时只读()
    {
        SqlTableSchema schema = Schema([Col("id", pk: true), Col("name")], indexes: []);

        SqlEditability verdict = SqlWriteBack.Judge(schema, ["name"]);

        Assert.IsFalse(verdict.Editable);
        Assert.AreEqual("Sql_GridReadOnlyKeyNotSelected", verdict.ReasonKey);
    }

    /// <summary>
    /// 生成的 UPDATE:SET 用参数,WHERE 里既有主键**也有被改列的原值**(乐观并发)。
    /// </summary>
    [TestMethod]
    public void 生成UPDATE_带主键与原值()
    {
        SqlTableSchema schema = Schema([Col("id", pk: true), Col("name")], indexes: []);
        var pack = new SqlitePack();

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            pack,
            new(SqlObjectKind.Table, "orders"),
            schema,
            ["id"],
            [new(0, "name", "旧名", "新名")],
            (_, column) => column == "id" ? "7" : null);

        Assert.AreEqual(1, writes.Count);
        StringAssert.Contains(writes[0].Sql, "UPDATE \"orders\" SET \"name\" = @p0");
        StringAssert.Contains(writes[0].Sql, "\"id\" = @p1");
        StringAssert.Contains(writes[0].Sql, "\"name\" = @p2");
        CollectionAssert.AreEqual(new object?[] { "新名", "7", "旧名" }, writes[0].Parameters.ToArray());
    }

    /// <summary>
    /// 原值是 NULL 时 WHERE 必须用 <c>IS NULL</c>。
    /// 写成 <c>= @p</c> 的话那一行**永远匹配不上** —— 用户改一格 NULL 会静默地什么都没发生。
    /// </summary>
    [TestMethod]
    public void 生成UPDATE_原值为NULL时用IS_NULL()
    {
        SqlTableSchema schema = Schema([Col("id", pk: true), Col("memo")], indexes: []);

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            new SqlitePack(),
            new(SqlObjectKind.Table, "t"),
            schema,
            ["id"],
            [new(0, "memo", null, "有值了")],
            (_, column) => column == "id" ? "1" : null);

        StringAssert.Contains(writes[0].Sql, "\"memo\" IS NULL");
        Assert.IsFalse(writes[0].Sql.Contains("\"memo\" = @p1", StringComparison.Ordinal),
            "原值是 NULL 时不能用 = 比较,那永远不成立。");
    }

    /// <summary>
    /// 生成列不能出现在 SET 里 —— 带上它 MySQL 直接报
    /// <c>The value specified for generated column ... is not allowed</c>(实测)。
    /// </summary>
    [TestMethod]
    public void 生成UPDATE_剔除生成列()
    {
        SqlTableSchema schema = Schema(
            [Col("id", pk: true), Col("total", generated: true), Col("name")],
            indexes: []);

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            new SqlitePack(),
            new(SqlObjectKind.Table, "t"),
            schema,
            ["id"],
            [new(0, "total", "1", "2"), new(0, "name", "a", "b")],
            (_, column) => column == "id" ? "1" : null);

        Assert.AreEqual(1, writes.Count);
        Assert.IsFalse(writes[0].Sql.Contains("\"total\"", StringComparison.Ordinal), "生成列不该进 SET。");
        StringAssert.Contains(writes[0].Sql, "\"name\"");
    }

    /// <summary>只改了生成列 → 一条语句都不该发(而不是发一条空 SET)。</summary>
    [TestMethod]
    public void 生成UPDATE_只改生成列时不发语句()
    {
        SqlTableSchema schema = Schema([Col("id", pk: true), Col("total", generated: true)], indexes: []);

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            new SqlitePack(), new(SqlObjectKind.Table, "t"), schema, ["id"],
            [new(0, "total", "1", "2")], (_, _) => "1");

        Assert.AreEqual(0, writes.Count);
    }

    /// <summary>表名与列名必须走定界符转义 —— 这条路径上一次没转义就删掉了整张表(§5.4.4)。</summary>
    [TestMethod]
    public void 生成UPDATE_标识符全部转义()
    {
        SqlTableSchema schema = Schema([Col("id", pk: true), Col("na\"me")], indexes: []);

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            new SqlitePack(),
            new(SqlObjectKind.Table, "or\"ders"),
            schema, ["id"], [new(0, "na\"me", "a", "b")], (_, _) => "1");

        StringAssert.Contains(writes[0].Sql, "\"or\"\"ders\"");
        StringAssert.Contains(writes[0].Sql, "\"na\"\"me\"");
    }

    /// <summary>
    /// <b>真机端到端</b>:改一格 → 提交 → 值真的落库了;
    /// 而且**别人先改过的话影响行数是 0**(乐观并发把它拦下来,不是悄悄盖上去)。
    /// </summary>
    [TestMethod]
    public async Task 端到端_改一格并被乐观并发拦下()
    {
        string file = Path.Combine(Path.GetTempPath(), $"wb-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await SqlSession.OpenAsync(
                new WorkspaceConnectRequest { SessionId = "wb", Host = file, Port = 1 },
                SqlDialect.Sqlite, Localization);
            DbConnection raw = session.Metadata.Raw;
            await ExecAsync(raw, "create table t(id integer primary key, name text)");
            await ExecAsync(raw, "insert into t(id, name) values(1, '旧名')");

            SqlTableSchema schema = await session.Pack.DescribeAsync(
                raw, new(SqlObjectKind.Table, "t"), TestContext.CancellationTokenSource.Token);

            // 正常改一格。
            IReadOnlyList<SqlWriteStatement> ok = SqlWriteBack.BuildUpdates(
                session.Pack, new(SqlObjectKind.Table, "t"), schema, ["id"],
                [new(0, "name", "旧名", "新名")], (_, c) => c == "id" ? "1" : null);
            IReadOnlyList<int> affected = await SqlWriteBack.ApplyAsync(raw, ok, 30);

            Assert.AreEqual(1, affected[0]);
            Assert.AreEqual("新名", await ScalarAsync(raw, "select name from t where id = 1"));

            // 拿一份**过期的原值**再提交一次 —— 这正是"别人在你打开网格之后改过它"的情形。
            IReadOnlyList<SqlWriteStatement> stale = SqlWriteBack.BuildUpdates(
                session.Pack, new(SqlObjectKind.Table, "t"), schema, ["id"],
                [new(0, "name", "旧名", "更新的名")], (_, c) => c == "id" ? "1" : null);
            IReadOnlyList<int> staleAffected = await SqlWriteBack.ApplyAsync(raw, stale, 30);

            Assert.AreEqual(0, staleAffected[0], "原值对不上就该一行都不改 —— 悄悄盖上去才是最坏的。");
            Assert.AreEqual("新名", await ScalarAsync(raw, "select name from t where id = 1"),
                "被拦下之后库里必须还是别人改成的那个值。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static SqlColumn Col(string name, bool pk = false, bool generated = false) =>
        new(name, 1, "text", IsNullable: true, IsPrimaryKey: pk, IsGenerated: generated);

    private static SqlTableSchema Schema(IReadOnlyList<SqlColumn> columns, IReadOnlyList<SqlIndex> indexes) =>
        new(new(SqlObjectKind.Table, "t"), columns, indexes, []);

    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
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

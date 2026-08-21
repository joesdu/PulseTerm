using SqlSugar;
using VelaShell.Plugin.Sql;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// **纪律回归**。这一组每一条都对着调研阶段实测出的一个坑
/// (docs/SqlSugar数据库管理插件调研与设计.md §十 的必做回归项),
/// 它们的共同点是:违反之后的表现都是**静默的错**——不是异常,是错答案或者整个插件不可用。
/// 没有测试兜着,将来某次重构漏掉一条,没人会发现。
/// </summary>
[TestClass]
public sealed class SqlDisciplineTests
{
    /// <summary>
    /// §3.3:碰一次未内置的方言会**永久污染** <c>InstanceFactory</c> 的静态状态 ——
    /// 之后**任何**方言都取不到 provider,报的还是上一次那个包名。
    /// 用户故事:在连接对话框里手滑选了一次 ClickHouse,之后连 SQLite 也打不开了,重启插件才恢复。
    /// <para>本用例先制造污染,再断言 <see cref="SqlSugarGate" /> 的复位真的把它治好了。</para>
    /// </summary>
    [TestMethod]
    public void 碰过未内置方言之后_内置方言仍然可用()
    {
        // 制造污染:直接用 SqlSugar 建一个未内置方言的 client 并取 DbMaintenance。
        try
        {
            using var poisoned = new SqlSugarClient(new ConnectionConfig
            {
                DbType = DbType.ClickHouse,
                ConnectionString = "Host=127.0.0.1"
            });
            _ = poisoned.DbMaintenance;
        }
        catch (Exception)
        {
            // 预期就是抛 —— 我们要的是它留下的那份静态污染。
        }

        // 不复位的话,这一步会因为上面那次失败而抛"Not Found SqlSugar.ClickHouseCore.dll"。
        using SqlSugarClient client = SqlSugarGate.Create(
            SqlDialect.Sqlite, "Data Source=:memory:", commandTimeoutSeconds: 5);
        object maintenance = client.DbMaintenance;

        Assert.IsNotNull(maintenance, "复位 InstanceFactory.CustomDllName 之后,内置方言必须照常装配。");
    }

    /// <summary>
    /// §5.4.5:管理工具**绝不能**让 ORM 改写用户的标识符大小写。
    /// 默认配置下 PG 会把 <c>"OrderDetail"</c> 压成 <c>"orderdetail"</c>,服务端以 42P01 打回,
    /// 而元数据通道照常读得到 —— 表现是"对象树里有这张表,一点开就报表不存在"。
    /// </summary>
    [TestMethod]
    public void 建出来的客户端_不会改写标识符大小写()
    {
        using SqlSugarClient client = SqlSugarGate.Create(
            SqlDialect.PostgreSql,
            "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d",
            commandTimeoutSeconds: 5);

        ConnMoreSettings settings = client.CurrentConnectionConfig.MoreSettings!;
        Assert.IsFalse(settings.PgSqlIsAutoToLower, "PG 系:不得把标识符压小写。");
        Assert.IsFalse(settings.PgSqlIsAutoToLowerCodeFirst);
        Assert.IsFalse(settings.PgSqlIsAutoToLowerSchema);
        Assert.IsFalse(settings.IsAutoToUpper, "Oracle 系:不得把标识符抬大写。");
    }

    /// <summary>
    /// §5.2:连接不能自动关。三个后果:取消拿不到 DbCommand、会话级 SET 静默丢失、
    /// 异常翻译拿不到原始驱动异常。
    /// </summary>
    [TestMethod]
    public void 建出来的客户端_不自动关连接()
    {
        using SqlSugarClient client = SqlSugarGate.Create(
            SqlDialect.MySql, "Server=127.0.0.1;Port=1", commandTimeoutSeconds: 7);

        Assert.IsFalse(client.CurrentConnectionConfig.IsAutoCloseConnection);
        // §5.1:CommandTimeout 只能设在这里 —— 放连接串里会被 SqlSugar 的默认 300 秒覆盖。
        Assert.AreEqual(7, client.Ado.CommandTimeOut);
    }

    /// <summary>
    /// §6.2:<c>new SqlSugarClient(cfg)</c> 会**就地改写 <c>cfg.DbType</c>**
    /// (传 Tidb 进去建完就变成 MySql,而且改的是传入对象本身)。
    /// 所以插件不能靠它记住用户选了什么 —— 这一条用测试钉住,免得将来有人"简化"掉
    /// <see cref="SqlDialect" /> 这个看似冗余的枚举。
    /// </summary>
    [TestMethod]
    public void 用户可见方言_不能从SqlSugar的配置里读回来()
    {
        var config = new ConnectionConfig
        {
            DbType = DbType.Tidb,
            ConnectionString = "Server=127.0.0.1;Port=1"
        };
        using var client = new SqlSugarClient(config);

        Assert.AreEqual(DbType.MySql, config.DbType,
            "SqlSugar 就地改写了传入的 DbType —— 这正是插件必须自带 SqlDialect 的理由。"
            + "如果哪天这条不成立了,说明上游改了行为,可以重新评估,但不要因此删掉 SqlDialect。");
    }

    /// <summary>
    /// §3.8:插件在第一个 PostgreSQL 客户端之前**绝不能碰 Npgsql** ——
    /// 否则 SqlSugar 的时间语义开关会迟到,PG 的写入路径(含结果网格改一格)当场炸。
    /// <para>
    /// 这一条只能测"自检函数认不认得出违规",因为一旦真的先碰了 Npgsql,
    /// 本进程内就再也回不去了(开关只在静态初始化那一刻读一次)。
    /// </para>
    /// </summary>
    [TestMethod]
    public void PG装配顺序自检_能认出违规()
    {
        SqlSugarGate.ResetPostgresGateForTests();

        // 主动把 Npgsql 装载进来(模拟"插件里有人先用了它")。
        _ = System.Reflection.Assembly.Load("Npgsql");

        string? violation = SqlSugarGate.CheckPostgresFirstTouch();

        Assert.IsNotNull(violation, "Npgsql 已在第一个 PG 客户端之前装载,自检必须报出来。");
        StringAssert.Contains(violation, "Npgsql");
    }
}

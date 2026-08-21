using System.Reflection;
using SqlSugar;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// **本插件里唯一允许 <c>new SqlSugarClient</c> 的地方。**
/// <para>
/// 它存在的理由不是封装,是纪律:设计文档 §3.3 / §3.8 / §5.2 / §5.4.5 里那几条
/// "不这么做就会出事"的实测结论,全部落在这一个类里。散在各处写迟早漏一处,
/// 而漏掉任何一条的表现都是**静默的错**——不是异常,是错答案或者整个插件不可用。
/// </para>
/// </summary>
internal static class SqlSugarGate
{
    /// <summary>
    /// 本进程是否已经建过 PostgreSQL 的 client。用于 §3.8 的顺序纪律自检。
    /// </summary>
    private static int _postgresClientCreated;

    /// <summary>
    /// <b>纪律一(§3.3):复位 <c>InstanceFactory</c> 的静态状态。</b>
    /// <para>
    /// <c>SqlSugar.InstanceFactory</c> 上有两个 static 且**只写不清**的成员
    /// (<c>CustomDllName</c> / <c>CustomDlls</c>)。碰一次未内置的方言(用户在下拉里手滑选了
    /// ClickHouse)之后,<b>之后任何方言</b>都取不到 provider,报的还是上一次那个包名 ——
    /// 用户改选 SQLite 打开一个 .db 文件也会失败,重启插件才恢复。
    /// </para>
    /// <para>实测:只复位 <c>CustomDllName</c> 就够;它是 public static 的可写属性。</para>
    /// </summary>
    private static void ResetInstanceFactory()
    {
        try
        {
            InstanceFactory.CustomDllName = "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 上游哪天把这个属性改成只读,我们不能因此连不上数据库 ——
            // 复位失败只影响"碰过未内置方言之后能否自愈",不影响本次连接。
        }
    }

    /// <summary>
    /// <b>纪律二(§3.8):PostgreSQL 的客户端装配顺序。</b>
    /// <para>
    /// SqlSugar 在 <c>new SqlSugarClient(PostgreSQL)</c> 的构造里会打开两个**进程级** AppContext 开关
    /// (<c>Npgsql.EnableLegacyTimestampBehavior</c> / <c>Npgsql.DisableDateTimeInfinityConversions</c>),
    /// 把 Npgsql 6+ 的时间语义摁回 5.x。而这两个开关**只在 Npgsql 静态初始化那一刻被读一次**。
    /// </para>
    /// <para>
    /// 于是有一条顺序竞争:只要插件里有人比 SqlSugar **先用一次 Npgsql**
    /// (哪怕只是 "先 new 个 NpgsqlConnection 试试连通性" 这种看着无害的写法),
    /// SqlSugar 的开关就迟到了,**写入路径当场炸 4 项** —— 包括字典 CRUD 写回,
    /// 也就是结果网格"改一格"要走的那条路:
    /// <c>Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'</c>。
    /// </para>
    /// <para>
    /// 所以插件的硬规则是:<b>永不直接引用 Npgsql 类型</b>。这里做一次运行期自检把违规抓出来 ——
    /// 编译期拦不住(传递依赖是可见的),而违规的后果只在运行期、且只在写入路径上暴露。
    /// </para>
    /// </summary>
    /// <returns>违规时返回一句诊断;合规返回 <see langword="null" />。</returns>
    internal static string? CheckPostgresFirstTouch()
    {
        if (Volatile.Read(ref _postgresClientCreated) != 0)
        {
            // 已经建过 PG client,开关早就定了,后面怎么用 Npgsql 都无所谓。
            return null;
        }
        Assembly? npgsql = Array.Find(
            AppDomain.CurrentDomain.GetAssemblies(),
            a => string.Equals(a.GetName().Name, "Npgsql", StringComparison.OrdinalIgnoreCase));
        return npgsql is null
            ? null
            : "Npgsql 已在第一个 PostgreSQL 客户端之前被装载 —— "
              + "SqlSugar 的时间语义开关会迟到,PG 的写入路径将失败(见设计文档 §3.8)。";
    }

    /// <summary>
    /// 造一个遵守全部纪律的 <see cref="SqlSugarClient" />。
    /// <para><b>注意它不会打开连接</b> —— 连接的建立必须由调用方自己 <c>Open()</c>,理由见 §5.3:
    /// SqlSugar 在"打开连接"这一步把驱动异常整个吞掉(抛 <c>SqlSugarException</c> 且
    /// <c>InnerException</c> 为 null),认证失败的错误码只剩在中文文案里,MSSQL 侧连 18456 都没了。
    /// 插件自己 Open 才拿得到 28P01 / 18456 / 1045 原样。</para>
    /// </summary>
    /// <param name="dialect">用户可见方言。</param>
    /// <param name="connectionString">已装配好的连接串。</param>
    /// <param name="commandTimeoutSeconds">语句超时(秒)。</param>
    /// <param name="onSql">AOP 回显钩子:拿到的是**参数化 SQL + 参数表**,不是拼好值的最终 SQL。</param>
    /// <returns>未打开连接的客户端。</returns>
    public static SqlSugarClient Create(
        SqlDialect dialect,
        string connectionString,
        int commandTimeoutSeconds,
        Action<string, SugarParameter[]>? onSql = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // 纪律一:每次建 client 前复位,成本是一次静态赋值。
        ResetInstanceFactory();

        SqlDialectInfo info = SqlDialects.Of(dialect);
        var config = new ConnectionConfig
        {
            DbType = info.SugarType,
            ConnectionString = connectionString,

            // 纪律三(§5.2):**不要自动关连接**。三个后果:
            //  ① 取消要拿到那个 DbCommand,自动关就没有对象可持有;
            //  ② 会话级 SET(statement_timeout / search_path / 时区)会静默丢失 ——
            //     实测 set statement_timeout='1s' 之后 show 出来还是 0,因为每条语句开一次连接;
            //  ③ 连接建立要由插件自己做,才拿得到原始驱动异常(§5.3)。
            IsAutoCloseConnection = false,

            MoreSettings = new ConnMoreSettings
            {
                // 纪律四(§5.4.5):**别改写用户的标识符大小写**。
                // 这是 ORM 的正确行为、管理工具的致命伤:默认配置下 PG 会把 "OrderDetail"
                // 压成 "orderdetail",服务端以 42P01 打回 —— 而元数据通道**照常读得到**,
                // 于是表现是"对象树里有这张表,一点开就报表不存在"。
                PgSqlIsAutoToLower = false,
                PgSqlIsAutoToLowerCodeFirst = false,
                PgSqlIsAutoToLowerSchema = false,
                IsAutoToUpper = false
            }
        };

        var client = new SqlSugarClient(config);

        // 纪律五(§5.1):CommandTimeout 只能在这里设。
        // SqlSugar 的 Ado.CommandTimeOut 默认 300 秒,并把它盖到每一条 DbCommand 上 ——
        // 连接串里的 DefaultCommandTimeout 会被它覆盖掉,配了也是白配。
        client.Ado.CommandTimeOut = commandTimeoutSeconds;

        if (onSql is not null)
        {
            client.Aop.OnLogExecuting = (sql, parameters) => onSql(sql, parameters ?? []);
        }

        if (dialect == SqlDialect.PostgreSql)
        {
            Volatile.Write(ref _postgresClientCreated, 1);
        }
        return client;
    }

    /// <summary>仅供单测:把"是否已建过 PG client"复位,以便在同一进程里重跑顺序纪律用例。</summary>
    internal static void ResetPostgresGateForTests() => Volatile.Write(ref _postgresClientCreated, 0);
}

using SugarDbType = SqlSugar.DbType;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// **用户可见的方言**。
/// <para>
/// 这个枚举存在的理由是一条实测事实(设计文档 §6.2):`new SqlSugarClient(cfg)` 会**就地改写
/// `cfg.DbType`** —— 传 <c>Tidb</c> 进去,建完 client 它就变成 <c>MySql</c> 了,而且改的是传入对象本身。
/// 所以 <b>插件不能靠 <c>ConnectionConfig.DbType</c> 记住用户选了什么</b>;
/// 选方言包、挑差异补丁、画界面文案,一律以本枚举为准。
/// </para>
/// <para>
/// v1 只放五种一等公民。同族方言(TiDB / OceanBase / openGauss / 人大金仓…)在 M4 追加,
/// 追加时它们各自是本枚举的一个成员,而不是复用 MySQL/PostgreSQL 的成员 ——
/// 否则又会掉进"记不住用户选了什么"的同一个坑。
/// </para>
/// </summary>
internal enum SqlDialect
{
    /// <summary>MySQL / MariaDB。</summary>
    MySql,

    /// <summary>PostgreSQL。</summary>
    PostgreSql,

    /// <summary>Microsoft SQL Server。</summary>
    SqlServer,

    /// <summary>Oracle Database。</summary>
    Oracle,

    /// <summary>SQLite(文件型,没有主机与端口)。</summary>
    Sqlite
}

/// <summary>一种方言的静态元信息:工作台 id、SqlSugar 枚举、默认端口、形态。</summary>
/// <param name="Dialect">用户可见方言。</param>
/// <param name="WorkspaceSuffix">工作台 id 在插件 id 之后的后缀(落进用户配置,<b>发布后不可更名</b>)。</param>
/// <param name="DisplayName">页签名称(非本地化的产品名,五种语言下都一样)。</param>
/// <param name="SugarType">SqlSugar 的方言枚举。</param>
/// <param name="DefaultPort">默认端口;文件型方言为 <see cref="FilePlaceholderPort" />。</param>
/// <param name="IsFileBased">是否文件型(没有网络端点)。</param>
internal sealed record SqlDialectInfo(
    SqlDialect Dialect,
    string WorkspaceSuffix,
    string DisplayName,
    SugarDbType SugarType,
    int DefaultPort,
    bool IsFileBased)
{
    /// <summary>
    /// 文件型方言的占位端口。SQLite 没有端点,但这个值<b>不能省、也不能填 0</b>。
    /// <para>
    /// 宿主那一栏现在已经收起来了(<c>WorkspaceFeatures.NoEndpoint</c>,配上改标成
    /// "数据库文件"的主机栏),用户根本看不到端口 —— 但<b>看不见不等于不校验</b>:
    /// 保存/连接/测试三个按钮的 <c>canExecute</c> 里仍有一条 <c>port is >= 1 and &lt;= 65535</c>。
    /// 填 0 的话按钮会**整排灰死**,而界面上又没有那一栏可以改,用户完全无从下手。
    /// 所以这里取区间内最小的合法值,纯粹是为了让那条判定过得去。
    /// </para>
    /// </summary>
    public const int FilePlaceholderPort = 1;
}

/// <summary>方言登记表。<b>本插件里所有"这个方言长什么样"的问题都从这里出发。</b></summary>
internal static class SqlDialects
{
    /// <summary>插件 id(与 <c>plugin.json</c> 一致)。工作台 id 必须以它为前缀,宿主强制。</summary>
    public const string PluginId = "velashell.sql";

    /// <summary>v1 的五种一等公民,顺序即连接配置页上的页签顺序。</summary>
    public static IReadOnlyList<SqlDialectInfo> All { get; } =
    [
        new(SqlDialect.MySql, "mysql", "MySQL", SugarDbType.MySql, 3306, false),
        new(SqlDialect.PostgreSql, "postgresql", "PostgreSQL", SugarDbType.PostgreSQL, 5432, false),
        new(SqlDialect.SqlServer, "sqlserver", "SQL Server", SugarDbType.SqlServer, 1433, false),
        new(SqlDialect.Oracle, "oracle", "Oracle", SugarDbType.Oracle, 1521, false),
        new(SqlDialect.Sqlite, "sqlite", "SQLite", SugarDbType.Sqlite, SqlDialectInfo.FilePlaceholderPort, true)
    ];

    /// <summary>
    /// 工作台 id。**五个方言共用这一个** —— 连接配置页上只有一个「数据库」页签,
    /// 具体哪个方言由表单里的 <c>dialect</c> 下拉决定(宿主的「变体」机制,
    /// 见 <see cref="VelaShell.PluginSdk.Workspaces.WorkspaceVariant" />)。
    /// <para>
    /// 这个 id 会落进用户的会话配置,<b>发布后不可更改</b>。
    /// </para>
    /// </summary>
    public const string WorkspaceId = PluginId;

    /// <summary>
    /// 选方言的那个字段键。它同时是三样东西的依据:
    /// 表单字段的 <c>VisibleWhen</c>、连接框的变体、以及打开会话时"这是哪个方言"。
    /// <b>落进用户配置,发布后不可更名。</b>
    /// </summary>
    public const string DialectKey = "dialect";

    /// <summary>
    /// 方言在配置里的取值(<c>mysql</c> / <c>postgresql</c> …)。
    /// <para>沿用原先工作台 id 的后缀:那串字符本来就是为"落进配置且不再变"挑的。</para>
    /// </summary>
    /// <param name="info">方言元信息。</param>
    /// <returns>取值。</returns>
    public static string VariantValue(SqlDialectInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.WorkspaceSuffix;
    }

    /// <summary>按配置里的取值反查方言;认不出时返回 <see langword="null" />。</summary>
    /// <param name="value">取值。</param>
    /// <returns>元信息或 <see langword="null" />。</returns>
    public static SqlDialectInfo? ByVariantValue(string value) =>
        All.FirstOrDefault(x => string.Equals(x.WorkspaceSuffix, value, StringComparison.Ordinal));

    /// <summary>按方言取元信息。</summary>
    /// <param name="dialect">方言。</param>
    /// <returns>元信息。</returns>
    public static SqlDialectInfo Of(SqlDialect dialect) =>
        All.FirstOrDefault(x => x.Dialect == dialect)
        ?? throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "未登记的方言。");

    /// <summary>
    /// 连接配置页上那个唯一页签的默认方言。
    /// <para>新建连接时下拉框停在这里,端口也据此预填 —— 挑用得最多的那个。</para>
    /// </summary>
    public static SqlDialectInfo Default => All[0];
}

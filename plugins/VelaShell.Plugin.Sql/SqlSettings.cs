using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 环境标记。它**不是**装饰:护栏强度、标签配色、只读默认值全由它派生。
/// <para>
/// 为什么是显式字段而不是猜的:替用户猜"这是生产"会让护栏在错误的地方紧或松,
/// 而护栏紧错了比没有护栏更让人烦。与 Redis 插件同一条决定。
/// </para>
/// </summary>
internal enum SqlEnvironment
{
    /// <summary>开发。</summary>
    Development,

    /// <summary>预发。</summary>
    Staging,

    /// <summary>生产:写操作要过确认框,危险操作要键入对象名。</summary>
    Production
}

/// <summary>TLS 档位。各方言翻译成自己的连接串键(见 <see cref="SqlConnectionString" />)。</summary>
internal enum SqlSslMode
{
    /// <summary>不加密。</summary>
    Disabled,

    /// <summary>能加密就加密,不能就明文(多数驱动的默认)。</summary>
    Preferred,

    /// <summary>必须加密,但不校验证书。</summary>
    Required,

    /// <summary>必须加密且校验 CA。</summary>
    VerifyCa
}

/// <summary>
/// 一条数据库连接的强类型设置。字段声明(<see cref="Declare" />)与解析(<see cref="From" />)
/// 刻意放在同一个文件里:两边靠字符串键对齐,分开写迟早对不上。
/// </summary>
internal sealed record SqlSettings
{
    /// <summary>字段键常量。落进用户配置,<b>发布后不可更名</b>。</summary>
    private const string KeyDatabase = "database";
    private const string KeySchema = "schema";
    private const string KeySsl = "ssl";
    private const string KeyEnvironment = "environment";
    private const string KeyReadOnly = "readOnly";
    private const string KeyConnectTimeout = "connectTimeout";
    private const string KeyCommandTimeout = "commandTimeout";
    private const string KeyTrustedThumbprint = "trustedThumbprint";
    private const string KeyJumpSession = "jumpSession";
    private const string KeyOracleServiceName = "oracleServiceName";
    private const string KeyOracleConnectAs = "oracleConnectAs";
    private const string KeyMySqlAllowUserVariables = "mysqlAllowUserVariables";
    private const string KeyMySqlZeroDate = "mysqlZeroDate";
    private const string KeySqliteReadOnlyOpen = "sqliteReadOnlyOpen";

    /// <summary>指纹回写字段的键(宿主在用户确认信任证书后写进这里)。</summary>
    public const string TrustedThumbprintKey = KeyTrustedThumbprint;

    /// <summary>用户可见方言。</summary>
    public required SqlDialect Dialect { get; init; }

    /// <summary>默认库;留空则连上后列全部库。SQLite 上是**文件路径**。</summary>
    public string Database { get; init; } = "";

    /// <summary>
    /// 默认 schema。PG 上它会写进连接串的 <c>Search Path</c> ——
    /// 这是 <c>DbMaintenance</c> 能看到自定义 schema 的**唯一**开关(§3.5 实测)。
    /// </summary>
    public string Schema { get; init; } = "";

    /// <summary>TLS 档位。</summary>
    public SqlSslMode SslMode { get; init; } = SqlSslMode.Preferred;

    /// <summary>环境标记。</summary>
    public SqlEnvironment Environment { get; init; } = SqlEnvironment.Development;

    /// <summary>只读连接:一切写操作在**发出之前**被拒(不是靠数据库权限)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>连接超时(秒)。</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// 语句超时(秒)。**必须由插件显式设到 <c>Ado.CommandTimeOut</c>** ——
    /// 放在连接串里对 SqlSugar 无效,它默认 300 秒并盖到每条 DbCommand 上(§5.1 实测)。
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>用户已确认信任的服务器证书指纹;为空表示还没信任过。</summary>
    public string TrustedThumbprint { get; init; } = "";

    /// <summary>经哪条 SSH 配置抵达(会话配置 id;空 = 直连)。宿主代建隧道,插件不写一行 SSH。</summary>
    public string JumpSession { get; init; } = "";

    /// <summary>Oracle 服务名 / SID。</summary>
    public string OracleServiceName { get; init; } = "";

    /// <summary>Oracle 以什么身份连(<c>NORMAL</c> / <c>SYSDBA</c> / <c>SYSOPER</c>)。</summary>
    public string OracleConnectAs { get; init; } = "NORMAL";

    /// <summary>
    /// MySQL:允许用户变量。**默认开**,理由是实测——不开的话用户手敲的
    /// <c>SET @x := 1; SELECT @x</c> 这类极常见 SQL 直接报错,
    /// 而且报的是"参数未定义"这种把人往参数化上引的误导消息(§5.1)。
    /// </summary>
    public bool MySqlAllowUserVariables { get; init; } = true;

    /// <summary>
    /// MySQL:怎么对待 <c>0000-00-00</c>。老库最常见的地雷 ——
    /// 默认配置下只要表里有一个零日期,**整张 <c>GetDataTable</c> 直接抛异常**,
    /// 不是那一格出错,是整个结果集拿不到(§5.1 实测)。
    /// </summary>
    public MySqlZeroDatePolicy MySqlZeroDate { get; init; } = MySqlZeroDatePolicy.Reject;

    /// <summary>SQLite:以只读方式打开文件(不改文件,也不建 -wal/-shm)。</summary>
    public bool SqliteReadOnlyOpen { get; init; }

    /// <summary>本连接对应的方言元信息。</summary>
    public SqlDialectInfo Info => SqlDialects.Of(Dialect);

    /// <summary>
    /// 声明连接表单。
    /// <para>
    /// <b>五个方言共用一张表,靠 <see cref="ProtocolSettingField.VisibleWhen" /> 分叉。</b>
    /// 初版是"一个方言一个工作台、字段各声明一份",那样更直白;
    /// 但它在连接类型那一排摆出五个页签,而这五个页签除了默认端口几乎一模一样。
    /// 现在收成一个「数据库」页签 + 第一栏的方言下拉,端口与"主机"那一栏的含义
    /// 由宿主的<b>变体</b>机制跟着走(见 <see cref="SqlPlugin" /> 里的 <c>Variants</c>)。
    /// </para>
    /// <para>
    /// <b>条件不成立的字段只是从表单上消失,已存的值照常保留并回传</b> ——
    /// 用户填过 Oracle 的服务名、切去 MySQL 看一眼再切回来,不该发现自己填的东西被顺手清掉了。
    /// </para>
    /// </summary>
    /// <param name="loc">文案表。</param>
    /// <returns>字段声明。</returns>
    public static IReadOnlyList<ProtocolSettingField> Declare(Loc loc)
    {
        ArgumentNullException.ThrowIfNull(loc);

        // 每个方言的取值,用来拼 VisibleWhen 的条件。
        string[] all = [.. SqlDialects.All.Select(SqlDialects.VariantValue)];
        string[] networked = [.. SqlDialects.All.Where(x => !x.IsFileBased).Select(SqlDialects.VariantValue)];
        string sqlite = SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.Sqlite));
        string mysql = SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.MySql));
        string oracle = SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.Oracle));
        string[] schemaAware =
        [
            SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.PostgreSql)),
            SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.SqlServer))
        ];

        List<ProtocolSettingField> fields =
        [
            // ═══ 第一栏:选哪种数据库 ═══
            // 它同时是三样东西的依据:其余字段的显隐、连接框的变体(端口/主机标签/凭据)、
            // 以及打开会话时"这是哪个方言"。
            new()
            {
                Key = SqlDialects.DialectKey,
                Label = loc["Sql_DialectLabel"],
                Kind = ProtocolSettingKind.Choice,
                DefaultValue = SqlDialects.VariantValue(SqlDialects.Default),
                Choices = [.. SqlDialects.All.Select(x => new ProtocolSettingChoice(SqlDialects.VariantValue(x), x.DisplayName))],
                Hint = loc["Sql_DialectHint"]
            }
        ];

        // SQLite 的文件路径走"主机"那一栏(变体已把它改标成"数据库文件"),
        // 所以这里不再重复声明 database —— 免得表单上出现两个都叫"文件"的输入框。
        fields.Add(new()
        {
            Key = KeyDatabase,
            Label = loc["Sql_Database"],
            Kind = ProtocolSettingKind.Text,
            Placeholder = loc["Sql_DatabasePlaceholder"],
            Hint = loc["Sql_DatabaseHint"],
            VisibleWhen = new(SqlDialects.DialectKey, networked)
        });

        fields.Add(new()
        {
            Key = KeySqliteReadOnlyOpen,
            Label = loc["Sql_SqliteReadOnlyOpen"],
            Kind = ProtocolSettingKind.Boolean,
            DefaultValue = "false",
            Hint = loc["Sql_SqliteReadOnlyOpenHint"],
            VisibleWhen = new(SqlDialects.DialectKey, sqlite)
        });

        // schema:PG 与 SQL Server 都有,但**默认值不同**(public / dbo),
        // 而一个字段只有一个 DefaultValue。所以这里不给默认值,
        // 由 From 在解析时按方言补上 —— 顺带比写死更对:用户清空它就是"用方言的默认"。
        fields.Add(new()
        {
            Key = KeySchema,
            Label = loc["Sql_Schema"],
            Kind = ProtocolSettingKind.Text,
            Placeholder = loc["Sql_SchemaPlaceholder"],
            // PG 上这句不是提示,是事实:不设 search_path,DbMaintenance 只看得见 public。
            Hint = loc["Sql_SchemaHintPg"],
            VisibleWhen = new(SqlDialects.DialectKey, schemaAware)
        });

        fields.Add(new()
        {
            Key = KeyOracleServiceName,
            Label = loc["Sql_OracleServiceName"],
            Kind = ProtocolSettingKind.Text,
            Placeholder = "ORCLPDB1",
            Hint = loc["Sql_OracleServiceNameHint"],
            VisibleWhen = new(SqlDialects.DialectKey, oracle)
        });
        fields.Add(new()
        {
            Key = KeyOracleConnectAs,
            Label = loc["Sql_OracleConnectAs"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "NORMAL",
            Choices =
            [
                new("NORMAL", loc["Sql_OracleConnectAsNormal"]),
                new("SYSDBA", "SYSDBA"),
                new("SYSOPER", "SYSOPER")
            ],
            IsAdvanced = true,
            VisibleWhen = new(SqlDialects.DialectKey, oracle)
        });

        // 环境与只读:两个决定护栏强度的字段,永远留在外面不折叠,也不分方言。
        fields.Add(new()
        {
            Key = KeyEnvironment,
            Label = loc["Sql_Environment"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = nameof(SqlEnvironment.Development),
            Choices =
            [
                new(nameof(SqlEnvironment.Development), loc["Sql_EnvDevelopment"]),
                new(nameof(SqlEnvironment.Staging), loc["Sql_EnvStaging"]),
                new(nameof(SqlEnvironment.Production), loc["Sql_EnvProduction"])
            ],
            Hint = loc["Sql_EnvironmentHint"]
        });
        fields.Add(new()
        {
            Key = KeyReadOnly,
            Label = loc["Sql_ReadOnly"],
            Kind = ProtocolSettingKind.Boolean,
            DefaultValue = "false",
            Hint = loc["Sql_ReadOnlyHint"]
        });

        fields.Add(new()
        {
            Key = KeySsl,
            Label = loc["Sql_Ssl"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = nameof(SqlSslMode.Preferred),
            Choices =
            [
                new(nameof(SqlSslMode.Disabled), loc["Sql_SslDisabled"]),
                new(nameof(SqlSslMode.Preferred), loc["Sql_SslPreferred"]),
                new(nameof(SqlSslMode.Required), loc["Sql_SslRequired"]),
                new(nameof(SqlSslMode.VerifyCa), loc["Sql_SslVerifyCa"])
            ],
            VisibleWhen = new(SqlDialects.DialectKey, networked)
        });
        fields.Add(new()
        {
            Key = KeyJumpSession,
            Label = loc["Sql_JumpSession"],
            Kind = ProtocolSettingKind.SshSession,
            Hint = loc["Sql_JumpSessionHint"],
            VisibleWhen = new(SqlDialects.DialectKey, networked)
        });

        // 这两个不是调优项,是"不设就有明确功能缺陷"的项 —— 所以给默认值但仍放进高级区,
        // 因为绝大多数用户不需要改。注意**没有 charset 下拉**:
        // 实测 MySqlConnector 的 CharacterSet 已被标为 Obsolete、六种取值(含乱填的)全部被忽略,
        // 会话字符集恒为 utf8mb4 —— 摆一个不起作用的下拉是骗人(§5.1)。
        fields.Add(new()
        {
            Key = KeyMySqlAllowUserVariables,
            Label = loc["Sql_MySqlAllowUserVariables"],
            Kind = ProtocolSettingKind.Boolean,
            DefaultValue = "true",
            Hint = loc["Sql_MySqlAllowUserVariablesHint"],
            IsAdvanced = true,
            VisibleWhen = new(SqlDialects.DialectKey, mysql)
        });
        fields.Add(new()
        {
            Key = KeyMySqlZeroDate,
            Label = loc["Sql_MySqlZeroDate"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = nameof(MySqlZeroDatePolicy.Reject),
            Choices =
            [
                new(nameof(MySqlZeroDatePolicy.Reject), loc["Sql_MySqlZeroDateReject"]),
                new(nameof(MySqlZeroDatePolicy.Convert), loc["Sql_MySqlZeroDateConvert"])
            ],
            Hint = loc["Sql_MySqlZeroDateHint"],
            IsAdvanced = true,
            VisibleWhen = new(SqlDialects.DialectKey, mysql)
        });

        fields.Add(new()
        {
            Key = KeyConnectTimeout,
            Label = loc["Sql_ConnectTimeout"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "15",
            IsAdvanced = true
        });
        fields.Add(new()
        {
            Key = KeyCommandTimeout,
            Label = loc["Sql_CommandTimeout"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "30",
            Hint = loc["Sql_CommandTimeoutHint"],
            IsAdvanced = true
        });

        // 指纹回写位:用户在信任对话框里点了"信任"之后,宿主把指纹写进这里。
        // 隐藏字段不进表单,所以不需要 VisibleWhen —— 它在哪个方言下都只是个存值的格子。
        fields.Add(new()
        {
            Key = KeyTrustedThumbprint,
            Label = "",
            Kind = ProtocolSettingKind.Text,
            IsHidden = true
        });

        _ = all;
        return fields;
    }

    /// <summary>从宿主递来的连接请求里解析设置。</summary>
    /// <param name="request">连接请求。</param>
    /// <param name="dialect">方言。</param>
    /// <returns>强类型设置。</returns>
    public static SqlSettings From(WorkspaceConnectRequest request, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new()
        {
            Dialect = dialect,
            // SQLite 的文件路径走"主机"一栏(描述符已把它改标成"数据库文件")。
            Database = dialect == SqlDialect.Sqlite ? request.Host : request.GetString(KeyDatabase),
            // schema 的默认值按方言补:一个字段只有一个 DefaultValue,而 PG 要 public、
            // SQL Server 要 dbo。用户清空它就是"用这个方言的默认",比写死更对。
            Schema = request.GetString(KeySchema) is { Length: > 0 } schema
                ? schema
                : dialect switch
                {
                    SqlDialect.PostgreSql => "public",
                    SqlDialect.SqlServer => "dbo",
                    _ => ""
                },
            SslMode = ParseEnum(request.GetString(KeySsl), SqlSslMode.Preferred),
            Environment = ParseEnum(request.GetString(KeyEnvironment), SqlEnvironment.Development),
            ReadOnly = request.GetBoolean(KeyReadOnly),
            ConnectTimeoutSeconds = Clamp(request.GetInt32(KeyConnectTimeout, 15), 1, 600, 15),
            CommandTimeoutSeconds = Clamp(request.GetInt32(KeyCommandTimeout, 30), 1, 86400, 30),
            TrustedThumbprint = request.GetString(KeyTrustedThumbprint),
            JumpSession = request.GetString(KeyJumpSession),
            OracleServiceName = request.GetString(KeyOracleServiceName),
            OracleConnectAs = request.GetString(KeyOracleConnectAs, "NORMAL"),
            MySqlAllowUserVariables = request.GetBoolean(KeyMySqlAllowUserVariables, true),
            MySqlZeroDate = ParseEnum(request.GetString(KeyMySqlZeroDate), MySqlZeroDatePolicy.Reject),
            SqliteReadOnlyOpen = request.GetBoolean(KeySqliteReadOnlyOpen)
        };
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T parsed) ? parsed : fallback;

    private static int Clamp(int value, int min, int max, int fallback) =>
        value < min || value > max ? fallback : value;
}

/// <summary>MySQL 的零日期策略。</summary>
internal enum MySqlZeroDatePolicy
{
    /// <summary>
    /// 不处理(驱动默认)。表里只要有一个 <c>0000-00-00</c>,整张结果集抛 <c>InvalidCastException</c>。
    /// 之所以仍把它设成默认:管理工具**宁可报错也不显示错值** ——
    /// 下一档会把零日期悄悄变成 0001-01-01,那是界面在说谎。
    /// </summary>
    Reject,

    /// <summary>转换成 <c>0001-01-01</c>(<c>ConvertZeroDateTime=true</c>)。能读出来,但显示的不是真值。</summary>
    Convert
}

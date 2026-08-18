using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis;

/// <summary>部署形态。连接方式与界面能力(有没有多个数据库)都由它决定。</summary>
public enum RedisDeployment
{
    /// <summary>单机(或主从里的某一台)。</summary>
    Standalone,

    /// <summary>哨兵:主地址由 <c>SENTINEL get-master-addr-by-name</c> 解析。</summary>
    Sentinel,

    /// <summary>集群:只有 db0,键按槽位分布在多个节点上。</summary>
    Cluster
}

/// <summary>
/// 环境标记。它**不是**装饰:护栏强度、标签配色、只读默认值全由它派生 ——
/// "我现在在动线上"这件事必须无法被忽略。
/// </summary>
public enum RedisEnvironment
{
    /// <summary>开发。</summary>
    Development,

    /// <summary>预发。</summary>
    Staging,

    /// <summary>生产:默认只读,清库类命令默认锁死。</summary>
    Production
}

/// <summary>
/// 一条 Redis 连接的强类型设置。字段声明(<see cref="Declare" />)与解析
/// (<see cref="From" />)刻意放在同一个文件里:两边靠字符串键对齐,分开写迟早对不上。
/// </summary>
internal sealed record RedisSettings
{
    /// <summary>字段键常量。落进用户配置,**发布后不可更名**。</summary>
    private const string KeyMode = "mode";
    private const string KeyMasterName = "masterName";
    private const string KeyDatabase = "database";
    private const string KeyTls = "tls";
    private const string KeyEnvironment = "environment";
    private const string KeyReadOnly = "readOnly";
    private const string KeyDelimiter = "delimiter";
    private const string KeyScanCount = "scanCount";
    private const string KeyGroupThreshold = "groupThreshold";
    private const string KeyScanBudget = "scanBudget";
    private const string KeyValuePreview = "valuePreview";
    private const string KeyClientName = "clientName";
    private const string KeyConnectTimeout = "connectTimeout";
    private const string KeyTrustedThumbprint = "trustedThumbprint";
    private const string KeyJumpSession = "jumpSession";

    /// <summary>指纹回写字段的键(宿主在用户确认信任证书后写进这里)。</summary>
    public const string TrustedThumbprintKey = KeyTrustedThumbprint;

    /// <summary>部署形态。</summary>
    public RedisDeployment Deployment { get; init; } = RedisDeployment.Standalone;

    /// <summary>哨兵监控的主节点名(仅哨兵模式有意义)。</summary>
    public string MasterName { get; init; } = "";

    /// <summary>默认数据库(集群模式下强制 0)。</summary>
    public int Database { get; init; }

    /// <summary>是否使用 TLS。</summary>
    public bool UseTls { get; init; }

    /// <summary>环境标记。</summary>
    public RedisEnvironment Environment { get; init; } = RedisEnvironment.Development;

    /// <summary>只读模式(生产默认开)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>键树的层级分隔符。</summary>
    public string Delimiter { get; init; } = ":";

    /// <summary><c>SCAN COUNT</c> 的批量。</summary>
    public int ScanCount { get; init; } = 500;

    /// <summary>
    /// 键列表里同前缀的键达到几个才折成一条分组行。
    /// <para>
    /// 折叠是为了**压噪音**,不是为了制造点击 —— 三五个同前缀的键折起来反而要多点一下
    /// 才看得到,所以默认取 8。低于 <see cref="Ui.RedisKeyLayout.MinThreshold" /> 即视为不折。
    /// </para>
    /// </summary>
    public int GroupThreshold { get; init; } = 8;

    /// <summary>自动扫描的键数软上限(到顶即停,把继续与否交给用户)。</summary>
    public int ScanBudget { get; init; } = 5000;

    /// <summary>值预览的字节上限(超出只取前 N 字节并如实标注)。</summary>
    public int ValuePreviewBytes { get; init; } = 256 * 1024;

    /// <summary>上报给 <c>CLIENT LIST</c> 的客户端名。</summary>
    public string ClientName { get; init; } = "velashell";

    /// <summary>连接超时(毫秒)。</summary>
    public int ConnectTimeoutMs { get; init; } = 5000;

    /// <summary>用户已确认信任的服务器证书指纹(自签 TLS 端点用);为空表示还没信任过。</summary>
    public string TrustedThumbprint { get; init; } = "";

    /// <summary>
    /// 经哪条 SSH 配置抵达(会话配置 id;空 = 直连)。
    /// <para>
    /// **插件不用它做任何事** —— 宿主在打开会话之前就把 SSH 会话与本地转发建好了,
    /// 递过来的主机/端口已是本地端点。留这个属性只为让"这条连接走隧道"这件事
    /// 在插件的设置模型里也看得见(界面上要显示来路)。
    /// </para>
    /// </summary>
    public string JumpSessionId { get; init; } = "";

    /// <summary>集群模式下没有多数据库这回事,界面据此禁用数据库下拉。</summary>
    public bool SupportsDatabases => Deployment != RedisDeployment.Cluster;

    /// <summary>从宿主递来的连接请求解析。缺失/不可解析的一律回落到声明的默认值。</summary>
    /// <param name="request">连接请求。</param>
    /// <returns>强类型设置。</returns>
    public static RedisSettings From(WorkspaceConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RedisDeployment deployment = request.GetString(KeyMode, "standalone") switch
        {
            "sentinel" => RedisDeployment.Sentinel,
            "cluster" => RedisDeployment.Cluster,
            _ => RedisDeployment.Standalone
        };
        RedisEnvironment environment = request.GetString(KeyEnvironment, "development") switch
        {
            "production" => RedisEnvironment.Production,
            "staging" => RedisEnvironment.Staging,
            _ => RedisEnvironment.Development
        };
        // 只读的默认值随环境走,但用户显式设过就听用户的 —— 所以要看"键在不在",
        // 不能只看解析出来的布尔值(那样分不清"用户关掉了"与"没配过")。
        bool readOnlyDefault = environment == RedisEnvironment.Production;
        bool readOnly = request.Settings.TryGetValue(KeyReadOnly, out string? readOnlyRaw)
                        && bool.TryParse(readOnlyRaw, out bool parsed)
            ? parsed
            : readOnlyDefault;
        string delimiter = request.GetString(KeyDelimiter, ":");
        return new()
        {
            Deployment = deployment,
            MasterName = request.GetString(KeyMasterName),
            // 集群只有 db0:与其等服务器回一句 SELECT 报错,不如在这里就归零。
            Database = deployment == RedisDeployment.Cluster ? 0 : Math.Clamp(request.GetInt32(KeyDatabase), 0, 255),
            UseTls = request.GetBoolean(KeyTls),
            Environment = environment,
            ReadOnly = readOnly,
            // 分隔符留空会让整个键空间塌成一层平列表(每个键都是根节点),
            // 不是用户想要的"不分层" —— 那种意图应当用"包含"匹配表达。
            Delimiter = string.IsNullOrEmpty(delimiter) ? ":" : delimiter,
            ScanCount = Math.Clamp(request.GetInt32(KeyScanCount, 500), 10, 10_000),
            // 上限 1000:再高就等于永不折叠,那用「不折」(填 1)表达更直白。
            GroupThreshold = Math.Clamp(request.GetInt32(KeyGroupThreshold, 8), 1, 1000),
            ScanBudget = Math.Clamp(request.GetInt32(KeyScanBudget, 5000), 100, 1_000_000),
            ValuePreviewBytes = Math.Clamp(request.GetInt32(KeyValuePreview, 256 * 1024), 1024, 16 * 1024 * 1024),
            ClientName = request.GetString(KeyClientName, "velashell"),
            ConnectTimeoutMs = Math.Clamp(request.GetInt32(KeyConnectTimeout, 5000), 500, 60_000),
            TrustedThumbprint = request.GetString(KeyTrustedThumbprint),
            JumpSessionId = request.GetString(KeyJumpSession)
        };
    }

    /// <summary>
    /// 声明式表单:宿主按形态渲染控件,插件因此没有一行连接对话框的界面代码。
    /// <para>
    /// 决定"连不连得上"的四项(形态、主节点名、数据库、TLS)留在外面;
    /// 其余调优项一律 <c>IsAdvanced</c> 收进「高级选项」—— 十四个字段一列铺开会把对话框
    /// 顶出屏幕,底部的保存/连接按钮就够不着了(S3 插件的用户反馈)。
    /// </para>
    /// </summary>
    /// <param name="loc">文案表。</param>
    /// <returns>字段声明。</returns>
    public static IReadOnlyList<ProtocolSettingField> Declare(Loc loc) =>
    [
        new()
        {
            Key = KeyMode,
            Label = loc["Redis_Mode"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "standalone",
            Choices =
            [
                new("standalone", loc["Redis_ModeStandalone"]),
                new("sentinel", loc["Redis_ModeSentinel"]),
                new("cluster", loc["Redis_ModeCluster"])
            ]
        },
        new()
        {
            Key = KeyMasterName,
            Label = loc["Redis_MasterName"],
            Placeholder = "mymaster",
            // 只有哨兵模式才有"主节点名"这回事。原先它在独立/集群下照样显示,
            // 唯一的线索是下面一行小字写着"仅哨兵模式" —— 用小字解释一个本该消失的字段,
            // 是把界面的活推给了文案。
            VisibleWhen = new(KeyMode, "sentinel")
        },
        new()
        {
            Key = KeyDatabase,
            Label = loc["Redis_Database"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "0",
            // 集群只有 db0,填什么都不作数(RedisSettings.From 也会把它归零)。
            // 与其留一个"填了不生效"的框加一行解释,不如让它在集群下不出现。
            VisibleWhen = new(KeyMode, ["standalone", "sentinel"])
        },
        new()
        {
            Key = KeyTls,
            Label = loc["Redis_UseTls"],
            Kind = ProtocolSettingKind.Boolean,
            DefaultValue = "false"
        },
        new()
        {
            Key = KeyEnvironment,
            Label = loc["Redis_Environment"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "development",
            Hint = loc["Redis_EnvironmentHint"],
            Choices =
            [
                new("development", loc["Redis_EnvDevelopment"]),
                new("staging", loc["Redis_EnvStaging"]),
                new("production", loc["Redis_EnvProduction"])
            ]
        },
        new()
        {
            Key = KeyReadOnly,
            Label = loc["Redis_ReadOnly"],
            Kind = ProtocolSettingKind.Boolean,
            // 刻意**不给** DefaultValue:默认值要随环境走(生产开、其余关),
            // 在这里写死任何一个都会让另一半环境的默认是错的。缺键即"按环境定"。
            Hint = loc["Redis_ReadOnlyHint"]
        },
        new()
        {
            Key = KeyDelimiter,
            Label = loc["Redis_Delimiter"],
            DefaultValue = ":",
            Hint = loc["Redis_DelimiterHint"],
            IsAdvanced = true
        },
        new()
        {
            Key = KeyScanCount,
            Label = loc["Redis_ScanCount"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "500",
            IsAdvanced = true
        },
        new()
        {
            Key = KeyScanBudget,
            Label = loc["Redis_ScanBudget"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "5000",
            Hint = loc["Redis_ScanBudgetHint"],
            IsAdvanced = true
        },
        new()
        {
            Key = KeyValuePreview,
            Label = loc["Redis_ValuePreview"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "262144",
            IsAdvanced = true
        },
        new()
        {
            Key = KeyClientName,
            Label = loc["Redis_ClientName"],
            DefaultValue = "velashell",
            Hint = loc["Redis_ClientNameHint"],
            IsAdvanced = true
        },
        new()
        {
            Key = KeyConnectTimeout,
            Label = loc["Redis_ConnectTimeout"],
            Kind = ProtocolSettingKind.Integer,
            DefaultValue = "5000",
            IsAdvanced = true
        },
        new()
        {
            // 「经 SSH 隧道」:选一条已保存的 SSH 配置,宿主代为建会话 + 本地转发。
            // 放在高级选项**之外**:线上 Redis 几乎从不裸露公网,这一项决定"连不连得上"。
            Key = KeyJumpSession,
            Label = loc["Redis_JumpSession"],
            Kind = ProtocolSettingKind.SshSession,
            Hint = loc["Redis_JumpSessionHint"]
        },
        new()
        {
            // 隐藏字段:用户在证书信任对话框上点"永久信任"后,由宿主把指纹写进这里。
            // 不进表单,但照常参与存取(见 SDK 的 ProtocolSettingField.IsHidden)。
            Key = KeyTrustedThumbprint,
            Label = "TLS thumbprint",
            IsHidden = true
        }
    ];
}

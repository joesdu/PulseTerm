namespace VelaShell.Plugin.S3;

/// <summary>
/// 一种桶级配置。S3 的二十多种桶配置在协议上是同一个形状
/// (<c>GetBucketXxx</c> / <c>PutBucketXxx</c> /(部分有)<c>DeleteBucketXxx</c>),
/// 差别只在载荷,因此收敛成"一个枚举 + 一组三元方法",而不是七十多个接口成员。
/// <para>
/// **新增一种配置 = 一个枚举成员 + 三处 switch 分支 + 一行导航项。**
/// 这正是这套功能能"完整覆盖"而不是"覆盖一半"的原因。
/// </para>
/// </summary>
public enum S3ConfigKind
{
    /// <summary>版本控制(<c>Get/PutBucketVersioning</c>)。</summary>
    Versioning,

    /// <summary>公共访问阻止(<c>Get/Put/DeletePublicAccessBlock</c>)。</summary>
    PublicAccessBlock,

    /// <summary>对象所有权(<c>Get/Put/DeleteBucketOwnershipControls</c>)。</summary>
    OwnershipControls,

    /// <summary>默认加密(<c>Get/Put/DeleteBucketEncryption</c>)。</summary>
    Encryption,

    /// <summary>对象锁定(<c>Get/PutObjectLockConfiguration</c>)。</summary>
    ObjectLock,

    /// <summary>桶标签(<c>Get/Put/DeleteBucketTagging</c>)。</summary>
    Tagging,

    /// <summary>静态网站托管(<c>Get/Put/DeleteBucketWebsite</c>)。</summary>
    Website,

    /// <summary>访问日志(<c>Get/PutBucketLogging</c>)。</summary>
    Logging,

    /// <summary>传输加速(<c>Get/PutBucketAccelerateConfiguration</c>)。</summary>
    AccelerateConfiguration,

    /// <summary>请求者付费(<c>Get/PutBucketRequestPayment</c>)。</summary>
    RequestPayment,

    /// <summary>生命周期规则(<c>Get/Put/DeleteLifecycleConfiguration</c>)。</summary>
    Lifecycle,

    /// <summary>桶策略(<c>Get/Put/DeleteBucketPolicy</c>)。</summary>
    Policy,

    /// <summary>桶 ACL(<c>Get/PutBucketAcl</c>)。</summary>
    Acl,

    /// <summary>跨域规则(<c>Get/Put/DeleteCORSConfiguration</c>)。</summary>
    Cors,

    /// <summary>跨区域复制(<c>Get/Put/DeleteBucketReplication</c>)。</summary>
    Replication,

    /// <summary>事件通知(<c>Get/PutBucketNotification</c>)。</summary>
    Notification,

    /// <summary>清单配置(按 id 分多份)。</summary>
    Inventory,

    /// <summary>分析配置(按 id 分多份)。</summary>
    Analytics,

    /// <summary>请求指标配置(按 id 分多份)。</summary>
    Metrics,

    /// <summary>智能分层配置(按 id 分多份)。</summary>
    IntelligentTiering,

    /// <summary>元数据表配置(<c>Get/Put/DeleteBucketMetadataConfiguration</c>)。</summary>
    MetadataConfiguration,

    /// <summary>基于属性的访问控制(ABAC)开关。</summary>
    Abac,
}

/// <summary>某项配置在界面上用哪种编辑器呈现。</summary>
public enum S3ConfigEditor
{
    /// <summary>结构化表单:字段少、形状稳定的配置。</summary>
    Form,

    /// <summary>
    /// JSON 文档编辑器:本身就是文档的配置(生命周期、策略、ACL、CORS、复制、通知…)。
    /// <para>
    /// 给文档编辑器**不是偷懒,而是更完整、更安全的选择**:这些配置硬做成表单既做不全,
    /// 也会在遇到未知字段时把它们静默清空 —— 那是真的改坏用户的生产配置。
    /// AWS 控制台在这些项上同样给 JSON。
    /// </para>
    /// </summary>
    Json,
}

/// <summary>
/// 一项桶配置的元数据。桶管理器的左侧导航、编辑器选择、删除/多份 id 的可用性
/// 全部由这张表驱动 —— **协议里有的这里就有**,新增配置不必改界面代码。
/// </summary>
/// <param name="Kind">配置种类。</param>
/// <param name="ResourceKey">显示名的本地化键。</param>
/// <param name="Editor">用哪种编辑器。</param>
/// <param name="SupportsDelete">协议是否有对应的 <c>DeleteBucketXxx</c>。</param>
/// <param name="IsKeyed">是否按 id 分多份(清单/分析/指标/智能分层)。</param>
public sealed record S3ConfigDescriptor(
    S3ConfigKind Kind,
    string ResourceKey,
    S3ConfigEditor Editor,
    bool SupportsDelete,
    bool IsKeyed = false)
{
    /// <summary>全部配置项,按「表单类在前、文档类在后」排列(即桶管理器的导航顺序)。</summary>
    public static IReadOnlyList<S3ConfigDescriptor> All { get; } =
    [
        new(S3ConfigKind.Versioning, "S3Cfg_Versioning", S3ConfigEditor.Form, SupportsDelete: false),
        new(S3ConfigKind.PublicAccessBlock, "S3Cfg_PublicAccessBlock", S3ConfigEditor.Form, SupportsDelete: true),
        new(S3ConfigKind.OwnershipControls, "S3Cfg_Ownership", S3ConfigEditor.Form, SupportsDelete: true),
        new(S3ConfigKind.Encryption, "S3Cfg_Encryption", S3ConfigEditor.Form, SupportsDelete: true),
        new(S3ConfigKind.ObjectLock, "S3Cfg_ObjectLock", S3ConfigEditor.Form, SupportsDelete: false),
        new(S3ConfigKind.Tagging, "S3Cfg_Tagging", S3ConfigEditor.Form, SupportsDelete: true),
        new(S3ConfigKind.Website, "S3Cfg_Website", S3ConfigEditor.Form, SupportsDelete: true),
        new(S3ConfigKind.Logging, "S3Cfg_Logging", S3ConfigEditor.Form, SupportsDelete: false),
        new(S3ConfigKind.AccelerateConfiguration, "S3Cfg_Accelerate", S3ConfigEditor.Form, SupportsDelete: false),
        new(S3ConfigKind.RequestPayment, "S3Cfg_RequestPayment", S3ConfigEditor.Form, SupportsDelete: false),
        new(S3ConfigKind.Lifecycle, "S3Cfg_Lifecycle", S3ConfigEditor.Json, SupportsDelete: true),
        new(S3ConfigKind.Policy, "S3Cfg_Policy", S3ConfigEditor.Json, SupportsDelete: true),
        new(S3ConfigKind.Acl, "S3Cfg_Acl", S3ConfigEditor.Json, SupportsDelete: false),
        new(S3ConfigKind.Cors, "S3Cfg_Cors", S3ConfigEditor.Json, SupportsDelete: true),
        new(S3ConfigKind.Replication, "S3Cfg_Replication", S3ConfigEditor.Json, SupportsDelete: true),
        new(S3ConfigKind.Notification, "S3Cfg_Notification", S3ConfigEditor.Json, SupportsDelete: false),
        new(S3ConfigKind.Inventory, "S3Cfg_Inventory", S3ConfigEditor.Json, SupportsDelete: true, IsKeyed: true),
        new(S3ConfigKind.Analytics, "S3Cfg_Analytics", S3ConfigEditor.Json, SupportsDelete: true, IsKeyed: true),
        new(S3ConfigKind.Metrics, "S3Cfg_Metrics", S3ConfigEditor.Json, SupportsDelete: true, IsKeyed: true),
        new(S3ConfigKind.IntelligentTiering, "S3Cfg_IntelligentTiering", S3ConfigEditor.Json, SupportsDelete: true, IsKeyed: true),
        new(S3ConfigKind.MetadataConfiguration, "S3Cfg_MetadataTable", S3ConfigEditor.Json, SupportsDelete: true),
        // ABAC 走 JSON:S3ManagementService 对它是 Wrap/Unwrap("Status") 的单值文档,
        // 而 S3ConfigForm 没有对应的表单分支 —— 标成 Form 会让右侧一片空白。
        new(S3ConfigKind.Abac, "S3Cfg_Abac", S3ConfigEditor.Json, SupportsDelete: false),
    ];

    private static readonly Dictionary<S3ConfigKind, S3ConfigDescriptor> Index =
        All.ToDictionary(static d => d.Kind);

    /// <summary>按种类取回描述。</summary>
    /// <param name="kind">配置种类。</param>
    /// <returns>描述。</returns>
    public static S3ConfigDescriptor For(S3ConfigKind kind) => Index[kind];
}

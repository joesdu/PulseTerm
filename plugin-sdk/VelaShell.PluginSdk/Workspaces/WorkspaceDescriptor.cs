using VelaShell.PluginSdk.Protocols;

namespace VelaShell.PluginSdk.Workspaces;

/// <summary>工作台连接类型的能力位。宿主据此决定要不要弹登录框、走证书信任流程、渲染隧道一节。</summary>
[Flags]
public enum WorkspaceFeatures
{
    /// <summary>无额外能力。</summary>
    None = 0,

    /// <summary>允许不填凭据直接连接;宿主据此决定要不要弹登录框。</summary>
    AnonymousAccess = 1 << 0,

    /// <summary>
    /// 端点为 TLS,可能使用自签证书:宿主据此在
    /// <see cref="ProtocolCertificateTrustException" /> 时走"提示 → 记指纹 → 重连"流程
    /// (与 FTPS / S3 共用同一个对话框与同一组文案)。
    /// </summary>
    CertificateTrust = 1 << 1,

    /// <summary>
    /// 端点可能藏在内网,支持经 SSH 隧道抵达。宿主据此在连接对话框里追加"经 SSH 隧道"一节
    /// (跳板会话下拉 + 目标地址),并在打开会话前**代为建好本地转发** ——
    /// 插件收到的 <see cref="WorkspaceConnectRequest.Host" /> 已是本地端点,
    /// 它因此一行 SSH 代码都不用写、一次凭据都不用见。
    /// </summary>
    SshTunnel = 1 << 2
}

/// <summary>
/// 一种由插件**全权渲染**的会话文档类型(Redis、MySQL、Kafka…)。
/// 注册后它在连接配置页里与 SSH/SFTP/FTP 平起平坐:用户建的会话由宿主路由到插件的
/// <see cref="IWorkspaceProvider" />,而连接对话框、凭据加密落盘、登录弹窗、云同步、
/// 会话树与最近连接**全部零改动复用**。
/// <para>
/// 与 <see cref="ProtocolDescriptor" /> 的分工:协议类型长得像文件系统(宿主打开双栏浏览器),
/// 工作台类型不是(宿主向插件索取一个 Avalonia 控件挂成停靠文档)。
/// 二者共用同一套**声明式表单**(<see cref="ProtocolSettingField" />)与同一族连接异常。
/// </para>
/// </summary>
public sealed record WorkspaceDescriptor
{
    /// <summary>
    /// 连接类型 id:必须等于插件 id,或以 <c>&lt;插件id&gt;.</c> 为前缀(宿主强制,防插件间冒名)。
    /// 该 id 会落进用户的会话配置,**发布后不可更改**,否则老配置认不出自己的连接类型。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>连接配置页上的页签名称(如 <c>Redis</c>)。</summary>
    public required string DisplayName { get; init; }

    /// <summary>新建配置时的默认端口(必填,1–65535)。</summary>
    public int DefaultPort { get; init; }

    /// <summary>"主机"输入框的标签;留空即用宿主的"主机名/IP"。</summary>
    public string? HostLabel { get; init; }

    /// <summary>"主机"输入框的占位提示。</summary>
    public string? HostPlaceholder { get; init; }

    /// <summary>"用户名"输入框的标签;留空即用宿主的"用户名"。</summary>
    public string? UsernameLabel { get; init; }

    /// <summary>"密码"输入框的标签;留空即用宿主的"密码"。</summary>
    public string? PasswordLabel { get; init; }

    /// <summary>专属设置的表单声明(按顺序渲染);形态与语义见 <see cref="ProtocolSettingField" />。</summary>
    public IReadOnlyList<ProtocolSettingField> Fields { get; init; } = [];

    /// <summary>能力位。</summary>
    public WorkspaceFeatures Features { get; init; } = WorkspaceFeatures.None;

    /// <summary>
    /// 用户确认信任服务器证书后,指纹写回的字段键(须是 <see cref="Fields" /> 里
    /// 一个 <see cref="ProtocolSettingField.IsHidden" /> 的字段)。仅在声明了
    /// <see cref="WorkspaceFeatures.CertificateTrust" /> 时有意义。
    /// </summary>
    public string? TrustedThumbprintSettingKey { get; init; }
}

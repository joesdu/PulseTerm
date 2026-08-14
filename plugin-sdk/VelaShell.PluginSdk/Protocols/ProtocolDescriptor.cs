namespace VelaShell.PluginSdk.Protocols;

/// <summary>连接设置里一个字段的输入形态。宿主按此渲染控件,插件不碰界面。</summary>
public enum ProtocolSettingKind
{
    /// <summary>单行文本。</summary>
    Text,

    /// <summary>
    /// 口令输入(掩码显示)。**不**因此加密落盘 —— 要加密请用
    /// <see cref="ProtocolSettingField.IsSecret" />(两者正交:掩码是显示,加密是存储)。
    /// </summary>
    Password,

    /// <summary>复选框;取值为 <c>"true"</c> / <c>"false"</c>。</summary>
    Boolean,

    /// <summary>整数输入;取值为不变文化的十进制串。</summary>
    Integer,

    /// <summary>下拉选择;取值为 <see cref="ProtocolSettingField.Choices" /> 中某项的 <c>Value</c>。</summary>
    Choice
}

/// <summary>下拉选项。</summary>
/// <param name="Value">落盘的值(稳定标识,不随语言变化)。</param>
/// <param name="Label">展示文案(插件自行本地化)。</param>
public sealed record ProtocolSettingChoice(string Value, string Label);

/// <summary>
/// 一条协议专属设置的声明。宿主的连接配置页据此渲染表单,并把用户填的值原样回传给
/// <see cref="ProtocolConnectRequest.Settings" /> —— 插件因此**不需要**任何界面代码,
/// 就能拥有与内建协议一致外观的连接表单。
/// <para>
/// 之所以是声明式 schema 而不是让插件塞一棵控件树:连接对话框是宿主的核心界面,
/// 布局、主题、校验与本地化都由它统一负责;插件只描述"要哪些参数"。
/// 这与蓝图 08 明确不做的通用声明式 UI(VelaUI)是两回事 —— 那是任意界面,
/// 这只是一张参数表,形状封闭、语义确定。
/// </para>
/// </summary>
public sealed record ProtocolSettingField
{
    /// <summary>字段键:落进 <see cref="ProtocolConnectRequest.Settings" /> 的字典键,须在本协议内唯一。</summary>
    public required string Key { get; init; }

    /// <summary>字段标签(插件自行本地化)。</summary>
    public required string Label { get; init; }

    /// <summary>输入形态。</summary>
    public ProtocolSettingKind Kind { get; init; } = ProtocolSettingKind.Text;

    /// <summary>默认值(新建配置时预填);<see cref="ProtocolSettingKind.Boolean" /> 用 <c>"true"</c>/<c>"false"</c>。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>输入框的占位提示。</summary>
    public string? Placeholder { get; init; }

    /// <summary>字段下方的一行说明(可选)。</summary>
    public string? Hint { get; init; }

    /// <summary><see cref="ProtocolSettingKind.Choice" /> 的候选项;其余形态忽略。</summary>
    public IReadOnlyList<ProtocolSettingChoice> Choices { get; init; } = [];

    /// <summary>
    /// 是否为机密:为 <see langword="true" /> 时该值随口令一起**加密落盘**,
    /// 且永不写进日志。STS 会话令牌、附加 API 密钥之类放这里。
    /// </summary>
    public bool IsSecret { get; init; }

    /// <summary>
    /// 是否对用户隐藏。隐藏字段不出现在表单里,但照常参与存取 ——
    /// 用于宿主或插件自己写回的状态(如用户确认信任后记下的证书指纹)。
    /// </summary>
    public bool IsHidden { get; init; }
}

/// <summary>协议在文件管理器里的能力位。宿主据此启用/隐藏对应操作,避免给出必然失败的菜单项。</summary>
[Flags]
public enum ProtocolFeatures
{
    /// <summary>仅列举与读写文件。</summary>
    None = 0,

    /// <summary>支持修改权限(chmod)。不置位时属性弹窗不显示权限矩阵。</summary>
    Permissions = 1 << 0,

    /// <summary>支持服务端复制(不经本地中转)。</summary>
    ServerSideCopy = 1 << 1,

    /// <summary>上传支持从断点续传(<c>resumeOffset</c> 有意义)。</summary>
    ResumeUpload = 1 << 2,

    /// <summary>下载支持从断点续传。</summary>
    ResumeDownload = 1 << 3,

    /// <summary>允许不填任何凭据直接连接(匿名访问);宿主据此决定要不要弹登录框。</summary>
    AnonymousAccess = 1 << 4,

    /// <summary>
    /// 端点为 TLS,可能使用自签证书:宿主据此在
    /// <see cref="ProtocolCertificateTrustException" /> 时走"提示 → 记指纹 → 重连"流程。
    /// </summary>
    CertificateTrust = 1 << 5
}

/// <summary>协议动作适用的条目类型。</summary>
[Flags]
public enum ProtocolActionScope
{
    /// <summary>文件条目。</summary>
    File = 1 << 0,

    /// <summary>目录条目。</summary>
    Directory = 1 << 1,

    /// <summary>目录空白处(不针对某个条目;路径为当前目录)。</summary>
    Background = 1 << 2,

    /// <summary>文件与目录。</summary>
    Entry = File | Directory,

    /// <summary>任何位置。</summary>
    Any = File | Directory | Background
}

/// <summary>
/// 协议专属的右键菜单项。宿主在文件浏览器的上下文菜单里按 <see cref="Scope" /> 渲染,
/// 点击后调用 <see cref="IProtocolFileSystem.InvokeActionAsync" />,由插件自行处置
/// (通常是打开自己的面板)。
/// <para>声明式而非"每次右键回调插件问一遍":菜单要在按下右键那一帧就画出来,不能等一次异步往返。</para>
/// </summary>
/// <param name="Id">动作 id,在本协议内唯一。</param>
/// <param name="Title">菜单文案(插件自行本地化)。</param>
/// <param name="Scope">适用的条目类型。</param>
public sealed record ProtocolAction(string Id, string Title, ProtocolActionScope Scope = ProtocolActionScope.Entry);

/// <summary>
/// 一种由插件提供的远程文件协议。注册后它在连接配置页里与 SSH/SFTP/FTP 平起平坐:
/// 用户建的会话由宿主路由到插件的 <see cref="IProtocolFileSystem" />,
/// 而双栏浏览、传输队列、限速、拖放、冲突策略全部零改动复用。
/// </summary>
public sealed record ProtocolDescriptor
{
    /// <summary>
    /// 协议 id:必须等于插件 id,或以 <c>&lt;插件id&gt;.</c> 为前缀(宿主强制,防插件间冒名)。
    /// 该 id 会落进用户的会话配置,**发布后不可更改**,否则老配置认不出自己的协议。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>协议页签上的名称(如 <c>S3</c>)。</summary>
    public required string DisplayName { get; init; }

    /// <summary>新建配置时的默认端口。</summary>
    public int DefaultPort { get; init; } = 22;

    /// <summary>"主机"输入框的标签;留空即用宿主的"主机名/IP"。</summary>
    public string? HostLabel { get; init; }

    /// <summary>"主机"输入框的占位提示。</summary>
    public string? HostPlaceholder { get; init; }

    /// <summary>"用户名"输入框的标签;留空即用宿主的"用户名"。</summary>
    public string? UsernameLabel { get; init; }

    /// <summary>"密码"输入框的标签;留空即用宿主的"密码"。</summary>
    public string? PasswordLabel { get; init; }

    /// <summary>协议专属设置的表单声明(按顺序渲染)。</summary>
    public IReadOnlyList<ProtocolSettingField> Fields { get; init; } = [];

    /// <summary>文件浏览器右键菜单里的协议专属动作。</summary>
    public IReadOnlyList<ProtocolAction> Actions { get; init; } = [];

    /// <summary>能力位。</summary>
    public ProtocolFeatures Features { get; init; } = ProtocolFeatures.None;

    /// <summary>
    /// 用户确认信任服务器证书后,指纹写回的字段键(须是 <see cref="Fields" /> 里
    /// 一个 <see cref="ProtocolSettingField.IsHidden" /> 的字段)。仅在声明了
    /// <see cref="ProtocolFeatures.CertificateTrust" /> 时有意义。
    /// </summary>
    public string? TrustedThumbprintSettingKey { get; init; }
}

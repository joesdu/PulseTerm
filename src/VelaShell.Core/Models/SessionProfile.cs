namespace VelaShell.Core.Models;

/// <summary>一条已保存的 SSH 连接配置,描述连接目标主机所需的地址、认证方式与凭据等信息。</summary>
public class SessionProfile
{
    /// <summary>
    /// 连接协议类型;缺失或未知值均按 SSH 处理。
    /// <para>
    /// 用 <see cref="Enum.IsDefined{TEnum}" /> 做白名单而非逐值三元:语义仍是「不认识的一律降级为 SSH」
    /// (旧数据兼容策略不变),但新增协议时不必再回来改这里 —— 之前正是这处三元把扩展口焊死了。
    /// </para>
    /// </summary>
    public ConnectionType ConnectionType
    {
        get;
        set => field = Enum.IsDefined(value) ? value : ConnectionType.SSH;
    } = ConnectionType.SSH;

    /// <summary>配置的全局唯一标识,创建时自动生成。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>配置的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SSH 端口,默认 22。</summary>
    public int Port { get; set; } = 22;

    /// <summary>登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>认证方式(密码 / 私钥等),默认使用密码认证。</summary>
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>登录密码;仅在密码认证时使用,可为空。</summary>
    public string? Password { get; set; }

    /// <summary>是否记住密码(AES-256 加密落盘);为 false 时密码仅用于本次连接,不持久化。</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>私钥文件路径;仅在私钥认证时使用,可为空。</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>私钥的解锁口令;私钥未加密时可为空。</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>所属分组的标识;未分组时为 null。</summary>
    public Guid? GroupId { get; set; }

    /// <summary>最近一次成功连接的时间;从未连接时为 null。</summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>用于分类与检索的标签集合。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// 跳板主机(ProxyJump,§12 P1-2):引用另一条已保存配置作为堡垒机;
    /// 跳板配置自身还可以再配跳板,链式即多段跳。null = 直连。
    /// </summary>
    public Guid? JumpHostProfileId { get; set; }

    /// <summary>
    /// FTP / FTPS 的协议专属设置;仅在 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.FTP" /> 时有意义,其余协议为 null。
    /// </summary>
    public FtpSettings? Ftp { get; set; }

    /// <summary>
    /// 插件协议 id(如 <c>velashell.s3</c>);仅在 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.Plugin" /> 时有意义,其余协议为 null。
    /// <para>
    /// 它是这条配置与某个插件之间唯一的绑定。插件未安装/未启用时配置仍然完好保存,
    /// 只是连不上并给出「协议不可用」的提示 —— 卸载一个插件绝不该毁掉用户的连接配置。
    /// </para>
    /// </summary>
    public string? PluginProtocolId { get; set; }

    /// <summary>
    /// 插件协议的非机密设置:键为插件在 <c>ProtocolSettingField.Key</c> 里声明的字段键。
    /// <para>
    /// 刻意用字符串字典而不是给每个插件在 Core 里开一个强类型模型:Core 不认识任何具体协议,
    /// 这正是把 S3 之类的协议移出宿主的前提。
    /// </para>
    /// </summary>
    public Dictionary<string, string>? PluginSettings { get; set; }

    /// <summary>
    /// 插件协议的机密设置(声明为 <c>IsSecret</c> 的字段),整体加密落盘。
    /// <para>
    /// 与 <see cref="PluginSettings" /> 分成两个字典,而不是在一个字典里按字段标记区分:
    /// 仓储层在落盘那一刻并不知道某个协议的字段声明(那在插件里),
    /// 分开存才能做到「机密永远加密」这条不依赖任何查表的硬保证。
    /// </para>
    /// <para>
    /// 主凭据本身仍走通用字段(<see cref="Username" /> / <see cref="Password" />),
    /// 因此「记住密码」的加密落盘、登录弹窗、导入器的凭据还原对插件协议全部零改动。
    /// </para>
    /// </summary>
    public Dictionary<string, string>? PluginSecrets { get; set; }

    /// <summary>返回协议专属设置的深拷贝(两个字典各一份);源为 null 时返回 null。</summary>
    /// <param name="source">源字典。</param>
    /// <returns>深拷贝,或 null。</returns>
    public static Dictionary<string, string>? CloneSettings(Dictionary<string, string>? source) =>
        source is null ? null : new Dictionary<string, string>(source, StringComparer.Ordinal);
}

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
}

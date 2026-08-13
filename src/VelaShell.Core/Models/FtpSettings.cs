namespace VelaShell.Core.Models;

/// <summary>FTP 连接的加密方式。</summary>
public enum FtpEncryptionMode
{
    /// <summary>明文 FTP,不加密(端口通常 21)。</summary>
    None = 0,

    /// <summary>显式 FTPS:先明文连接,再用 <c>AUTH TLS</c> 升级(端口通常 21)。</summary>
    Explicit = 1,

    /// <summary>隐式 FTPS:连接即 TLS 握手(端口通常 990)。</summary>
    Implicit = 2,

    /// <summary>自动:优先尝试显式 FTPS,服务器不支持时回落明文。</summary>
    Auto = 3,
}

/// <summary>FTP 数据连接的建立方式。</summary>
public enum FtpDataConnectionMode
{
    /// <summary>被动模式(PASV/EPSV):由客户端连服务端开的数据端口,能穿过大多数客户端侧 NAT。</summary>
    Passive = 0,

    /// <summary>主动模式(PORT/EPRT):由服务端回连客户端,客户端在 NAT 后通常不可用。</summary>
    Active = 1,
}

/// <summary>
/// FTP / FTPS 会话的协议专属设置。挂在 <see cref="SessionProfile.Ftp" /> 上,
/// 缺失即 <c>null</c>(旧数据零影响),与 <see cref="SessionProfile.JumpHostProfileId" /> 的手法一致。
/// <para>
/// 刻意**不**扩展 <see cref="ConnectionInfo" />:那是 SSH 传输参数,FTP 不经 SSH 握手。
/// </para>
/// </summary>
public sealed class FtpSettings
{
    /// <summary>默认的明文 / 显式 FTPS 端口。</summary>
    public const int DefaultPort = 21;

    /// <summary>默认的隐式 FTPS 端口。</summary>
    public const int DefaultImplicitPort = 990;

    /// <summary>加密方式,默认自动(优先显式 FTPS)。</summary>
    public FtpEncryptionMode EncryptionMode { get; set; } = FtpEncryptionMode.Auto;

    /// <summary>数据连接方式,默认被动。</summary>
    public FtpDataConnectionMode DataConnectionMode { get; set; } = FtpDataConnectionMode.Passive;

    /// <summary>是否匿名登录(用户名 <c>anonymous</c>,口令用邮箱占位);为 true 时不校验用户名非空。</summary>
    public bool Anonymous { get; set; }

    /// <summary>
    /// 已信任的服务器证书指纹(SHA-256,十六进制无分隔)。用户在证书提示里选择「始终信任」后写入;
    /// 与 SSH 的 known_hosts 是两套独立信任链 —— 主机密钥那套对 X.509 不适用。
    /// </summary>
    public string? TrustedCertificateThumbprint { get; set; }

    /// <summary>
    /// 该会话允许的最大并发连接数(1 条跑元数据 + 其余跑传输)。
    /// FTP 一条控制连接同一时刻只能跑一条命令,并发传输必须靠多连接,与 SFTP 的多路复用不同。
    /// </summary>
    public int MaxConnections { get; set; } = 4;

    /// <summary>返回一份深拷贝(<see cref="SessionProfile" /> 全仓是逐字段手写拷贝,这里配套提供)。</summary>
    public FtpSettings Clone() =>
        new()
        {
            EncryptionMode = EncryptionMode,
            DataConnectionMode = DataConnectionMode,
            Anonymous = Anonymous,
            TrustedCertificateThumbprint = TrustedCertificateThumbprint,
            MaxConnections = MaxConnections,
        };
}

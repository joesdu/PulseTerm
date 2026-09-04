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

    /// <summary>
    /// 连上后远程面板默认打开的目录;null / 空 = 沿用旧行为(登录工作目录,再回退根目录)。
    /// <para>
    /// 存在的理由很朴素:上传目标常年是同一个 <c>/var/www/html</c> 或 <c>/pub/incoming</c>,
    /// 而 FTP 服务器给的登录工作目录往往就是根。每连一次手点四五层是纯粹的重复劳动。
    /// </para>
    /// <para>
    /// **配错了不该把人堵在报错页上**:它只是候选路径里排第一的那个,进不去就依次回退到
    /// 登录工作目录、根目录 —— 与家目录进不去时回退根目录是同一条纪律。
    /// </para>
    /// <para>
    /// 赋值时归一化(去首尾空白、反斜杠转正斜杠、补前导 <c>/</c>、去尾部 <c>/</c>、空串归 null):
    /// 用户会照着 Windows 的习惯敲 <c>\pub</c>,也会从别处粘一个带尾斜杠的路径进来,
    /// 而 FTP 的 <c>CWD</c> 对这些写法并不一律宽容。归一化放在 setter 上,
    /// 界面、导入器与手改的配置文件因此共用同一套规则。
    /// </para>
    /// </summary>
    public string? InitialRemotePath
    {
        get;
        set => field = NormalizeRemotePath(value);
    }

    /// <summary>
    /// 把用户输入的远程路径归一化成 <c>CWD</c> 吃得下的绝对路径;无内容时返回 null。
    /// </summary>
    /// <param name="path">原始输入,可为 null。</param>
    /// <returns>形如 <c>/var/www/html</c> 的绝对路径;根目录归一为 <c>/</c>;无内容为 null。</returns>
    public static string? NormalizeRemotePath(string? path)
    {
        string trimmed = path?.Trim().Replace('\\', '/') ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (trimmed[0] != '/')
        {
            trimmed = "/" + trimmed;
        }
        trimmed = trimmed.TrimEnd('/');
        // 全是斜杠(用户敲了 "/" 或 "///")= 根目录 = 本来就是默认行为,当作没配。
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>返回一份深拷贝(<see cref="SessionProfile" /> 全仓是逐字段手写拷贝,这里配套提供)。</summary>
    public FtpSettings Clone() =>
        new()
        {
            EncryptionMode = EncryptionMode,
            DataConnectionMode = DataConnectionMode,
            Anonymous = Anonymous,
            TrustedCertificateThumbprint = TrustedCertificateThumbprint,
            MaxConnections = MaxConnections,
            InitialRemotePath = InitialRemotePath,
        };
}

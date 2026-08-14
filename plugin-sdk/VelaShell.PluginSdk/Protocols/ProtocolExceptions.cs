namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 凭据无效(口令/密钥错误、签名不匹配)。宿主捕获后重新弹登录框并重试连接,
/// 因此**只在确实是凭据问题时**抛它 —— 拿它兜底网络错误会让用户对着登录框反复无功。
/// </summary>
public sealed class ProtocolAuthenticationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 端点不可达 / 协议层面的连接失败。宿主按普通连接错误上报,不重试。
/// </summary>
public sealed class ProtocolConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 该协议没有这个操作的对应语义(如对象存储没有 chmod)。
/// 宿主把它当作"功能不适用"呈现,而不是失败 —— 因此**不要**拿它表示"暂未实现"。
/// </summary>
public sealed class ProtocolUnsupportedException(string message)
    : Exception(message);

/// <summary>
/// 服务器证书未通过校验且尚未被信任。宿主弹出与 FTPS 同一套的信任提示,
/// 用户确认后把 <see cref="Thumbprint" /> 写进
/// <see cref="ProtocolDescriptor.TrustedThumbprintSettingKey" /> 指定的设置字段并重连。
/// <para>
/// 之所以做成"先失败 → 提示 → 重连"而不是在 TLS 回调里同步等用户点按钮:
/// 后者要把异步对话框阻塞成同步,而证书回调不保证在哪个线程上触发,极易死锁。
/// </para>
/// </summary>
/// <param name="message">面向用户的失败说明。</param>
/// <param name="subject">证书主体。</param>
/// <param name="issuer">签发者。</param>
/// <param name="expiresOn">过期时间。</param>
/// <param name="thumbprint">SHA-256 指纹(十六进制,无分隔符)。</param>
/// <param name="policyErrors">校验未通过的原因(可读文本)。</param>
public sealed class ProtocolCertificateTrustException(
    string message,
    string subject,
    string issuer,
    DateTimeOffset expiresOn,
    string thumbprint,
    string policyErrors) : Exception(message)
{
    /// <summary>证书主体。</summary>
    public string Subject { get; } = subject;

    /// <summary>签发者。</summary>
    public string Issuer { get; } = issuer;

    /// <summary>过期时间。</summary>
    public DateTimeOffset ExpiresOn { get; } = expiresOn;

    /// <summary>SHA-256 指纹(十六进制,无分隔符);用户确认信任后由宿主记进会话配置。</summary>
    public string Thumbprint { get; } = thumbprint;

    /// <summary>校验未通过的原因。</summary>
    public string PolicyErrors { get; } = policyErrors;
}

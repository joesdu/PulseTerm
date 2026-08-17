namespace VelaShell.Core.Protocols;

/// <summary>
/// 会话配置指名的插件协议当前不可用:插件未安装、被禁用、激活失败,或配置里根本没记协议 id。
/// <para>
/// 这不是"连接失败"而是"这条配置暂时无处可去"——界面据此提示用户去插件管理页,
/// 而不是让他反复重试一个永远连不上的地址。配置本身完好无损。
/// </para>
/// </summary>
/// <param name="protocolId">配置里记录的协议 id;为空表示配置缺失该字段。</param>
/// <param name="message">面向用户的说明。</param>
public sealed class PluginProtocolUnavailableException(string? protocolId, string message) : Exception(message)
{
    /// <summary>配置里记录的协议 id(可能为空)。</summary>
    public string? ProtocolId { get; } = protocolId;
}

/// <summary>插件协议报告凭据无效;界面重新弹登录框后重试。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class PluginProtocolAuthenticationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>插件协议报告端点不可达或协议层连接失败。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class PluginProtocolConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 插件协议的服务器证书未通过校验且未被信任。界面弹出与 FTPS 共用的那套信任提示,
/// 用户确认后把 <see cref="Thumbprint" /> 写进会话配置的 <see cref="SettingKey" /> 字段并重连。
/// </summary>
/// <param name="message">面向用户的失败说明。</param>
/// <param name="subject">证书主体。</param>
/// <param name="issuer">签发者。</param>
/// <param name="expiresOn">过期时间。</param>
/// <param name="thumbprint">SHA-256 指纹(十六进制,无分隔符)。</param>
/// <param name="policyErrors">校验未通过的原因。</param>
/// <param name="settingKey">指纹应写回的协议设置字段键;协议未声明时为 null(此时只能本次信任)。</param>
public sealed class PluginProtocolCertificateException(
    string message,
    string subject,
    string issuer,
    DateTimeOffset expiresOn,
    string thumbprint,
    string policyErrors,
    string? settingKey) : Exception(message)
{
    /// <summary>证书主体。</summary>
    public string Subject { get; } = subject;

    /// <summary>签发者。</summary>
    public string Issuer { get; } = issuer;

    /// <summary>过期时间。</summary>
    public DateTimeOffset ExpiresOn { get; } = expiresOn;

    /// <summary>SHA-256 指纹(十六进制,无分隔符)。</summary>
    public string Thumbprint { get; } = thumbprint;

    /// <summary>校验未通过的原因。</summary>
    public string PolicyErrors { get; } = policyErrors;

    /// <summary>指纹应写回的协议设置字段键;为 null 时无处可记,只能本次连接内信任。</summary>
    public string? SettingKey { get; } = settingKey;
}

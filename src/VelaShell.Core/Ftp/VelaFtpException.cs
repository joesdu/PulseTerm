namespace VelaShell.Core.Ftp;

// 库中立的 FTP 异常层级,与 Core/Ssh/VelaSshClientException.cs 同样的约定:
// Infrastructure 的 FluentFtpInterop 把 FluentFTP 的异常翻译成这些类型,Core/App 只依赖它们。
// 上层请**直接按类型匹配**,不要按类型名字符串(见那份文件里记录的历史教训)。

/// <summary>FTP 客户端操作失败的基类。</summary>
public class VelaFtpClientException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>无法建立或维持 FTP 控制连接(网络不可达、服务器拒绝、数据连接建立失败等)。</summary>
public class VelaFtpConnectionException(string message, Exception? innerException = null) : VelaFtpClientException(message, innerException);

/// <summary>登录失败(用户名/口令错误,或服务器不允许匿名登录)。</summary>
public class VelaFtpAuthenticationException(string message, Exception? innerException = null) : VelaFtpClientException(message, innerException);

/// <summary>FTP 命令被服务器拒绝(5xx),或本地路径/参数不合法。</summary>
public class VelaFtpOperationException(string message, Exception? innerException = null) : VelaFtpClientException(message, innerException);

/// <summary>远端路径不存在。</summary>
public class VelaFtpPathNotFoundException(string message, Exception? innerException = null) : VelaFtpOperationException(message, innerException);

/// <summary>服务器拒绝该操作(权限不足)。</summary>
public class VelaFtpPermissionDeniedException(string message, Exception? innerException = null) : VelaFtpOperationException(message, innerException);

/// <summary>
/// 服务器证书未通过校验,且用户尚未信任它。
/// <para>
/// 携带指纹与主体信息,供上层弹出信任提示;用户确认后把
/// <see cref="Thumbprint" /> 写进 <c>FtpSettings.TrustedCertificateThumbprint</c> 并重连即可。
/// </para>
/// </summary>
public sealed class VelaFtpCertificateException(
    string message,
    string thumbprint,
    string subject,
    string issuer,
    DateTimeOffset expiresOn,
    string policyErrors,
    Exception? innerException = null) : VelaFtpConnectionException(message, innerException)
{
    /// <summary>服务器证书的 SHA-256 指纹(十六进制,无分隔)。</summary>
    public string Thumbprint { get; } = thumbprint;

    /// <summary>证书主体。</summary>
    public string Subject { get; } = subject;

    /// <summary>证书颁发者。</summary>
    public string Issuer { get; } = issuer;

    /// <summary>证书到期时间。</summary>
    public DateTimeOffset ExpiresOn { get; } = expiresOn;

    /// <summary>校验未通过的原因(SslPolicyErrors 的可读形式)。</summary>
    public string PolicyErrors { get; } = policyErrors;
}

namespace VelaShell.Plugin.S3;

/// <summary>
/// S3 客户端异常族的基类。AWSSDK 的 <c>AmazonS3Exception</c> 携带一整套 SDK 内部概念
/// (重试上下文、endpoint 解析结果),不得越过 <see cref="S3Interop" /> 这条边界 ——
/// 与 FluentFTP / Tmds.Ssh 在宿主里的待遇一致。
/// <para>
/// 插件内部用这一族;在 <see cref="S3ProtocolFileSystem" /> 的出口再翻成 SDK 的
/// <c>Protocol*</c> 异常交给宿主。两段翻译看着多余,实则各有分工:这一族要区分出
/// 「没配过 / 不支持 / 权限不足 / 不存在」这些**S3 特有**的分类给桶管理器用,
/// 而宿主只关心「认证失败 / 证书不可信 / 连不上 / 不支持」这四类。
/// </para>
/// </summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public abstract class VelaS3ClientException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>凭据无效、签名不匹配或令牌过期。上层据此重新弹登录框。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3AuthenticationException(string message, Exception? innerException = null)
    : VelaS3ClientException(message, innerException);

/// <summary>
/// 端点不可达、TLS 握手失败、服务端 5xx/429,或会话已被关闭。
/// 这一类代表「会话已不可用」,上层据此把会话标记为离线并提示重试。
/// </summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3ConnectionException(string message, Exception? innerException = null)
    : VelaS3ClientException(message, innerException);

/// <summary>
/// 服务器证书未通过校验且未被信任。自建 MinIO / Ceph 用自签证书是常态,
/// 没有这条路径这类端点根本连不上;上层据此弹「是否信任该证书」,记下指纹后重连。
/// </summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="thumbprint">SHA-256 指纹(十六进制,无分隔符)。</param>
/// <param name="subject">证书主体。</param>
/// <param name="issuer">签发者。</param>
/// <param name="expiresOn">过期时间。</param>
/// <param name="policyErrors">校验未通过的原因。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3CertificateException(
    string message,
    string thumbprint,
    string subject,
    string issuer,
    DateTimeOffset expiresOn,
    string policyErrors,
    Exception? innerException = null) : VelaS3ClientException(message, innerException)
{
    /// <summary>SHA-256 指纹(十六进制,无分隔符)。</summary>
    public string Thumbprint { get; } = thumbprint;

    /// <summary>证书主体。</summary>
    public string Subject { get; } = subject;

    /// <summary>签发者。</summary>
    public string Issuer { get; } = issuer;

    /// <summary>过期时间。</summary>
    public DateTimeOffset ExpiresOn { get; } = expiresOn;

    /// <summary>校验未通过的原因。</summary>
    public string PolicyErrors { get; } = policyErrors;
}

/// <summary>
/// 一次操作被服务端拒绝。带上错误码、状态码与请求 id —— 排查 S3 兼容实现的问题时,
/// 这三样是唯一能拿去问对方运维的凭据。
/// </summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public class VelaS3OperationException(string message, Exception? innerException = null)
    : VelaS3ClientException(message, innerException)
{
    /// <summary>服务端返回的错误码(如 <c>NoSuchLifecycleConfiguration</c>);未知时为空串。</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>HTTP 状态码;未知时为 0。</summary>
    public int StatusCode { get; init; }

    /// <summary>服务端返回的请求 id;未知时为空串。</summary>
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>权限不足(<c>AccessDenied</c> / 403)。与「不存在」分开:前者要提示换账号。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3PermissionDeniedException(string message, Exception? innerException = null)
    : VelaS3OperationException(message, innerException);

/// <summary>桶/键/版本/分片上传不存在(404)。与「权限不足」分开:后者要提示刷新。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3PathNotFoundException(string message, Exception? innerException = null)
    : VelaS3OperationException(message, innerException);

/// <summary>
/// 服务端不支持该操作(<c>NotImplemented</c> / 501),或该操作在 S3 上根本没有对应语义
/// (如 chmod)。界面据此把面板灰掉并说明原因,而不是让用户以为自己配错了。
/// </summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="innerException">原始异常。</param>
public sealed class VelaS3UnsupportedOperationException(string message, Exception? innerException = null)
    : VelaS3OperationException(message, innerException);

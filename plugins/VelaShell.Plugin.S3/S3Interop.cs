using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Amazon.Runtime;
using Amazon.S3;

namespace VelaShell.Plugin.S3;

/// <summary>
/// AWSSDK 异常 → Core 的 <see cref="VelaS3ClientException" /> 族的一处翻译。
/// 与 <c>FluentFtpInterop</c> / <c>TmdsSshInterop</c> 同样的约定:
/// 具体库的异常类型不得越过 Infrastructure 边界(见 docs/架构设计.md 的分层硬规则)。
/// <para>
/// 这条边界在引入 AWSSDK 之后比以往更重要:<c>AmazonS3Exception</c> 携带一整套
/// SDK 内部概念(重试上下文、endpoint 解析结果),让它冒到 Core/UI 会把整个上层
/// 焊死在这一个客户端库上。
/// </para>
/// </summary>
internal static class S3Interop
{
    /// <summary>把 SDK / 传输层异常翻译成库中立异常;已是中立异常的原样返回。</summary>
    /// <param name="ex">原始异常。</param>
    /// <param name="operation">操作名,用于错误文案(如 <c>list objects</c>)。</param>
    /// <param name="probe">
    /// 证书探针。TLS 握手失败时 SDK 只会说一句「The SSL connection could not be established」——
    /// 把探针记下的指纹换成 <see cref="VelaS3CertificateException" />,上层才能弹「是否信任该证书」。
    /// </param>
    public static Exception Translate(Exception ex, string operation, S3CertificateProbe? probe = null)
    {
        if (probe?.Failure is { } failure && IsTlsFailure(ex))
        {
            return new VelaS3CertificateException(
                $"The server certificate is not trusted ({failure.PolicyErrors}).",
                failure.Thumbprint, failure.Subject, failure.Issuer, failure.ExpiresOn, failure.PolicyErrors, ex);
        }
        return ex switch
        {
            VelaS3ClientException or OperationCanceledException => ex,
            AmazonS3Exception s3 => FromAmazon(s3, operation),
            // 凭据本身构造失败(如 STS 令牌格式不对)。
            AmazonServiceException { StatusCode: HttpStatusCode.Forbidden } forbidden =>
                new VelaS3AuthenticationException($"S3 authentication failed: {forbidden.Message}", forbidden),
            AmazonServiceException service => new VelaS3ConnectionException($"S3 {operation} failed: {service.Message}", service),
            AmazonClientException client => new VelaS3ConnectionException($"S3 {operation} failed: {client.Message}", client),
            AuthenticationException tls => new VelaS3ConnectionException($"TLS handshake failed: {tls.Message}", tls),
            HttpRequestException http => TranslateHttp(http, operation),
            SocketException socket => new VelaS3ConnectionException($"S3 {operation} failed: {socket.Message}", socket),
            TimeoutException timeout => new VelaS3ConnectionException($"S3 {operation} timed out.", timeout),
            IOException io => new VelaS3ConnectionException($"S3 {operation} failed: {io.Message}", io),
            // 客户端释放后仍被调用(会话已关闭却有在飞的操作)会抛这两个;
            // 不翻译的话用户只会看到一句「Cannot access a disposed object」。
            ObjectDisposedException or InvalidOperationException =>
                new VelaS3ConnectionException($"The S3 session was closed during {operation}.", ex),
            _ => ex,
        };
    }

    /// <summary>该异常是否代表「会话已不可用」—— 据此把会话标记为离线。</summary>
    public static bool IsConnectionLost(Exception ex) => ex is VelaS3ConnectionException;

    /// <summary>
    /// 该失败是否只是「这个能力没配过 / 服务端不支持」,而不是真的出错。
    /// <para>
    /// 桶配置的读取普遍如此:没配生命周期规则时 AWS 回 <c>NoSuchLifecycleConfiguration</c>,
    /// 没配 CORS 回 <c>NoSuchCORSConfiguration</c>,而多数 S3 兼容实现干脆回 501/<c>NotImplemented</c>。
    /// 这些都该在界面上呈现为「未配置 / 不支持」的空状态,而不是弹一个红色错误。
    /// </para>
    /// </summary>
    public static bool IsMissingOrUnsupported(Exception ex) =>
        ex switch
        {
            VelaS3PathNotFoundException => true,
            VelaS3UnsupportedOperationException => true,
            VelaS3OperationException op =>
                op.StatusCode is 404 or 501 ||
                op.ErrorCode.StartsWith("NoSuch", StringComparison.Ordinal) ||
                op.ErrorCode is "NotImplemented" or "MethodNotAllowed" or "UnsupportedOperation"
                    or "ServerSideEncryptionConfigurationNotFoundError" or "ObjectLockConfigurationNotFoundError"
                    or "ReplicationConfigurationNotFoundError" or "NoSuchTagSet",
            _ => false,
        };

    /// <summary>把 SDK 的 <see cref="AmazonS3Exception" /> 按错误码/状态码分类。</summary>
    private static VelaS3ClientException FromAmazon(AmazonS3Exception ex, string operation)
    {
        string code = ex.ErrorCode ?? string.Empty;
        int status = (int)ex.StatusCode;
        string detail = string.IsNullOrWhiteSpace(ex.Message) ? $"HTTP {status}" : ex.Message.Trim();
        string prefix = $"S3 {operation} failed";

        return code switch
        {
            "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "TokenRefreshRequired"
                or "InvalidToken" or "InvalidSecurity" =>
                new VelaS3AuthenticationException($"S3 authentication failed: {detail}", ex),
            "AccessDenied" or "AllAccessDisabled" or "AccountProblem" =>
                new VelaS3PermissionDeniedException($"{prefix}: {detail}", ex) { ErrorCode = code, StatusCode = status, RequestId = ex.RequestId ?? string.Empty },
            "NoSuchKey" or "NoSuchBucket" or "NoSuchUpload" or "NoSuchVersion" or "NotFound" =>
                new VelaS3PathNotFoundException($"{prefix}: {detail}", ex) { ErrorCode = code, StatusCode = status, RequestId = ex.RequestId ?? string.Empty },
            "NotImplemented" or "MethodNotAllowed" or "UnsupportedOperation" =>
                new VelaS3UnsupportedOperationException($"{prefix}: {detail}", ex) { ErrorCode = code, StatusCode = status, RequestId = ex.RequestId ?? string.Empty },
            _ => FromStatus(status, code, detail, prefix, ex.RequestId ?? string.Empty, ex),
        };
    }

    private static VelaS3ClientException FromStatus(int statusCode, string code, string detail, string prefix, string requestId, Exception inner) =>
        (HttpStatusCode)statusCode switch
        {
            HttpStatusCode.Unauthorized => new VelaS3AuthenticationException($"S3 authentication failed: {detail}", inner),
            HttpStatusCode.Forbidden => new VelaS3PermissionDeniedException($"{prefix}: {detail}", inner)
            {
                ErrorCode = code,
                StatusCode = statusCode,
                RequestId = requestId,
            },
            HttpStatusCode.NotFound => new VelaS3PathNotFoundException($"{prefix}: {detail}", inner)
            {
                ErrorCode = code,
                StatusCode = statusCode,
                RequestId = requestId,
            },
            HttpStatusCode.NotImplemented => new VelaS3UnsupportedOperationException($"{prefix}: {detail}", inner)
            {
                ErrorCode = code,
                StatusCode = statusCode,
                RequestId = requestId,
            },
            // 5xx 与 429 是「服务端暂时不行」,归为连接类:上层据此把会话标记为离线并提示重试,
            // 而不是把它当成「这个对象有问题」。501 已在上面单独处理。
            HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
                new VelaS3ConnectionException($"{prefix}: {detail}", inner),
            _ => new VelaS3OperationException($"{prefix}: {detail}", inner)
            {
                ErrorCode = code,
                StatusCode = statusCode,
                RequestId = requestId,
            },
        };

    private static Exception TranslateHttp(HttpRequestException http, string operation)
    {
        for (Exception? inner = http.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException tls)
            {
                return new VelaS3ConnectionException($"TLS handshake failed: {tls.Message}", http);
            }
            if (inner is SocketException socket)
            {
                return new VelaS3ConnectionException($"S3 {operation} failed: {socket.Message}", http);
            }
        }
        return new VelaS3ConnectionException($"S3 {operation} failed: {http.Message}", http);
    }

    private static bool IsTlsFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }
        return false;
    }
}

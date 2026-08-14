using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Plugin.S3;

/// <summary>未通过校验的证书信息。</summary>
/// <param name="Thumbprint">SHA-256 指纹(十六进制,无分隔)。</param>
/// <param name="Subject">证书主体。</param>
/// <param name="Issuer">证书颁发者。</param>
/// <param name="ExpiresOn">到期时间。</param>
/// <param name="PolicyErrors">校验未通过的原因。</param>
internal sealed record S3CertificateFailure(
    string Thumbprint,
    string Subject,
    string Issuer,
    DateTimeOffset ExpiresOn,
    string PolicyErrors);

/// <summary>
/// HTTPS 端点的服务器证书校验策略:指纹已被用户信任、或链路本身无误 → 放行;
/// 否则记下证书信息并拒绝,由上层换成带指纹的 <see cref="VelaS3CertificateException" /> 抛出。
/// <para>
/// 与 FTPS 侧 <c>FtpFileService.CertificateProbe</c> 完全同一套取舍:刻意**不**在这个同步回调里
/// 弹 UI 等用户点确认 —— 那要把异步的对话框阻塞成同步,极易死锁。改成
/// 「先拒绝 → 上层提示 → 记住指纹后重连」,流程干净且不阻塞。
/// </para>
/// <para>
/// 自签证书在自建 MinIO / Ceph 上是常态,没有这条路径的话这类端点根本连不上;
/// 而无条件 <c>return true</c> 则等于把 TLS 降级成加密但不认证 —— 两者都不可接受。
/// </para>
/// </summary>
internal sealed class S3CertificateProbe(string? trustedThumbprint)
{
    /// <summary>最近一次未通过校验的证书信息;没有失败时为 null。</summary>
    public S3CertificateFailure? Failure { get; private set; }

    /// <summary>挂到 <c>SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback</c> 的回调。</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors policyErrors)
    {
        if (policyErrors == SslPolicyErrors.None)
        {
            // 握手恢复正常 ⇒ 作废上一次记下的失败。探针随会话长存,而
            // PooledConnectionLifetime 会让长会话反复重新握手 —— 不清零的话,
            // 之后任何带 AuthenticationException 的失败(服务端重启、LB 撤流)
            // 都会被 S3Interop 翻成"证书不可信"并带上一个早已作废的指纹。
            Failure = null;
            return true;
        }
        string thumbprint = ComputeThumbprint(certificate);
        if (trustedThumbprint is { Length: > 0 } trusted &&
            thumbprint.Length > 0 &&
            string.Equals(trusted, thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            Failure = null;
            return true;
        }
        Failure = new(
            thumbprint,
            certificate?.Subject ?? string.Empty,
            certificate?.Issuer ?? string.Empty,
            certificate is X509Certificate2 cert ? cert.NotAfter : DateTimeOffset.MinValue,
            policyErrors.ToString());
        return false;
    }

    private static string ComputeThumbprint(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return string.Empty;
        }
        try
        {
            return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }
}

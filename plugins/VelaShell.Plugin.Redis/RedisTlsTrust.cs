using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// TLS 证书校验的记录器与放行器。
/// <para>
/// 刻意**不在校验回调里同步等用户点按钮**:那要把异步对话框阻塞成同步,极易死锁
/// (S3 插件为同一件事写下过同样的注记)。因此回调只做两件事 ——
/// 命中已信任指纹就放行,否则把看到的证书与失败原因**记下来**并拒绝;
/// 连接随即失败,由提供方把记录组装成 <c>ProtocolCertificateTrustException</c> 交给宿主,
/// 宿主弹出与 FTPS/S3 共用的那套信任提示,用户确认后指纹落进会话配置、重连即通。
/// </para>
/// </summary>
/// <param name="trustedThumbprint">用户此前确认信任的指纹(十六进制,无分隔符);为空表示还没信任过。</param>
internal sealed class RedisTlsTrust(string? trustedThumbprint)
{
    /// <summary>最近一次校验失败时看到的证书;没失败过为 <see langword="null" />。</summary>
    public X509Certificate2? SeenCertificate { get; private set; }

    /// <summary>最近一次校验失败的原因。</summary>
    public SslPolicyErrors PolicyErrors { get; private set; }

    /// <summary>校验回调:命中已信任指纹则放行,否则记录并拒绝。</summary>
    /// <param name="sender">发起方(未使用)。</param>
    /// <param name="certificate">服务器证书。</param>
    /// <param name="chain">证书链(未使用)。</param>
    /// <param name="errors">校验错误。</param>
    /// <returns>是否接受该证书。</returns>
    public bool Validate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        if (certificate is null)
        {
            // 连证书都没拿到:没有任何可供用户判断的信息,也没有指纹可记 —— 只能拒。
            PolicyErrors = errors;
            return false;
        }
        X509Certificate2 typed = certificate as X509Certificate2
                                 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (!string.IsNullOrEmpty(trustedThumbprint)
            && string.Equals(typed.Thumbprint, trustedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            // 用户确认过这一张:按指纹固定,与 FTPS 侧同一口径。
            return true;
        }
        SeenCertificate = typed;
        PolicyErrors = errors;
        return false;
    }
}

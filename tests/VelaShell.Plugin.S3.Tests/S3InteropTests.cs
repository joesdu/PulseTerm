using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Amazon.S3;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// AWSSDK 异常 → Core 中立异常族的翻译。这条边界是分层硬规则(见 docs/架构设计.md):
/// <c>AmazonS3Exception</c> 携带一整套 SDK 内部概念,让它冒到 Core/UI 会把上层焊死在这个客户端库上。
/// <para>
/// 另一半同样重要:**「没配过」与「不支持」必须与「出错了」区分开**。S3 兼容实现普遍只实现了
/// 协议的一个子集,把它们当错误会让桶管理器一打开就一片红。
/// </para>
/// </summary>
[TestClass]
public sealed class S3InteropTests
{
    private static AmazonS3Exception Aws(string code, HttpStatusCode status) =>
        new("boom") { ErrorCode = code, StatusCode = status, RequestId = "REQ1" };

    /// <summary>凭据类错误要归为认证失败,好让上层重新弹登录框而不是提示"文件有问题"。</summary>
    [TestMethod]
    [DataRow("InvalidAccessKeyId")]
    [DataRow("SignatureDoesNotMatch")]
    [DataRow("ExpiredToken")]
    [DataRow("TokenRefreshRequired")]
    public void Translate_CredentialErrors_BecomeAuthenticationFailures(string code)
    {
        Assert.IsInstanceOfType<VelaS3AuthenticationException>(
            S3Interop.Translate(Aws(code, HttpStatusCode.Forbidden), "list"));
    }

    /// <summary>权限不足与"不存在"要分开:前者要提示换账号,后者要提示刷新。</summary>
    [TestMethod]
    public void Translate_SeparatesDeniedFromMissing()
    {
        Assert.IsInstanceOfType<VelaS3PermissionDeniedException>(
            S3Interop.Translate(Aws("AccessDenied", HttpStatusCode.Forbidden), "delete"));
        Assert.IsInstanceOfType<VelaS3PathNotFoundException>(
            S3Interop.Translate(Aws("NoSuchKey", HttpStatusCode.NotFound), "stat"));
        Assert.IsInstanceOfType<VelaS3PathNotFoundException>(
            S3Interop.Translate(Aws("NoSuchBucket", HttpStatusCode.NotFound), "list"));
    }

    /// <summary>服务端不支持某个操作要单列一类,好让界面把对应的面板灰掉而不是报错。</summary>
    [TestMethod]
    public void Translate_UnsupportedOperationsAreTheirOwnCategory()
    {
        Assert.IsInstanceOfType<VelaS3UnsupportedOperationException>(
            S3Interop.Translate(Aws("NotImplemented", HttpStatusCode.NotImplemented), "get replication"));
        Assert.IsInstanceOfType<VelaS3UnsupportedOperationException>(
            S3Interop.Translate(Aws("MethodNotAllowed", HttpStatusCode.MethodNotAllowed), "put abac"));
    }

    /// <summary>5xx 与 429 是"服务端暂时不行",归连接类,上层据此提示重试而不是说这个对象有问题。</summary>
    [TestMethod]
    public void Translate_ServerSideFailuresAreConnectionErrors()
    {
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(Aws("InternalError", HttpStatusCode.InternalServerError), "upload"));
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(Aws("SlowDown", HttpStatusCode.TooManyRequests), "upload"));
    }

    /// <summary>传输层异常不得越过 Infrastructure 边界。</summary>
    [TestMethod]
    public void Translate_WrapsTransportExceptions()
    {
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(new HttpRequestException("no route"), "connect"));
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(new SocketException(10061), "connect"));
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(new TimeoutException("slow"), "download"));
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(new AuthenticationException("tls"), "connect"));
        // 会话关闭后仍有在飞的操作:不翻译的话用户只会看到 "Cannot access a disposed object"。
        Assert.IsInstanceOfType<VelaS3ConnectionException>(
            S3Interop.Translate(new ObjectDisposedException("AmazonS3Client"), "list"));
    }

    /// <summary>取消不是错误,原样放行(否则会被上层当成连接失败提示给用户)。</summary>
    [TestMethod]
    public void Translate_PassesCancellationThrough()
    {
        var canceled = new OperationCanceledException();
        Assert.AreSame(canceled, S3Interop.Translate(canceled, "list"));
    }

    /// <summary>
    /// 自签证书的 TLS 失败要换成带指纹的专用异常,上层才能弹「是否信任该证书」。
    /// SDK 本身只会给一句「SSL connection could not be established」,没有任何可操作信息。
    /// </summary>
    [TestMethod]
    public void Translate_TlsFailureWithProbedCertificate_BecomesCertificateException()
    {
        var probe = new S3CertificateProbe(trustedThumbprint: null);
        // 走一次真实的校验回调,让探针记下"这张证书没过校验"。
        Assert.IsFalse(probe.Validate(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));

        Exception translated = S3Interop.Translate(
            new HttpRequestException("ssl", new AuthenticationException("bad cert")), "connect", probe);

        var certificate = translated as VelaS3CertificateException;
        Assert.IsNotNull(certificate);
        StringAssert.Contains(certificate!.PolicyErrors, "RemoteCertificateNameMismatch");
    }

    /// <summary>指纹已被信任时校验必须放行,否则自签的 MinIO 永远连不上。</summary>
    [TestMethod]
    public void CertificateProbe_AcceptsATrustedThumbprint()
    {
        // 没有证书对象时算不出指纹,只能拒绝 —— 但不能崩。
        var probe = new S3CertificateProbe("AABBCC");
        Assert.IsFalse(probe.Validate(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsNotNull(probe.Failure);

        // 链路本身无误时一律放行,与指纹无关。
        Assert.IsTrue(probe.Validate(this, null, null, System.Net.Security.SslPolicyErrors.None));
    }

    /// <summary>「没配过 / 不支持」的识别 —— 桶管理器的空状态全靠它。</summary>
    [TestMethod]
    public void IsMissingOrUnsupported_RecognisesEmptyStates()
    {
        Assert.IsTrue(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("NoSuchLifecycleConfiguration", HttpStatusCode.NotFound), "get lifecycle")));
        Assert.IsTrue(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("NoSuchCORSConfiguration", HttpStatusCode.NotFound), "get cors")));
        Assert.IsTrue(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("ServerSideEncryptionConfigurationNotFoundError", HttpStatusCode.NotFound), "get encryption")));
        Assert.IsTrue(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("NotImplemented", HttpStatusCode.NotImplemented), "get replication")));

        // 真正的失败不能被当成空状态吞掉。
        Assert.IsFalse(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("AccessDenied", HttpStatusCode.Forbidden), "get policy")));
        Assert.IsFalse(S3Interop.IsMissingOrUnsupported(
            S3Interop.Translate(Aws("InternalError", HttpStatusCode.InternalServerError), "get policy")));
    }
}

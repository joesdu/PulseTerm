using System.Text;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// SigV4 签名的对拍测试。
/// <para>
/// **测试向量全部取自 AWS 官方文档**(《Signature Version 4 signing process》里
/// "Examples: Signature Calculations" 那一组 S3 示例)。这些示例给出了每一步的中间结果 ——
/// 规范请求、待签串、最终签名 —— 因此这里逐段断言,而不是只对最后那个哈希。
/// 逐段断言的价值在于:一旦某天签名对不上,能直接指出是编码、排序、还是密钥派生错了,
/// 而不是只知道"签名不对"。
/// </para>
/// <para>
/// 这组测试是整个 S3 后端里**唯一无法靠代码审阅确认正确性**的部分:签名差一个字节,
/// 服务端只会回一句 <c>SignatureDoesNotMatch</c>,没有任何可循的线索。
/// </para>
/// </summary>
[TestClass]
public sealed class SigV4VerifierTests
{
    // AWS 文档示例统一使用的这对凭据(公开示例值,不是真实密钥)。
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Region = "us-east-1";
    private const string AmzDate = "20130524T000000Z";
    private const string DateStamp = "20130524";

    /// <summary>AWS 文档 "Example: GET Object" —— 带 Range 头的对象读取。</summary>
    [TestMethod]
    public void GetObject_MatchesAwsDocumentedSignature()
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = SigV4Verifier.EmptyPayloadHash,
            ["x-amz-date"] = AmzDate,
        };

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "GET", "/test.txt", string.Empty, headers, SigV4Verifier.EmptyPayloadHash, out string signedHeaders);

        Assert.AreEqual("host;range;x-amz-content-sha256;x-amz-date", signedHeaders);
        Assert.AreEqual(
            string.Join('\n',
                "GET",
                "/test.txt",
                string.Empty,
                "host:examplebucket.s3.amazonaws.com",
                "range:bytes=0-9",
                "x-amz-content-sha256:" + SigV4Verifier.EmptyPayloadHash,
                "x-amz-date:" + AmzDate,
                string.Empty,
                "host;range;x-amz-content-sha256;x-amz-date",
                SigV4Verifier.EmptyPayloadHash),
            canonicalRequest);

        string scope = SigV4Verifier.CreateScope(DateStamp, Region);
        Assert.AreEqual("20130524/us-east-1/s3/aws4_request", scope);

        string stringToSign = SigV4Verifier.CreateStringToSign(AmzDate, scope, canonicalRequest);
        Assert.AreEqual(
            string.Join('\n',
                "AWS4-HMAC-SHA256",
                AmzDate,
                scope,
                "7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972"),
            stringToSign);

        string signature = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, DateStamp, Region), stringToSign);
        Assert.AreEqual("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            SigV4Verifier.CreateAuthorizationHeader(AccessKey, scope, signedHeaders, signature));
    }

    /// <summary>AWS 文档 "Example: PUT Object" —— 带请求体、且签名头里含 date 与 x-amz-storage-class。</summary>
    [TestMethod]
    public void PutObject_MatchesAwsDocumentedSignature()
    {
        const string payloadHash = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = AmzDate,
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "PUT", "/test%24file.text", string.Empty, headers, payloadHash, out string signedHeaders);

        Assert.AreEqual("date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class", signedHeaders);

        string scope = SigV4Verifier.CreateScope(DateStamp, Region);
        string signature = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, DateStamp, Region),
            SigV4Verifier.CreateStringToSign(AmzDate, scope, canonicalRequest));
        Assert.AreEqual("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", signature);
    }

    /// <summary>AWS 文档 "Example: GET Bucket Lifecycle" —— 查询串参数参与签名的形态。</summary>
    [TestMethod]
    public void GetBucketLifecycle_MatchesAwsDocumentedSignature()
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = SigV4Verifier.EmptyPayloadHash,
            ["x-amz-date"] = AmzDate,
        };
        // 无值参数在规范查询串里写成 `name=`。
        string canonicalQuery = SigV4Verifier.CreateCanonicalQueryString([new("lifecycle", null)]);
        Assert.AreEqual("lifecycle=", canonicalQuery);

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "GET", "/", canonicalQuery, headers, SigV4Verifier.EmptyPayloadHash, out _);
        string scope = SigV4Verifier.CreateScope(DateStamp, Region);
        string signature = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, DateStamp, Region),
            SigV4Verifier.CreateStringToSign(AmzDate, scope, canonicalRequest));
        Assert.AreEqual("fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", signature);
    }

    /// <summary>AWS 文档 "Example: List Objects" —— 多个查询参数需要按名字节序排序。</summary>
    [TestMethod]
    public void ListObjects_MatchesAwsDocumentedSignature()
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = SigV4Verifier.EmptyPayloadHash,
            ["x-amz-date"] = AmzDate,
        };
        // 刻意乱序传入,验证排序确实发生在这一层。
        string canonicalQuery = SigV4Verifier.CreateCanonicalQueryString(
        [
            new("prefix", "J"),
            new("max-keys", "2"),
        ]);
        Assert.AreEqual("max-keys=2&prefix=J", canonicalQuery);

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "GET", "/", canonicalQuery, headers, SigV4Verifier.EmptyPayloadHash, out _);
        string scope = SigV4Verifier.CreateScope(DateStamp, Region);
        string signature = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, DateStamp, Region),
            SigV4Verifier.CreateStringToSign(AmzDate, scope, canonicalRequest));
        Assert.AreEqual("34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", signature);
    }

    /// <summary>AWS 文档的预签名 URL 示例(查询串签名,负载为 UNSIGNED-PAYLOAD)。</summary>
    [TestMethod]
    public void PresignedUrl_MatchesAwsDocumentedSignature()
    {
        string scope = SigV4Verifier.CreateScope(DateStamp, Region);
        string canonicalQuery = SigV4Verifier.CreateCanonicalQueryString(
        [
            new("X-Amz-Algorithm", SigV4Verifier.Algorithm),
            new("X-Amz-Credential", $"{AccessKey}/{scope}"),
            new("X-Amz-Date", AmzDate),
            new("X-Amz-Expires", "86400"),
            new("X-Amz-SignedHeaders", "host"),
        ]);
        // Credential 里的斜杠必须编码成 %2F —— 这一条错了,预签名 URL 会全线失效。
        Assert.AreEqual(
            "X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            "X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&" +
            "X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host",
            canonicalQuery);

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
        };
        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "GET", "/test.txt", canonicalQuery, headers, SigV4Verifier.UnsignedPayload, out _);
        string signature = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, DateStamp, Region),
            SigV4Verifier.CreateStringToSign(AmzDate, scope, canonicalRequest));
        Assert.AreEqual("aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", signature);
    }

    /// <summary>
    /// URI 编码规则:未保留集是 <c>A-Za-z0-9-_.~</c>,其余一律大写十六进制百分号编码,
    /// 空格编成 <c>%20</c> 而不是 <c>+</c>。
    /// </summary>
    [TestMethod]
    public void UriEncode_FollowsAwsRules()
    {
        Assert.AreEqual("abcXYZ019-_.~", SigV4Verifier.UriEncode("abcXYZ019-_.~", true));
        Assert.AreEqual("%20", SigV4Verifier.UriEncode(" ", true));
        Assert.AreEqual("a%2Fb", SigV4Verifier.UriEncode("a/b", true));
        Assert.AreEqual("a/b", SigV4Verifier.UriEncode("a/b", false));
        Assert.AreEqual("%2B%3D%26%3F%23", SigV4Verifier.UriEncode("+=&?#", true));
        // 非 ASCII 按 UTF-8 逐字节编码(中文文件名是实机里最常见的一种)。
        Assert.AreEqual("%E4%B8%AD", SigV4Verifier.UriEncode("中", true));
        Assert.AreEqual(string.Empty, SigV4Verifier.UriEncode(null, true));
    }

    /// <summary>
    /// 对象键里的 <c>/</c> 保留为分隔符,其余字符照常编码;**绝不做路径规范化** ——
    /// 键 <c>a//b</c> 与 <c>a/b</c> 在 S3 上是两个不同的对象。
    /// </summary>
    [TestMethod]
    public void EncodeObjectPath_KeepsSlashesAndDoesNotNormalize()
    {
        Assert.AreEqual("dir/sub/file%20name.txt", SigV4Verifier.EncodeObjectPath("dir/sub/file name.txt"));
        Assert.AreEqual("a//b", SigV4Verifier.EncodeObjectPath("a//b"));
        Assert.AreEqual("a/../b", SigV4Verifier.EncodeObjectPath("a/../b"));
        Assert.AreEqual("test%24file.text", SigV4Verifier.EncodeObjectPath("test$file.text"));
    }

    /// <summary>头值要去首尾空白并把内部连续空白折叠成单个空格。</summary>
    [TestMethod]
    public void CanonicalRequest_NormalizesHeaderWhitespace()
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = "  example.com  ",
            ["x-amz-meta-note"] = "a\t \tb",
        };
        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            "GET", "/", string.Empty, headers, SigV4Verifier.EmptyPayloadHash, out _);
        StringAssert.Contains(canonicalRequest, "host:example.com\n");
        StringAssert.Contains(canonicalRequest, "x-amz-meta-note:a b\n");
    }

    /// <summary>签名头名要按字节序排序(而不是按插入顺序或忽略大小写的顺序)。</summary>
    [TestMethod]
    public void CanonicalRequest_SortsHeadersByByteOrder()
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["x-amz-date"] = AmzDate,
            ["host"] = "example.com",
            ["content-type"] = "application/xml",
        };
        SigV4Verifier.CreateCanonicalRequest(
            "PUT", "/", string.Empty, headers, SigV4Verifier.EmptyPayloadHash, out string signedHeaders);
        Assert.AreEqual("content-type;host;x-amz-date", signedHeaders);
    }

    /// <summary>空负载哈希常量必须等于 SHA-256("")。</summary>
    [TestMethod]
    public void EmptyPayloadHash_IsSha256OfEmptyInput()
    {
        Assert.AreEqual(SigV4Verifier.EmptyPayloadHash, SigV4Verifier.HashHex(ReadOnlySpan<byte>.Empty));
    }

    /// <summary>负载哈希用的是真实内容的 SHA-256(以文档 PUT 示例的正文对拍)。</summary>
    [TestMethod]
    public void HashHex_MatchesAwsDocumentedPayloadHash()
    {
        Assert.AreEqual(
            "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072",
            SigV4Verifier.HashHex(Encoding.UTF8.GetBytes("Welcome to Amazon S3.")));
    }
}

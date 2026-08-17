using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// AWS Signature Version 4(<c>AWS4-HMAC-SHA256</c>)的**校验用**实现。**这是测试基建,不是产品代码。**
/// <para>
/// 产品侧的签名由 AWSSDK.S3 负责(见 <c>docs/S3协议支持设计.md §2</c>)。这里保留一份独立实现,
/// 是为了让 <see cref="LoopbackS3Server" /> 能按收到的**原始请求行**重算签名并比对 ——
/// 那是端到端测试里唯一能证明「客户端配置(端点/区域/寻址方式/凭据)真的被正确送上线」的手段:
/// 这些配错时,真实服务端只会回一句 <c>SignatureDoesNotMatch</c>,毫无线索。
/// </para>
/// <para>
/// 因为它自己就是判据,所以它自己也必须被验证:<see cref="SigV4VerifierTests" /> 拿 AWS 官方文档的
/// 示例对这份实现逐段对拍(规范请求 / 待签串 / 签名),否则一个错误的校验器会把什么都放行。
/// </para>
/// </summary>
internal static class SigV4Verifier
{
    /// <summary>签名算法标识。</summary>
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// 「不签名负载」占位符。预签名 URL 必须用它(签名时还没有请求体),
    /// 大文件的流式上传也可以用它换取「不必为算哈希把文件读两遍」。
    /// </summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>空负载的 SHA-256(十六进制小写);GET/HEAD/DELETE 这类无体请求用它。</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>S3 的服务名(SigV4 credential scope 的第三段)。</summary>
    public const string ServiceName = "s3";

    private const string Terminator = "aws4_request";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary><c>x-amz-date</c> 的格式:<c>yyyyMMddTHHmmssZ</c>(UTC)。</summary>
    public static string FormatAmzDate(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>credential scope 第一段的日期戳:<c>yyyyMMdd</c>(UTC)。</summary>
    public static string FormatDateStamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// AWS 规定的 URI 编码:未保留字符集为 <c>A-Za-z0-9-_.~</c>,其余一律 <c>%XY</c>(大写十六进制),
    /// 空格必须编成 <c>%20</c> 而**不是** <c>+</c>。
    /// <para>
    /// 刻意手写而不用 <see cref="Uri.EscapeDataString(string)" />:后者的未保留集虽然当前与 AWS 一致,
    /// 但那是 BCL 的实现细节(历史上变过),签名算法不该押在它上面。
    /// </para>
    /// </summary>
    /// <param name="value">待编码的原文。</param>
    /// <param name="encodeSlash">
    /// 是否连 <c>/</c> 一起编码。规范 URI(路径)传 false —— 分隔符要保持原样;
    /// 查询串的名与值传 true。
    /// </param>
    public static string UriEncode(string? value, bool encodeSlash)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(value.Length + 16);
        foreach (byte b in Utf8.GetBytes(value))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                builder.Append('/');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 把对象键编成规范 URI 的路径部分。
    /// <para>
    /// S3 的键里 <c>/</c> 只是个普通字符,但既然目录树按它分段,这里就按分隔符保留
    /// (即 <paramref name="key" /> 的每一段各自编码)。**绝不做路径规范化** ——
    /// AWS 明确要求 S3 请求不得归一化路径:键 <c>a//b</c> 与 <c>a/b</c> 是两个不同的对象。
    /// </para>
    /// </summary>
    public static string EncodeObjectPath(string? key) => UriEncode(key, false);

    /// <summary>
    /// 生成规范查询串:按**编码后**的参数名做字节序排序(同名再按编码后的值排),
    /// 以 <c>name=value</c> 拼接,无值的参数写成 <c>name=</c>。
    /// </summary>
    public static string CreateCanonicalQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        List<(string Name, string Value)> encoded =
        [
            .. parameters.Select(p => (Name: UriEncode(p.Key, true), Value: UriEncode(p.Value, true)))
        ];
        encoded.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });
        return string.Join('&', encoded.Select(static p => $"{p.Name}={p.Value}"));
    }

    /// <summary>
    /// 生成规范请求。<paramref name="headers" /> 的键必须已是小写;
    /// 本方法负责排序、折叠值里的连续空白并产出 <paramref name="signedHeaders" />。
    /// </summary>
    public static string CreateCanonicalRequest(
        string method,
        string canonicalUri,
        string canonicalQueryString,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        out string signedHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);
        string[] names = [.. headers.Keys.Select(static k => k.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
        Array.Sort(names, StringComparer.Ordinal);
        signedHeaders = string.Join(';', names);

        var builder = new StringBuilder(256);
        builder.Append(method).Append('\n');
        builder.Append(string.IsNullOrEmpty(canonicalUri) ? "/" : canonicalUri).Append('\n');
        builder.Append(canonicalQueryString).Append('\n');
        foreach (string name in names)
        {
            builder.Append(name).Append(':').Append(NormalizeHeaderValue(headers[name])).Append('\n');
        }
        builder.Append('\n');
        builder.Append(signedHeaders).Append('\n');
        builder.Append(payloadHash);
        return builder.ToString();
    }

    /// <summary>credential scope:<c>yyyyMMdd/region/s3/aws4_request</c>。</summary>
    public static string CreateScope(string dateStamp, string region, string service = ServiceName) =>
        $"{dateStamp}/{region}/{service}/{Terminator}";

    /// <summary>生成待签字符串。</summary>
    public static string CreateStringToSign(string amzDate, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{amzDate}\n{scope}\n{ToHex(SHA256.HashData(Utf8.GetBytes(canonicalRequest)))}";

    /// <summary>逐级派生签名密钥:<c>kDate → kRegion → kService → kSigning</c>。</summary>
    public static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region, string service = ServiceName)
    {
        byte[] key = Utf8.GetBytes("AWS4" + secretAccessKey);
        byte[] date = HmacSha256(key, dateStamp);
        byte[] regionKey = HmacSha256(date, region);
        byte[] serviceKey = HmacSha256(regionKey, service);
        return HmacSha256(serviceKey, Terminator);
    }

    /// <summary>用派生出的签名密钥对待签串做 HMAC,输出十六进制小写签名。</summary>
    public static string CalculateSignature(byte[] signingKey, string stringToSign) =>
        ToHex(HmacSha256(signingKey, stringToSign));

    /// <summary>
    /// 组装 <c>Authorization</c> 头的值。
    /// <para>逗号后的那个空格是 AWS 文档里的写法,服务端对此宽容,但保持一致省得排查时起疑。</para>
    /// </summary>
    public static string CreateAuthorizationHeader(string accessKeyId, string scope, string signedHeaders, string signature) =>
        $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

    /// <summary>SHA-256 十六进制小写摘要。</summary>
    public static string HashHex(ReadOnlySpan<byte> payload) => ToHex(SHA256.HashData(payload));

    /// <summary>
    /// 头值规范化:去掉首尾空白,并把内部连续空白折叠成单个空格。
    /// (AWS 对引号内的空白另有豁免,但 S3 请求里我们从不发带引号的头,不实现那条分支。)
    /// </summary>
    private static string NormalizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
        var builder = new StringBuilder(trimmed.Length);
        bool previousSpace = false;
        foreach (char c in trimmed)
        {
            bool isSpace = c is ' ' or '\t';
            if (isSpace)
            {
                if (!previousSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(c);
            }
            previousSpace = isSpace;
        }
        return builder.ToString();
    }

    private static byte[] HmacSha256(byte[] key, string data) => HMACSHA256.HashData(key, Utf8.GetBytes(data));

    private static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}

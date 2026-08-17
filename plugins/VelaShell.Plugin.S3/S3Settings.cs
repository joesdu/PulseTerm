using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.S3;

/// <summary>S3 请求的寻址方式(桶名放在主机名里还是路径里)。</summary>
public enum S3AddressingStyle
{
    /// <summary>
    /// 自动:只有 AWS 自家端点走虚拟主机式,其余一律路径式。
    /// <para>
    /// 判据刻意保守 —— 自建 MinIO / Ceph RGW 默认根本不解析 <c>bucket.host</c>
    /// (要额外配 <c>MINIO_DOMAIN</c>),而路径式在 AWS 与它们身上都能工作。
    /// 含点的桶名也刻意不走虚拟主机式:<c>my.bucket.s3.amazonaws.com</c> 的通配证书
    /// <c>*.s3.amazonaws.com</c> 只覆盖一级标签,TLS 校验会失败。
    /// </para>
    /// </summary>
    Auto = 0,

    /// <summary>始终路径式:<c>https://endpoint/bucket/key</c>。MinIO / Ceph 等自建服务的常用形态。</summary>
    Path = 1,

    /// <summary>始终虚拟主机式:<c>https://bucket.endpoint/key</c>。AWS S3 的现行推荐形态。</summary>
    VirtualHosted = 2,
}

/// <summary>
/// S3 兼容对象存储的协议专属设置。取值来自宿主连接配置页按
/// <see cref="S3ProtocolFields" /> 渲染出的表单,经
/// <see cref="ProtocolConnectRequest.Settings" /> 原样送达。
/// <para>
/// 刻意不复用宿主的任何设置模型:S3 既不经 SSH 握手,也没有 FTP 的控制/数据连接概念 ——
/// 它是一套 HTTP REST + SigV4 签名的协议,参数集(端点、区域、寻址方式、分片大小)
/// 与两者都不重叠。宿主对这些字段一无所知,这正是它能作为插件存在的前提。
/// </para>
/// </summary>
public sealed class S3Settings
{
    /// <summary>默认 HTTPS 端口。</summary>
    public const int DefaultPort = 443;

    /// <summary>默认 HTTP 端口(自建 MinIO 常见的明文端口)。</summary>
    public const int DefaultHttpPort = 80;

    /// <summary>未指定区域时使用的区域名;AWS 之外的实现基本都接受它。</summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>分片上传的默认分片大小(8 MiB)。</summary>
    public const long DefaultPartSizeBytes = 8L * 1024 * 1024;

    /// <summary>S3 协议允许的最小分片大小(5 MiB);最后一片可以更小。</summary>
    public const long MinPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>S3 协议允许的最大分片大小(5 GiB)。</summary>
    public const long MaxPartSizeBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>单次 PUT 允许的最大对象大小(5 GiB);超过必须走分片上传。</summary>
    public const long MaxSinglePutBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>分片上传允许的最大分片数。</summary>
    public const int MaxPartCount = 10_000;

    /// <summary>
    /// 服务端点主机名(如 <c>s3.amazonaws.com</c>、<c>minio.internal</c>);
    /// 留空时由连接配置的「主机」字段充当。
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>是否使用 HTTPS;默认开。关掉即明文 HTTP,凭据虽仍经签名保护,但内容会裸奔。</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>签名用的区域名(SigV4 的 credential scope 第二段)。</summary>
    public string Region { get; set; } = DefaultRegion;

    /// <summary>寻址方式;默认自动。</summary>
    public S3AddressingStyle AddressingStyle { get; set; } = S3AddressingStyle.Auto;

    /// <summary>
    /// 临时凭据的会话令牌(STS / AssumeRole 场景),随请求以
    /// <c>x-amz-security-token</c> 头发送;长期凭据留空。
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// 默认桶。填了它,会话打开后直接落在该桶内而不是桶列表;
    /// 也是**只授予单桶权限**的账号唯一能用的形态 —— 那种账号调 ListBuckets 会被拒。
    /// </summary>
    public string? DefaultBucket { get; set; }

    /// <summary>分片上传的分片大小(字节);会被夹在 <see cref="MinPartSizeBytes" />…<see cref="MaxPartSizeBytes" /> 之间。</summary>
    public long PartSizeBytes { get; set; } = DefaultPartSizeBytes;

    /// <summary>
    /// 单个文件内并发上传的分片数。S3 是 HTTP,天然可并发,
    /// 与 FTP 那种「一条控制连接同时只能跑一条命令」的限制无关。
    /// </summary>
    public int MaxConcurrentParts { get; set; } = 4;

    /// <summary>
    /// 已信任的服务器证书指纹(SHA-256,十六进制无分隔)。自建 MinIO 常用自签证书,
    /// 与 FTPS 侧同一套「先拒绝 → 提示 → 记指纹」流程,由宿主写回。
    /// </summary>
    public string? TrustedCertificateThumbprint { get; set; }

    /// <summary>
    /// 新建对象的存储类别(如 <c>STANDARD</c>、<c>STANDARD_IA</c>、<c>GLACIER</c>);
    /// 留空即不发 <c>x-amz-storage-class</c> 头,由服务端取默认值。
    /// </summary>
    public string? StorageClass { get; set; }

    /// <summary>
    /// 服务端加密算法(如 <c>AES256</c>、<c>aws:kms</c>);留空即不发
    /// <c>x-amz-server-side-encryption</c> 头。
    /// </summary>
    public string? ServerSideEncryption { get; set; }

    /// <summary>
    /// 列举时是否把「以 / 结尾的零字节对象」也当成普通文件显示。
    /// 默认关:那些是各家工具造出来的目录占位符,显示出来只会让文件列表里多出一堆与目录同名的空文件。
    /// </summary>
    public bool ShowFolderMarkers { get; set; }

    /// <summary>把分片大小夹到协议允许的区间内。</summary>
    public long EffectivePartSize => Math.Clamp(PartSizeBytes <= 0 ? DefaultPartSizeBytes : PartSizeBytes, MinPartSizeBytes, MaxPartSizeBytes);

    /// <summary>把并发分片数夹到 1…16。</summary>
    public int EffectiveConcurrency => Math.Clamp(MaxConcurrentParts, 1, 16);

    /// <summary>解析出实际使用的区域名(留空时回落 <see cref="DefaultRegion" />)。</summary>
    public string EffectiveRegion => string.IsNullOrWhiteSpace(Region) ? DefaultRegion : Region.Trim();

    /// <summary>从宿主送来的设置字典还原一份设置。</summary>
    /// <param name="request">宿主的连接请求。</param>
    /// <returns>设置对象。</returns>
    public static S3Settings FromRequest(ProtocolConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new()
        {
            Region = request.GetString(S3ProtocolFields.Region, DefaultRegion),
            UseTls = request.GetBoolean(S3ProtocolFields.UseTls, true),
            AddressingStyle = request.GetString(S3ProtocolFields.Addressing, "auto") switch
            {
                "path" => S3AddressingStyle.Path,
                "virtual" => S3AddressingStyle.VirtualHosted,
                _ => S3AddressingStyle.Auto,
            },
            DefaultBucket = NullIfBlank(request.GetString(S3ProtocolFields.DefaultBucket)),
            SessionToken = NullIfBlank(request.GetString(S3ProtocolFields.SessionToken)),
            PartSizeBytes = request.GetInt64(S3ProtocolFields.PartSize, DefaultPartSizeBytes),
            MaxConcurrentParts = request.GetInt32(S3ProtocolFields.Concurrency, 4),
            TrustedCertificateThumbprint = NullIfBlank(request.GetString(S3ProtocolFields.TrustedThumbprint)),
            StorageClass = NullIfBlank(request.GetString(S3ProtocolFields.StorageClass)),
            ServerSideEncryption = NullIfBlank(request.GetString(S3ProtocolFields.ServerSideEncryption)),
            ShowFolderMarkers = request.GetBoolean(S3ProtocolFields.ShowFolderMarkers),
        };
    }

    /// <summary>返回一份深拷贝。</summary>
    /// <returns>深拷贝。</returns>
    public S3Settings Clone() =>
        new()
        {
            Endpoint = Endpoint,
            UseTls = UseTls,
            Region = Region,
            AddressingStyle = AddressingStyle,
            SessionToken = SessionToken,
            DefaultBucket = DefaultBucket,
            PartSizeBytes = PartSizeBytes,
            MaxConcurrentParts = MaxConcurrentParts,
            TrustedCertificateThumbprint = TrustedCertificateThumbprint,
            StorageClass = StorageClass,
            ServerSideEncryption = ServerSideEncryption,
            ShowFolderMarkers = ShowFolderMarkers,
        };

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 协议设置的字段键。集中成常量而不是各处写字符串字面量:这些键会**落进用户的会话配置**,
/// 改一个就等于让老配置的那一项失联,必须一眼看得见全集。
/// </summary>
public static class S3ProtocolFields
{
    /// <summary>签名区域。</summary>
    public const string Region = "region";

    /// <summary>寻址方式(<c>auto</c> / <c>path</c> / <c>virtual</c>)。</summary>
    public const string Addressing = "addressing";

    /// <summary>是否使用 HTTPS。</summary>
    public const string UseTls = "useTls";

    /// <summary>默认桶。</summary>
    public const string DefaultBucket = "defaultBucket";

    /// <summary>STS 会话令牌(机密)。</summary>
    public const string SessionToken = "sessionToken";

    /// <summary>分片大小(字节)。</summary>
    public const string PartSize = "partSize";

    /// <summary>单文件内并发分片数。</summary>
    public const string Concurrency = "concurrency";

    /// <summary>新建对象的存储类别。</summary>
    public const string StorageClass = "storageClass";

    /// <summary>服务端加密算法。</summary>
    public const string ServerSideEncryption = "sse";

    /// <summary>是否显示目录占位符对象。</summary>
    public const string ShowFolderMarkers = "showFolderMarkers";

    /// <summary>已信任的服务器证书指纹(隐藏字段,由宿主在用户确认信任后写回)。</summary>
    public const string TrustedThumbprint = "trustedThumbprint";
}

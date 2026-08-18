using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 由一份 <see cref="S3ConnectionInfo" /> 构造出配置正确的 <see cref="AmazonS3Client" />。
/// <para>
/// 这层薄工厂承担的是**兼容性**:AWSSDK 的默认值是按 AWS 自家服务调的,
/// 直接拿去连 MinIO / Ceph RGW / R2 会在几个地方翻车(见各处注释)。
/// 把这些取舍集中在一个地方,比散落在每个调用点更容易维护。
/// </para>
/// </summary>
internal static class S3ClientFactory
{
    /// <summary>按连接参数创建客户端;<paramref name="probe" /> 用于自签证书的信任判定。</summary>
    public static AmazonS3Client Create(S3ConnectionInfo info, S3CertificateProbe probe)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(probe);

        var config = new AmazonS3Config
        {
            ServiceURL = info.BaseUri.ToString(),
            // 区域只用于签名。端点是自定义的,不能让 SDK 去解析 RegionEndpoint,
            // 否则它会把请求打到 AWS 的真实域名上。
            AuthenticationRegion = info.Settings.EffectiveRegion,
            UseHttp = !info.Settings.UseTls,
            ForcePathStyle = UsePathStyle(info),
            Timeout = TimeSpan.FromMinutes(30),
            // 大文件传输由 TransferUtility 自己分片重试;这里再叠一层重试只会放大失败时的等待。
            MaxErrorRetry = 3,
            RetryMode = RequestRetryMode.Standard,
            HttpClientFactory = new ProbingHttpClientFactory(probe, info.Settings.UseTls),
        };

        // AWSSDK v4 默认对每个请求计算 CRC32 并要求响应回带校验和。这是 AWS 的新行为,
        // 而相当一部分 S3 兼容实现(较旧的 MinIO / Ceph RGW / 各类网关)不认这些头,
        // 表现为上传直接被拒或下载报校验失败。改成"仅在协议要求时"计算,
        // 既保留 DeleteObjects 这类必须带校验和的场景,又不会把兼容实现挡在门外。
        config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
        config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;

        return new(CreateCredentials(info), config);
    }

    /// <summary>按凭据形态选择:匿名(公开只读桶)/ 临时凭据(STS)/ 长期密钥。</summary>
    private static AWSCredentials CreateCredentials(S3ConnectionInfo info)
    {
        if (info.IsAnonymous)
        {
            return new AnonymousAWSCredentials();
        }
        return info.Settings.SessionToken is { Length: > 0 } token
            ? new SessionAWSCredentials(info.AccessKeyId, info.SecretAccessKey, token)
            : new BasicAWSCredentials(info.AccessKeyId, info.SecretAccessKey);
    }

    /// <summary>
    /// 是否使用路径式寻址。
    /// <para>
    /// <see cref="S3AddressingStyle.Auto" /> 的判据刻意保守:**只有 AWS 自家端点才用虚拟主机式**,
    /// 其余一律路径式。自建 MinIO / Ceph RGW 默认根本不解析 <c>bucket.host</c> 这种形态
    /// (要额外配 <c>MINIO_DOMAIN</c>),而路径式在 AWS 与它们身上都能工作 ——
    /// 把"都能用"的那个作为默认,最常见的自建场景才不会一上来就连不上。
    /// </para>
    /// </summary>
    private static bool UsePathStyle(S3ConnectionInfo info) =>
        info.Settings.AddressingStyle switch
        {
            S3AddressingStyle.Path => true,
            S3AddressingStyle.VirtualHosted => false,
            _ => !IsAwsEndpoint(info.Endpoint),
        };

    private static bool IsAwsEndpoint(string endpoint) =>
        endpoint.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
        endpoint.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 让 SDK 用我们自己的 <see cref="HttpClient" />,以便挂上证书信任回调
    /// —— 自签证书在自建 MinIO / Ceph 上是常态,没有这条路径这类端点根本连不上。
    /// </summary>
    private sealed class ProbingHttpClientFactory(S3CertificateProbe probe, bool useTls) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new SocketsHttpHandler
            {
                // 区域重定向要由 SDK 自己按 x-amz-bucket-region 重签重发;
                // 自动跟随会带着旧区域的签名去请求新地址,必然 SignatureDoesNotMatch。
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };
            if (useTls)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = probe.Validate;
            }
            return new(handler, disposeHandler: true)
            {
                // 单次请求超时由 SDK 的 Timeout / 调用方的 CancellationToken 控制:
                // 大文件传输可能跑几十分钟,HttpClient 的全局超时会把它们一刀切掉。
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>
        /// **让 SDK 按客户端缓存**。这三个 override 必须一起看:
        /// <c>UseSDKHttpClientCaching=false</c> + <c>DisposeHttpClientsAfterUse=false</c> 是
        /// AWSSDK 文档明确点名不该出现的组合 —— 没人缓存也没人释放,于是每个请求都新建一个
        /// <see cref="HttpClient" /> 与 <see cref="SocketsHttpHandler" /> 并永久泄漏,
        /// 连接池彻底失效、每请求重做一次 TCP + TLS。
        /// <para>
        /// 缓存打开后仍然满足"每条会话一份":<see cref="GetConfigUniqueString" /> 返回 null 表示
        /// 按 SDK 客户端实例缓存,而每条会话本就各有一个 <c>AmazonS3Client</c>,
        /// 客户端释放时 HttpClient 一并回收。证书信任指纹因此不会跨会话串味。
        /// </para>
        /// </summary>
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => true;

        /// <summary>由缓存持有并随 SDK 客户端释放,不在单次请求后释放。</summary>
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

        /// <summary>返回 null = 不做跨客户端共享,每个 <c>AmazonS3Client</c> 独占一份。</summary>
        public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
    }
}

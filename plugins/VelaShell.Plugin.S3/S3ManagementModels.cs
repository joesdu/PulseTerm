namespace VelaShell.Plugin.S3;

/// <summary>
/// 一次桶配置读取的结果。
/// <para>
/// 三态而不是「成功/失败」两态,是这套桶管理器可用的关键:AWS 对每种**未配置**的能力
/// 都有专属错误码(<c>NoSuchLifecycleConfiguration</c>、<c>NoSuchCORSConfiguration</c>…),
/// 而多数 S3 兼容实现干脆回 501。把这两类当成错误,桶管理器一打开就是一片红。
/// </para>
/// </summary>
/// <param name="Kind">配置种类。</param>
/// <param name="Exists">是否配置过。</param>
/// <param name="Supported">服务端是否支持这项配置。</param>
/// <param name="Json">配置文档(未配置/不支持时为空串)。</param>
/// <param name="Message">不支持时的原因说明。</param>
public sealed record S3ConfigResult(
    S3ConfigKind Kind,
    bool Exists,
    bool Supported,
    string Json,
    string Message = "")
{
    /// <summary>服务端支持但从未配置过 —— 空状态,不是错误。</summary>
    /// <param name="kind">配置种类。</param>
    /// <returns>结果。</returns>
    public static S3ConfigResult NotConfigured(S3ConfigKind kind) =>
        new(kind, Exists: false, Supported: true, Json: string.Empty);

    /// <summary>服务端不支持这项配置 —— 同样是空状态,但界面要说明原因。</summary>
    /// <param name="kind">配置种类。</param>
    /// <param name="message">原因。</param>
    /// <returns>结果。</returns>
    public static S3ConfigResult NotSupported(S3ConfigKind kind, string message) =>
        new(kind, Exists: false, Supported: false, Json: string.Empty, message);

    /// <summary>读到了一份配置。</summary>
    /// <param name="kind">配置种类。</param>
    /// <param name="json">配置文档。</param>
    /// <returns>结果。</returns>
    public static S3ConfigResult FromJson(S3ConfigKind kind, string json) =>
        new(kind, Exists: true, Supported: true, json);
}

/// <summary>桶的概览信息(区域、公开状态、版本控制与对象锁定)。</summary>
/// <param name="Name">桶名。</param>
/// <param name="Region">桶所在区域;不支持 <c>GetBucketLocation</c> 的实现沿用会话配置的区域。</param>
/// <param name="CreatedAt">创建时间;<c>ListBuckets</c> 之外拿不到时为 <see cref="DateTime.MinValue" />。</param>
/// <param name="IsPublic">
/// 桶策略是否使其成为公开桶。**三态**:<see langword="null" /> = 问不出来
/// (不支持 <c>GetBucketPolicyStatus</c>),不能跟"不公开"混为一谈。
/// </param>
/// <param name="VersioningStatus">版本控制状态(<c>Enabled</c> / <c>Suspended</c> / <c>Off</c>)。</param>
/// <param name="ObjectLockEnabled">是否启用了对象锁定。</param>
public sealed record S3BucketOverview(
    string Name,
    string Region,
    DateTime CreatedAt,
    bool? IsPublic,
    string VersioningStatus,
    bool ObjectLockEnabled);

/// <summary>一个对象版本。</summary>
/// <param name="Key">对象键。</param>
/// <param name="VersionId">版本 id;未开版本控制时可能为 <c>null</c>。</param>
/// <param name="IsLatest">是否为当前版本。</param>
/// <param name="IsDeleteMarker">是否为删除标记(删除标记没有内容,只是遮住了下面的版本)。</param>
/// <param name="Size">大小(字节)。</param>
/// <param name="LastModified">最后修改时间。</param>
/// <param name="ETag">实体标签(已去掉引号)。</param>
/// <param name="StorageClass">存储类别。</param>
public sealed record S3ObjectVersion(
    string Key,
    string? VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    long Size,
    DateTime LastModified,
    string ETag,
    string StorageClass);

/// <summary>一次未完成的分片上传。列出来是为了能中止它 —— 未中止的分片会一直计费。</summary>
/// <param name="Key">目标对象键。</param>
/// <param name="UploadId">分片上传 id。</param>
/// <param name="Initiated">发起时间。</param>
/// <param name="StorageClass">存储类别。</param>
/// <param name="OwnerDisplayName">发起者。</param>
public sealed record S3MultipartUpload(
    string Key,
    string UploadId,
    DateTime Initiated,
    string StorageClass,
    string OwnerDisplayName);

/// <summary>一条键值对(对象标签与自定义元数据共用)。</summary>
/// <param name="Key">键。</param>
/// <param name="Value">值。</param>
public sealed record S3Tag(string Key, string Value);

/// <summary>对象的详细属性。</summary>
/// <param name="Key">对象键。</param>
/// <param name="Size">大小(字节)。</param>
/// <param name="LastModified">最后修改时间。</param>
/// <param name="ETag">实体标签(已去掉引号)。</param>
/// <param name="VersionId">版本 id。</param>
/// <param name="StorageClass">存储类别。</param>
/// <param name="ContentType">内容类型。</param>
/// <param name="ServerSideEncryption">服务端加密算法。</param>
/// <param name="KmsKeyId">KMS 密钥 id(<c>aws:kms</c> 时)。</param>
/// <param name="Checksum">校验和(格式为 <c>算法:值</c>);服务端未提供时为空串。</param>
/// <param name="PartCount">分片数;非分片上传的对象为 0。</param>
/// <param name="RestoreStatus">归档取回状态(进行中 / 已就绪并注明到期时间 / 空)。</param>
/// <param name="ExpiresOn">生命周期规则给出的过期时间。</param>
/// <param name="Metadata">自定义元数据(<c>x-amz-meta-*</c>)。</param>
public sealed record S3ObjectDetails(
    string Key,
    long Size,
    DateTime LastModified,
    string ETag,
    string VersionId,
    string StorageClass,
    string ContentType,
    string ServerSideEncryption,
    string KmsKeyId,
    string Checksum,
    int PartCount,
    string RestoreStatus,
    DateTime? ExpiresOn,
    IReadOnlyList<S3Tag> Metadata);

/// <summary>对象的保留策略。<see cref="Mode" /> 为空串表示没设过。</summary>
/// <param name="Mode">保留模式(<c>GOVERNANCE</c> / <c>COMPLIANCE</c>);未设置为空串。</param>
/// <param name="RetainUntil">保留到期时间。</param>
public sealed record S3Retention(string Mode, DateTime? RetainUntil);

/// <summary>归档取回请求(Glacier 系存储类别的对象要先取回才能下载)。</summary>
/// <param name="Days">取回后的可下载天数。</param>
/// <param name="Tier">取回档位(<c>Expedited</c> / <c>Standard</c> / <c>Bulk</c>);留空用服务端默认。</param>
public sealed record S3RestoreRequest(int Days, string? Tier = null);

/// <summary>一次 S3 Select 查询。</summary>
/// <param name="Expression">SQL 表达式。</param>
/// <param name="InputFormat">输入格式(<c>CSV</c> / <c>JSON</c> / <c>PARQUET</c>)。</param>
/// <param name="OutputFormat">输出格式(<c>CSV</c> / <c>JSON</c>)。</param>
/// <param name="CompressionType">输入压缩方式(<c>NONE</c> / <c>GZIP</c> / <c>BZIP2</c>)。</param>
/// <param name="CsvHasHeader">CSV 输入是否带表头行。</param>
public sealed record S3SelectRequest(
    string Expression,
    string InputFormat = "CSV",
    string OutputFormat = "CSV",
    string CompressionType = "NONE",
    bool CsvHasHeader = true);

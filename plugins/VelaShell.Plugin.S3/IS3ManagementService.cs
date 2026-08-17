namespace VelaShell.Plugin.S3;

/// <summary>
/// S3 协议里「文件管理器」之外的那近百个操作:桶配置、对象版本、ACL、对象锁定、标签、
/// 归档取回、分片上传管理与 S3 Select。与文件服务共用同一条会话和同一个客户端。
/// <para>
/// 桶配置刻意收敛成一组三元方法而不是为每种配置各开三个成员 —— 那会让接口膨胀到
/// 七十多个。载荷用 JSON 字符串而不是为每种配置在这里造 DTO:后者要上千行搬运代码,
/// 且**漏掉的字段会在写回时被静默清空**(读进来渲染不出的字段,序列化回去就没了),
/// 那是真的改坏用户的生产配置。详见 docs/S3协议插件化设计.md。
/// </para>
/// </summary>
public interface IS3ManagementService
{
    // ---- 桶配置(二十余种,同一组方法) ----

    /// <summary>读取一项桶配置。「没配过」与「不支持」都返回空状态而不是抛异常。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="kind">配置种类。</param>
    /// <param name="id">按 id 分多份的配置(清单/分析/指标/智能分层)的 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取结果。</returns>
    Task<S3ConfigResult> GetBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string? id = null, CancellationToken cancellationToken = default);

    /// <summary>写回一项桶配置。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="kind">配置种类。</param>
    /// <param name="json">配置文档。</param>
    /// <param name="id">按 id 分多份的配置的 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string json, string? id = null, CancellationToken cancellationToken = default);

    /// <summary>删除一项桶配置(仅协议提供了 <c>DeleteBucketXxx</c> 的那些)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="kind">配置种类。</param>
    /// <param name="id">按 id 分多份的配置的 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string? id = null, CancellationToken cancellationToken = default);

    /// <summary>列出某种「按 id 分多份」的配置的全部 id;其余种类返回空列表。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="kind">配置种类。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置 id 列表。</returns>
    Task<IReadOnlyList<string>> ListBucketConfigIdsAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, CancellationToken cancellationToken = default);

    /// <summary>桶概览(区域、公开状态、版本控制、对象锁定)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>概览。</returns>
    Task<S3BucketOverview> GetBucketOverviewAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default);

    // ---- 对象版本 ----

    /// <summary>列出某个键或前缀下的对象版本(含删除标记)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="keyOrPrefix">对象键或前缀。</param>
    /// <param name="maxKeys">最多返回多少条。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>版本列表。</returns>
    Task<IReadOnlyList<S3ObjectVersion>> ListObjectVersionsAsync(
        Guid sessionId, string bucket, string keyOrPrefix, int maxKeys = 1000, CancellationToken cancellationToken = default);

    /// <summary>永久删除一个指定版本(不可恢复)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteObjectVersionAsync(Guid sessionId, string bucket, string key, string versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个历史版本恢复为当前版本。**以复制实现** —— S3 没有"回滚"操作,
    /// 协议规定的做法是把旧版本复制成一个新版本。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">要恢复的版本 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RestoreObjectVersionAsync(Guid sessionId, string bucket, string key, string versionId, CancellationToken cancellationToken = default);

    /// <summary>下载指定版本到本地文件。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="localPath">本地路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DownloadObjectVersionAsync(
        Guid sessionId, string bucket, string key, string versionId, string localPath, CancellationToken cancellationToken = default);

    // ---- 对象属性 ----

    /// <summary>对象详情(大小、校验和、存储类别、加密、分片数、取回状态、自定义元数据)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">版本 id;留空即当前版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对象详情。</returns>
    Task<S3ObjectDetails> GetObjectDetailsAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>读取对象标签;没有标签集时返回空列表(不是错误)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签列表。</returns>
    Task<IReadOnlyList<S3Tag>> GetObjectTagsAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>写回对象标签;传空列表即删除整个标签集。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="tags">标签列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutObjectTagsAsync(Guid sessionId, string bucket, string key, IReadOnlyList<S3Tag> tags, CancellationToken cancellationToken = default);

    /// <summary>读取对象 ACL(JSON 文档)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>ACL 文档。</returns>
    Task<string> GetObjectAclAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>套用一条预置 ACL(<c>private</c> / <c>public-read</c> …)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="cannedAcl">预置 ACL 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutObjectCannedAclAsync(Guid sessionId, string bucket, string key, string cannedAcl, CancellationToken cancellationToken = default);

    /// <summary>改变存储类别。**以自复制实现** —— S3 没有原地改属性的操作。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="storageClass">目标存储类别。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ChangeStorageClassAsync(Guid sessionId, string bucket, string key, string storageClass, CancellationToken cancellationToken = default);

    /// <summary>改变服务端加密方式(同样以自复制实现)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="encryptionMethod">加密算法。</param>
    /// <param name="kmsKeyId">KMS 密钥 id(<c>aws:kms</c> 时)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ChangeEncryptionAsync(Guid sessionId, string bucket, string key, string encryptionMethod, string? kmsKeyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写回自定义元数据(同样以自复制实现,且**必须带 <c>REPLACE</c> 指令**——
    /// 默认的 <c>COPY</c> 会把新元数据原样丢弃)。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="metadata">元数据键值对。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutObjectMetadataAsync(Guid sessionId, string bucket, string key, IReadOnlyList<S3Tag> metadata, CancellationToken cancellationToken = default);

    // ---- 对象锁定 ----

    /// <summary>读取保留策略;没设过时返回 <see cref="S3Retention.Mode" /> 为空串的实例。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>保留策略。</returns>
    Task<S3Retention> GetObjectRetentionAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>写回保留策略。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="retention">保留策略。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="bypassGovernance">是否绕过 <c>GOVERNANCE</c> 模式(需要额外权限)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutObjectRetentionAsync(
        Guid sessionId, string bucket, string key, S3Retention retention, string? versionId = null,
        bool bypassGovernance = false, CancellationToken cancellationToken = default);

    /// <summary>读取合法保留(legal hold)开关。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否处于合法保留。</returns>
    Task<bool> GetObjectLegalHoldAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>写回合法保留开关。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="enabled">是否开启。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PutObjectLegalHoldAsync(
        Guid sessionId, string bucket, string key, bool enabled, string? versionId = null, CancellationToken cancellationToken = default);

    // ---- 归档取回 / 分片上传 / 查询 ----

    /// <summary>发起归档对象的取回(Glacier 系存储类别的对象要先取回才能下载)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="request">取回参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RestoreArchivedObjectAsync(
        Guid sessionId, string bucket, string key, S3RestoreRequest request, CancellationToken cancellationToken = default);

    /// <summary>列出未完成的分片上传(未中止的分片会一直计费);不支持的实现返回空列表。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分片上传列表。</returns>
    Task<IReadOnlyList<S3MultipartUpload>> ListMultipartUploadsAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default);

    /// <summary>中止一次分片上传并释放其已上传的分片。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="uploadId">分片上传 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task AbortMultipartUploadAsync(Guid sessionId, string bucket, string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>在服务端对单个对象跑一段 SQL(S3 Select),返回结果文本。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="request">查询参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果文本。</returns>
    Task<string> SelectObjectContentAsync(
        Guid sessionId, string bucket, string key, S3SelectRequest request, CancellationToken cancellationToken = default);

    /// <summary>为某个对象生成预签名 URL(有效期上限 7 天,协议硬限制)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="expiry">有效期。</param>
    /// <param name="verb">HTTP 方法(<c>GET</c> / <c>PUT</c> / <c>DELETE</c> / <c>HEAD</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>预签名 URL。</returns>
    Task<string> CreatePresignedUrlAsync(
        Guid sessionId, string bucket, string key, TimeSpan expiry, string verb = "GET", CancellationToken cancellationToken = default);
}

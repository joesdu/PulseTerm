using System.Globalization;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using CoreS3ObjectVersion = VelaShell.Plugin.S3.S3ObjectVersion;
using SdkS3ObjectVersion = Amazon.S3.Model.S3ObjectVersion;

namespace VelaShell.Plugin.S3;

/// <summary>
/// <see cref="IS3ManagementService" /> 在 AWSSDK.S3 上的实现:S3 协议里除「文件管理器」
/// 之外的全部能力。与 <see cref="S3ProtocolFileSystem" /> 共用同一条会话与同一个客户端
/// (经 <see cref="IS3ClientAccessor" />),因此断线判定、证书信任、连接池都是同一套。
/// <para>
/// 桶配置全部走 <see cref="GetBucketConfigAsync" /> / <see cref="PutBucketConfigAsync" /> /
/// <see cref="DeleteBucketConfigAsync" /> 三个入口 + 一个 <see cref="S3ConfigKind" /> 分派,
/// 理由见 <see cref="IS3ManagementService" /> 的说明。
/// </para>
/// </summary>
public sealed class S3ManagementService : IS3ManagementService
{
    private readonly IS3ClientAccessor _accessor;

    /// <summary>创建管理服务;<paramref name="accessor" /> 提供会话到客户端的解析。</summary>
    /// <param name="accessor">会话客户端访问器,通常就是 <see cref="S3ProtocolFileSystem" />。</param>
    internal S3ManagementService(IS3ClientAccessor accessor) =>
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    /// <summary>供 DI 使用的工厂:把文件服务同时当作客户端访问器。</summary>
    /// <param name="fileService">同一条会话上的 S3 文件服务。</param>
    public static S3ManagementService Create(S3ProtocolFileSystem fileService) =>
        new(fileService ?? throw new ArgumentNullException(nameof(fileService)));

    // ---- 桶级配置 -----------------------------------------------------------

    /// <inheritdoc />
    public async Task<S3ConfigResult> GetBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string? id = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            string json = await ReadConfigAsync(client, bucket, kind, id, cancellationToken).ConfigureAwait(false);
            return json.Length == 0 ? S3ConfigResult.NotConfigured(kind) : S3ConfigResult.FromJson(kind, json);
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, $"get {kind} configuration");
            // 「没配过」与「服务端不支持」在 S3 兼容实现上都极其常见,是空状态不是错误 ——
            // 当成错误会让桶管理器一打开就一片红。
            if (translated is VelaS3UnsupportedOperationException)
            {
                return S3ConfigResult.NotSupported(kind, translated.Message);
            }
            if (S3Interop.IsMissingOrUnsupported(translated))
            {
                return S3ConfigResult.NotConfigured(kind);
            }
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task PutBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string json, string? id = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await WriteConfigAsync(client, bucket, kind, json, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, $"put {kind} configuration");
        }
    }

    /// <inheritdoc />
    public async Task DeleteBucketConfigAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, string? id = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await RemoveConfigAsync(client, bucket, kind, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, $"delete {kind} configuration");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListBucketConfigIdsAsync(
        Guid sessionId, string bucket, S3ConfigKind kind, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            return kind switch
            {
                S3ConfigKind.Inventory =>
                [
                    .. (await client.ListBucketInventoryConfigurationsAsync(new ListBucketInventoryConfigurationsRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                       .InventoryConfigurationList?.Select(c => c.InventoryId ?? string.Empty) ?? []
                ],
                S3ConfigKind.Analytics =>
                [
                    .. (await client.ListBucketAnalyticsConfigurationsAsync(new ListBucketAnalyticsConfigurationsRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                       .AnalyticsConfigurationList?.Select(c => c.AnalyticsId ?? string.Empty) ?? []
                ],
                S3ConfigKind.Metrics =>
                [
                    .. (await client.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                       .MetricsConfigurationList?.Select(c => c.MetricsId ?? string.Empty) ?? []
                ],
                S3ConfigKind.IntelligentTiering =>
                [
                    .. (await client.ListBucketIntelligentTieringConfigurationsAsync(new ListBucketIntelligentTieringConfigurationsRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                       .IntelligentTieringConfigurationList?.Select(c => c.IntelligentTieringId ?? string.Empty) ?? []
                ],
                _ => [],
            };
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, $"list {kind} configurations");
            // 不支持这类配置的实现直接给空列表,界面呈现为「没有配置」。
            return S3Interop.IsMissingOrUnsupported(translated) ? [] : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<S3BucketOverview> GetBucketOverviewAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            string region = _accessor.GetConnectionInfo(sessionId).Settings.EffectiveRegion;
            try
            {
                GetBucketLocationResponse location = await client
                    .GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }, cancellationToken)
                    .ConfigureAwait(false);
                // 空串是协议规定的 us-east-1 表示法。
                if (location.Location?.Value is { Length: > 0 } value)
                {
                    region = value;
                }
            }
            catch (Exception ex) when (S3Interop.IsMissingOrUnsupported(S3Interop.Translate(ex, "get bucket location")))
            {
                // 不支持 GetBucketLocation 的实现就沿用会话配置的区域。
            }

            bool? isPublic = await TryAsync(async () =>
                ((await client.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                .PolicyStatus?.IsPublic)).ConfigureAwait(false);

            string versioning = await TryAsync(async () =>
                (await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                .VersioningConfig?.Status?.Value ?? "Off").ConfigureAwait(false) ?? "Off";

            bool objectLock = await TryAsync(async () =>
                (bool?)((await client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false))
                .ObjectLockConfiguration?.ObjectLockEnabled is not null)).ConfigureAwait(false) ?? false;

            return new(bucket, region, DateTime.MinValue, isPublic, versioning, objectLock);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "get bucket overview");
        }
    }

    // ---- 版本控制 -----------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoreS3ObjectVersion>> ListObjectVersionsAsync(
        Guid sessionId, string bucket, string keyOrPrefix, int maxKeys = 1000, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            List<CoreS3ObjectVersion> versions = [];
            string? keyMarker = null;
            string? versionMarker = null;
            do
            {
                ListVersionsResponse page = await client.ListVersionsAsync(new ListVersionsRequest
                {
                    BucketName = bucket,
                    Prefix = keyOrPrefix,
                    MaxKeys = Math.Clamp(maxKeys, 1, 1000),
                    KeyMarker = keyMarker,
                    VersionIdMarker = versionMarker,
                }, cancellationToken).ConfigureAwait(false);

                foreach (SdkS3ObjectVersion version in page.Versions ?? [])
                {
                    versions.Add(new(
                        version.Key ?? string.Empty,
                        version.VersionId,
                        version.IsLatest ?? false,
                        version.IsDeleteMarker ?? false,
                        version.Size ?? 0,
                        ToLocal(version.LastModified),
                        (version.ETag ?? string.Empty).Trim('"'),
                        version.StorageClass?.Value ?? string.Empty));
                    if (versions.Count >= maxKeys)
                    {
                        return versions;
                    }
                }
                keyMarker = (page.IsTruncated ?? false) ? page.NextKeyMarker : null;
                versionMarker = (page.IsTruncated ?? false) ? page.NextVersionIdMarker : null;
            }
            while (keyMarker is { Length: > 0 });
            return versions;
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "list object versions");
        }
    }

    /// <inheritdoc />
    public async Task DeleteObjectVersionAsync(Guid sessionId, string bucket, string key, string versionId, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "delete object version");
        }
    }

    /// <inheritdoc />
    public async Task RestoreObjectVersionAsync(Guid sessionId, string bucket, string key, string versionId, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            // 把旧版本复制成一个**新版本**,而不是删掉它之后的版本 —— 后者会永久销毁数据,
            // 且一旦删错无法挽回。复制是幂等且完全可逆的。
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = key,
                SourceVersionId = versionId,
                DestinationBucket = bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.COPY,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "restore object version");
        }
    }

    /// <inheritdoc />
    public async Task DownloadObjectVersionAsync(
        Guid sessionId, string bucket, string key, string versionId, string localPath, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
            }, cancellationToken).ConfigureAwait(false);
            await using Stream source = response.ResponseStream;
            await using var target = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "download object version");
        }
    }

    // ---- 对象级属性 ---------------------------------------------------------

    /// <inheritdoc />
    public async Task<S3ObjectDetails> GetObjectDetailsAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
            }, cancellationToken).ConfigureAwait(false);

            // GetObjectAttributes 才有校验和与分片数,但它是较新的接口,兼容实现常常没有;
            // 取不到就退回只用 HeadObject 的信息,而不是让整个属性对话框打不开。
            GetObjectAttributesResponse? attributes = await TryAsync(() =>
                client.GetObjectAttributesAsync(new GetObjectAttributesRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                    ObjectAttributes = [ObjectAttributes.ETag, ObjectAttributes.Checksum, ObjectAttributes.ObjectSize, ObjectAttributes.StorageClass, ObjectAttributes.ObjectParts],
                }, cancellationToken)).ConfigureAwait(false);

            List<S3Tag> metadata = [];
            if (head.Metadata is { } collection)
            {
                foreach (string name in collection.Keys)
                {
                    metadata.Add(new(name, collection[name] ?? string.Empty));
                }
            }

            return new(
                key,
                head.ContentLength,
                ToLocal(head.LastModified),
                (head.ETag ?? string.Empty).Trim('"'),
                head.VersionId ?? string.Empty,
                head.StorageClass?.Value ?? "STANDARD",
                head.ContentType ?? string.Empty,
                head.ServerSideEncryptionMethod?.Value ?? string.Empty,
                head.ServerSideEncryptionKeyManagementServiceKeyId ?? string.Empty,
                FormatChecksum(attributes?.Checksum),
                attributes?.ObjectParts?.TotalPartsCount ?? head.PartsCount ?? 0,
                FormatRestoreStatus(head),
                head.Expiration?.ExpiryDate,
                metadata);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "get object details");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<S3Tag>> GetObjectTagsAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            GetObjectTaggingResponse response = await client
                .GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
            return [.. (response.Tagging ?? []).Select(t => new S3Tag(t.Key ?? string.Empty, t.Value ?? string.Empty))];
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, "get object tags");
            return S3Interop.IsMissingOrUnsupported(translated) ? [] : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task PutObjectTagsAsync(Guid sessionId, string bucket, string key, IReadOnlyList<S3Tag> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            if (tags.Count == 0)
            {
                await client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
                return;
            }
            await client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = bucket,
                Key = key,
                Tagging = new() { TagSet = [.. tags.Select(t => new Tag { Key = t.Key, Value = t.Value })] },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "put object tags");
        }
    }

    /// <inheritdoc />
    public async Task<string> GetObjectAclAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            // 用分离后的 GetObjectAcl,而不是已弃用的 GetACL(它把桶与对象两件事糅在一起)。
            GetObjectAclResponse response = await client
                .GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
            return S3ConfigJson.Serialize(new S3AccessControlList { Grants = response.Grants, Owner = response.Owner });
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, "get object ACL");
            return S3Interop.IsMissingOrUnsupported(translated) ? string.Empty : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task PutObjectCannedAclAsync(Guid sessionId, string bucket, string key, string cannedAcl, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.PutObjectAclAsync(new PutObjectAclRequest
            {
                BucketName = bucket,
                Key = key,
                ACL = S3CannedACL.FindValue(cannedAcl),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "put object ACL");
        }
    }

    /// <inheritdoc />
    public Task ChangeStorageClassAsync(Guid sessionId, string bucket, string key, string storageClass, CancellationToken cancellationToken = default) =>
        SelfCopyAsync(sessionId, bucket, key, "change storage class", request =>
            request.StorageClass = S3StorageClass.FindValue(storageClass), cancellationToken);

    /// <inheritdoc />
    public Task ChangeEncryptionAsync(Guid sessionId, string bucket, string key, string encryptionMethod, string? kmsKeyId, CancellationToken cancellationToken = default) =>
        SelfCopyAsync(sessionId, bucket, key, "change encryption", request =>
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(encryptionMethod);
            if (kmsKeyId is { Length: > 0 })
            {
                request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task PutObjectMetadataAsync(Guid sessionId, string bucket, string key, IReadOnlyList<S3Tag> metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return SelfCopyAsync(sessionId, bucket, key, "put object metadata", request =>
        {
            // REPLACE 才会用请求里的元数据覆盖原有的;默认的 COPY 会原样保留旧值。
            request.MetadataDirective = S3MetadataDirective.REPLACE;
            foreach (S3Tag entry in metadata)
            {
                request.Metadata.Add(entry.Key, entry.Value);
            }
        }, cancellationToken);
    }

    // ---- 对象锁定 -----------------------------------------------------------

    /// <inheritdoc />
    public async Task<S3Retention> GetObjectRetentionAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            GetObjectRetentionResponse response = await client
                .GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId }, cancellationToken)
                .ConfigureAwait(false);
            return new(response.Retention?.Mode?.Value ?? string.Empty, response.Retention?.RetainUntilDate);
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, "get object retention");
            // 没设过保留策略是常态,不是错误。
            return S3Interop.IsMissingOrUnsupported(translated) ? new(string.Empty, null) : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task PutObjectRetentionAsync(
        Guid sessionId, string bucket, string key, S3Retention retention, string? versionId = null, bool bypassGovernance = false, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
                BypassGovernanceRetention = bypassGovernance,
                Retention = new()
                {
                    Mode = retention.Mode is { Length: > 0 } mode ? ObjectLockRetentionMode.FindValue(mode) : null,
                    RetainUntilDate = retention.RetainUntil,
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "put object retention");
        }
    }

    /// <inheritdoc />
    public async Task<bool> GetObjectLegalHoldAsync(
        Guid sessionId, string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            GetObjectLegalHoldResponse response = await client
                .GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = key, VersionId = versionId }, cancellationToken)
                .ConfigureAwait(false);
            return response.LegalHold?.Status == ObjectLockLegalHoldStatus.On;
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, "get object legal hold");
            return S3Interop.IsMissingOrUnsupported(translated) ? false : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task PutObjectLegalHoldAsync(
        Guid sessionId, string bucket, string key, bool enabled, string? versionId = null, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
                LegalHold = new() { Status = enabled ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "put object legal hold");
        }
    }

    // ---- 归档取回 -----------------------------------------------------------

    /// <inheritdoc />
    public async Task RestoreArchivedObjectAsync(
        Guid sessionId, string bucket, string key, S3RestoreRequest request, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.RestoreObjectAsync(new RestoreObjectRequest
            {
                BucketName = bucket,
                Key = key,
                Days = Math.Max(1, request.Days),
                RetrievalTier = request.Tier is { Length: > 0 } tier ? GlacierJobTier.FindValue(tier) : null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "restore archived object");
        }
    }

    // ---- 分片上传管理 -------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<S3MultipartUpload>> ListMultipartUploadsAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            List<S3MultipartUpload> uploads = [];
            string? keyMarker = null;
            string? uploadMarker = null;
            do
            {
                ListMultipartUploadsResponse page = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
                {
                    BucketName = bucket,
                    KeyMarker = keyMarker,
                    UploadIdMarker = uploadMarker,
                }, cancellationToken).ConfigureAwait(false);
                foreach (MultipartUpload upload in page.MultipartUploads ?? [])
                {
                    uploads.Add(new(
                        upload.Key ?? string.Empty,
                        upload.UploadId ?? string.Empty,
                        ToLocal(upload.Initiated),
                        upload.StorageClass?.Value ?? string.Empty,
                        upload.Owner?.DisplayName ?? upload.Initiator?.DisplayName ?? string.Empty));
                }
                keyMarker = (page.IsTruncated ?? false) ? page.NextKeyMarker : null;
                uploadMarker = (page.IsTruncated ?? false) ? page.NextUploadIdMarker : null;
            }
            while (keyMarker is { Length: > 0 });
            return uploads;
        }
        catch (Exception ex)
        {
            Exception translated = _accessor.TranslateFault(sessionId, ex, "list multipart uploads");
            return S3Interop.IsMissingOrUnsupported(translated) ? [] : throw translated;
        }
    }

    /// <inheritdoc />
    public async Task AbortMultipartUploadAsync(Guid sessionId, string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "abort multipart upload");
        }
    }

    // ---- 查询与分享 ---------------------------------------------------------

    /// <inheritdoc />
    public async Task<string> SelectObjectContentAsync(
        Guid sessionId, string bucket, string key, S3SelectRequest request, CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            var select = new SelectObjectContentRequest
            {
                BucketName = bucket,
                Key = key,
                Expression = request.Expression,
                ExpressionType = ExpressionType.SQL,
                InputSerialization = BuildInput(request),
                OutputSerialization = BuildOutput(request),
            };
            SelectObjectContentResponse response = await client.SelectObjectContentAsync(select, cancellationToken).ConfigureAwait(false);

            // 结果是一条事件流:数据分块到达,末尾还有统计/结束事件。这里只收数据块。
            var output = new StringBuilder();
            using ISelectObjectContentEventStream events = response.Payload;
            foreach (IS3Event item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is RecordsEvent { Payload: { } records })
                {
                    using var reader = new StreamReader(records, Encoding.UTF8);
                    output.Append(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            return output.ToString();
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "select object content");
        }
    }

    /// <inheritdoc />
    public Task<string> CreatePresignedUrlAsync(
        Guid sessionId, string bucket, string key, TimeSpan expiry, string verb = "GET", CancellationToken cancellationToken = default)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            // 上限 7 天是协议硬限制;下限 1 秒是防手滑传 0/负数。
            double seconds = Math.Clamp(expiry.TotalSeconds, 1, TimeSpan.FromDays(7).TotalSeconds);
            return Task.FromResult(client.GetPreSignedURL(new()
            {
                BucketName = bucket,
                Key = key,
                Expires = DateTime.UtcNow.AddSeconds(seconds),
                Verb = verb.ToUpperInvariant() switch
                {
                    "PUT" => HttpVerb.PUT,
                    "DELETE" => HttpVerb.DELETE,
                    "HEAD" => HttpVerb.HEAD,
                    _ => HttpVerb.GET,
                },
                // **必须显式给**:SDK 的预签名一律按 HTTPS 出 URL,不看 config.UseHttp。
                // 明文 HTTP 的自建端点会因此拿到一条 https:// 的链接,粘出去连不上。
                Protocol = client.Config.UseHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
            }));
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, "presign");
        }
    }

    // ---- 配置分派 -----------------------------------------------------------

    private static async Task<string> ReadConfigAsync(IAmazonS3 client, string bucket, S3ConfigKind kind, string? id, CancellationToken ct) =>
        kind switch
        {
            S3ConfigKind.Versioning => S3ConfigJson.Serialize(
                (await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, ct).ConfigureAwait(false)).VersioningConfig),
            S3ConfigKind.Lifecycle => S3ConfigJson.Serialize(
                (await client.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Configuration),
            // 桶策略本身就是一段 JSON,服务端原样给回;只重新缩进,绝不重新序列化 ——
            // 那会丢掉我们不认识的字段。
            S3ConfigKind.Policy => S3ConfigJson.Prettify(
                (await client.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Policy),
            S3ConfigKind.PublicAccessBlock => S3ConfigJson.Serialize(
                (await client.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }, ct).ConfigureAwait(false)).PublicAccessBlockConfiguration),
            S3ConfigKind.OwnershipControls => S3ConfigJson.Serialize(
                (await client.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket }, ct).ConfigureAwait(false)).OwnershipControls),
            S3ConfigKind.Acl => S3ConfigJson.Serialize(
                ToAccessControlList(await client.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket }, ct).ConfigureAwait(false))),
            S3ConfigKind.Cors => S3ConfigJson.Serialize(
                (await client.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Configuration),
            S3ConfigKind.Encryption => S3ConfigJson.Serialize(
                (await client.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket }, ct).ConfigureAwait(false)).ServerSideEncryptionConfiguration),
            S3ConfigKind.ObjectLock => S3ConfigJson.Serialize(
                (await client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).ObjectLockConfiguration),
            S3ConfigKind.Tagging => S3ConfigJson.Serialize(
                new Tagging { TagSet = (await client.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket }, ct).ConfigureAwait(false)).TagSet }),
            S3ConfigKind.Replication => S3ConfigJson.Serialize(
                (await client.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Configuration),
            S3ConfigKind.Website => S3ConfigJson.Serialize(
                (await client.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket }, ct).ConfigureAwait(false)).WebsiteConfiguration),
            S3ConfigKind.Logging => S3ConfigJson.Serialize(
                (await client.GetBucketLoggingAsync(new GetBucketLoggingRequest { BucketName = bucket }, ct).ConfigureAwait(false)).BucketLoggingConfig),
            S3ConfigKind.Notification => S3ConfigJson.Serialize(
                ToNotification(await client.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket }, ct).ConfigureAwait(false))),
            S3ConfigKind.AccelerateConfiguration => S3ConfigJson.Wrap("Status",
                (await client.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Status?.Value),
            S3ConfigKind.RequestPayment => S3ConfigJson.Wrap("Payer",
                (await client.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket }, ct).ConfigureAwait(false)).Payer),
            S3ConfigKind.Inventory => S3ConfigJson.Serialize(
                (await client.GetBucketInventoryConfigurationAsync(new GetBucketInventoryConfigurationRequest { BucketName = bucket, InventoryId = id }, ct).ConfigureAwait(false)).InventoryConfiguration),
            S3ConfigKind.Analytics => S3ConfigJson.Serialize(
                (await client.GetBucketAnalyticsConfigurationAsync(new GetBucketAnalyticsConfigurationRequest { BucketName = bucket, AnalyticsId = id }, ct).ConfigureAwait(false)).AnalyticsConfiguration),
            S3ConfigKind.Metrics => S3ConfigJson.Serialize(
                (await client.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = id }, ct).ConfigureAwait(false)).MetricsConfiguration),
            S3ConfigKind.IntelligentTiering => S3ConfigJson.Serialize(
                (await client.GetBucketIntelligentTieringConfigurationAsync(new GetBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = id }, ct).ConfigureAwait(false)).IntelligentTieringConfiguration),
            S3ConfigKind.MetadataConfiguration => S3ConfigJson.Serialize(
                (await client.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false)).GetBucketMetadataConfigurationResult),
            S3ConfigKind.Abac => S3ConfigJson.Wrap("Status",
                (await client.GetBucketAbacAsync(new GetBucketAbacRequest { BucketName = bucket }, ct).ConfigureAwait(false)).AbacStatus?.Status?.Value),
            _ => throw new VelaS3UnsupportedOperationException($"Unknown S3 bucket configuration: {kind}."),
        };

    private static async Task WriteConfigAsync(IAmazonS3 client, string bucket, S3ConfigKind kind, string json, string? id, CancellationToken ct)
    {
        switch (kind)
        {
            case S3ConfigKind.Versioning:
                await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
                {
                    BucketName = bucket,
                    VersioningConfig = S3ConfigJson.Deserialize<S3BucketVersioningConfig>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Lifecycle:
                await client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
                {
                    BucketName = bucket,
                    Configuration = S3ConfigJson.Deserialize<LifecycleConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Policy:
                await client.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = json }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.PublicAccessBlock:
                await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
                {
                    BucketName = bucket,
                    PublicAccessBlockConfiguration = S3ConfigJson.Deserialize<PublicAccessBlockConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.OwnershipControls:
                await client.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
                {
                    BucketName = bucket,
                    OwnershipControls = S3ConfigJson.Deserialize<OwnershipControls>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Acl:
                await client.PutBucketAclAsync(new PutBucketAclRequest
                {
                    BucketName = bucket,
                    AccessControlPolicy = S3ConfigJson.Deserialize<S3AccessControlList>(json),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Cors:
                await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
                {
                    BucketName = bucket,
                    Configuration = S3ConfigJson.Deserialize<CORSConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Encryption:
                await client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
                {
                    BucketName = bucket,
                    ServerSideEncryptionConfiguration = S3ConfigJson.Deserialize<ServerSideEncryptionConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.ObjectLock:
                await client.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
                {
                    BucketName = bucket,
                    ObjectLockConfiguration = S3ConfigJson.Deserialize<ObjectLockConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Tagging:
                await client.PutBucketTaggingAsync(new PutBucketTaggingRequest
                {
                    BucketName = bucket,
                    TagSet = S3ConfigJson.Deserialize<Tagging>(json)?.TagSet ?? [],
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Replication:
                await client.PutBucketReplicationAsync(new PutBucketReplicationRequest
                {
                    BucketName = bucket,
                    Configuration = S3ConfigJson.Deserialize<ReplicationConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Website:
                await client.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
                {
                    BucketName = bucket,
                    WebsiteConfiguration = S3ConfigJson.Deserialize<WebsiteConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Logging:
                await client.PutBucketLoggingAsync(new PutBucketLoggingRequest
                {
                    BucketName = bucket,
                    LoggingConfig = S3ConfigJson.Deserialize<S3BucketLoggingConfig>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Notification:
                NotificationDocument notification = S3ConfigJson.Deserialize<NotificationDocument>(json) ?? new();
                await client.PutBucketNotificationAsync(new PutBucketNotificationRequest
                {
                    BucketName = bucket,
                    TopicConfigurations = notification.TopicConfigurations,
                    QueueConfigurations = notification.QueueConfigurations,
                    LambdaFunctionConfigurations = notification.LambdaFunctionConfigurations,
                    EventBridgeConfiguration = notification.EventBridgeConfiguration,
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.AccelerateConfiguration:
                await client.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
                {
                    BucketName = bucket,
                    AccelerateConfiguration = new() { Status = BucketAccelerateStatus.FindValue(S3ConfigJson.Unwrap(json, "Status")) },
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.RequestPayment:
                await client.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
                {
                    BucketName = bucket,
                    RequestPaymentConfiguration = new() { Payer = S3ConfigJson.Unwrap(json, "Payer") },
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Inventory:
                await client.PutBucketInventoryConfigurationAsync(new PutBucketInventoryConfigurationRequest
                {
                    BucketName = bucket,
                    InventoryId = id,
                    InventoryConfiguration = S3ConfigJson.Deserialize<InventoryConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Analytics:
                await client.PutBucketAnalyticsConfigurationAsync(new PutBucketAnalyticsConfigurationRequest
                {
                    BucketName = bucket,
                    AnalyticsId = id,
                    AnalyticsConfiguration = S3ConfigJson.Deserialize<AnalyticsConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Metrics:
                await client.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
                {
                    BucketName = bucket,
                    MetricsId = id,
                    MetricsConfiguration = S3ConfigJson.Deserialize<MetricsConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.IntelligentTiering:
                await client.PutBucketIntelligentTieringConfigurationAsync(new PutBucketIntelligentTieringConfigurationRequest
                {
                    BucketName = bucket,
                    IntelligentTieringId = id,
                    IntelligentTieringConfiguration = S3ConfigJson.Deserialize<IntelligentTieringConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.MetadataConfiguration:
                await client.CreateBucketMetadataConfigurationAsync(new CreateBucketMetadataConfigurationRequest
                {
                    BucketName = bucket,
                    MetadataConfiguration = S3ConfigJson.Deserialize<MetadataConfiguration>(json) ?? new(),
                }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Abac:
                await client.PutBucketAbacAsync(new PutBucketAbacRequest
                {
                    BucketName = bucket,
                    AbacStatus = new() { Status = BucketAbacStatus.FindValue(S3ConfigJson.Unwrap(json, "Status")) },
                }, ct).ConfigureAwait(false);
                break;
            default:
                throw new VelaS3UnsupportedOperationException($"S3 bucket configuration {kind} cannot be written.");
        }
    }

    private static async Task RemoveConfigAsync(IAmazonS3 client, string bucket, S3ConfigKind kind, string? id, CancellationToken ct)
    {
        switch (kind)
        {
            case S3ConfigKind.Lifecycle:
                await client.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Policy:
                await client.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.PublicAccessBlock:
                await client.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.OwnershipControls:
                await client.DeleteBucketOwnershipControlsAsync(new DeleteBucketOwnershipControlsRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Cors:
                await client.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Encryption:
                await client.DeleteBucketEncryptionAsync(new DeleteBucketEncryptionRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Tagging:
                await client.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Replication:
                await client.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Website:
                await client.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Inventory:
                await client.DeleteBucketInventoryConfigurationAsync(new DeleteBucketInventoryConfigurationRequest { BucketName = bucket, InventoryId = id }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Analytics:
                await client.DeleteBucketAnalyticsConfigurationAsync(new DeleteBucketAnalyticsConfigurationRequest { BucketName = bucket, AnalyticsId = id }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.Metrics:
                await client.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = id }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.IntelligentTiering:
                await client.DeleteBucketIntelligentTieringConfigurationAsync(new DeleteBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = id }, ct).ConfigureAwait(false);
                break;
            case S3ConfigKind.MetadataConfiguration:
                await client.DeleteBucketMetadataConfigurationAsync(new DeleteBucketMetadataConfigurationRequest { BucketName = bucket }, ct).ConfigureAwait(false);
                break;
            default:
                throw new VelaS3UnsupportedOperationException($"S3 bucket configuration {kind} cannot be deleted.");
        }
    }

    // ---- 小工具 -------------------------------------------------------------

    /// <summary>
    /// 「把对象复制到自己身上」。S3 没有原地改属性的操作:改存储类别、改加密、改元数据,
    /// 协议规定的做法都是复制一次并带上新属性。
    /// </summary>
    private async Task SelfCopyAsync(Guid sessionId, string bucket, string key, string operation, Action<CopyObjectRequest> configure, CancellationToken cancellationToken)
    {
        IAmazonS3 client = _accessor.GetClient(sessionId);
        try
        {
            var request = new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = key,
                DestinationBucket = bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.COPY,
            };
            configure(request);
            await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw _accessor.TranslateFault(sessionId, ex, operation);
        }
    }

    /// <summary>
    /// 跑一个「可以合理失败」的可选调用:失败时返回默认值而不是抛。
    /// 用于概览页这类「有就显示、没有就留空」的字段 —— 不该因为某台服务器不支持
    /// <c>GetBucketPolicyStatus</c>,整个概览页就打不开。
    /// </summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    /// <summary>分离后的 GetBucketAcl 直接给 Grants + Owner;这里拼回成与对象 ACL 同构的文档。</summary>
    private static S3AccessControlList ToAccessControlList(GetBucketAclResponse response) =>
        new() { Grants = response.Grants, Owner = response.Owner };

    private static NotificationDocument ToNotification(GetBucketNotificationResponse response) =>
        new()
        {
            TopicConfigurations = response.TopicConfigurations,
            QueueConfigurations = response.QueueConfigurations,
            LambdaFunctionConfigurations = response.LambdaFunctionConfigurations,
            EventBridgeConfiguration = response.EventBridgeConfiguration,
        };

    private static InputSerialization BuildInput(S3SelectRequest request) =>
        request.InputFormat.ToUpperInvariant() switch
        {
            "JSON" => new()
            {
                JSON = new() { JsonType = JsonType.Lines },
                CompressionType = CompressionType.FindValue(request.CompressionType),
            },
            "PARQUET" => new() { Parquet = new() },
            _ => new()
            {
                CSV = new() { FileHeaderInfo = request.CsvHasHeader ? FileHeaderInfo.Use : FileHeaderInfo.None },
                CompressionType = CompressionType.FindValue(request.CompressionType),
            },
        };

    private static OutputSerialization BuildOutput(S3SelectRequest request) =>
        request.OutputFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase)
            ? new() { JSON = new() }
            : new() { CSV = new() };

    private static string FormatChecksum(Checksum? checksum)
    {
        if (checksum is null)
        {
            return string.Empty;
        }
        (string Name, string? Value)[] candidates =
        [
            ("CRC32", checksum.ChecksumCRC32), ("CRC32C", checksum.ChecksumCRC32C),
            ("CRC64NVME", checksum.ChecksumCRC64NVME), ("SHA1", checksum.ChecksumSHA1),
            ("SHA256", checksum.ChecksumSHA256), ("MD5", checksum.ChecksumMD5),
        ];
        foreach ((string name, string? value) in candidates)
        {
            if (value is { Length: > 0 })
            {
                return $"{name}: {value}";
            }
        }
        return string.Empty;
    }

    /// <summary>把归档取回状态整理成一句人话(界面上直接显示)。</summary>
    private static string FormatRestoreStatus(GetObjectMetadataResponse head)
    {
        if (head.RestoreInProgress == true)
        {
            return "in-progress";
        }
        if (head.RestoreExpiration is { } expiry)
        {
            return $"restored until {expiry.ToLocalTime().ToString("u", CultureInfo.InvariantCulture)}";
        }
        return head.ArchiveStatus?.Value ?? string.Empty;
    }

    private static DateTime ToLocal(DateTime? value) =>
        value is not { } instant || instant == DateTime.MinValue
            ? DateTime.MinValue
            : instant.Kind == DateTimeKind.Utc ? instant.ToLocalTime() : instant;

    /// <summary>事件通知在协议上是四个并列的列表,这里包成一个文档以便整体编辑。</summary>
    private sealed class NotificationDocument
    {
        public List<TopicConfiguration>? TopicConfigurations { get; set; }

        public List<QueueConfiguration>? QueueConfigurations { get; set; }

        public List<LambdaFunctionConfiguration>? LambdaFunctionConfigurations { get; set; }

        public EventBridgeConfiguration? EventBridgeConfiguration { get; set; }
    }
}

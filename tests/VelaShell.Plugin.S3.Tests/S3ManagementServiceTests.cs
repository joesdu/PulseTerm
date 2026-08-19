using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using CoreS3ObjectVersion = VelaShell.Plugin.S3.S3ObjectVersion;
using SdkS3ObjectVersion = Amazon.S3.Model.S3ObjectVersion;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// <see cref="S3ManagementService" /> 对 AWSSDK 的映射。用替身客户端而不是环回服务器:
/// 这里要验的是**映射与错误分类**,不是网络行为(网络那半由
/// <see cref="S3FileServiceIntegrationTests" /> 端到端覆盖)。
/// </summary>
[TestClass]
[TestCategory("S3")]
public sealed class S3ManagementServiceTests
{
    private const string Bucket = "test-bucket";

    private IAmazonS3 _client = null!;
    private S3ManagementService _service = null!;
    private Guid _session;

    [TestInitialize]
    public void SetUp()
    {
        _client = Substitute.For<IAmazonS3>();
        _session = Guid.NewGuid();
        _service = new(new FakeAccessor(_client, _session));
    }

    private static AmazonS3Exception Aws(string code, HttpStatusCode status) =>
        new("boom") { ErrorCode = code, StatusCode = status };

    // ---- 桶配置:空状态 -----------------------------------------------------

    /// <summary>
    /// 「没配过」必须呈现为空状态而不是异常。AWS 对每种未配置的能力都有一个专属错误码,
    /// 把它们当错误会让桶管理器一打开就一片红。
    /// </summary>
    [TestMethod]
    public async Task GetBucketConfig_WhenNeverConfigured_ReportsEmptyStateInsteadOfThrowing()
    {
        _client.GetLifecycleConfigurationAsync(Arg.Any<GetLifecycleConfigurationRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetLifecycleConfigurationResponse>>(_ => throw Aws("NoSuchLifecycleConfiguration", HttpStatusCode.NotFound));

        S3ConfigResult result = await _service.GetBucketConfigAsync(_session, Bucket, S3ConfigKind.Lifecycle);

        Assert.IsFalse(result.Exists);
        Assert.IsTrue(result.Supported, "「没配过」不等于「不支持」。");
        Assert.IsEmpty(result.Json);
    }

    /// <summary>
    /// 「服务端不支持」要与「没配过」区分开:界面据此把面板灰掉并说明原因,
    /// 而不是让用户以为自己配错了。S3 兼容实现普遍只实现协议的一个子集。
    /// </summary>
    [TestMethod]
    public async Task GetBucketConfig_WhenServerDoesNotImplementIt_ReportsUnsupported()
    {
        _client.GetBucketReplicationAsync(Arg.Any<GetBucketReplicationRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetBucketReplicationResponse>>(_ => throw Aws("NotImplemented", HttpStatusCode.NotImplemented));

        S3ConfigResult result = await _service.GetBucketConfigAsync(_session, Bucket, S3ConfigKind.Replication);

        Assert.IsFalse(result.Supported);
        Assert.IsFalse(result.Exists);
    }

    /// <summary>真正的失败(权限不足)仍必须抛出来,不能被当成空状态吞掉。</summary>
    [TestMethod]
    public async Task GetBucketConfig_WhenDenied_StillThrows()
    {
        _client.GetBucketPolicyAsync(Arg.Any<GetBucketPolicyRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetBucketPolicyResponse>>(_ => throw Aws("AccessDenied", HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<VelaS3PermissionDeniedException>(
            () => _service.GetBucketConfigAsync(_session, Bucket, S3ConfigKind.Policy));
    }

    // ---- 桶配置:读写 -------------------------------------------------------

    /// <summary>桶策略是一段原始 JSON,读取时只重新缩进,绝不重新序列化(那会丢字段)。</summary>
    [TestMethod]
    public async Task GetBucketConfig_Policy_ReturnsTheServerDocumentVerbatim()
    {
        const string policy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Vendor":"custom"}]}""";
        _client.GetBucketPolicyAsync(Arg.Any<GetBucketPolicyRequest>(), Arg.Any<CancellationToken>())
               .Returns(new GetBucketPolicyResponse { Policy = policy });

        S3ConfigResult result = await _service.GetBucketConfigAsync(_session, Bucket, S3ConfigKind.Policy);

        Assert.IsTrue(result.Exists);
        // 缩进会变,内容不变 —— 尤其是我们不认识的 "Vendor" 字段必须还在。
        StringAssert.Contains(result.Json, "\"Vendor\"");
        StringAssert.Contains(result.Json, "2012-10-17");
    }

    /// <summary>版本控制的读写映射。</summary>
    [TestMethod]
    public async Task BucketConfig_Versioning_RoundTrips()
    {
        _client.GetBucketVersioningAsync(Arg.Any<GetBucketVersioningRequest>(), Arg.Any<CancellationToken>())
               .Returns(new GetBucketVersioningResponse { VersioningConfig = new() { Status = VersionStatus.Enabled } });

        S3ConfigResult result = await _service.GetBucketConfigAsync(_session, Bucket, S3ConfigKind.Versioning);
        StringAssert.Contains(result.Json, "Enabled");

        await _service.PutBucketConfigAsync(_session, Bucket, S3ConfigKind.Versioning, """{"Status":"Suspended"}""");

        await _client.Received(1).PutBucketVersioningAsync(
            Arg.Is<PutBucketVersioningRequest>(r => r.BucketName == Bucket && r.VersioningConfig.Status == VersionStatus.Suspended),
            Arg.Any<CancellationToken>());
    }

    /// <summary>删除一项配置要打到对应的 DeleteBucketXxx 上。</summary>
    [TestMethod]
    public async Task DeleteBucketConfig_CallsTheMatchingOperation()
    {
        await _service.DeleteBucketConfigAsync(_session, Bucket, S3ConfigKind.Cors);

        await _client.Received(1).DeleteCORSConfigurationAsync(
            Arg.Is<DeleteCORSConfigurationRequest>(r => r.BucketName == Bucket), Arg.Any<CancellationToken>());
    }

    /// <summary>不可删除的配置要明确报「不支持」,而不是静默什么都不做。</summary>
    [TestMethod]
    public async Task DeleteBucketConfig_ForNonDeletableKind_ReportsUnsupported()
    {
        await Assert.ThrowsAsync<VelaS3UnsupportedOperationException>(
            () => _service.DeleteBucketConfigAsync(_session, Bucket, S3ConfigKind.Versioning));
    }

    /// <summary>按 id 分多份的配置要能列出全部 id。</summary>
    [TestMethod]
    public async Task ListBucketConfigIds_ReturnsEveryNamedConfiguration()
    {
        _client.ListBucketInventoryConfigurationsAsync(Arg.Any<ListBucketInventoryConfigurationsRequest>(), Arg.Any<CancellationToken>())
               .Returns(new ListBucketInventoryConfigurationsResponse
               {
                   InventoryConfigurationList = [new() { InventoryId = "daily" }, new() { InventoryId = "weekly" }],
               });

        IReadOnlyList<string> ids = await _service.ListBucketConfigIdsAsync(_session, Bucket, S3ConfigKind.Inventory);

        Assert.AreSequenceEqual(["daily", "weekly"], [.. ids], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    // ---- 版本 ---------------------------------------------------------------

    /// <summary>版本列表要把删除标记如实标出来 —— 删掉它就是"恢复被删除的对象"。</summary>
    [TestMethod]
    public async Task ListObjectVersions_MapsDeleteMarkersAndLatestFlag()
    {
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
               .Returns(new ListVersionsResponse
               {
                   IsTruncated = false,
                   Versions =
                   [
                       new SdkS3ObjectVersion { Key = "a.txt", VersionId = "v2", IsLatest = true, IsDeleteMarker = true, Size = 0 },
                       new SdkS3ObjectVersion { Key = "a.txt", VersionId = "v1", IsLatest = false, IsDeleteMarker = false, Size = 12, ETag = "\"abc\"" },
                   ],
               });

        IReadOnlyList<CoreS3ObjectVersion> versions = await _service.ListObjectVersionsAsync(_session, Bucket, "a.txt");

        Assert.HasCount(2, versions);
        Assert.IsTrue(versions[0].IsDeleteMarker);
        Assert.IsTrue(versions[0].IsLatest);
        Assert.IsFalse(versions[1].IsDeleteMarker);
        Assert.AreEqual(12, versions[1].Size);
        Assert.AreEqual("abc", versions[1].ETag, "ETag 两端的引号要去掉。");
    }

    /// <summary>
    /// 恢复历史版本必须以**复制**实现,绝不能删掉它之后的版本 ——
    /// 后者会永久销毁数据且无法挽回。
    /// </summary>
    [TestMethod]
    public async Task RestoreObjectVersion_CopiesRatherThanDeleting()
    {
        await _service.RestoreObjectVersionAsync(_session, Bucket, "a.txt", "v1");

        await _client.Received(1).CopyObjectAsync(
            Arg.Is<CopyObjectRequest>(r =>
                r.SourceBucket == Bucket && r.SourceKey == "a.txt" && r.SourceVersionId == "v1" &&
                r.DestinationBucket == Bucket && r.DestinationKey == "a.txt"),
            Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    // ---- 对象属性 -----------------------------------------------------------

    /// <summary>清空标签要走 DeleteObjectTagging,而不是写一个空的标签集(有些实现会拒)。</summary>
    [TestMethod]
    public async Task PutObjectTags_WithEmptyList_DeletesTheTagSet()
    {
        await _service.PutObjectTagsAsync(_session, Bucket, "a.txt", []);

        await _client.Received(1).DeleteObjectTaggingAsync(
            Arg.Is<DeleteObjectTaggingRequest>(r => r.Key == "a.txt"), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().PutObjectTaggingAsync(Arg.Any<PutObjectTaggingRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>写标签的映射。</summary>
    [TestMethod]
    public async Task PutObjectTags_SendsEveryTag()
    {
        await _service.PutObjectTagsAsync(_session, Bucket, "a.txt", [new("env", "prod"), new("team", "infra")]);

        await _client.Received(1).PutObjectTaggingAsync(
            Arg.Is<PutObjectTaggingRequest>(r => r.Tagging.TagSet.Count == 2 &&
                                                 r.Tagging.TagSet.Any(t => t.Key == "env" && t.Value == "prod")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 改存储类别在 S3 上没有原地操作,协议规定的做法是**把对象复制到自己身上**并带上新类别。
    /// </summary>
    [TestMethod]
    public async Task ChangeStorageClass_IsImplementedAsASelfCopy()
    {
        await _service.ChangeStorageClassAsync(_session, Bucket, "a.txt", "GLACIER");

        await _client.Received(1).CopyObjectAsync(
            Arg.Is<CopyObjectRequest>(r =>
                r.SourceBucket == Bucket && r.SourceKey == "a.txt" &&
                r.DestinationBucket == Bucket && r.DestinationKey == "a.txt" &&
                r.StorageClass == S3StorageClass.Glacier),
            Arg.Any<CancellationToken>());
    }

    /// <summary>写自定义元数据必须带 REPLACE,否则服务端会原样保留旧元数据。</summary>
    [TestMethod]
    public async Task PutObjectMetadata_UsesReplaceDirective()
    {
        await _service.PutObjectMetadataAsync(_session, Bucket, "a.txt", [new("owner", "ops")]);

        await _client.Received(1).CopyObjectAsync(
            Arg.Is<CopyObjectRequest>(r => r.MetadataDirective == S3MetadataDirective.REPLACE),
            Arg.Any<CancellationToken>());
    }

    /// <summary>没设过保留策略是常态,要给空值而不是抛。</summary>
    [TestMethod]
    public async Task GetObjectRetention_WhenNoneSet_ReturnsEmpty()
    {
        _client.GetObjectRetentionAsync(Arg.Any<GetObjectRetentionRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetObjectRetentionResponse>>(_ => throw Aws("NoSuchObjectLockConfiguration", HttpStatusCode.NotFound));

        S3Retention retention = await _service.GetObjectRetentionAsync(_session, Bucket, "a.txt");

        Assert.IsEmpty(retention.Mode);
        Assert.IsNull(retention.RetainUntil);
    }

    /// <summary>合法保留的开关映射。</summary>
    [TestMethod]
    public async Task PutObjectLegalHold_MapsBooleanToStatus()
    {
        await _service.PutObjectLegalHoldAsync(_session, Bucket, "a.txt", enabled: true);

        await _client.Received(1).PutObjectLegalHoldAsync(
            Arg.Is<PutObjectLegalHoldRequest>(r => r.LegalHold.Status == ObjectLockLegalHoldStatus.On),
            Arg.Any<CancellationToken>());
    }

    /// <summary>归档取回的天数下限是 1,传 0 会被服务端拒。</summary>
    [TestMethod]
    public async Task RestoreArchivedObject_ClampsDaysToAtLeastOne()
    {
        await _service.RestoreArchivedObjectAsync(_session, Bucket, "a.txt", new(0, "Bulk"));

        await _client.Received(1).RestoreObjectAsync(
            Arg.Is<RestoreObjectRequest>(r => r.Days == 1 && r.RetrievalTier == GlacierJobTier.Bulk),
            Arg.Any<CancellationToken>());
    }

    /// <summary>中止分片上传的映射。</summary>
    [TestMethod]
    public async Task AbortMultipartUpload_SendsKeyAndUploadId()
    {
        await _service.AbortMultipartUploadAsync(_session, Bucket, "big.bin", "upload-1");

        await _client.Received(1).AbortMultipartUploadAsync(
            Arg.Is<AbortMultipartUploadRequest>(r => r.Key == "big.bin" && r.UploadId == "upload-1"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>列不出未完成上传时给空列表(不少实现不支持该接口),而不是让页面报错。</summary>
    [TestMethod]
    public async Task ListMultipartUploads_WhenUnsupported_ReturnsEmpty()
    {
        _client.ListMultipartUploadsAsync(Arg.Any<ListMultipartUploadsRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<ListMultipartUploadsResponse>>(_ => throw Aws("NotImplemented", HttpStatusCode.NotImplemented));

        Assert.IsEmpty(await _service.ListMultipartUploadsAsync(_session, Bucket));
    }

    /// <summary>概览页在个别接口不被支持时也必须能打开,缺的字段留空即可。</summary>
    [TestMethod]
    public async Task GetBucketOverview_SurvivesUnsupportedSubQueries()
    {
        _client.GetBucketLocationAsync(Arg.Any<GetBucketLocationRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetBucketLocationResponse>>(_ => throw Aws("NotImplemented", HttpStatusCode.NotImplemented));
        _client.GetBucketPolicyStatusAsync(Arg.Any<GetBucketPolicyStatusRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GetBucketPolicyStatusResponse>>(_ => throw Aws("NotImplemented", HttpStatusCode.NotImplemented));
        _client.GetBucketVersioningAsync(Arg.Any<GetBucketVersioningRequest>(), Arg.Any<CancellationToken>())
               .Returns(new GetBucketVersioningResponse { VersioningConfig = new() { Status = VersionStatus.Enabled } });

        S3BucketOverview overview = await _service.GetBucketOverviewAsync(_session, Bucket);

        Assert.AreEqual(Bucket, overview.Name);
        // 拿不到区域时回落会话配置的区域,而不是空串。
        Assert.AreEqual("us-east-1", overview.Region);
        Assert.AreEqual("Enabled", overview.VersioningStatus);
        Assert.IsNull(overview.IsPublic, "拿不到就该是「未知」,不是 false。");
    }

    /// <summary>把会话解析成替身客户端的访问器。</summary>
    private sealed class FakeAccessor(IAmazonS3 client, Guid sessionId) : IS3ClientAccessor
    {
        public IAmazonS3 GetClient(Guid id) =>
            id == sessionId ? client : throw new VelaS3ConnectionException($"S3 session {id} is not open.");

        public S3ConnectionInfo GetConnectionInfo(Guid id) =>
            new() { Endpoint = "127.0.0.1", Settings = new S3Settings { Region = "us-east-1" } };

        public Exception TranslateFault(Guid id, Exception exception, string operation) =>
            S3Interop.Translate(exception, operation);
    }
}

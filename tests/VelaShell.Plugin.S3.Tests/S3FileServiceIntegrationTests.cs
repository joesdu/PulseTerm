using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// <see cref="S3ProtocolFileSystem" /> 打到一台真的会说 HTTP、也真的会校验 SigV4 的环回服务器上
/// (<see cref="LoopbackS3Server" />)。
/// <para>
/// 这组测试守住的是这套后端最容易错、也最值钱的两件事:
/// 其一,**平的键空间能不能被折叠成目录树** —— 公共前缀、目录占位符、分页;
/// 其二,**签好的那份规范形式与实际发出去的请求是不是同一个东西** —— 服务器会重算签名,
/// 对不上就直接 403,而不是让 bug 潜到真实 MinIO 上才炸。
/// </para>
/// </summary>
[TestClass]
[TestCategory("S3")]
public sealed class S3FileServiceIntegrationTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Bucket = "test-bucket";

    private LoopbackS3Server _server = null!;
    private S3ProtocolFileSystem _service = null!;
    private Guid _session;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _server = new(AccessKey, SecretKey);
        _server.AddBucket(Bucket);
        _service = new();
        _session = await _service.OpenSessionAsync("test-session", CreateConnectionInfo());
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        await _service.DisposeAsync();
        _server.Dispose();
    }

    private S3ConnectionInfo CreateConnectionInfo(string? defaultBucket = null) =>
        new()
        {
            Endpoint = "127.0.0.1",
            Port = _server.Port,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            Settings = new()
            {
                UseTls = false,
                Region = _server.Region,
                AddressingStyle = S3AddressingStyle.Path,
                DefaultBucket = defaultBucket,
                // 压到最小分片,让分片上传路径在几十 KB 的测试数据上就能走到。
                PartSizeBytes = S3Settings.MinPartSizeBytes,
            },
        };

    /// <summary>每个用例结束时都断言签名从未失败过 —— 这是整组测试的隐含契约。</summary>
    private void AssertAllRequestsSigned() =>
        Assert.AreEqual(0, _server.SignatureFailures,
            "服务器重算 SigV4 后与客户端发来的签名对不上:客户端签的和发的不是同一份请求。");

    // ---- 列举 ---------------------------------------------------------------

    /// <summary>根目录列出的是桶,每个桶显示为一个目录。</summary>
    [TestMethod]
    public async Task ListDirectory_Root_ReturnsBucketsAsDirectories()
    {
        _server.AddBucket("another-bucket");

        List<S3FileEntry> entries = await _service.ListDirectoryAsync(_session, "/");

        Assert.HasCount(2, entries);
        Assert.IsTrue(entries.All(e => e.IsDirectory));
        Assert.AreSequenceEqual(["another-bucket", "test-bucket"], [.. entries.Select(e => e.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreEqual("/test-bucket", entries.Single(e => e.Name == Bucket).FullPath);
        AssertAllRequestsSigned();
    }

    /// <summary>
    /// 平的键空间折叠成目录树:同层的对象是文件,含分隔符的键收敛成一个目录。
    /// 这是整个后端最核心的一次翻译。
    /// </summary>
    [TestMethod]
    public async Task ListDirectory_FoldsCommonPrefixesIntoDirectories()
    {
        _server.AddObject(Bucket, "readme.txt", "hello");
        _server.AddObject(Bucket, "logs/a.log", "a");
        _server.AddObject(Bucket, "logs/b.log", "b");
        _server.AddObject(Bucket, "logs/2026/c.log", "c");

        List<S3FileEntry> root = await _service.ListDirectoryAsync(_session, "/test-bucket");

        Assert.HasCount(2, root);
        S3FileEntry file = root.Single(e => !e.IsDirectory);
        Assert.AreEqual("readme.txt", file.Name);
        Assert.AreEqual("/test-bucket/readme.txt", file.FullPath);
        Assert.AreEqual(5, file.Size);
        S3FileEntry directory = root.Single(e => e.IsDirectory);
        Assert.AreEqual("logs", directory.Name);
        Assert.AreEqual("/test-bucket/logs", directory.FullPath);

        // 进到子目录:两个文件 + 一个更深的目录。
        List<S3FileEntry> logs = await _service.ListDirectoryAsync(_session, "/test-bucket/logs");
        Assert.AreSequenceEqual(["a.log", "b.log", "2026"], [.. logs.Select(e => e.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreEqual("/test-bucket/logs/2026", logs.Single(e => e.IsDirectory).FullPath);
        AssertAllRequestsSigned();
    }

    /// <summary>
    /// 以 <c>/</c> 结尾的零字节对象是目录占位符,默认必须隐掉 ——
    /// 否则每个目录旁边都会多出一个同名的空文件。
    /// </summary>
    [TestMethod]
    public async Task ListDirectory_HidesFolderMarkerOfCurrentDirectory()
    {
        _server.AddObject(Bucket, "empty/", string.Empty);
        _server.AddObject(Bucket, "empty/file.txt", "x");

        List<S3FileEntry> entries = await _service.ListDirectoryAsync(_session, "/test-bucket/empty");

        // 只有 file.txt;键正好等于前缀的那个占位符是"这个目录自己",不该作为条目出现。
        Assert.HasCount(1, entries);
        Assert.AreEqual("file.txt", entries[0].Name);
        AssertAllRequestsSigned();
    }

    /// <summary>分页:服务器每页只给 1 条,客户端必须靠续传令牌把所有页都取回来。</summary>
    [TestMethod]
    public async Task ListDirectory_FollowsContinuationTokenAcrossPages()
    {
        for (int i = 0; i < 7; i++)
        {
            _server.AddObject(Bucket, $"page/file-{i}.txt", $"content-{i}");
        }

        List<S3FileEntry> entries = await _service.ListDirectoryAsync(_session, "/test-bucket/page");

        Assert.HasCount(7, entries);
        Assert.AreSequenceEqual(
            [.. Enumerable.Range(0, 7).Select(i => $"file-{i}.txt")], [.. entries.Select(e => e.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        AssertAllRequestsSigned();
    }

    // ---- 元数据 -------------------------------------------------------------

    /// <summary>对象与「目录」都要能被 stat 出来,且目录判定基于「该前缀下有没有东西」。</summary>
    [TestMethod]
    public async Task GetFileInfo_DistinguishesObjectsFromPrefixes()
    {
        _server.AddObject(Bucket, "docs/spec.md", "spec");

        S3FileEntry file = await _service.GetFileInfoAsync(_session, "/test-bucket/docs/spec.md");
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(4, file.Size);
        Assert.AreEqual("spec.md", file.Name);

        S3FileEntry directory = await _service.GetFileInfoAsync(_session, "/test-bucket/docs");
        Assert.IsTrue(directory.IsDirectory);

        S3FileEntry bucket = await _service.GetFileInfoAsync(_session, "/test-bucket");
        Assert.IsTrue(bucket.IsDirectory);

        await Assert.ThrowsExactlyAsync<VelaS3PathNotFoundException>(
            () => _service.GetFileInfoAsync(_session, "/test-bucket/nope.txt"));
        AssertAllRequestsSigned();
    }

    /// <summary>存在性判定对对象、前缀、桶都成立。</summary>
    [TestMethod]
    public async Task Exists_CoversObjectsPrefixesAndBuckets()
    {
        _server.AddObject(Bucket, "a/b.txt", "x");

        Assert.IsTrue(await _service.ExistsAsync(_session, "/test-bucket/a/b.txt"));
        Assert.IsTrue(await _service.ExistsAsync(_session, "/test-bucket/a"));
        Assert.IsTrue(await _service.ExistsAsync(_session, "/test-bucket"));
        Assert.IsTrue(await _service.ExistsAsync(_session, "/"));
        Assert.IsFalse(await _service.ExistsAsync(_session, "/test-bucket/missing"));
        Assert.IsFalse(await _service.ExistsAsync(_session, "/no-such-bucket"));
        AssertAllRequestsSigned();
    }

    /// <summary>chmod 在 S3 上没有对应语义,必须明确拒绝而不是假装成功。</summary>
    [TestMethod]
    public async Task SetPermissions_IsRejectedAsUnsupported()
    {
        await Assert.ThrowsExactlyAsync<VelaS3UnsupportedOperationException>(
            () => _service.SetPermissionsAsync(_session, "/test-bucket/a.txt", 644));
    }

    /// <summary>没配默认桶时落在根;配了则直接落在桶内。</summary>
    [TestMethod]
    public async Task GetWorkingDirectory_HonoursDefaultBucket()
    {
        Assert.AreEqual("/", await _service.GetWorkingDirectoryAsync(_session));

        await using var scoped = new S3ProtocolFileSystem();
        Guid session = await scoped.OpenSessionAsync("scoped-session", CreateConnectionInfo(Bucket));
        Assert.AreEqual("/test-bucket", await scoped.GetWorkingDirectoryAsync(session));
        AssertAllRequestsSigned();
    }

    // ---- 传输 ---------------------------------------------------------------

    /// <summary>小文件走单次 PUT;内容与进度都要对得上。</summary>
    [TestMethod]
    public async Task Upload_SmallFile_UsesSinglePut()
    {
        string local = CreateTempFile(Encoding.UTF8.GetBytes("hello s3"));
        try
        {
            List<RemoteTransferProgress> progress = [];
            await _service.UploadFileAsync(_session, local, "/test-bucket/greeting.txt",
                new SynchronousProgress<RemoteTransferProgress>(progress.Add));

            Assert.AreSequenceEqual("hello s3"u8.ToArray(), _server.GetObject(Bucket, "greeting.txt"));
            Assert.IsGreaterThanOrEqualTo(1, progress.Count);
            Assert.AreEqual(progress[^1].TotalBytes, progress[^1].TransferredBytes, "最后一次上报必须是满进度。");
            // 单次 PUT 不会发起分片上传。
            Assert.DoesNotContain(r => r.Contains("uploads", StringComparison.Ordinal), _server.Requests);
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// 超过分片阈值的文件走分片上传:必须发起 uploads、分片并发上传、最后 complete,
    /// 且拼回来的内容与原文件逐字节一致(分片顺序错了这里就会不一致)。
    /// </summary>
    [TestMethod]
    public async Task Upload_LargeFile_UsesMultipartAndReassemblesExactly()
    {
        // 略大于两个最小分片,保证至少 3 片(含一个小尾片)。
        byte[] content = new byte[(int)(S3Settings.MinPartSizeBytes * 2) + 4096];
        Random.Shared.NextBytes(content);
        string local = CreateTempFile(content);
        try
        {
            await _service.UploadFileAsync(_session, local, "/test-bucket/big.bin");

            byte[]? stored = _server.GetObject(Bucket, "big.bin");
            Assert.IsNotNull(stored);
            Assert.AreSequenceEqual(content, stored);
            Assert.Contains(r => r.Contains("uploads", StringComparison.Ordinal), _server.Requests,
                "大文件必须走分片上传。");
            Assert.Contains(r => r.Contains("partNumber=", StringComparison.Ordinal), _server.Requests);
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// 内容类型按扩展名推断并真的发出去。S3 会把它原样存下来、下载时回给浏览器 ——
    /// 全传 application/octet-stream 会让分享出去的图片/网页变成下载文件。
    /// <para>
    /// **分片上传这条尤其要守**:对象的内容类型只在发起分片上传那一步能指定,
    /// 后续的 UploadPart / Complete 都没有机会再给。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Upload_SendsGuessedContentType_ForBothSinglePutAndMultipart()
    {
        string small = CreateTempFile("<html></html>"u8.ToArray());
        byte[] large = new byte[(int)S3Settings.MinPartSizeBytes + 1024];
        Random.Shared.NextBytes(large);
        string big = CreateTempFile(large);
        try
        {
            await _service.UploadFileAsync(_session, small, "/test-bucket/page.html");
            await _service.UploadFileAsync(_session, big, "/test-bucket/photo.png");

            Assert.AreEqual("text/html; charset=utf-8", _server.ContentTypes.GetValueOrDefault("page.html"));
            Assert.AreEqual("image/png", _server.ContentTypes.GetValueOrDefault("photo.png"),
                "分片上传的内容类型必须在发起那一步就带上。");
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(small);
            File.Delete(big);
        }
    }

    /// <summary>下载写出的内容要与远端一致。</summary>
    [TestMethod]
    public async Task Download_WritesExactContent()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('x', 4096) + "tail");
        _server.AddObject(Bucket, "data/blob.bin", content);
        string local = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        try
        {
            List<RemoteTransferProgress> progress = [];
            await _service.DownloadFileAsync(_session, "/test-bucket/data/blob.bin", local,
                new SynchronousProgress<RemoteTransferProgress>(progress.Add));

            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(local));
            Assert.AreEqual(progress[^1].TotalBytes, progress[^1].TransferredBytes, "最后一次上报必须是满进度。");
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// HEAD 被拒、GET 放行时下载照样要成功 —— HEAD 只是拿总长度与 mtime 的优化,不是下载的前提。
    /// <para>
    /// 真实场景:对象是公共读的,而这把访问密钥没被授予 HeadObject。强制先 HEAD 会把
    /// 本来能下的文件全挡死,且 HEAD 的 403 没有响应体,用户只会看到一句
    /// 「No further error information was returned by the service」。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Download_HeadDenied_StillDownloadsViaGet()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('z', 3000) + "end");
        _server.AddObject(Bucket, "public/asset.png", content);
        _server.DeniedMethods.Add("HEAD");
        string local = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        try
        {
            List<RemoteTransferProgress> progress = [];
            await _service.DownloadFileAsync(_session, "/test-bucket/public/asset.png", local,
                new SynchronousProgress<RemoteTransferProgress>(progress.Add));

            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(local));
            // 总长度只能来自 GET 响应,但进度依然要收在满格上。
            Assert.AreEqual(content.Length, progress[^1].TotalBytes);
            Assert.AreEqual(progress[^1].TotalBytes, progress[^1].TransferredBytes, "最后一次上报必须是满进度。");
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>
    /// 直取被拒、但预签名放行时,下载要自动改走预签名 URL 完成 —— 「有的桶只给了预签名下载权限」。
    /// </summary>
    [TestMethod]
    public async Task Download_DirectReadDenied_FallsBackToPresignedUrl()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('p', 5000) + "tail");
        _server.AddObject(Bucket, "locked/asset.bin", content);
        _server.DenyDirectReads = true;
        string local = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        try
        {
            List<RemoteTransferProgress> progress = [];
            await _service.DownloadFileAsync(_session, "/test-bucket/locked/asset.bin", local,
                new SynchronousProgress<RemoteTransferProgress>(progress.Add));

            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(local));
            Assert.AreEqual(content.Length, progress[^1].TotalBytes);
            Assert.AreEqual(progress[^1].TotalBytes, progress[^1].TransferredBytes, "最后一次上报必须是满进度。");
            // 预签名那次也必须是签对的:服务器重算签名,对不上会计入 SignatureFailures。
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>预览(OpenRead)走的是同一条取流路径,同样要能靠预签名兜住。</summary>
    [TestMethod]
    public async Task OpenRead_DirectReadDenied_FallsBackToPresignedUrl()
    {
        _server.AddObject(Bucket, "locked/note.txt", "hello presigned");
        _server.DenyDirectReads = true;

        await using Stream stream = await _service.OpenReadAsync(_session, "/test-bucket/locked/note.txt");
        using var reader = new StreamReader(stream);

        Assert.AreEqual("hello presigned", await reader.ReadToEndAsync());
        AssertAllRequestsSigned();
    }

    /// <summary>
    /// GET 也被拒时,报出来的必须是 GET 那份带正文的错误(AccessDenied),而不是 HEAD 那句空话;
    /// 且消息里要带上是哪个对象 —— 多选下载失败时用户得知道是哪一个。
    /// </summary>
    [TestMethod]
    public async Task Download_GetDenied_ReportsAccessDeniedWithObjectPath()
    {
        _server.AddObject(Bucket, "public/asset.png", "x");
        _server.DeniedMethods.Add("HEAD");
        _server.DeniedMethods.Add("GET");
        string local = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        try
        {
            VelaS3PermissionDeniedException error =
                await Assert.ThrowsExactlyAsync<VelaS3PermissionDeniedException>(
                    () => _service.DownloadFileAsync(_session, "/test-bucket/public/asset.png", local));

            Assert.AreEqual("AccessDenied", error.ErrorCode);
            StringAssert.Contains(error.Message, "/test-bucket/public/asset.png");
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>断点续传:已有半个本地文件时用 Range 只取剩下那半,拼出来仍要与原文一致。</summary>
    [TestMethod]
    public async Task Download_ResumesWithRangeRequest()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('a', 1000) + new string('b', 1000));
        _server.AddObject(Bucket, "resume.bin", content);
        string local = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(local, content[..1000]);

            await _service.DownloadFileAsync(_session, "/test-bucket/resume.bin", local, resumeOffset: 1000);

            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(local));
            AssertAllRequestsSigned();
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>只读流按顺序读出完整内容(大文件预览走这条,不经本地临时文件)。</summary>
    [TestMethod]
    public async Task OpenRead_StreamsObjectContent()
    {
        _server.AddObject(Bucket, "stream.txt", "streamed content");

        await using Stream stream = await _service.OpenReadAsync(_session, "/test-bucket/stream.txt");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        Assert.AreEqual("streamed content", await reader.ReadToEndAsync());
        AssertAllRequestsSigned();
    }

    // ---- 写与删 -------------------------------------------------------------

    /// <summary>建目录写的是以 <c>/</c> 结尾的零字节占位对象(S3 生态的既成约定)。</summary>
    [TestMethod]
    public async Task CreateDirectory_WritesFolderMarker()
    {
        await _service.CreateDirectoryAsync(_session, "/test-bucket/newdir");

        byte[]? marker = _server.GetObject(Bucket, "newdir/");
        Assert.IsNotNull(marker);
        Assert.IsEmpty(marker);
        AssertAllRequestsSigned();
    }

    /// <summary>在桶这一层建目录 = 新建桶。</summary>
    [TestMethod]
    public async Task CreateDirectory_AtBucketLevel_CreatesBucket()
    {
        await _service.CreateDirectoryAsync(_session, "/brand-new-bucket");

        Assert.IsTrue(_server.HasBucket("brand-new-bucket"));
        AssertAllRequestsSigned();
    }

    /// <summary>删文件只删那一个键。</summary>
    [TestMethod]
    public async Task Delete_Object_RemovesOnlyThatKey()
    {
        _server.AddObject(Bucket, "keep.txt", "keep");
        _server.AddObject(Bucket, "drop.txt", "drop");

        await _service.DeleteAsync(_session, "/test-bucket/drop.txt");

        Assert.AreSequenceEqual(["keep.txt"], [.. _server.Keys(Bucket)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        AssertAllRequestsSigned();
    }

    /// <summary>删目录要递归删掉该前缀下的全部对象,且只删这个前缀下的。</summary>
    [TestMethod]
    public async Task Delete_Prefix_RemovesEverythingBeneathIt()
    {
        _server.AddObject(Bucket, "tree/a.txt", "a");
        _server.AddObject(Bucket, "tree/sub/b.txt", "b");
        _server.AddObject(Bucket, "tree/sub/deep/c.txt", "c");
        _server.AddObject(Bucket, "treasure.txt", "not part of tree/");

        List<ProtocolDeleteProgress> progress = [];
        await _service.DeleteAsync(_session, "/test-bucket/tree",
            new SynchronousProgress<ProtocolDeleteProgress>(progress.Add));

        // "treasure.txt" 以 "tree" 开头但不在 "tree/" 前缀下 —— 前缀拼接漏了斜杠就会误删它。
        Assert.AreSequenceEqual(["treasure.txt"], [.. _server.Keys(Bucket)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.IsGreaterThanOrEqualTo(1, progress.Count);
        AssertAllRequestsSigned();
    }

    /// <summary>删不存在的路径要报「找不到」,而不是静默成功。</summary>
    [TestMethod]
    public async Task Delete_MissingPath_Throws()
    {
        await Assert.ThrowsExactlyAsync<VelaS3PathNotFoundException>(
            () => _service.DeleteAsync(_session, "/test-bucket/ghost"));
    }

    /// <summary>空桶可以删掉。</summary>
    [TestMethod]
    public async Task Delete_EmptyBucket_RemovesIt()
    {
        _server.AddBucket("empty-bucket");

        await _service.DeleteAsync(_session, "/empty-bucket");

        Assert.IsFalse(_server.HasBucket("empty-bucket"));
        AssertAllRequestsSigned();
    }

    /// <summary>
    /// 非空桶**不得**被代为清空。桶在根视图里与普通目录长得一样,删除确认也只说
    /// 「删除文件夹 xxx?」—— 代为清空等于让一次误点删掉整桶数据。这里如实透出
    /// 服务端的拒绝,桶与其内容都必须原封不动。
    /// </summary>
    [TestMethod]
    public async Task Delete_NonEmptyBucket_IsRefusedAndLeavesEverythingIntact()
    {
        _server.AddObject(Bucket, "precious.dat", "irreplaceable");

        await Assert.ThrowsAsync<VelaS3OperationException>(
            () => _service.DeleteAsync(_session, "/test-bucket"));

        Assert.IsTrue(_server.HasBucket(Bucket), "桶不能被删掉。");
        Assert.AreSequenceEqual("irreplaceable"u8.ToArray(), _server.GetObject(Bucket, "precious.dat"), "桶内的对象一个都不能少。");
        AssertAllRequestsSigned();
    }

    // ---- 复制与移动 ---------------------------------------------------------

    /// <summary>单对象复制走服务端 CopyObject,数据不经本地。</summary>
    [TestMethod]
    public async Task Copy_SingleObject_UsesServerSideCopy()
    {
        _server.AddObject(Bucket, "src.txt", "payload");

        await _service.CopyAsync(_session, "/test-bucket/src.txt", "/test-bucket/dst.txt");

        Assert.AreSequenceEqual("payload"u8.ToArray(), _server.GetObject(Bucket, "dst.txt"));
        Assert.AreSequenceEqual("payload"u8.ToArray(), _server.GetObject(Bucket, "src.txt"));
        AssertAllRequestsSigned();
    }

    /// <summary>整棵前缀树的复制要保持相对结构。</summary>
    [TestMethod]
    public async Task Copy_Prefix_PreservesRelativeLayout()
    {
        _server.AddObject(Bucket, "from/one.txt", "1");
        _server.AddObject(Bucket, "from/nested/two.txt", "2");

        await _service.CopyAsync(_session, "/test-bucket/from", "/test-bucket/to");

        Assert.AreSequenceEqual("1"u8.ToArray(), _server.GetObject(Bucket, "to/one.txt"));
        Assert.AreSequenceEqual("2"u8.ToArray(), _server.GetObject(Bucket, "to/nested/two.txt"));
        AssertAllRequestsSigned();
    }

    /// <summary>重命名 = 复制 + 删源;源必须真的消失。</summary>
    [TestMethod]
    public async Task Rename_CopiesThenDeletesSource()
    {
        _server.AddObject(Bucket, "old-name.txt", "content");

        await _service.RenameAsync(_session, "/test-bucket/old-name.txt", "/test-bucket/new-name.txt");

        Assert.AreSequenceEqual("content"u8.ToArray(), _server.GetObject(Bucket, "new-name.txt"));
        Assert.IsNull(_server.GetObject(Bucket, "old-name.txt"));
        AssertAllRequestsSigned();
    }

    /// <summary>目录改名要把整棵子树搬过去,原前缀下不能有残留。</summary>
    [TestMethod]
    public async Task Rename_Prefix_MovesWholeSubtree()
    {
        _server.AddObject(Bucket, "v1/a.txt", "a");
        _server.AddObject(Bucket, "v1/sub/b.txt", "b");

        await _service.RenameAsync(_session, "/test-bucket/v1", "/test-bucket/v2");

        Assert.AreSequenceEqual(["v2/a.txt", "v2/sub/b.txt"], [.. _server.Keys(Bucket)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        AssertAllRequestsSigned();
    }

    /// <summary>桶改不了名 —— 这是 S3 的硬限制,要明确报「不支持」。</summary>
    [TestMethod]
    public async Task Rename_Bucket_IsRejectedAsUnsupported()
    {
        await Assert.ThrowsExactlyAsync<VelaS3UnsupportedOperationException>(
            () => _service.RenameAsync(_session, "/test-bucket", "/renamed-bucket"));
    }

    // ---- 会话 ---------------------------------------------------------------

    /// <summary>凭据不对时,打开会话就该失败(而不是等用户点开目录才炸)。</summary>
    [TestMethod]
    public async Task OpenSession_WithWrongCredentials_FailsFast()
    {
        await using var service = new S3ProtocolFileSystem();

        await Assert.ThrowsExactlyAsync<VelaS3AuthenticationException>(() => service.OpenSessionAsync("probe-session", new()
        {
            Endpoint = "127.0.0.1",
            Port = _server.Port,
            AccessKeyId = AccessKey,
            SecretAccessKey = "wrong-secret",
            Settings = CreateConnectionInfo().Settings,
        }));
    }

    /// <summary>端点不可达时抛连接类异常(而不是让裸 SocketException 越过 Infrastructure 边界)。</summary>
    [TestMethod]
    public async Task OpenSession_WithUnreachableEndpoint_ThrowsConnectionException()
    {
        await using var service = new S3ProtocolFileSystem();

        await Assert.ThrowsExactlyAsync<VelaS3ConnectionException>(() => service.OpenSessionAsync("probe-session", new()
        {
            // 端口 1 上不会有 S3 服务。
            Endpoint = "127.0.0.1",
            Port = 1,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            Settings = new() { UseTls = false, AddressingStyle = S3AddressingStyle.Path },
        }));
    }

    /// <summary>会话归属可查(供路由分派),关闭后不再持有。</summary>
    [TestMethod]
    public async Task Session_OwnershipTracksLifecycle()
    {
        Assert.IsTrue(_service.OwnsSession(_session));

        List<ProtocolSessionStateChange> changes = [];
        _service.SessionStateChanged += (_, change) => changes.Add(change);
        await _service.CloseSessionAsync(_session);

        Assert.IsFalse(_service.OwnsSession(_session));
        Assert.AreEqual(ProtocolSessionState.Closed, changes.Single().State);
    }

    /// <summary>预签名 URL 要带齐 SigV4 的查询参数(它是 S3 独有、用户最常要的一个能力)。</summary>
    [TestMethod]
    public async Task CreatePresignedUrl_ContainsSignatureParameters()
    {
        _server.AddObject(Bucket, "share/report.pdf", "pdf");

        string url = await _service.CreatePresignedUrlAsync(_session, "/test-bucket/share/report.pdf", TimeSpan.FromHours(1));

        // 按组成部分断言而不是整串前缀:签名参数的**顺序**由 SDK 决定,不是我们的契约,
        // 钉死顺序只会在 SDK 升级时无谓地红一次。真正要守的是"指向对的对象 + 带齐签名参数"。
        var parsed = new Uri(url);
        Assert.AreEqual("127.0.0.1", parsed.Host, url);
        Assert.AreEqual(_server.Port, parsed.Port, url);
        Assert.AreEqual("/test-bucket/share/report.pdf", Uri.UnescapeDataString(parsed.AbsolutePath), url);
        StringAssert.Contains(url, "X-Amz-Algorithm=AWS4-HMAC-SHA256", url);
        StringAssert.Contains(url, "X-Amz-Expires=3600", url);
        StringAssert.Contains(url, "X-Amz-SignedHeaders=host", url);
        StringAssert.Contains(url, "X-Amz-Signature=", url);
        StringAssert.Contains(url, "X-Amz-Credential=", url);
    }

    /// <summary>预签名 URL 要求一个对象路径;对着桶或根要求它没有意义。</summary>
    [TestMethod]
    public async Task CreatePresignedUrl_RequiresObjectPath()
    {
        await Assert.ThrowsExactlyAsync<VelaS3OperationException>(
            () => _service.CreatePresignedUrlAsync(_session, "/test-bucket", TimeSpan.FromHours(1)));
    }

    private static string CreateTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-s3-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>同步转发的进度接收器(<see cref="Progress{T}" /> 会异步投递,测试里拿不到确定的时序)。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

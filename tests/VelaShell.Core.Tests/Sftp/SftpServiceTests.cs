using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class SftpServiceTests
{
    private readonly ISshConnectionService _connectionService;
    private readonly Guid _sessionId;
    private readonly ISftpClientWrapper _sftpClient;
    private readonly SftpService _sftpService;

    public SftpServiceTests()
    {
        _connectionService = Substitute.For<ISshConnectionService>();
        _sftpClient = Substitute.For<ISftpClientWrapper>();
        _sessionId = Guid.NewGuid();
        var session = new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new()
            {
                Host = "test.example.com",
                Port = 22,
                Username = "testuser",
                AuthMethod = AuthMethod.Password,
                Password = "testpass"
            },
            Status = SessionStatus.Connected
        };
        _connectionService.GetSession(_sessionId).Returns(session);
        _sftpClient.IsConnected.Returns(true);
        _sftpService = new SftpService(_connectionService, _ => _sftpClient);
    }

    [TestMethod]
    public async Task UploadFileAsync_PreserveTimestampsOn_SetsRemoteMtimeToLocalFileMtime()
    {
        // 默认(无设置服务)保留时间戳 = true:上传完成后必须把远端 mtime 设回本地源文件的 mtime(scp -p 语义)。
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-mtime-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "timestamp probe");
        var knownUtc = new DateTime(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(localPath, knownUtc);
        try
        {
            _sftpClient.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<Action<ulong>>(), Arg.Any<CancellationToken>())
                       .Returns(Task.CompletedTask);

            await _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt");

            await _sftpClient.Received(1).SetLastWriteTimeAsync(
                "/home/user/up.txt",
                Arg.Is<DateTimeOffset>(d => d.UtcDateTime == knownUtc),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task UploadFileAsync_PreserveTimestampsOff_DoesNotTouchRemoteMtime()
    {
        ISettingsService settings = Substitute.For<VelaShell.Core.Data.ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Transfer = { PreserveTimestamps = false } });
        var service = new SftpService(_connectionService, _ => _sftpClient, settings);
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-mtime-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "timestamp probe");
        try
        {
            _sftpClient.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<Action<ulong>>(), Arg.Any<CancellationToken>())
                       .Returns(Task.CompletedTask);

            await service.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt");

            await _sftpClient.DidNotReceive().SetLastWriteTimeAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task GetWorkingDirectoryAsync_ReturnsClientWorkingDirectory()
    {
        _sftpClient.WorkingDirectory.Returns("/home/testuser");
        string result = await _sftpService.GetWorkingDirectoryAsync(_sessionId);
        Assert.AreEqual("/home/testuser", result);
    }

    [TestMethod]
    public async Task CloseSessionAsync_DisconnectsAndDisposesClient()
    {
        // Open a channel so it gets cached, then close it.
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));
        await _sftpService.ListDirectoryAsync(_sessionId, "/");
        await _sftpService.CloseSessionAsync(_sessionId);
        _sftpClient.Received(1).Disconnect();
        _sftpClient.Received(1).Dispose();
    }

    [TestMethod]
    public async Task CloseSessionAsync_UnknownSession_IsNoOp()
    {
        await _sftpService.CloseSessionAsync(Guid.NewGuid());
        _sftpClient.DidNotReceive().Disconnect();
        _sftpClient.DidNotReceive().Dispose();
    }

    [TestMethod]
    public async Task RenameAsync_CallsRenameFileOnClient()
    {
        await _sftpService.RenameAsync(_sessionId, "/home/user/old.txt", "/home/user/new.txt");
        await _sftpClient.Received(1).RenameFileAsync("/home/user/old.txt", "/home/user/new.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenPlainRenameRejected_FallsBackToPosixRename()
    {
        // Some servers answer the plain SSH_FXP_RENAME with SSH_FX_BAD_MESSAGE.
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpOperationException("bad message"));
        await _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir");
        await _sftpClient.Received(1).PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RenameAsync_WhenBothRenamesFail_SurfacesOriginalError()
    {
        _sftpClient
            .RenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpOperationException("bad message"));
        _sftpClient
            .PosixRenameFileAsync("/home/user/dir", "/tmp/dir", Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("posix-rename not supported"));
        VelaSftpOperationException ex = await Assert.ThrowsExactlyAsync<VelaSftpOperationException>(() => _sftpService.RenameAsync(_sessionId, "/home/user/dir", "/tmp/dir"));
        Assert.AreEqual("bad message", ex.Message);
    }

    [TestMethod]
    public async Task UploadFileAsync_WhenCancelledMidTransfer_SurfacesCleanOperationCanceled()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "payload");
        using var cts = new CancellationTokenSource();
        try
        {
            // 兜底路径:取消之后连接被撕掉,底层先冒出的是 IO 错误而不是取消异常。
            // 服务必须把它归一成干净的 OperationCanceledException,别让 IOException 漏到上层
            // 被当成"传输失败"而标红。
            _sftpClient.UploadAsync(Arg.Any<Stream>(), "/home/user/up.txt",
                           Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           cts.Cancel();
                           throw new IOException("connection torn down by cancellation");
                       });
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt", null, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    /// <summary>
    /// 正常取消路径:底层库自己响应 CancellationToken 抛出 OperationCanceledException,
    /// 服务原样放行 —— 不额外包一层、也不产生任何多余的异常。
    /// <para>
    /// 这条锁定的是"取消不该制造噪音"。早先的实现会在取消时把本地流 Dispose 掉
    /// (SSH.NET 时代的绕行手法,那时传输不响应取消),导致库内部先抛
    /// ObjectDisposedException/IOException,再被翻译成取消 —— 一次取消白白多出两个异常,
    /// 还和正在读写该流的工作线程赛跑。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_WhenLibraryHonoursTheToken_PropagatesCancellationWithoutExtraExceptions()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-up-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "payload");
        using var cts = new CancellationTokenSource();
        var thrown = new OperationCanceledException(cts.Token);
        try
        {
            _sftpClient.UploadAsync(Arg.Any<Stream>(), "/home/user/up.txt",
                           Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           cts.Cancel();
                           throw thrown;
                       });

            OperationCanceledException actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => _sftpService.UploadFileAsync(_sessionId, localPath, "/home/user/up.txt", null, cancellationToken: cts.Token));

            // 同一个实例原样上抛,证明没有被重新包装(重新包装 = 又多一个异常)。
            Assert.AreSame(thrown, actual);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task SetPermissionsAsync_CallsChangePermissionsOnClient()
    {
        await _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", 755);
        await _sftpClient.Received(1).ChangePermissionsAsync("/home/user/run.sh", 755, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow((short)-1)]
    [DataRow((short)778)] // last digit > 7
    [DataRow((short)787)] // middle digit > 7
    [DataRow((short)800)] // leading digit > 7
    public async Task SetPermissionsAsync_RejectsNonOctalModes(short mode)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => _sftpService.SetPermissionsAsync(_sessionId, "/home/user/run.sh", mode));
        await _sftpClient.DidNotReceive().ChangePermissionsAsync(Arg.Any<string>(), Arg.Any<short>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateFileAsync_UploadsAnEmptyStream()
    {
        await _sftpService.CreateFileAsync(_sessionId, "/home/user/empty.txt");
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            "/home/user/empty.txt",
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ListDirectoryAsync_ReturnsRemoteFileInfoArray()
    {
        // Arrange
        var mockFiles = new List<SftpEntry>
        {
            CreateMockSftpFile("file1.txt", "/home/user/file1.txt", 1024, false, "rw-r--r--"),
            CreateMockSftpFile("dir1", "/home/user/dir1", 0, true, "rwxr-xr-x")
        };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(mockFiles));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.HasCount(2, result);
        Assert.AreEqual("file1.txt", result[0].Name);
        Assert.AreEqual("/home/user/file1.txt", result[0].FullPath);
        Assert.AreEqual(1024L, result[0].Size);
        Assert.IsFalse(result[0].IsDirectory);
        Assert.Contains("rw-r--r--", result[0].Permissions);
        Assert.AreEqual("dir1", result[1].Name);
        Assert.IsTrue(result[1].IsDirectory);
        Assert.Contains("rwxr-xr-x", result[1].Permissions);
    }

    [TestMethod]
    public async Task UploadFileAsync_VerifiesBytesWritten()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[1024];
        new Random().NextBytes(testData);
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/uploaded.bin";
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(1024);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(),
            remotePath,
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsNotEmpty(progressReports, "because progress should be reported during upload");
        File.Delete(localPath);
    }

    [TestMethod]
    public async Task DownloadFileAsync_VerifiesBytesRead()
    {
        // Arrange
        string localPath = Path.Combine(Path.GetTempPath(), $"download_{Guid.NewGuid()}.bin");
        string remotePath = "/home/user/remote.bin";
        SftpEntry mockFile = CreateMockSftpFile("remote.bin", remotePath, 2048, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);
        _sftpClient.DownloadAsync(remotePath,
                       Arg.Any<Stream>(),
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               callback(512);
                               callback(2048);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(async callInfo =>
                   {
                       Stream? stream = callInfo.Arg<Stream>();
                       byte[] testData = new byte[2048];
                       new Random().NextBytes(testData);
                       await stream.WriteAsync(testData);
                   });
        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).DownloadAsync(remotePath,
            Arg.Any<Stream>(),
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());
        Assert.IsTrue(File.Exists(localPath));
        Assert.IsNotEmpty(progressReports);
        Assert.AreEqual(2048L, progressReports.Last().BytesTransferred);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_DeletesFile()
    {
        // Arrange
        string remotePath = "/home/user/todelete.txt";
        SftpEntry mockFile = CreateMockSftpFile("todelete.txt", remotePath, 1024, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteFileAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // 单次 stat 就该回答"存在吗 + 是不是目录",不该再去列举父目录。
        await _sftpClient.DidNotReceive().ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_WhenAlreadyExists_SkipsCreateAndThrowsNothing()
    {
        // 重复上传同一文件夹树的常态:目录已存在。必须先探测后返回,绝不调用 CreateDirectory
        // ——否则 Tmds.Ssh 会对已存在目录抛 SftpException,上千文件的文件夹重传即刷出上百条异常。
        string remotePath = "/home/user/existing";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(true);

        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);

        await _sftpClient.Received(1).ExistsAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().CreateDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_WhenAbsent_CreatesWithoutProbingAgain()
    {
        // 目录缺失(默认 ExistsAsync 返回 false):探测一次后创建;创建必然成功,不产生异常。
        string remotePath = "/home/user/fresh";

        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);

        await _sftpClient.Received(1).CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_CreateRacesButExistsAfter_TreatsAsSuccess()
    {
        // 探测=不存在,但创建时被并发抢先(抛 VelaSshClientException),随后目录已存在 → 幂等成功。
        string remotePath = "/home/user/raced";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false, true);
        _sftpClient.CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .ThrowsAsync(new VelaSshClientException("already exists"));

        // 不抛即为通过。
        await _sftpService.EnsureDirectoryAsync(_sessionId, remotePath);
    }

    [TestMethod]
    public async Task EnsureDirectoryAsync_CreateFailsAndStillAbsent_Rethrows()
    {
        // 创建失败(权限/父目录缺失),复查仍不存在 → 抛出原错,不吞掉真实失败。
        string remotePath = "/home/user/denied";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false, false);
        _sftpClient.CreateDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .ThrowsAsync(new VelaSshClientException("permission denied"));

        await Assert.ThrowsExactlyAsync<VelaSshClientException>(
            () => _sftpService.EnsureDirectoryAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        string remotePath = "/home/user/mydir";
        SftpEntry mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockDir);
        _sftpClient.ListDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        await _sftpClient.Received(1).DeleteDirectoryAsync(remotePath, Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task DeleteAsync_RecursivelyDeletesDirectoryContents_AndReportsProgress()
    {
        const string dir = "/home/user/proj";
        SftpEntry mockDir = CreateMockSftpFile("proj", dir, 0, true, "rwxr-xr-x");
        SftpEntry childFile = CreateMockSftpFile("a.txt", "/home/user/proj/a.txt", 10, false, "rw-r--r--");
        SftpEntry childSub = CreateMockSftpFile("sub", "/home/user/proj/sub", 0, true, "rwxr-xr-x");
        SftpEntry grandchild = CreateMockSftpFile("b.txt", "/home/user/proj/sub/b.txt", 20, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(dir, Arg.Any<CancellationToken>()).Returns(mockDir); // stat → proj 是目录
        _sftpClient.ListDirectoryAsync(dir, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([childFile, childSub]));
        _sftpClient.ListDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([grandchild]));
        var reports = new List<SftpDeleteProgress>();
        await _sftpService.DeleteAsync(_sessionId, dir,
            new SynchronousProgress<SftpDeleteProgress>(reports.Add));

        // Files removed, and each directory removed only after its contents.
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/a.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteFileAsync("/home/user/proj/sub/b.txt", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync("/home/user/proj/sub", Arg.Any<CancellationToken>());
        await _sftpClient.Received(1).DeleteDirectoryAsync(dir, Arg.Any<CancellationToken>());
        Assert.HasCount(5, reports); // initial 0/total + 2 files + 2 directories
        Assert.AreEqual(0, reports[0].DeletedCount);
        Assert.AreEqual(4, reports[0].TotalCount);
        Assert.AreEqual(4, reports[^1].DeletedCount);
        Assert.AreEqual(4, reports[^1].TotalCount);
    }

    [TestMethod]
    public async Task DeleteAsync_WhenPathNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        string remotePath = "/home/user/nonexistent.txt";
        _sftpClient.ExistsAsync(remotePath, Arg.Any<CancellationToken>()).Returns(false);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _sftpService.DeleteAsync(_sessionId, remotePath));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_OwnerAndGroup_AreNotBooleanStrings()
    {
        // Arrange
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 1024, false, "rw-r--r--")
            with
        { UserId = 1000, GroupId = 1000 };
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));

        // Act
        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("1000", result[0].Group);
        Assert.AreNotEqual("True", result[0].Owner);
        Assert.AreNotEqual("False", result[0].Owner);
        Assert.AreNotEqual("True", result[0].Group);
        Assert.AreNotEqual("False", result[0].Group);
    }

    [TestMethod]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        // Arrange — trigger client caching by calling any method
        SftpEntry mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 0, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([mockFile]));
        await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Act
        await _sftpService.DisposeAsync();

        // Assert
        _sftpClient.Received(1).Disconnect();
        _sftpClient.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ListDirectoryAsync_WhenPermissionDenied_ThrowsException()
    {
        // Arrange
        _sftpClient.ListDirectoryAsync("/root/restricted", Arg.Any<CancellationToken>())
                   .Throws(new VelaSftpPermissionDeniedException("Permission denied"));

        // Act & Assert
        await Assert.ThrowsExactlyAsync<VelaSftpPermissionDeniedException>(() => _sftpService.ListDirectoryAsync(_sessionId, "/root/restricted"));
    }

    [TestMethod]
    public async Task UploadFileAsync_WithProgressCallback_FiresCorrectPercentages()
    {
        // Arrange
        string localPath = Path.GetTempFileName();
        byte[] testData = new byte[10000];
        await File.WriteAllBytesAsync(localPath, testData);
        string remotePath = "/home/user/upload.bin";
        var progressReports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           if (callback != null)
                           {
                               // Simulate progress at 25%, 50%, 75%, 100%
                               callback(2500);
                               callback(5000);
                               callback(7500);
                               callback(10000);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        // Act
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert —— 进度上报按时间片节流(见 Core/Sftp/TransferProgressThrottle.cs),因此不保证
        // "每个底层回调都对应一次上报"。这里断言的是真正的契约:立刻有首帧、单调不回退、
        // 收尾必达 100%。
        Assert.IsGreaterThanOrEqualTo(1, progressReports.Count);
        Assert.AreEqual(100, progressReports[^1].Percentage);
        Assert.AreEqual(10000, progressReports[^1].BytesTransferred);
        Assert.AreSequenceEqual(
            [.. progressReports.Select(p => p.BytesTransferred).OrderBy(b => b)], [.. progressReports.Select(p => p.BytesTransferred)], "进度必须单调不回退");

        // Cleanup
        File.Delete(localPath);
    }

    /// <summary>
    /// 底层按分块回调,GB 级文件会产生几十万次;若 1:1 转成 IProgress 上报会灌爆 UI 调度器
    /// (表现为传输到 1GB 左右界面长时间卡死)。这里证明节流确实把洪流收敛掉了。
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_FloodOfCallbacks_IsThrottledButStillReaches100()
    {
        string localPath = Path.GetTempFileName();
        const int total = 100_000;
        await File.WriteAllBytesAsync(localPath, new byte[total]);
        string remotePath = "/home/user/flood.bin";
        var progressReports = new List<TransferProgress>();
        _sftpClient.UploadAsync(Arg.Any<Stream>(),
                       remotePath,
                       Arg.Do<Action<ulong>?>(callback =>
                       {
                           for (ulong sent = 1; sent <= total; sent++)
                           {
                               callback?.Invoke(sent);
                           }
                       }),
                       Arg.Any<CancellationToken>())
                   .Returns(Task.CompletedTask);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // 十万次回调必须被压到极少数几次(首帧 + 每 100ms 一帧 + 收尾),绝不是十万次。
        Assert.IsLessThan(100, progressReports.Count,
            $"节流失效:{total} 次回调产生了 {progressReports.Count} 次上报");
        Assert.AreEqual(100, progressReports[^1].Percentage);

        File.Delete(localPath);
    }

    [TestMethod]
    public async Task GetFileInfoAsync_ReturnsFileInformation()
    {
        // Arrange
        string remotePath = "/home/user/info.txt";
        SftpEntry mockFile = CreateMockSftpFile("info.txt", remotePath, 4096, false, "rw-r--r--");
        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(mockFile);

        // Act
        RemoteFileInfo result = await _sftpService.GetFileInfoAsync(_sessionId, remotePath);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("info.txt", result.Name);
        Assert.AreEqual(remotePath, result.FullPath);
        Assert.AreEqual(4096L, result.Size);
        Assert.IsFalse(result.IsDirectory);

        // 单个文件的 stat 绝不能退化成列举整个父目录 —— 父目录上万条时那是灾难性的。
        await _sftpClient.DidNotReceive().ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>路径不存在时 stat 返回 null,应翻译成 FileNotFoundException(契约不变)。</summary>
    [TestMethod]
    public async Task GetFileInfoAsync_WhenEntryMissing_ThrowsFileNotFound()
    {
        _sftpClient.GetEntryAsync("/home/user/gone.txt", Arg.Any<CancellationToken>())
                   .Returns((SftpEntry?)null);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _sftpService.GetFileInfoAsync(_sessionId, "/home/user/gone.txt"));
    }

    // —— 属主/属组的数字 id → 名称翻译(见 RemoteIdentityResolver)————————————

    /// <summary>查表命令的返回样本:passwd 段 + 分隔标记 + group 段。</summary>
    private const string IdentityLookupOutput = """
                                                root:x:0:0:root:/root:/bin/bash
                                                deploy:x:1000:1000:Deploy User:/home/deploy:/bin/bash
                                                ###VELA-GROUPS###
                                                root:x:0:
                                                www-data:x:33:
                                                """;

    [TestMethod]
    public async Task ListDirectoryAsync_TranslatesNumericIdsUsingRemotePasswdDatabase()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("deploy", result[0].Owner);
        Assert.AreEqual("www-data", result[0].Group);
    }

    [TestMethod]
    public async Task ListDirectoryAsync_IdWithoutPasswdEntry_FallsBackToNumericId()
    {
        GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("orphan", "/srv/orphan", 10, false, "rw-r--r--") with { UserId = 4242, GroupId = 4242 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.AreEqual("4242", result[0].Owner);
        Assert.AreEqual("4242", result[0].Group);
    }

    /// <summary>仅 SFTP、没有 exec 通道的主机:查表整个失败,不该拖垮列目录。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_WhenIdentityLookupFails_StillListsWithNumericIds()
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new InvalidOperationException("no exec channel"));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        _sftpClient.ListDirectoryAsync("/srv", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>(
                       [CreateMockSftpFile("app.log", "/srv/app.log", 10, false, "rw-r--r--") with { UserId = 1000, GroupId = 33 }]));

        List<RemoteFileInfo> result = await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        Assert.HasCount(1, result);
        Assert.AreEqual("1000", result[0].Owner);
        Assert.AreEqual("33", result[0].Group);
    }

    /// <summary>整表只查一次:切目录不该每次都往返一条 getent。</summary>
    [TestMethod]
    public async Task ListDirectoryAsync_QueriesIdentityDatabaseOncePerSession()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.ListDirectoryAsync(_sessionId, "/b");
        await _sftpService.ListDirectoryAsync(_sessionId, "/c");

        await sshClient.Received(1).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>会话关闭要丢弃查表缓存,重连后才会按新主机重新查。</summary>
    [TestMethod]
    public async Task CloseSessionAsync_DiscardsIdentityCache()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/a");
        await _sftpService.CloseSessionAsync(_sessionId);
        await _sftpService.ListDirectoryAsync(_sessionId, "/a");

        await sshClient.Received(2).RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 分隔标记以 '#' 开头,在命令里必须带引号:裸写时 shell 会把它当注释,连同其后的
    /// group 查询一起吞掉,于是只有 passwd 段跑出来 —— 属主显示名称而属组回退数字。
    /// 其余查表测试都直接喂假输出,测不到命令本身,故在此守住。
    /// </summary>
    [TestMethod]
    public async Task ListDirectoryAsync_IdentityLookupCommand_QuotesSectionSeparator()
    {
        ISshClientWrapper sshClient = GivenIdentityLookupReturns(IdentityLookupOutput);
        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IEnumerable<SftpEntry>>([]));

        await _sftpService.ListDirectoryAsync(_sessionId, "/srv");

        string command = (string)sshClient.ReceivedCalls()
                                          .Single(c => c.GetMethodInfo().Name == nameof(ISshClientWrapper.RunCommandAsync))
                                          .GetArguments()[0]!;
        Assert.Contains("echo '###VELA-GROUPS###'", command);
        Assert.IsFalse(command.Contains("echo ###", StringComparison.Ordinal),
                       "分隔标记未加引号,group 段会被 shell 当注释吞掉。");
    }

    private ISshClientWrapper GivenIdentityLookupReturns(string output)
    {
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        sshClient.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(output));
        _connectionService.GetClient(_sessionId).Returns(sshClient);
        return sshClient;
    }

    private static SftpEntry CreateMockSftpFile(string name, string fullName, long length, bool isDirectory, string permissions)
    {
        // SftpEntry 是 Core 的中立不可变记录(不再是 SSH 库的接口),直接构造即可。
        return new()
        {
            Name = name,
            FullName = fullName,
            Length = length,
            IsDirectory = isDirectory,
            LastWriteTime = DateTime.UtcNow,
            OwnerCanRead = permissions.Length > 0 && permissions[0] == 'r',
            OwnerCanWrite = permissions.Length > 1 && permissions[1] == 'w',
            OwnerCanExecute = permissions.Length > 2 && permissions[2] == 'x',
            GroupCanRead = permissions.Length > 3 && permissions[3] == 'r',
            GroupCanWrite = permissions.Length > 4 && permissions[4] == 'w',
            GroupCanExecute = permissions.Length > 5 && permissions[5] == 'x',
            OthersCanRead = permissions.Length > 6 && permissions[6] == 'r',
            OthersCanWrite = permissions.Length > 7 && permissions[7] == 'w',
            OthersCanExecute = permissions.Length > 8 && permissions[8] == 'x'
        };
    }

    private class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ---- 断点续传的起点核实(SftpService.ResolveUploadResumeAsync / ResolveDownloadResumeAsync)----
    //
    // 调用方给出的 resumeOffset 是更早之前探测出来的,从探测到真正开始写之间隔着冲突对话框
    // 和传输队列。这组测试锁定的行为是:以"此刻的实际长度"为准,并比对尾部确认那半截确实是
    // 同一个文件的前缀 —— 否则宁可整份重传/直接失败,也不能静默产出损坏文件。

    /// <summary>远端那半截确实是本地文件的前缀 → 按远端实际长度续传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithMatchingPrefix_ResumesAtRemoteLength()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(200_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 120_000;
        const string remotePath = "/home/user/resume.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));

        // 调用方声称从 50_000 续 —— 应被"此刻远端实际长度"覆盖为 120_000。
        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 50_000);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath, remoteLength,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>
    /// 底层库并发写多个缓冲区,中断后文件尾部可能留有空洞:文件长度只是"已确认的最高偏移"。
    /// 续传起点必须从长度处回退一整个在途写入窗口,否则尾部比对会落在已写入的那段上顺利通过,
    /// 却从一个带洞的位置接着传 —— 产出静默损坏的文件。
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_Resume_BacksOffBySafetyMarginBeforeResuming()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(500_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 300_000;
        const long margin = 64 * 1024;
        const long expected = remoteLength - margin;
        const string remotePath = "/home/user/holes.bin";

        _sftpClient.ResumeSafetyMargin.Returns(margin);
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content[..(int)remoteLength], false)));

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, remoteLength);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath, expected,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>已传部分比安全回退窗口还短 → 没有可信的续传起点,整份重传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeShorterThanSafetyMargin_FallsBackToFullUpload()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(500_000));
        const string remotePath = "/home/user/tiny-partial.bin";

        _sftpClient.ResumeSafetyMargin.Returns(64 * 1024L);
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(50_000L);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 50_000);

        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>远端是同名的另一个文件(内容不同) → 必须报错,而不是接着往后追加。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithDifferentContent_ThrowsInsteadOfCorrupting()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(200_000));
        byte[] impostor = CreatePattern(120_000, seed: 77);
        const string remotePath = "/home/user/impostor.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(impostor.LongLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(impostor, false)));

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(
            () => _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 120_000));

        // 关键:一个字节都不能写出去。
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>远端已不短于本地(已传完/是别的更大文件) → 没有可续的半截,退化为整份重传。</summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWhenRemoteNotShorter_FallsBackToFullUpload()
    {
        string localPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(localPath, CreatePattern(100_000));
        const string remotePath = "/home/user/complete.bin";
        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(100_000L);

        await _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, 40_000);

        // 走的是不带偏移量的全量重传重载。
        await _sftpClient.Received(1).UploadAsync(Arg.Any<Stream>(), remotePath,
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        await _sftpClient.DidNotReceive().UploadAsync(Arg.Any<Stream>(), remotePath, Arg.Any<long>(),
            Arg.Any<Action<ulong>?>(), Arg.Any<CancellationToken>());
        File.Delete(localPath);
    }

    /// <summary>本地残留的那半截不是远端文件的前缀 → 报错,不能把错位内容追加进去。</summary>
    [TestMethod]
    public async Task DownloadFileAsync_ResumeWithDifferentContent_ThrowsInsteadOfCorrupting()
    {
        string localPath = Path.Combine(Path.GetTempPath(), $"vela-resume-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localPath, CreatePattern(120_000, seed: 77));
        byte[] remote = CreatePattern(200_000);
        const string remotePath = "/home/user/download.bin";

        _sftpClient.GetEntryAsync(remotePath, Arg.Any<CancellationToken>())
                   .Returns(new SftpEntry { Name = "download.bin", FullName = remotePath, Length = remote.LongLength });
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(new MemoryStream(remote, false)));

        await Assert.ThrowsExactlyAsync<VelaSftpResumeMismatchException>(
            () => _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath, null, 120_000));

        File.Delete(localPath);
    }

    /// <summary>
    /// 续传校验依赖按偏移定位。若 <see cref="ISftpClientWrapper.OpenAsync" /> 的实现返回了不可 Seek 的流,
    /// 必须给出指明契约的清晰错误,而不是让底层库抛一个看不出所以然的裸 NotSupportedException。
    /// <para>
    /// 这条测试针对的是一个真实踩过的坑:Tmds.Ssh 的 FileOpenOptions 默认 Seekable/CacheLength 均为 false,
    /// 打开的流一 Seek 就抛 NotSupportedException;而当时的测试全用 MemoryStream(可 Seek),
    /// 于是测试全绿、真机必炸。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task UploadFileAsync_ResumeWithNonSeekableRemoteStream_ReportsTheContractViolation()
    {
        string localPath = Path.GetTempFileName();
        byte[] content = CreatePattern(500_000);
        await File.WriteAllBytesAsync(localPath, content);
        const long remoteLength = 300_000;
        const string remotePath = "/home/user/nonseekable.bin";

        _sftpClient.GetFileSizeAsync(remotePath, Arg.Any<CancellationToken>()).Returns(remoteLength);
        _sftpClient.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromResult<Stream>(
                       new NonSeekableStream(content[..(int)remoteLength])));

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _sftpService.UploadFileAsync(_sessionId, localPath, remotePath, null, remoteLength));
        Assert.Contains("CanSeek", ex.Message);

        File.Delete(localPath);
    }

    /// <summary>模拟"打开选项没开 Seekable"的远端流:能读,但不能定位。</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    /// <summary>生成可复现的伪随机内容,使"尾部比对"能真正区分开不同文件。</summary>
    private static byte[] CreatePattern(int length, int seed = 42)
    {
        byte[] buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }
}

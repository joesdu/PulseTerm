using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using PulseTerm.Core.Ssh;
using Renci.SshNet.Sftp;
using Renci.SshNet.Common;
using Xunit;
using ConnectionInfo = PulseTerm.Core.Models.ConnectionInfo;

namespace PulseTerm.Core.Tests.Sftp;

[Trait("Category", "Sftp")]
public class SftpServiceTests
{
    private readonly ISshConnectionService _connectionService;
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;
    private readonly ISftpClientWrapper _sftpClient;

    public SftpServiceTests()
    {
        _connectionService = Substitute.For<ISshConnectionService>();
        _sftpClient = Substitute.For<ISftpClientWrapper>();
        _sessionId = Guid.NewGuid();

        var session = new SshSession
        {
            SessionId = _sessionId,
            ConnectionInfo = new ConnectionInfo
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
        _sftpService = new SftpService(_connectionService, () => _sftpClient);
    }

    [Fact]
    public async Task ListDirectoryAsync_ReturnsRemoteFileInfoArray()
    {
        // Arrange
        var mockFiles = new List<ISftpFile>
        {
            CreateMockSftpFile("file1.txt", "/home/user/file1.txt", 1024, false, "rw-r--r--"),
            CreateMockSftpFile("dir1", "/home/user/dir1", 0, true, "rwxr-xr-x")
        };

        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(mockFiles));

        // Act
        var result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("file1.txt");
        result[0].FullPath.Should().Be("/home/user/file1.txt");
        result[0].Size.Should().Be(1024);
        result[0].IsDirectory.Should().BeFalse();
        result[0].Permissions.Should().Contain("rw-r--r--");

        result[1].Name.Should().Be("dir1");
        result[1].IsDirectory.Should().BeTrue();
        result[1].Permissions.Should().Contain("rwxr-xr-x");
    }

    [Fact]
    public async Task UploadFileAsync_VerifiesBytesWritten()
    {
        // Arrange
        var localPath = Path.GetTempFileName();
        var testData = new byte[1024];
        new Random().NextBytes(testData);
        await File.WriteAllBytesAsync(localPath, testData);

        var remotePath = "/home/user/uploaded.bin";

        _sftpClient.UploadAsync(
            Arg.Any<Stream>(),
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
        await _sftpClient.Received(1).UploadAsync(
            Arg.Any<Stream>(),
            remotePath,
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());

        progressReports.Should().NotBeEmpty("because progress should be reported during upload");

        File.Delete(localPath);
    }

    [Fact]
    public async Task DownloadFileAsync_VerifiesBytesRead()
    {
        // Arrange
        var localPath = Path.Combine(Path.GetTempPath(), $"download_{Guid.NewGuid()}.bin");
        var remotePath = "/home/user/remote.bin";

        var mockFile = CreateMockSftpFile("remote.bin", remotePath, 2048, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        _sftpClient.DownloadAsync(
            remotePath,
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
                var stream = callInfo.Arg<Stream>();
                var testData = new byte[2048];
                new Random().NextBytes(testData);
                await stream.WriteAsync(testData);
            });

        var progressReports = new List<TransferProgress>();

        // Act
        await _sftpService.DownloadFileAsync(_sessionId, remotePath, localPath,
            new SynchronousProgress<TransferProgress>(p => progressReports.Add(p)));

        // Assert
        await _sftpClient.Received(1).DownloadAsync(
            remotePath,
            Arg.Any<Stream>(),
            Arg.Any<Action<ulong>?>(),
            Arg.Any<CancellationToken>());

        File.Exists(localPath).Should().BeTrue();
        progressReports.Should().NotBeEmpty();
        progressReports.Last().BytesTransferred.Should().Be(2048);

        if (File.Exists(localPath))
            File.Delete(localPath);
    }

    [Fact]
    public async Task DeleteAsync_DeletesFile()
    {
        // Arrange
        var remotePath = "/home/user/todelete.txt";
        var mockFile = CreateMockSftpFile("todelete.txt", remotePath, 1024, false, "rw-r--r--");

        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
            .Returns(new[] { mockFile });

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).DeleteFile(remotePath);
        _sftpClient.DidNotReceive().DeleteDirectory(Arg.Any<string>());
    }

    [Fact]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        // Arrange
        var remotePath = "/home/user/newdir";

        // Act
        await _sftpService.CreateDirectoryAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).CreateDirectory(remotePath);
    }

    [Fact]
    public async Task DeleteAsync_WithDirectory_DeletesDirectory()
    {
        // Arrange
        var remotePath = "/home/user/mydir";
        var mockDir = CreateMockSftpFile("mydir", remotePath, 0, true, "rwxr-xr-x");

        _sftpClient.Exists(remotePath).Returns(true);
        _sftpClient.ListDirectory("/home/user")
            .Returns(new[] { mockDir });

        // Act
        await _sftpService.DeleteAsync(_sessionId, remotePath);

        // Assert
        _sftpClient.Received(1).DeleteDirectory(remotePath);
        _sftpClient.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteAsync_WhenPathNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var remotePath = "/home/user/nonexistent.txt";
        _sftpClient.Exists(remotePath).Returns(false);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sftpService.DeleteAsync(_sessionId, remotePath));
    }

    [Fact]
    public async Task ListDirectoryAsync_OwnerAndGroup_AreNotBooleanStrings()
    {
        // Arrange
        var mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 1024, false, "rw-r--r--");
        mockFile.UserId.Returns(1000);
        mockFile.GroupId.Returns(1000);

        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        // Act
        var result = await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Assert
        result[0].Owner.Should().Be("1000");
        result[0].Group.Should().Be("1000");
        result[0].Owner.Should().NotBe("True");
        result[0].Owner.Should().NotBe("False");
        result[0].Group.Should().NotBe("True");
        result[0].Group.Should().NotBe("False");
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        // Arrange — trigger client caching by calling any method
        var mockFile = CreateMockSftpFile("file.txt", "/home/user/file.txt", 0, false, "rw-r--r--");
        _sftpClient.ListDirectoryAsync("/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        await _sftpService.ListDirectoryAsync(_sessionId, "/home/user");

        // Act
        await ((IAsyncDisposable)_sftpService).DisposeAsync();

        // Assert
        _sftpClient.Received(1).Disconnect();
        _sftpClient.Received(1).Dispose();
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenPermissionDenied_ThrowsException()
    {
        // Arrange
        _sftpClient.ListDirectoryAsync("/root/restricted", Arg.Any<CancellationToken>())
            .Throws(new SftpPermissionDeniedException("Permission denied"));

        // Act & Assert
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(
            () => _sftpService.ListDirectoryAsync(_sessionId, "/root/restricted"));
    }

    [Fact]
    public async Task UploadFileAsync_WithProgressCallback_FiresCorrectPercentages()
    {
        // Arrange
        var localPath = Path.GetTempFileName();
        var testData = new byte[10000];
        await File.WriteAllBytesAsync(localPath, testData);

        var remotePath = "/home/user/upload.bin";
        var progressReports = new List<TransferProgress>();

        _sftpClient.UploadAsync(
            Arg.Any<Stream>(),
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

        // Assert
        progressReports.Should().HaveCountGreaterThanOrEqualTo(4);
        progressReports.Should().Contain(p => p.Percentage >= 25 && p.Percentage < 35);
        progressReports.Should().Contain(p => p.Percentage >= 50 && p.Percentage < 60);
        progressReports.Should().Contain(p => p.Percentage >= 75 && p.Percentage < 85);
        progressReports.Last().Percentage.Should().Be(100);

        // Cleanup
        File.Delete(localPath);
    }

    [Fact]
    public async Task GetFileInfoAsync_ReturnsFileInformation()
    {
        // Arrange
        var remotePath = "/home/user/info.txt";
        var mockFile = CreateMockSftpFile("info.txt", remotePath, 4096, false, "rw-r--r--");

        _sftpClient.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<ISftpFile>>(new[] { mockFile }));

        // Act
        var result = await _sftpService.GetFileInfoAsync(_sessionId, remotePath);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("info.txt");
        result.FullPath.Should().Be(remotePath);
        result.Size.Should().Be(4096);
        result.IsDirectory.Should().BeFalse();
    }

    private class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private ISftpFile CreateMockSftpFile(string name, string fullName, long length, bool isDirectory, string permissions)
    {
        var file = Substitute.For<ISftpFile>();
        file.Name.Returns(name);
        file.FullName.Returns(fullName);
        file.Length.Returns(length);
        file.IsDirectory.Returns(isDirectory);
        file.LastWriteTime.Returns(DateTime.UtcNow);
        
        file.OwnerCanRead.Returns(permissions.Length > 0 && permissions[0] == 'r');
        file.OwnerCanWrite.Returns(permissions.Length > 1 && permissions[1] == 'w');
        file.OwnerCanExecute.Returns(permissions.Length > 2 && permissions[2] == 'x');
        
        file.GroupCanRead.Returns(permissions.Length > 3 && permissions[3] == 'r');
        file.GroupCanWrite.Returns(permissions.Length > 4 && permissions[4] == 'w');
        file.GroupCanExecute.Returns(permissions.Length > 5 && permissions[5] == 'x');
        
        file.OthersCanRead.Returns(permissions.Length > 6 && permissions[6] == 'r');
        file.OthersCanWrite.Returns(permissions.Length > 7 && permissions[7] == 'w');
        file.OthersCanExecute.Returns(permissions.Length > 8 && permissions[8] == 'x');

        return file;
    }
}

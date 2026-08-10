using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SerializedSftpServiceTests
{
    [TestMethod]
    public async Task OperationsAndCloseAsync_WhenConcurrent_SerializesWorkAndRejectsQueuedRequests()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When —— 全部用元数据操作:传输已刻意不占串行闸,见下面两个专门的测试。
        Task<List<RemoteFileInfo>> list = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();
        Task ensure = service.EnsureDirectoryAsync(inner.SessionId, "/ensure");
        Task delete = service.DeleteAsync(inner.SessionId, "/delete");
        Task close = service.CloseAsync();
        Task<RemoteFileInfo> afterClose = service.GetFileInfoAsync(inner.SessionId, "/after-close");
        inner.ReleaseFirstOperation();

        // Then
        await list;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => ensure);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => delete);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => afterClose);
        await close;
        Assert.AreEqual(1, inner.MaximumConcurrency);
        Assert.AreEqual(1, inner.CloseCalls);
        Assert.AreSequenceEqual(["ListDirectory", "CloseSession"], inner.OperationNames);
    }

    /// <summary>
    /// 传输不占串行闸:一次 GB 级上传跑几十分钟,期间面板的目录刷新必须照常可用。
    /// 这条也是"最大并发传输数"设置能真正生效的前提 —— 单闸会把它悄悄压回 1。
    /// </summary>
    [TestMethod]
    public async Task Transfer_WhenInFlight_DoesNotBlockMetadataOperations()
    {
        // Given:一个卡住不返回的上传占着传输路径。
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task upload = service.UploadFileAsync(inner.SessionId, "local", "/big.bin");
        await inner.WaitForFirstOperationAsync();

        // When / Then:目录刷新不该排在这次上传后面。
        await service.ListDirectoryAsync(inner.SessionId, "/").WaitAsync(TimeSpan.FromSeconds(10));

        inner.ReleaseFirstOperation();
        await upload;
        Assert.AreEqual(2, inner.MaximumConcurrency, "传输与元数据操作应能并行");
    }

    /// <summary>
    /// 传输虽然不占闸,但仍计入在途:关闭必须等它跑完,不能把连接从底下抽掉。
    /// </summary>
    [TestMethod]
    public async Task CloseAsync_WhenTransferInFlight_StillDrainsItBeforeClosingTheSession()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task upload = service.UploadFileAsync(inner.SessionId, "local", "/big.bin");
        await inner.WaitForFirstOperationAsync();

        // When
        Task close = service.CloseAsync();

        // Then
        Assert.IsFalse(close.IsCompleted, "because close must wait for the in-flight transfer");
        Assert.AreEqual(0, inner.CloseCalls);
        inner.ReleaseFirstOperation();
        await upload;
        await close;
        Assert.AreEqual(1, inner.CloseCalls);
        Assert.AreSequenceEqual(["Upload", "CloseSession"], inner.OperationNames);
    }

    [TestMethod]
    public async Task CloseAsync_WhenCalledRepeatedly_IsHarmlessAndWaitsForActiveOperation()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();

        // When
        Task firstClose = service.CloseAsync();
        Task secondClose = service.CloseAsync();

        // Then
        Assert.IsFalse(firstClose.IsCompleted, "because close must wait for the gate holder");
        Assert.IsFalse(secondClose.IsCompleted, "because all close callers await the same lifecycle");
        Assert.AreEqual(0, inner.CloseCalls);
        inner.ReleaseFirstOperation();
        await active;
        await Task.WhenAll(firstClose, secondClose);
        Assert.AreEqual(1, inner.CloseCalls);
    }

    [TestMethod]
    public async Task CloseAsync_WhenCallerCancelsWait_CancelsOnlyCallerAndCloseCompletesWithNonCancellableToken()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);

        // Start an active operation that holds the gate.
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();

        // Call CloseAsync with a cancellable token.
        using var cancellation = new CancellationTokenSource();
        Task close = service.CloseAsync(cancellation.Token);

        // Cancel the caller's wait — only this caller gets OCE.
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => close);

        // Release the active operation, allowing the shared close to drain.
        inner.ReleaseFirstOperation();
        await active;

        // Inner close completes exactly once with a non-cancellable token.
        await service.CloseAsync();
        Assert.AreEqual(1, inner.CloseCalls);
        Assert.AreEqual(CancellationToken.None, inner.CancellationTokens[^1]);

        // Later CloseAsync on the same cached cleanup succeeds without re-closing.
        await service.CloseAsync();
        Assert.AreEqual(1, inner.CloseCalls);
    }

    [TestMethod]
    public async Task DisposeAsync_WhenCalledRepeatedly_ClosesTheBoundSessionOnce()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When
        await service.DisposeAsync();
        await service.DisposeAsync();

        // Then
        Assert.AreEqual(1, inner.CloseCalls);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => service.ListDirectoryAsync(inner.SessionId, "/"));
    }

    [TestMethod]
    public async Task Operations_WhenSessionIdDoesNotMatch_RejectWithoutCallingInnerService()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When
        Task operation = service.CreateDirectoryAsync(Guid.NewGuid(), "/wrong-session");

        // Then
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => operation);
        Assert.IsEmpty(inner.OperationNames);
    }

    [TestMethod]
    public async Task QueuedOperation_WhenCancelled_DoesNotPoisonLaterDocumentWork()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();
        using var cancellation = new CancellationTokenSource();
        Task<bool> queued = service.ExistsAsync(inner.SessionId, "/cancelled", cancellation.Token);

        // When
        cancellation.Cancel();
        inner.ReleaseFirstOperation();

        // Then
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queued);
        await active;
        Assert.IsTrue(await service.ExistsAsync(inner.SessionId, "/resumed"));
        Assert.AreEqual(1, inner.MaximumConcurrency);
        Assert.AreSequenceEqual(["ListDirectory", "Exists"], inner.OperationNames);
    }

    [TestMethod]
    public async Task AllOperations_WhenOpen_DelegateBoundSessionCancellationAndProgress()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);
        using var cancellation = new CancellationTokenSource();
        var transferProgress = new List<TransferProgress>();
        var deleteProgress = new List<SftpDeleteProgress>();

        // When
        await service.ListDirectoryAsync(inner.SessionId, "/", cancellation.Token);
        await service.UploadFileAsync(inner.SessionId, "local", "/upload", new InlineProgress<TransferProgress>(transferProgress.Add), cancellationToken: cancellation.Token);
        await service.DownloadFileAsync(inner.SessionId, "/download", "local", new InlineProgress<TransferProgress>(transferProgress.Add), cancellationToken: cancellation.Token);
        await service.DeleteAsync(inner.SessionId, "/delete", new InlineProgress<SftpDeleteProgress>(deleteProgress.Add), cancellationToken: cancellation.Token);
        await service.CreateDirectoryAsync(inner.SessionId, "/directory", cancellationToken: cancellation.Token);
        await service.CreateFileAsync(inner.SessionId, "/file", cancellationToken: cancellation.Token);
        await service.EnsureDirectoryAsync(inner.SessionId, "/ensure", cancellationToken: cancellation.Token);
        await service.RenameAsync(inner.SessionId, "/old", "/new", cancellation.Token);
        await service.SetPermissionsAsync(inner.SessionId, "/permissions", 755, cancellation.Token);
        await service.GetFileInfoAsync(inner.SessionId, "/info", cancellation.Token);
        await service.ExistsAsync(inner.SessionId, "/exists", cancellation.Token);
        await service.GetWorkingDirectoryAsync(inner.SessionId, cancellation.Token);
        await service.CloseSessionAsync(inner.SessionId, cancellation.Token);

        // Then
        Assert.AreSequenceEqual(
            ["ListDirectory", "Upload", "Download", "Delete", "CreateDirectory", "CreateFile", "EnsureDirectory", "Rename", "SetPermissions", "GetFileInfo", "Exists", "GetWorkingDirectory", "CloseSession"],
            inner.OperationNames);
        Assert.IsTrue(inner.CancellationTokens[..^1].All(token => token == cancellation.Token));
        Assert.AreEqual(CancellationToken.None, inner.CancellationTokens[^1]);
        Assert.HasCount(2, transferProgress);
        Assert.HasCount(1, deleteProgress);
        Assert.AreEqual(1, inner.CloseCalls);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BlockingSftpService(bool blockFirstOperation = false) : ISftpService
    {
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource _firstOperationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeOperations;
        private int _blockFirstOperation = blockFirstOperation ? 1 : 0;

        public Guid SessionId { get; } = Guid.NewGuid();
        public int CloseCalls { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public List<string> OperationNames { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task WaitForFirstOperationAsync() => _firstOperationStarted.Task;

        public void ReleaseFirstOperation() => _releaseFirstOperation.TrySetResult();

        public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => InvokeAsync("ListDirectory", sessionId, new List<RemoteFileInfo>(), cancellationToken);

        public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default)
        {
            progress?.Report(CreateTransferProgress(localPath));
            return InvokeAsync("Upload", sessionId, cancellationToken);
        }

        public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default)
        {
            progress?.Report(CreateTransferProgress(remotePath));
            return InvokeAsync("Download", sessionId, cancellationToken);
        }

        public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(CreateTransferProgress(sourcePath));
            return InvokeAsync("Copy", sessionId, cancellationToken);
        }

        public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new(1, 1, remotePath));
            return InvokeAsync("Delete", sessionId, cancellationToken);
        }

        public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("CreateDirectory", sessionId, cancellationToken);
        public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("CreateFile", sessionId, cancellationToken);
        public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("EnsureDirectory", sessionId, cancellationToken);
        public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => InvokeAsync("Rename", sessionId, cancellationToken);
        public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => InvokeAsync("SetPermissions", sessionId, cancellationToken);
        public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("GetFileInfo", sessionId, CreateRemoteFileInfo(remotePath), cancellationToken);

        public Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync<Stream>("OpenRead", sessionId, new MemoryStream(), cancellationToken);
        public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("Exists", sessionId, true, cancellationToken);
        public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => InvokeAsync("GetWorkingDirectory", sessionId, "/", cancellationToken);

        public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return InvokeAsync("CloseSession", sessionId, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task InvokeAsync(string operationName, Guid sessionId, CancellationToken cancellationToken)
        {
            _ = await InvokeAsync(operationName, sessionId, true, cancellationToken);
        }

        private async Task<T> InvokeAsync<T>(string operationName, Guid sessionId, T result, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                OperationNames.Add(operationName);
                CancellationTokens.Add(cancellationToken);
                int active = ++_activeOperations;
                MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            }

            _firstOperationStarted.TrySetResult();
            try
            {
                if (Interlocked.Exchange(ref _blockFirstOperation, 0) == 1)
                {
                    await _releaseFirstOperation.Task.WaitAsync(cancellationToken);
                }

                return result;
            }
            finally
            {
                lock (_sync)
                {
                    _activeOperations--;
                }
            }
        }

        private static TransferProgress CreateTransferProgress(string fileName) => new()
        {
            FileName = fileName,
            BytesTransferred = 1,
            TotalBytes = 1,
            Percentage = 100,
            SpeedBytesPerSecond = 1,
            EstimatedTimeRemaining = TimeSpan.Zero
        };

        private static RemoteFileInfo CreateRemoteFileInfo(string remotePath) => new()
        {
            Name = Path.GetFileName(remotePath),
            FullPath = remotePath,
            Size = 0,
            Permissions = "-rw-r--r--",
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Owner = "owner",
            Group = "group"
        };
    }
}

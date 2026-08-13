using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 为单个文档所属的会话串行化 SFTP 元数据操作,而不改变终端侧边栏所用共享 SFTP 服务的行为。
/// <para>
/// **传输(上传/下载/远端复制)不占串行闸**。它们可能持续几十分钟,若也排进同一条队列,
/// 一次 GB 级传输就会把该面板的目录刷新、stat、改名全部堵死;而且会把用户设置的
/// "最大并发传输数"悄悄压回 1。底层 Tmds.Ssh 的 SftpClient 本身即为并发使用而设计
/// (最初引入本串行器是 SSH.NET 时代为规避其并发问题),因此放行是安全的。
/// </para>
/// <para>
/// 关闭仍然排空**全部**在途工作(含不占闸的传输):串行闸只管顺序,在途计数才是生命周期依据。
/// </para>
/// </summary>
/// <remarks>创建绑定到唯一一个文档所属 SFTP 会话的串行器。</remarks>
public sealed class SerializedSftpService(ISftpService inner, Guid sessionId) : ISftpService
{
    private readonly ISftpService _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>元数据操作的串行闸;传输不经过它。</summary>
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private readonly Lock _lifecycleSync = new();

    /// <summary>所有在途操作的计数(含不占闸的传输),关闭时据此排空。</summary>
    private int _inFlight;

    /// <summary>在途操作归零的信号;仅在开始关闭时创建。</summary>
    private TaskCompletionSource? _drained;

    private Task? _closeTask;
    private bool _closing;

    /// <summary>该文档作用域服务唯一接受的会话 ID。</summary>
    public Guid SessionId { get; } = sessionId;

    /// <summary>列举远端目录的串行化透传。</summary>
    public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ListDirectoryAsync(sessionId, path, token), cancellationToken);

    /// <summary>上传文件的透传;不占串行闸(见类型说明),但计入在途,关闭时会被排空。</summary>
    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.UploadFileAsync(sessionId, localPath, remotePath, progress, resumeOffset, token), cancellationToken, serialize: false);

    /// <summary>下载文件的透传;不占串行闸(见类型说明),但计入在途,关闭时会被排空。</summary>
    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DownloadFileAsync(sessionId, remotePath, localPath, progress, resumeOffset, token), cancellationToken, serialize: false);

    /// <summary>删除远端文件或目录的串行化透传。</summary>
    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.DeleteAsync(sessionId, remotePath, progress, token), cancellationToken);

    /// <summary>创建远端目录的串行化透传。</summary>
    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>创建远端文件的串行化透传。</summary>
    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CreateFileAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>确保远端目录存在的串行化透传。</summary>
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.EnsureDirectoryAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>重命名远端条目的串行化透传。</summary>
    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.RenameAsync(sessionId, oldPath, newPath, token), cancellationToken);

    /// <summary>远端复制的透传;内部是"下载到临时文件再上传",同属传输,不占串行闸。</summary>
    public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.CopyAsync(sessionId, sourcePath, destPath, progress, token), cancellationToken, serialize: false);

    /// <summary>设置远端文件权限的串行化透传。</summary>
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.SetPermissionsAsync(sessionId, remotePath, octalMode, token), cancellationToken);

    /// <summary>获取远端文件元数据的串行化透传。</summary>
    public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetFileInfoAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>打开只读流的透传;打开动作串行化,流本身的读取不占闸(与传输同理,长读不堵元数据队列)。</summary>
    public Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.OpenReadAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>检查远端路径是否存在的串行化透传。</summary>
    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.ExistsAsync(sessionId, remotePath, token), cancellationToken);

    /// <summary>获取远端工作目录的串行化透传。</summary>
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => ExecuteAsync(sessionId, token => _inner.GetWorkingDirectoryAsync(sessionId, token), cancellationToken);

    /// <summary>关闭所绑定 SFTP 会话的串行化透传。</summary>
    public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        return CloseAsync(cancellationToken);
    }

    /// <summary>拒绝新任务,排空当前操作,并关闭所绑定的会话一次。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            _closing = true;
            _closeTask ??= CloseCoreAsync();
            return _closeTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>关闭会话并释放内部信号量。</summary>
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _metadataGate.Dispose();
    }

    private async Task<T> ExecuteAsync<T>(Guid sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool serialize = true)
    {
        ValidateSession(sessionId);

        // 登记在途必须在拿闸之前:关闭要排空的是"所有已被接纳的操作",
        // 包括还堵在闸上的,以及压根不走闸的传输。
        EnterOperation();
        try
        {
            if (!serialize)
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 取消与"闸被释放"同时发生时,SemaphoreSlim.WaitAsync 谁赢是不确定的:
                // 闸一放开,已经取消的等待者也可能被放行。拿到闸后复核一次,
                // 调用方既已放弃就绝不替他发起远端操作。
                cancellationToken.ThrowIfCancellationRequested();

                // 排队期间也可能已经开始关闭。
                ThrowIfClosing();
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private Task<bool> ExecuteAsync(Guid sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken, bool serialize = true) =>
        ExecuteAsync(sessionId, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken, serialize);

    /// <summary>接纳一个操作并计入在途;已开始关闭时拒绝。与 <see cref="CloseAsync" /> 共用锁,二者互斥。</summary>
    private void EnterOperation()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _inFlight++;
        }
    }

    /// <summary>操作离场;在途归零且正在关闭时放行排空信号。</summary>
    private void ExitOperation()
    {
        lock (_lifecycleSync)
        {
            if (--_inFlight == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    private async Task CloseCoreAsync()
    {
        Task drained;
        lock (_lifecycleSync)
        {
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight == 0)
            {
                _drained.TrySetResult();
            }
            drained = _drained.Task;
        }

        // 排空全部在途工作(含正在跑的传输)后再关会话;此时 _closing 已为 true,不会有新工作进来。
        await drained.ConfigureAwait(false);
        await _inner.CloseSessionAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
    }

    private void ValidateSession(Guid sessionId)
    {
        if (sessionId != SessionId)
        {
            throw new ArgumentException("The SFTP document service is bound to a different session.", nameof(sessionId));
        }
    }

    private void ThrowIfClosing()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
        }
    }
}

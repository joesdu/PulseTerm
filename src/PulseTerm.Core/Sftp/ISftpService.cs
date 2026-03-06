using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

public interface ISftpService : IAsyncDisposable
{
    Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);
    Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
    Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
}

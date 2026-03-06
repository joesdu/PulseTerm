using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

/// <summary>
/// Delegate for executing a file transfer. Injected by the caller (e.g., SftpService)
/// to perform the actual upload/download work.
/// </summary>
public delegate Task TransferExecutor(
    TransferTask task,
    IProgress<TransferProgress> progress,
    CancellationToken cancellationToken);

public interface ITransferManager : IDisposable
{
    int MaxConcurrentTransfers { get; set; }
    IReadOnlyList<TransferTask> ActiveTransfers { get; }
    IReadOnlyList<TransferTask> QueuedTransfers { get; }
    
    Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default);
    Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    TransferTask? GetTransfer(Guid transferId);
}

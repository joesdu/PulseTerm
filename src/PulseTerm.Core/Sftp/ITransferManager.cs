using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

public interface ITransferManager
{
    int MaxConcurrentTransfers { get; set; }
    IReadOnlyList<TransferTask> ActiveTransfers { get; }
    IReadOnlyList<TransferTask> QueuedTransfers { get; }
    
    Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default);
    Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    TransferTask? GetTransfer(Guid transferId);
}

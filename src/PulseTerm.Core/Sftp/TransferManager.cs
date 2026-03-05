using System.Collections.Concurrent;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

public class TransferManager : ITransferManager
{
    private readonly ConcurrentDictionary<Guid, TransferTask> _allTransfers = new();
    private readonly ConcurrentQueue<TransferTask> _queuedTransfers = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private int _maxConcurrentTransfers = 3;

    public TransferManager()
    {
        _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentTransfers, _maxConcurrentTransfers);
    }

    public int MaxConcurrentTransfers
    {
        get => _maxConcurrentTransfers;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentTransfers must be greater than 0");
            
            _maxConcurrentTransfers = value;
        }
    }

    public IReadOnlyList<TransferTask> ActiveTransfers =>
        _allTransfers.Values.Where(t => t.Status == TransferStatus.InProgress).ToList();

    public IReadOnlyList<TransferTask> QueuedTransfers =>
        _allTransfers.Values.Where(t => t.Status == TransferStatus.Queued).ToList();

    public async Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        _allTransfers[task.Id] = task;
        task.Status = TransferStatus.Queued;
        _queuedTransfers.Enqueue(task);

        _ = Task.Run(() => ProcessTransferQueueAsync(cancellationToken), cancellationToken);
    }

    public Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (_allTransfers.TryGetValue(transferId, out var task))
        {
            task.Status = TransferStatus.Cancelled;
        }

        return Task.CompletedTask;
    }

    public TransferTask? GetTransfer(Guid transferId)
    {
        return _allTransfers.TryGetValue(transferId, out var task) ? task : null;
    }

    private async Task ProcessTransferQueueAsync(CancellationToken cancellationToken)
    {
        while (_queuedTransfers.TryDequeue(out var task))
        {
            if (task.Status == TransferStatus.Cancelled)
                continue;

            await _concurrencySemaphore.WaitAsync(cancellationToken);

            try
            {
                task.Status = TransferStatus.InProgress;

                await Task.Delay(50, cancellationToken);

                task.Status = TransferStatus.Completed;
            }
            catch (Exception)
            {
                task.Status = TransferStatus.Failed;
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }
    }
}

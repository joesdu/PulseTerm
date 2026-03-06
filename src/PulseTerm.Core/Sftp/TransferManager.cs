using System.Collections.Concurrent;
using System.Threading.Channels;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Sftp;

public class TransferManager : ITransferManager
{
    private readonly ConcurrentDictionary<Guid, TransferTask> _allTransfers = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _transferCts = new();
    private readonly Channel<TransferTask> _channel;
    private readonly TransferExecutor? _executor;
    private readonly CancellationTokenSource _disposeCts = new();
    private SemaphoreSlim _concurrencySemaphore;
    private int _maxConcurrentTransfers = 3;
    private Task? _processorTask;
    private readonly object _processorLock = new();
    private bool _disposed;

    public TransferManager() : this(null)
    {
    }

    public TransferManager(TransferExecutor? executor)
    {
        _executor = executor;
        _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentTransfers, _maxConcurrentTransfers);
        _channel = Channel.CreateUnbounded<TransferTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int MaxConcurrentTransfers
    {
        get => _maxConcurrentTransfers;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentTransfers must be greater than 0");

            if (_processorTask is not null)
                throw new InvalidOperationException(
                    "Cannot change MaxConcurrentTransfers while transfers are being processed.");

            _maxConcurrentTransfers = value;
            _concurrencySemaphore.Dispose();
            _concurrencySemaphore = new SemaphoreSlim(value, value);
        }
    }

    public IReadOnlyList<TransferTask> ActiveTransfers =>
        _allTransfers.Values.Where(t => t.Status == TransferStatus.InProgress).ToList();

    public IReadOnlyList<TransferTask> QueuedTransfers =>
        _allTransfers.Values.Where(t => t.Status == TransferStatus.Queued).ToList();

    public Task QueueTransferAsync(TransferTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _transferCts[task.Id] = taskCts;

        _allTransfers[task.Id] = task;
        task.Status = TransferStatus.Queued;
        _channel.Writer.TryWrite(task);

        EnsureProcessorRunning();

        return Task.CompletedTask;
    }

    public Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (_allTransfers.TryGetValue(transferId, out var task))
        {
            task.Status = TransferStatus.Cancelled;

            if (_transferCts.TryGetValue(transferId, out var cts))
                cts.Cancel();
        }

        return Task.CompletedTask;
    }

    public TransferTask? GetTransfer(Guid transferId)
    {
        return _allTransfers.TryGetValue(transferId, out var task) ? task : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _channel.Writer.TryComplete();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _concurrencySemaphore.Dispose();

        foreach (var cts in _transferCts.Values)
            cts.Dispose();

        _transferCts.Clear();
        GC.SuppressFinalize(this);
    }

    private void EnsureProcessorRunning()
    {
        if (_processorTask is not null)
            return;

        lock (_processorLock)
        {
            _processorTask ??= Task.Run(() => ProcessTransferChannelAsync(_disposeCts.Token));
        }
    }

    private async Task ProcessTransferChannelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (task.Status == TransferStatus.Cancelled)
                    continue;

                await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                _ = ExecuteTransferAsync(task, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExecuteTransferAsync(TransferTask task, CancellationToken processorToken)
    {
        _transferCts.TryGetValue(task.Id, out var taskCts);
        var transferToken = taskCts?.Token ?? processorToken;

        try
        {
            if (task.Status == TransferStatus.Cancelled)
                return;

            task.Status = TransferStatus.InProgress;

            if (_executor is not null)
            {
                var progress = new Progress<TransferProgress>(p => task.Progress = p);
                await _executor(task, progress, transferToken).ConfigureAwait(false);
            }

            if (task.Status == TransferStatus.InProgress)
                task.Status = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            task.Status = TransferStatus.Cancelled;
        }
        catch (Exception)
        {
            task.Status = TransferStatus.Failed;
        }
        finally
        {
            _concurrencySemaphore.Release();

            if (_transferCts.TryRemove(task.Id, out var cts))
                cts.Dispose();
        }
    }
}

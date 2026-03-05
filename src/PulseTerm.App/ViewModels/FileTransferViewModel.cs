using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class FileTransferViewModel : ReactiveObject
{
    private readonly ITransferManager _transferManager;

    public FileTransferViewModel(ITransferManager transferManager)
    {
        _transferManager = transferManager ?? throw new ArgumentNullException(nameof(transferManager));

        Transfers = new ObservableCollection<TransferItemViewModel>();

        CancelTransferCommand = ReactiveCommand.Create<Guid>(CancelTransfer);
        RetryTransferCommand = ReactiveCommand.Create<Guid>(RetryTransfer);
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompleted);
    }

    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    public ReactiveCommand<Guid, Unit> CancelTransferCommand { get; }
    public ReactiveCommand<Guid, Unit> RetryTransferCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

    public void AddTransfer(TransferTask task)
    {
        var item = new TransferItemViewModel(task);
        Transfers.Add(item);
    }

    public TransferItemViewModel? FindTransfer(Guid transferId)
    {
        return Transfers.FirstOrDefault(t => t.Id == transferId);
    }

    private void CancelTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item == null) return;

        item.Status = TransferStatus.Cancelled;
        _transferManager.CancelTransferAsync(transferId);
    }

    private void RetryTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item == null || item.Status != TransferStatus.Failed) return;

        item.Status = TransferStatus.Queued;
    }

    private void ClearCompleted()
    {
        var completed = Transfers.Where(t =>
            t.Status == TransferStatus.Completed ||
            t.Status == TransferStatus.Cancelled).ToList();

        foreach (var item in completed)
        {
            Transfers.Remove(item);
        }
    }
}

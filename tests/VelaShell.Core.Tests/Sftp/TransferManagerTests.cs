using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public class TransferManagerTests
{
    [TestMethod]
    public async Task QueueTransferAsync_AddsTransferToQueue()
    {
        using var manager = new TransferManager(SlowExecutor());
        TransferTask task = CreateTransferTask(TransferType.Upload);
        await manager.QueueTransferAsync(task);
        await Task.Delay(20);
        TransferTask? retrieved = manager.GetTransfer(task.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(task.Id, retrieved!.Id);
    }

    [TestMethod]
    public async Task QueueTransferAsync_RespectsMaxConcurrentLimit()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = Enumerable.Range(0, 5)
                              .Select(_ => CreateTransferTask(TransferType.Download))
                              .ToList();
        foreach (TransferTask task in tasks)
        {
            await manager.QueueTransferAsync(task);
        }
        await Task.Delay(100);
        int activeCount = manager.ActiveTransfers.Count;
        int queuedCount = manager.QueuedTransfers.Count;
        int totalInSystem = activeCount + queuedCount;
        Assert.IsLessThanOrEqualTo(3, activeCount, "because max concurrent is 3");
        Assert.IsGreaterThan(0, totalInSystem, "because transfers should still be in progress");
    }

    [TestMethod]
    public async Task QueueTransferAsync_WithConcurrentLimit_OnlyThreeRunSimultaneously()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = new List<TransferTask>();
        for (int i = 0; i < 5; i++)
        {
            TransferTask task = CreateTransferTask(TransferType.Upload);
            tasks.Add(task);
            await manager.QueueTransferAsync(task);
        }
        await Task.Delay(200);
        Assert.IsLessThanOrEqualTo(3, manager.ActiveTransfers.Count);
    }

    [TestMethod]
    public async Task CancelTransferAsync_CancelsSpecificTransfer()
    {
        using var manager = new TransferManager();
        TransferTask task = CreateTransferTask(TransferType.Upload);
        await manager.QueueTransferAsync(task);
        await manager.CancelTransferAsync(task.Id);
        TransferTask? cancelledTask = manager.GetTransfer(task.Id);
        Assert.AreEqual(TransferStatus.Cancelled, cancelledTask?.Status);
    }

    [TestMethod]
    public async Task GetTransfer_ReturnsCorrectTransferTask()
    {
        using var manager = new TransferManager();
        TransferTask task = CreateTransferTask(TransferType.Download);
        await manager.QueueTransferAsync(task);
        TransferTask? retrieved = manager.GetTransfer(task.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(task.Id, retrieved!.Id);
        Assert.AreEqual(task.LocalPath, retrieved.LocalPath);
        Assert.AreEqual(task.RemotePath, retrieved.RemotePath);
    }

    [TestMethod]
    public void GetTransfer_WhenNotFound_ReturnsNull()
    {
        using var manager = new TransferManager();
        var nonExistentId = Guid.NewGuid();
        TransferTask? result = manager.GetTransfer(nonExistentId);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ActiveTransfers_ReflectsCurrentlyRunningTransfers()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 2 };
        TransferTask task1 = CreateTransferTask(TransferType.Upload);
        TransferTask task2 = CreateTransferTask(TransferType.Download);
        TransferTask task3 = CreateTransferTask(TransferType.Upload);
        await manager.QueueTransferAsync(task1);
        await manager.QueueTransferAsync(task2);
        await manager.QueueTransferAsync(task3);
        await Task.Delay(100);
        Assert.IsLessThanOrEqualTo(2, manager.ActiveTransfers.Count);
    }

    [TestMethod]
    public void MaxConcurrentTransfers_DefaultsToThree()
    {
        using var manager = new TransferManager();
        Assert.AreEqual(3, manager.MaxConcurrentTransfers);
    }

    [TestMethod]
    public void MaxConcurrentTransfers_CanBeChanged()
    {
        using var manager = new TransferManager();
        manager.MaxConcurrentTransfers = 5;
        Assert.AreEqual(5, manager.MaxConcurrentTransfers);
    }

    private static TransferExecutor SlowExecutor(int delayMs = 2000) => async (task, progress, ct) => { await Task.Delay(delayMs, ct); };

    private static TransferTask CreateTransferTask(TransferType type)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = $"/local/path/file_{Guid.NewGuid()}.txt",
            RemotePath = $"/remote/path/file_{Guid.NewGuid()}.txt",
            Status = TransferStatus.Queued
        };
    }
}

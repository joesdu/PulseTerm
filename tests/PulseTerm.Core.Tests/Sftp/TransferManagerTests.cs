using FluentAssertions;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;
using Xunit;

namespace PulseTerm.Core.Tests.Sftp;

[Trait("Category", "Sftp")]
public class TransferManagerTests
{
    [Fact]
    public async Task QueueTransferAsync_AddsTransferToQueue()
    {
        using var manager = new TransferManager(SlowExecutor());
        var task = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task);
        await Task.Delay(20);

        var retrieved = manager.GetTransfer(task.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task QueueTransferAsync_RespectsMaxConcurrentLimit()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => CreateTransferTask(TransferType.Download))
            .ToList();

        foreach (var task in tasks)
        {
            await manager.QueueTransferAsync(task);
        }

        await Task.Delay(100);

        var activeCount = manager.ActiveTransfers.Count;
        var queuedCount = manager.QueuedTransfers.Count;
        var totalInSystem = activeCount + queuedCount;
        
        activeCount.Should().BeLessThanOrEqualTo(3, "because max concurrent is 3");
        totalInSystem.Should().BeGreaterThan(0, "because transfers should still be in progress");
    }

    [Fact]
    public async Task QueueTransferAsync_WithConcurrentLimit_OnlyThreeRunSimultaneously()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 3 };
        var tasks = new List<TransferTask>();

        for (int i = 0; i < 5; i++)
        {
            var task = CreateTransferTask(TransferType.Upload);
            tasks.Add(task);
            await manager.QueueTransferAsync(task);
        }

        await Task.Delay(200);

        manager.ActiveTransfers.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task CancelTransferAsync_CancelsSpecificTransfer()
    {
        using var manager = new TransferManager();
        var task = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task);
        await manager.CancelTransferAsync(task.Id);

        var cancelledTask = manager.GetTransfer(task.Id);
        cancelledTask?.Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task GetTransfer_ReturnsCorrectTransferTask()
    {
        using var manager = new TransferManager();
        var task = CreateTransferTask(TransferType.Download);

        await manager.QueueTransferAsync(task);
        var retrieved = manager.GetTransfer(task.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(task.Id);
        retrieved.LocalPath.Should().Be(task.LocalPath);
        retrieved.RemotePath.Should().Be(task.RemotePath);
    }

    [Fact]
    public void GetTransfer_WhenNotFound_ReturnsNull()
    {
        using var manager = new TransferManager();
        var nonExistentId = Guid.NewGuid();

        var result = manager.GetTransfer(nonExistentId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ActiveTransfers_ReflectsCurrentlyRunningTransfers()
    {
        using var manager = new TransferManager(SlowExecutor()) { MaxConcurrentTransfers = 2 };
        
        var task1 = CreateTransferTask(TransferType.Upload);
        var task2 = CreateTransferTask(TransferType.Download);
        var task3 = CreateTransferTask(TransferType.Upload);

        await manager.QueueTransferAsync(task1);
        await manager.QueueTransferAsync(task2);
        await manager.QueueTransferAsync(task3);

        await Task.Delay(100);

        manager.ActiveTransfers.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void MaxConcurrentTransfers_DefaultsToThree()
    {
        using var manager = new TransferManager();

        manager.MaxConcurrentTransfers.Should().Be(3);
    }

    [Fact]
    public void MaxConcurrentTransfers_CanBeChanged()
    {
        using var manager = new TransferManager();

        manager.MaxConcurrentTransfers = 5;

        manager.MaxConcurrentTransfers.Should().Be(5);
    }

    private static TransferExecutor SlowExecutor(int delayMs = 2000)
    {
        return async (task, progress, ct) =>
        {
            await Task.Delay(delayMs, ct);
        };
    }

    private static TransferTask CreateTransferTask(TransferType type)
    {
        return new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = $"/local/path/file_{Guid.NewGuid()}.txt",
            RemotePath = $"/remote/path/file_{Guid.NewGuid()}.txt",
            Status = TransferStatus.Queued
        };
    }
}

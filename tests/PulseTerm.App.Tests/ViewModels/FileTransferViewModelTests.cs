using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;

namespace PulseTerm.App.Tests.ViewModels;

public class FileTransferViewModelTests
{
    private readonly ITransferManager _transferManager;
    private readonly FileTransferViewModel _vm;

    public FileTransferViewModelTests()
    {
        _transferManager = Substitute.For<ITransferManager>();
        _transferManager.ActiveTransfers.Returns(new List<TransferTask>());
        _transferManager.QueuedTransfers.Returns(new List<TransferTask>());
        _vm = new FileTransferViewModel(_transferManager);
    }

    private static TransferTask CreateTask(
        TransferType type = TransferType.Upload,
        TransferStatus status = TransferStatus.Queued,
        string remotePath = "/home/user/file.txt",
        string localPath = "/tmp/file.txt")
    {
        return new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = type,
            RemotePath = remotePath,
            LocalPath = localPath,
            Status = status
        };
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void TransferAdded_AppearsInTransfersCollection()
    {
        // Arrange
        var task = CreateTask();

        // Act
        _vm.AddTransfer(task);

        // Assert
        _vm.Transfers.Should().HaveCount(1);
        _vm.Transfers[0].FileName.Should().Be("file.txt");
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void ProgressUpdate_ChangesTransferItemProgress()
    {
        // Arrange
        var task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        var item = _vm.Transfers[0];

        // Act
        var progress = new TransferProgress
        {
            FileName = "file.txt",
            BytesTransferred = 512_000,
            TotalBytes = 1_024_000,
            Percentage = 50,
            SpeedBytesPerSecond = 256_000,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(2)
        };
        item.UpdateProgress(progress);

        // Assert
        item.Progress.Should().Be(50);
        item.TransferredBytes.Should().Be(512_000);
        item.TotalSize.Should().Be(1_024_000);
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void CancelTransfer_UpdatesStatusToCancelled()
    {
        // Arrange
        var task = CreateTask(status: TransferStatus.InProgress);
        _transferManager.CancelTransferAsync(task.Id, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _vm.AddTransfer(task);

        // Act
        _vm.CancelTransferCommand.Execute(task.Id).Subscribe();

        // Assert
        _vm.Transfers[0].Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void ClearCompleted_RemovesCompletedItemsFromList()
    {
        // Arrange
        var active = CreateTask(status: TransferStatus.InProgress);
        var completed1 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done1.txt");
        var completed2 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done2.txt");

        _vm.AddTransfer(active);
        _vm.AddTransfer(completed1);
        _vm.AddTransfer(completed2);
        _vm.Transfers.Should().HaveCount(3);

        // Act
        _vm.ClearCompletedCommand.Execute().Subscribe();

        // Assert
        _vm.Transfers.Should().HaveCount(1);
        _vm.Transfers[0].FileName.Should().Be("file.txt");
    }

    [Theory]
    [Trait("Category", "FileTransfer")]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1_230, "1.2 KB/s")]
    [InlineData(3_670_016, "3.5 MB/s")]
    [InlineData(1_181_116_006, "1.1 GB/s")]
    public void SpeedFormatting_ReturnsHumanReadable(double bytesPerSecond, string expected)
    {
        TransferItemViewModel.FormatSpeed(bytesPerSecond).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void Direction_ShowsCorrectArrow()
    {
        // Arrange & Act
        var upload = CreateTask(type: TransferType.Upload);
        var download = CreateTask(type: TransferType.Download);

        _vm.AddTransfer(upload);
        _vm.AddTransfer(download);

        // Assert
        _vm.Transfers[0].Direction.Should().Be("↑");
        _vm.Transfers[1].Direction.Should().Be("↓");
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void RetryTransfer_RequeuesFailedTransfer()
    {
        // Arrange
        var task = CreateTask(status: TransferStatus.Failed);
        _transferManager.QueueTransferAsync(Arg.Any<TransferTask>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _vm.AddTransfer(task);
        _vm.Transfers[0].Status.Should().Be(TransferStatus.Failed);

        // Act
        _vm.RetryTransferCommand.Execute(task.Id).Subscribe();

        // Assert
        _vm.Transfers[0].Status.Should().Be(TransferStatus.Queued);
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void TimeRemainingFormatting_ShowsReadableString()
    {
        // Arrange
        var task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        var item = _vm.Transfers[0];

        // Act
        var progress = new TransferProgress
        {
            FileName = "file.txt",
            BytesTransferred = 500_000,
            TotalBytes = 1_000_000,
            Percentage = 50,
            SpeedBytesPerSecond = 100_000,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(65)
        };
        item.UpdateProgress(progress);

        // Assert
        item.TimeRemaining.Should().Be("1m 5s");
    }

    [Fact]
    [Trait("Category", "FileTransfer")]
    public void MultipleTransfers_TrackedIndependently()
    {
        // Arrange
        var task1 = CreateTask(remotePath: "/home/user/alpha.zip");
        var task2 = CreateTask(remotePath: "/home/user/beta.tar.gz");

        // Act
        _vm.AddTransfer(task1);
        _vm.AddTransfer(task2);

        // Assert
        _vm.Transfers.Should().HaveCount(2);
        _vm.Transfers[0].FileName.Should().Be("alpha.zip");
        _vm.Transfers[1].FileName.Should().Be("beta.tar.gz");
    }
}

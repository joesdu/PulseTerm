using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class FileTransferViewModelTests
{
    private readonly ITransferManager _transferManager;
    private readonly FileTransferViewModel _vm;

    public FileTransferViewModelTests()
    {
        _transferManager = Substitute.For<ITransferManager>();
        _transferManager.ActiveTransfers.Returns([]);
        _transferManager.QueuedTransfers.Returns([]);
        _vm = new(_transferManager);
    }

    private static TransferTask CreateTask(
        TransferType type = TransferType.Upload,
        TransferStatus status = TransferStatus.Queued,
        string remotePath = "/home/user/file.txt",
        string localPath = "/tmp/file.txt")
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            RemotePath = remotePath,
            LocalPath = localPath,
            Status = status
        };
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TransferAdded_AppearsInTransfersCollection()
    {
        // Arrange
        TransferTask task = CreateTask();

        // Act
        _vm.AddTransfer(task);

        // Assert
        Assert.HasCount(1, _vm.Transfers);
        Assert.AreEqual("file.txt", _vm.Transfers[0].FileName);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ProgressUpdate_ChangesTransferItemProgress()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        TransferItemViewModel item = _vm.Transfers[0];

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
        Assert.AreEqual(50, item.Progress);
        Assert.AreEqual(512_000L, item.TransferredBytes);
        Assert.AreEqual(1_024_000L, item.TotalSize);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void CancelTransfer_UpdatesStatusToCancelled()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _transferManager.CancelTransferAsync(task.Id, Arg.Any<CancellationToken>())
                        .Returns(Task.CompletedTask);
        _vm.AddTransfer(task);

        // Act
        _vm.CancelTransferCommand.Execute(task.Id).Subscribe();

        // Assert
        Assert.AreEqual(TransferStatus.Cancelled, _vm.Transfers[0].Status);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ClearCompleted_RemovesCompletedItemsFromList()
    {
        // Arrange
        TransferTask active = CreateTask(status: TransferStatus.InProgress);
        TransferTask completed1 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done1.txt");
        TransferTask completed2 = CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done2.txt");
        _vm.AddTransfer(active);
        _vm.AddTransfer(completed1);
        _vm.AddTransfer(completed2);
        Assert.HasCount(3, _vm.Transfers);

        // Act
        _vm.ClearCompletedCommand.Execute().Subscribe();

        // Assert
        Assert.HasCount(1, _vm.Transfers);
        Assert.AreEqual("file.txt", _vm.Transfers[0].FileName);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    [DataRow(0, "0 B/s")]
    [DataRow(512, "512 B/s")]
    [DataRow(1_230, "1.2 KB/s")]
    [DataRow(3_670_016, "3.5 MB/s")]
    [DataRow(1_181_116_006, "1.1 GB/s")]
    public void SpeedFormatting_ReturnsHumanReadable(double bytesPerSecond, string expected) => Assert.AreEqual(expected, TransferItemViewModel.FormatSpeed(bytesPerSecond));

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void Direction_ShowsCorrectArrow()
    {
        // Arrange & Act
        TransferTask upload = CreateTask();
        TransferTask download = CreateTask(TransferType.Download);
        _vm.AddTransfer(upload);
        _vm.AddTransfer(download);

        // Assert
        Assert.AreEqual("↓", _vm.Transfers[0].Direction);
        Assert.AreEqual("↑", _vm.Transfers[1].Direction);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void RetryTransfer_WithRetryDelegate_RemovesRowAndInvokesDelegate()
    {
        // 重试的语义:移除失败行并执行浏览器视图模型挂上的重试动作
        // (它会重新探测续传起点并以新行重跑),而不是只把状态改成 Queued。
        TransferTask task = CreateTask(status: TransferStatus.Failed);
        _vm.AddTransfer(task);
        bool invoked = false;
        _vm.Transfers[0].RetryAsync = () =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        _vm.RetryTransferCommand.Execute(task.Id).Subscribe();

        Assert.IsTrue(invoked, "重试动作必须被执行。");
        Assert.IsEmpty(_vm.Transfers, "失败行应被移除,新行由重跑的传输自行添加。");
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void RetryTransfer_WithoutRetryDelegate_IsNoOp()
    {
        // 重启后从历史恢复的记录没有重试委托(原会话已不存在):命令必须安全无操作。
        TransferTask task = CreateTask(status: TransferStatus.Failed);
        _vm.AddTransfer(task);

        _vm.RetryTransferCommand.Execute(task.Id).Subscribe();

        Assert.HasCount(1, _vm.Transfers);
        Assert.AreEqual(TransferStatus.Failed, _vm.Transfers[0].Status);
        Assert.IsFalse(_vm.Transfers[0].CanRetry);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TransferHistory_SavedOnAdd_AndOnFinalStatus()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var vm = new FileTransferViewModel(_transferManager, store);
        TransferTask task = CreateTask(status: TransferStatus.InProgress);

        vm.AddTransfer(task);
        vm.Transfers[0].Status = TransferStatus.Completed;

        store.Received().UpsertAsync(
            "transfer-history",
            "recent",
            Arg.Is<TransferHistoryDocument>(d =>
                d.Items.Count == 1
                && d.Items[0].Id == task.Id
                && d.Items[0].Status == TransferStatus.Completed),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TransferHistory_RestoredOnStartup_InterruptedItemsShownAsFailed_WithoutPoppingPanel()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var doc = new TransferHistoryDocument
        {
            Items =
            [
                new() { Id = Guid.NewGuid(), Type = TransferType.Download, LocalPath = @"C:\dl\a.bin", RemotePath = "/srv/a.bin", Status = TransferStatus.InProgress },
                new() { Id = Guid.NewGuid(), Type = TransferType.Upload, LocalPath = @"C:\ul\b.bin", RemotePath = "/srv/b.bin", Status = TransferStatus.Completed }
            ]
        };
        store.GetAsync<TransferHistoryDocument>("transfer-history", "recent", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<TransferHistoryDocument?>(doc));

        var vm = new FileTransferViewModel(_transferManager, store);

        Assert.HasCount(2, vm.Transfers);
        // 退出瞬间仍在传输的项恢复为失败(会话已不存在);半截文件仍在,重新发起会自动续传。
        Assert.AreEqual(TransferStatus.Failed, vm.Transfers[0].Status);
        Assert.IsFalse(vm.Transfers[0].CanRetry, "历史恢复的记录没有重试委托,不得出现指向已死会话的重试按钮。");
        Assert.AreEqual(TransferStatus.Completed, vm.Transfers[1].Status);
        Assert.IsFalse(vm.IsPanelVisible, "启动恢复历史不该自动弹出传输浮窗。");
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void AddTransfer_CapsListAtHistoryLimit_DroppingOldestSettledRows()
    {
        // 传一个几千文件的目录时,面板列表原本会无限增长,每加一行都要重排整个面板 ——
        // 传输期间拖窗口 / 敲命令的卡顿正出在这里。上限与落盘历史一致:100 条。
        for (int i = 0; i < 150; i++)
        {
            _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: $"/srv/f{i}.bin"));
        }

        Assert.HasCount(100, _vm.Transfers);
        // 新的在前:最后加进来的那条必须还在,被丢掉的是最旧的。
        Assert.AreEqual("f149.bin", _vm.Transfers[0].FileName);
        Assert.AreEqual("f50.bin", _vm.Transfers[99].FileName);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void AddTransfer_OverLimit_NeverDropsInFlightOrFailedTransfers()
    {
        // 超限时只能丢"已完成/已取消"这类用户已知结果的行。
        // 失败行是用户唯一能看到"哪些文件没传成功"的地方(还挂着重试入口),
        // 被后续几千个成功的文件挤掉就等于谎报全部成功;进行中的行同理不能丢。
        TransferTask running = CreateTask(status: TransferStatus.InProgress, remotePath: "/srv/live.bin");
        TransferTask failed = CreateTask(status: TransferStatus.Failed, remotePath: "/srv/broken.bin");
        _vm.AddTransfer(running);
        _vm.AddTransfer(failed);
        for (int i = 0; i < 150; i++)
        {
            _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: $"/srv/f{i}.bin"));
        }

        Assert.Contains(t => t.Id == running.Id, _vm.Transfers);
        Assert.Contains(t => t.Id == failed.Id, _vm.Transfers);
        Assert.HasCount(100, _vm.Transfers);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void AddTransfer_WhenFailuresExceedLimit_KeepsThemAllRatherThanSwallowingThem()
    {
        // 失败多到超过上限时,宁可让列表突破 100 行,也不能悄悄吞掉失败记录。
        for (int i = 0; i < 120; i++)
        {
            _vm.AddTransfer(CreateTask(status: TransferStatus.Failed, remotePath: $"/srv/bad{i}.bin"));
        }

        Assert.HasCount(120, _vm.Transfers);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TimeRemainingFormatting_ShowsReadableString()
    {
        // Arrange
        TransferTask task = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(task);
        TransferItemViewModel item = _vm.Transfers[0];

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
        Assert.AreEqual("1m 5s", item.TimeRemaining);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginBatch_ShowsRemainingCount_AndCountsDownAsFilesSettle()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginBatch(3, cts);
        Assert.IsTrue(_vm.IsBatchActive);
        Assert.AreEqual(3, _vm.PendingCount); // remaining count, not stuck at 1
        _vm.NotifyBatchItemSettled();
        Assert.AreEqual(2, _vm.PendingCount);
        _vm.NotifyBatchItemSettled();
        Assert.AreEqual(1, _vm.PendingCount);
        _vm.EndBatch();
        Assert.IsFalse(_vm.IsBatchActive);
        Assert.AreEqual(0, _vm.PendingCount); // falls back to (empty) active count
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void CancelAll_CancelsBatchToken_AndMarksActiveItemsCancelled()
    {
        using var cts = new CancellationTokenSource();
        TransferTask running = CreateTask(status: TransferStatus.InProgress);
        _vm.AddTransfer(running);
        _vm.BeginBatch(5, cts);
        _vm.CancelAllCommand.Execute().Subscribe();
        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.AreEqual(TransferStatus.Cancelled, _vm.Transfers[0].Status);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ShowPanel_ReopensToast_ForReviewingHistory()
    {
        // A finished transfer leaves the toast collapsed but its history retained.
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        _vm.HidePanelCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.ShowPanel();
        Assert.IsTrue(_vm.IsPanelVisible);
        Assert.HasCount(1, _vm.Transfers); // past record still there to review
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void PendingCount_WithoutBatch_FallsBackToActiveCount()
    {
        _vm.AddTransfer(CreateTask(status: TransferStatus.InProgress));
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        Assert.IsFalse(_vm.IsBatchActive);
        Assert.AreEqual(1, _vm.PendingCount); // one in-flight single transfer
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginPreparing_ShowsPanelImmediately_AndCountsDiscoveredFiles()
    {
        // 选择大文件夹后,扫描期间面板立即可见、徽标随发现数递增。
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.BeginPreparing();
        Assert.IsTrue(_vm.IsPreparing);
        Assert.IsTrue(_vm.IsPanelVisible);
        Assert.AreEqual(0, _vm.PendingCount);
        _vm.UpdatePreparingCount(1);
        Assert.AreEqual(1, _vm.PendingCount);
        _vm.UpdatePreparingCount(42);
        Assert.AreEqual(42, _vm.PendingCount);
        Assert.Contains("42", _vm.PreparingText);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void BeginBatch_TakesOverFromPreparing_BadgeSwitchesToRemaining()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginPreparing();
        _vm.UpdatePreparingCount(7);
        _vm.BeginBatch(7, cts);
        Assert.IsFalse(_vm.IsPreparing);
        Assert.IsTrue(_vm.IsBatchActive);
        Assert.AreEqual(7, _vm.PendingCount); // 从"已发现"无缝切换为"剩余"
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void EndPreparing_WithNothingPlanned_HidesPanelAgain()
    {
        // 计划为空(全部冲突跳过/取消)时退出准备态,面板不残留。
        _vm.BeginPreparing();
        Assert.IsTrue(_vm.IsPanelVisible);
        _vm.EndPreparing();
        Assert.IsFalse(_vm.IsPreparing);
        Assert.IsFalse(_vm.IsPanelVisible);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void UpdatePreparingCount_AfterPreparingEnded_IsIgnored()
    {
        using var cts = new CancellationTokenSource();
        _vm.BeginPreparing();
        _vm.BeginBatch(3, cts);
        _vm.UpdatePreparingCount(99); // 迟到的扫描回调不得污染"剩余"徽标
        Assert.AreEqual(3, _vm.PendingCount);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void ShowPanelTransient_ShowsPanel_WithoutPinningIt()
    {
        // 完成通知展开面板,但自动隐藏倒计时照常进行(ShowPanel 则会钉住面板)。
        _vm.AddTransfer(CreateTask(status: TransferStatus.Completed, remotePath: "/home/user/done.txt"));
        _vm.HidePanelCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsPanelVisible);
        _vm.ShowPanelTransient();
        Assert.IsTrue(_vm.IsPanelVisible);
        // 隐藏倒计时已排定:指针进入应暂停、离开应重启,而不是像 ShowPanel 那样清掉挂起状态。
        _vm.SetPointerOver(true);
        _vm.SetPointerOver(false);
        Assert.IsTrue(_vm.IsPanelVisible); // 3 秒倒计时尚未到期,面板仍在
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void MultipleTransfers_TrackedIndependently()
    {
        // Arrange
        TransferTask task1 = CreateTask(remotePath: "/home/user/alpha.zip");
        TransferTask task2 = CreateTask(remotePath: "/home/user/beta.tar.gz");

        // Act
        _vm.AddTransfer(task1);
        _vm.AddTransfer(task2);

        // Assert
        Assert.HasCount(2, _vm.Transfers);
        Assert.AreEqual("beta.tar.gz", _vm.Transfers[0].FileName);
        Assert.AreEqual("alpha.zip", _vm.Transfers[1].FileName);
    }

    // ---- 面板拖拽位置 ----

    /// <summary>拖拽结束后位置要落盘,否则下次打开又回到默认锚点。</summary>
    [TestMethod]
    public void PersistPanelPosition_WritesCurrentOffsetToStore()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var vm = new FileTransferViewModel(_transferManager, store)
        {
            PanelOffsetX = -320,
            PanelOffsetY = 180,
        };

        vm.PersistPanelPosition();

        store.Received(1).UpsertAsync(
            "ui-layout",
            "transfer-panel",
            Arg.Is<TransferPanelPosition>(p => p.OffsetX == -320 && p.OffsetY == 180),
            Arg.Any<CancellationToken>());
    }

    /// <summary>构造时从存储恢复上次的位置 —— 这就是"再次打开回到原有位置"。</summary>
    [TestMethod]
    public async Task Construction_RestoresPersistedPanelPosition()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<TransferPanelPosition>("ui-layout", "transfer-panel", Arg.Any<CancellationToken>())
             .Returns(new TransferPanelPosition { OffsetX = -240, OffsetY = 96 });

        var vm = new FileTransferViewModel(_transferManager, store);

        // 恢复是异步的,给它一次调度机会。
        await Task.Yield();
        await Task.Delay(50);

        Assert.AreEqual(-240, vm.PanelOffsetX);
        Assert.AreEqual(96, vm.PanelOffsetY);
    }

    /// <summary>没有存储(单元测试/精简宿主)时不该炸,位置退回默认锚点。</summary>
    [TestMethod]
    public void WithoutStore_PanelPositionDefaultsToAnchorAndPersistIsHarmless()
    {
        var vm = new FileTransferViewModel(_transferManager);

        Assert.AreEqual(0, vm.PanelOffsetX);
        Assert.AreEqual(0, vm.PanelOffsetY);
        vm.PersistPanelPosition();
    }

    /// <summary>存储读取失败不能把界面带崩 —— 位置记不住是小事,启动不了是大事。</summary>
    [TestMethod]
    public async Task Construction_WhenStoreThrows_FallsBackToDefaultPosition()
    {
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<TransferPanelPosition>("ui-layout", "transfer-panel", Arg.Any<CancellationToken>())
             .Returns<TransferPanelPosition?>(_ => throw new InvalidOperationException("store offline"));

        var vm = new FileTransferViewModel(_transferManager, store);
        await Task.Delay(50);

        Assert.AreEqual(0, vm.PanelOffsetX);
        Assert.AreEqual(0, vm.PanelOffsetY);
    }
}

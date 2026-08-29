using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
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
    public void TransferPanel_StartsEmpty_AndNeverRestoresRecordsFromDisk()
    {
        // 这是个浮动 toast,只反映本次会话:重启后不该还挂着上次的已完成/已失败记录。
        // 曾经把最近 100 条落盘再恢复进面板,除了不是用户要的,还引出一片渲染不出来的空白 ——
        // 那 100 行是在面板隐藏期间加进集合的,占着高度却画不出来。
        IAppDataStore store = Substitute.For<IAppDataStore>();

        var vm = new FileTransferViewModel(_transferManager, store);

        Assert.IsEmpty(vm.Transfers, "启动时面板必须是空的 —— 任何一行都只能来自本次会话。");
        Assert.IsFalse(vm.IsPanelVisible);
        // 存储里除了面板位置,不该再有任何一次读取:历史那条路径已经整条拆掉。
        store.DidNotReceive().GetAsync<object>("transfer-history", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void AddTransfer_ShowsPanelBeforeTheRowLands_SoNoRowIsEverAddedWhileHidden()
    {
        // 这条是空白区的真正修复点。面板隐藏期间加进来的行会占住高度(面板因此顶到
        // 280px 上限、滚动条也在)却渲染不出来 —— 之前 100 条历史正是这么进来的。
        // 只要"集合非空 ⇔ 面板可见"这条不变量成立,就再没有行能在隐藏状态下入列。
        bool visibleWhenRowLanded = false;
        _vm.Transfers.CollectionChanged += (_, _) => visibleWhenRowLanded = _vm.IsPanelVisible;

        _vm.AddTransfer(CreateTask(status: TransferStatus.InProgress));

        Assert.IsTrue(visibleWhenRowLanded, "行落进集合时面板必须已经可见。");
        Assert.IsTrue(_vm.IsPanelVisible);

        // 反向:清空后面板收起,不留一个空壳挂在界面上。
        _vm.Transfers.Clear();
        Assert.IsFalse(_vm.IsPanelVisible);
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void TransferPanel_PurgesLegacyHistoryDocument_SoStaleRecordsDoNotLinger()
    {
        // 老版本已经把历史写进存储了;既然不再读它,启动时顺手清掉,别留废数据。
        IAppDataStore store = Substitute.For<IAppDataStore>();

        _ = new FileTransferViewModel(_transferManager, store);

        store.Received().DeleteAsync("transfer-history", "recent", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileTransfer")]
    public void AddTransfer_CapsListAtRowLimit_DroppingOldestSettledRows()
    {
        // 传一个几千文件的目录时,面板列表原本会无限增长,每加一行都要重排整个面板 ——
        // 传输期间拖窗口 / 敲命令的卡顿正出在这里。上限:100 行。
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

    /// <summary>
    /// 排队中的行显示"等待中",而不是 0% + "0 B / 0 B • 0 B/s • ↑ 上传中"。
    /// </summary>
    /// <remarks>
    /// FTP 对端只允许一条连接(或一次只让传一个文件)时,整批传输会被连接池排成串行,
    /// 后面的项要等很久才轮到 —— 那期间它们一个字节都没在动,却写着"上传中 0%",
    /// 看上去就是卡死了。
    /// </remarks>
    [TestMethod]
    [TestCategory("FileTransfer")]
    public void QueuedTransfer_ReadsAsWaiting_NotZeroPercent()
    {
        _vm.AddTransfer(CreateTask(status: TransferStatus.Queued));
        TransferItemViewModel item = _vm.Transfers[0];

        Assert.IsTrue(item.IsWaiting);
        Assert.AreEqual(Strings.Get("Msg_Waiting"), item.ProgressText);
        Assert.Contains(Strings.Get("Msg_Waiting"), item.InfoLine);
        Assert.DoesNotContain("0 B/s", item.InfoLine);
        Assert.DoesNotContain(Strings.Get("Msg_Uploading"), item.InfoLine);
    }

    /// <summary>轮到它开跑之后,那一行要变回正常的百分比与速度。</summary>
    [TestMethod]
    [TestCategory("FileTransfer")]
    public void QueuedTransfer_OnceStarted_ShowsPercentAgain()
    {
        _vm.AddTransfer(CreateTask(status: TransferStatus.Queued));
        TransferItemViewModel item = _vm.Transfers[0];

        item.Status = TransferStatus.InProgress;
        item.UpdateProgress(new TransferProgress
        {
            FileName = "file.txt",
            BytesTransferred = 512,
            TotalBytes = 1024,
            Percentage = 50,
            SpeedBytesPerSecond = 128,
            EstimatedTimeRemaining = TimeSpan.FromSeconds(4),
        });

        Assert.IsFalse(item.IsWaiting);
        Assert.AreEqual("50%", item.ProgressText);
        Assert.Contains(Strings.Get("Msg_Uploading"), item.InfoLine);
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

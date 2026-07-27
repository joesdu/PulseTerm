using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>
/// 文件传输面板(toast)的视图模型:聚合活动/历史传输项,驱动准备扫描、
/// 可取消批量传输、徽标计数与自动隐藏等交互逻辑。
/// </summary>
public class FileTransferViewModel : ReactiveObject
{
    /// <summary>面板位置的存储位置(IAppDataStore 的用途之一即 UI 配置)。</summary>
    private const string LayoutCollection = "ui-layout";

    private const string PanelPositionId = "transfer-panel";

    /// <summary>传输历史的存储位置:单文档保存最近若干条记录,重启后可见(断点续传收尾 #2)。</summary>
    private const string HistoryCollection = "transfer-history";

    private const string HistoryId = "recent";

    /// <summary>历史最多保留的记录数(既是落盘上限,也是面板里同时存在的行数上限),超出丢弃最旧的。</summary>
    private const int HistoryLimit = 100;

    // 可空:无参构造的宿主(单元测试/无 SFTP 服务的场景)不提供传输管理器。
    private readonly ITransferManager? _transferManager;

    // 可空:同上,无存储时面板位置只在本次运行内保持。
    private readonly IAppDataStore? _dataStore;

    private IDisposable? _autoHide;

    // 当前批次(一个文件夹/多文件的下载或上传):尚未完成的文件数,
    // 以及用于取消剩余所有文件(包括正在传输的那个)的令牌源。
    private CancellationTokenSource? _batchCts;
    private int _batchRemaining;
    private bool _hidePending;
    private bool _isPointerOver;

    // 准备阶段(上传/下载前的目录扫描):大文件夹的扫描可能持续数秒,期间面板立即弹出、
    // 徽标随发现的文件数递增,让用户知道处理已经开始。
    private bool _isPreparing;
    private int _preparingCount;

    /// <summary>
    /// 构造视图模型并初始化各命令;<paramref name="transferManager" /> 可为空
    /// (单元测试或无 SFTP 服务的宿主场景不提供传输管理器)。
    /// <paramref name="dataStore" /> 为空时面板位置只在本次运行内保持,不跨重启。
    /// </summary>
    public FileTransferViewModel(ITransferManager? transferManager, IAppDataStore? dataStore = null)
    {
        _transferManager = transferManager;
        _dataStore = dataStore;
        RestorePanelPosition();
        Transfers = [];
        Transfers.CollectionChanged += OnTransfersChanged;
        RestoreTransferHistory();
        CancelTransferCommand = ReactiveCommand.Create<Guid>(CancelTransfer);
        RetryTransferCommand = ReactiveCommand.Create<Guid>(RetryTransfer);
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompleted);
        CancelAllCommand = ReactiveCommand.Create(CancelAll);
        HidePanelCommand = ReactiveCommand.Create(() => { IsPanelVisible = false; });
    }

    /// <summary>当前所有传输项(活动与已完成),新任务插入到列表顶部。</summary>
    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    /// <summary>进行中(传输中或排队中)的任务数量。</summary>
    public int ActiveCount => Transfers.Count(t => t.IsActive);

    /// <summary>当前是否正在运行一个可取消的传输批次。</summary>
    public bool IsBatchActive => _batchCts is not null;

    /// <summary>
    /// 头部徽标(design 9Ralg):准备阶段随扫描发现的文件数递增;
    /// 批处理期间为尚待传输的文件数(递减);其余情况为进行中的单文件传输数。
    /// </summary>
    public int PendingCount => IsPreparing
                                   ? _preparingCount
                                   : IsBatchActive
                                       ? _batchRemaining
                                       : ActiveCount;

    /// <summary>上传/下载是否仍在扫描目录以制定计划。</summary>
    public bool IsPreparing
    {
        get => _isPreparing;
        private set => this.RaiseAndSetIfChanged(ref _isPreparing, value);
    }

    /// <summary>准备阶段的状态行文案:随扫描进度动态刷新。</summary>
    public string PreparingText => Strings.Format("Msg_ScanningTransferFiles", _preparingCount);

    /// <summary>
    /// 浮窗仅在有内容且未被手动收起时存在(规范 §9):
    /// 新任务会淡入显示,点关闭(x)只是隐藏 —— 任务继续运行。
    /// </summary>
    public bool IsPanelVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>隐藏传输面板(点击关闭按钮),任务继续在后台运行。</summary>
    public ReactiveCommand<RxVoid, RxVoid> HidePanelCommand { get; }

    /// <summary>取消指定 Id 的单个传输。</summary>
    public ReactiveCommand<Guid, RxVoid> CancelTransferCommand { get; }

    /// <summary>重试指定 Id 的失败传输,将其重新排队。</summary>
    public ReactiveCommand<Guid, RxVoid> RetryTransferCommand { get; }

    /// <summary>清除列表中所有已完成或已取消的传输项。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearCompletedCommand { get; }

    /// <summary>
    /// 取消当前批次中所有剩余文件:中止正在传输的那个并跳过其余
    /// (规范 §9:取消剩余传输)。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelAllCommand { get; }

    /// <summary>
    /// 进入准备(目录扫描)状态:浮窗立即弹出并显示实时文件计数,
    /// 这样选择一个大文件夹时不会再像什么都没发生。由 <see cref="BeginBatch" />
    /// (扫描完成、传输开始)或 <see cref="EndPreparing" /> 结束。
    /// </summary>
    public void BeginPreparing()
    {
        _preparingCount = 0;
        IsPreparing = true;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));

        // 面板立即可见;挂起中的自动隐藏作废(新一轮任务开始了)。
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>更新准备扫描过程中迄今发现的文件实时计数。</summary>
    public void UpdatePreparingCount(int discovered)
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = discovered;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));
    }

    /// <summary>
    /// 不启动批次即离开准备状态(计划为空、取消或出错)。当
    /// <see cref="BeginBatch" /> 已接管时为无操作。若扫描未产出可显示内容则再次隐藏浮窗。
    /// </summary>
    public void EndPreparing()
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = 0;
        IsPreparing = false;
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count == 0)
        {
            IsPanelVisible = false;
        }
        else
        {
            NotifyTaskSettled();
        }
    }

    /// <summary>
    /// 开始一个包含 <paramref name="totalFiles" /> 个传输的可取消批次。随后
    /// 头部显示剩余计数,并提供触发 <paramref name="cts" /> 的取消控件。
    /// </summary>
    public void BeginBatch(int totalFiles, CancellationTokenSource cts)
    {
        // 准备阶段结束,徽标从"已发现"切换为"剩余"。
        _isPreparing = false;
        _preparingCount = 0;
        this.RaisePropertyChanged(nameof(IsPreparing));
        _batchCts = cts;
        _batchRemaining = totalFiles;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>将当前批次中的一个文件标记为完成,并递减剩余计数。</summary>
    public void NotifyBatchItemSettled()
    {
        if (_batchRemaining > 0)
        {
            _batchRemaining--;
        }
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>结束当前批次;浮窗恢复为空闲徽标并恢复自动隐藏。</summary>
    public void EndBatch()
    {
        _batchCts = null;
        _batchRemaining = 0;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));

        // 批次结束后重新评估自动隐藏(批次期间曾抑制了它)。
        NotifyTaskSettled();
    }

    private void CancelAll()
    {
        // Cancel() 内联运行传输的取消回调;加保护,使行为异常的回调绝不会从取消按钮处让应用崩溃。
        try
        {
            _batchCts?.Cancel();
        }
        catch
        {
            // 尽力而为:批次已在拆除中。
        }

        // 立即反映取消状态;正在运行的文件会随其流关闭而逐步结束。
        foreach (TransferItemViewModel item in Transfers.Where(t => t.IsActive).ToList())
        {
            item.Status = TransferStatus.Cancelled;
        }
    }

    /// <summary>
    /// 重新打开传输浮窗(通过工具栏“传输历史”按钮),以便查看过往与
    /// 正在进行的传输。取消任何待定的自动隐藏,并保持显示直到用户用 x 收起。
    /// </summary>
    public void ShowPanel()
    {
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>
    /// 传输完成通知用的临时展开:面板可见,但不像 <see cref="ShowPanel" /> 那样锁定——
    /// 自动隐藏倒计时照常进行(指针悬停时照常暂停)。修复完成通知把面板钉死在界面上、
    /// 只能手动关闭的问题。
    /// </summary>
    public void ShowPanelTransient()
    {
        IsPanelVisible = true;
        NotifyTaskSettled();
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));

        // 项状态变化(完成/失败)也要触发历史落盘,故对增删的项挂/摘状态监听。
        if (e.NewItems is not null)
        {
            foreach (TransferItemViewModel item in e.NewItems.OfType<TransferItemViewModel>())
            {
                item.PropertyChanged += OnTransferItemChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (TransferItemViewModel item in e.OldItems.OfType<TransferItemViewModel>())
            {
                item.PropertyChanged -= OnTransferItemChanged;
            }
        }
        SaveTransferHistory();
        if (Transfers.Count > 0)
        {
            // 启动时恢复的历史是背景信息,不该一开程序就弹传输浮窗;用户经"传输历史"按钮查看。
            if (!_restoringHistory)
            {
                IsPanelVisible = true;
            }
        }
        else
        {
            IsPanelVisible = false;
        }
    }

    private void OnTransferItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferItemViewModel.Status))
        {
            SaveTransferHistory();
        }
    }

    /// <summary>
    /// 任务完成时调用;一旦没有活动项,浮窗会停留约 3 秒显示完成状态,
    /// 随后淡出(规范 §9.4)。指针在浮窗内时暂停倒计时 —— 仅当指针离开后才开始(或重启)计时。
    /// </summary>
    public void NotifyTaskSettled()
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        _autoHide?.Dispose();
        _autoHide = null;

        // 批次有剩余文件时、任一单文件传输仍在进行时、或扫描正在规划下一批次时,保持浮窗开启。
        if (ActiveCount > 0 || IsBatchActive || IsPreparing)
        {
            _hidePending = false;
            return;
        }
        _hidePending = true;
        if (!_isPointerOver)
        {
            ScheduleAutoHide();
        }
    }

    /// <summary>
    /// 由视图在指针进入/离开时调用:进入时暂停任何待定的自动隐藏,
    /// 以便用户查看结果;离开时恢复 3 秒倒计时。
    /// </summary>
    public void SetPointerOver(bool isOver)
    {
        _isPointerOver = isOver;
        if (isOver)
        {
            _autoHide?.Dispose();
            _autoHide = null;
            return;
        }
        if (_hidePending && ActiveCount == 0)
        {
            ScheduleAutoHide();
        }
    }

    // ---- 面板拖拽位置 ----
    //
    // 存的是相对默认锚点(右上角)的偏移,而不是绝对坐标:这样窗口缩放/最大化后面板
    // 仍然贴着右上角的相对位置,不会因为窗口变小而跑到可视区之外。
    // 越界夹紧由视图负责(只有它知道父容器和自身的实际尺寸)。

    /// <summary>面板相对默认锚点的水平偏移(像素,向左为负)。</summary>
    public double PanelOffsetX
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>面板相对默认锚点的垂直偏移(像素,向上为负)。</summary>
    public double PanelOffsetY
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>拖拽结束时由视图调用:把当前位置落盘,供下次打开恢复。失败不影响使用。</summary>
    public void PersistPanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        var position = new TransferPanelPosition { OffsetX = PanelOffsetX, OffsetY = PanelOffsetY };
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _dataStore.UpsertAsync(LayoutCollection, PanelPositionId, position).ConfigureAwait(false);
            }
            catch
            {
                // 位置记不住不该影响传输本身;下次拖动会再试一次。
            }
        }
    }

    /// <summary>启动时异步取回上次的位置。取不到就保持默认锚点。</summary>
    private void RestorePanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                TransferPanelPosition? saved = await _dataStore
                                                     .GetAsync<TransferPanelPosition>(LayoutCollection, PanelPositionId)
                                                     .ConfigureAwait(true);
                if (saved is null)
                {
                    return;
                }
                PanelOffsetX = saved.OffsetX;
                PanelOffsetY = saved.OffsetY;
            }
            catch
            {
                // 读不出来就用默认位置,不打扰用户。
            }
        }
    }

    // ---- 传输历史持久化 ----
    //
    // 目标是"重启后未完成的传输不凭空消失":每次增删项或项落定状态就把最近 100 条快照
    // 落进 IAppDataStore。恢复时,退出瞬间仍活动的项(传输中/排队/续传中)标为"失败"呈现——
    // 那次会话已不存在,只能重新发起;半截文件仍在盘上,重新传同一文件会自动续传接上。
    // 恢复出来的行没有重试委托(CanRetry=false),不会出现指向已死会话的"重试"按钮。

    private bool _restoringHistory;

    private void RestoreTransferHistory()
    {
        if (_dataStore is null)
        {
            return;
        }
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                TransferHistoryDocument? doc = await _dataStore
                                                     .GetAsync<TransferHistoryDocument>(HistoryCollection, HistoryId)
                                                     .ConfigureAwait(true);
                if (doc is not { Items.Count: > 0 })
                {
                    return;
                }
                _restoringHistory = true;
                try
                {
                    foreach (TransferHistoryRecord record in doc.Items.Take(HistoryLimit))
                    {
                        var task = new TransferTask
                        {
                            Id = record.Id,
                            Type = record.Type,
                            LocalPath = record.LocalPath,
                            RemotePath = record.RemotePath,
                            Status = record.Status is TransferStatus.InProgress or TransferStatus.Queued or TransferStatus.Resuming
                                ? TransferStatus.Failed // 上次退出时被打断。
                                : record.Status
                        };
                        Transfers.Add(new(task));
                    }
                }
                finally
                {
                    _restoringHistory = false;
                }
            }
            catch
            {
                // 历史读不出来不影响新传输;下次落盘会重建文档。
            }
        }
    }

    // 落盘合流:一个大目录的传输会在几秒内打出成百上千次"新增行 / 行落定"的变更,
    // 每次都独立发起一次全量写会把存储引擎(以及它背后的锁)按在地上打,IO 争用又反过来
    // 拖慢传输本身。这里改为"同时最多一个写在飞":飞行期间的变更只置脏标记,
    // 写完再补最后一次 —— 落盘结果与逐次写完全一致,只是省掉了中间那些注定被覆盖的版本。
    private bool _saveDirty;
    private bool _saveRunning;

    private void SaveTransferHistory()
    {
        if (_dataStore is null || _restoringHistory)
        {
            return;
        }
        _saveDirty = true;
        if (_saveRunning)
        {
            return;
        }
        _saveRunning = true;
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                while (_saveDirty)
                {
                    _saveDirty = false;

                    // 快照必须在续回 UI 线程后取:Transfers 只在 UI 线程上被增删。
                    TransferHistoryDocument doc = BuildHistoryDocument();
                    await _dataStore.UpsertAsync(HistoryCollection, HistoryId, doc).ConfigureAwait(true);
                }
            }
            catch
            {
                // 历史记不上不影响传输本身;下一次状态变化会再试。
            }
            finally
            {
                _saveRunning = false;
            }
        }
    }

    private TransferHistoryDocument BuildHistoryDocument() =>
        new()
        {
            Items = Transfers.Take(HistoryLimit)
                             .Select(t => new TransferHistoryRecord
                             {
                                 Id = t.Id,
                                 Type = t.Type,
                                 LocalPath = t.LocalPath,
                                 RemotePath = t.RemotePath,
                                 Status = t.Status
                             })
                             .ToList()
        };

    private void ScheduleAutoHide()
    {
        _autoHide = DispatcherTimer.RunOnce(() =>
        {
            if (ActiveCount != 0 || _isPointerOver)
            {
                return;
            }
            IsPanelVisible = false;
            _hidePending = false;
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>新增一个传输任务;插入列表顶部,使进行中的传输无需滚动即可看到。</summary>
    public void AddTransfer(TransferTask task)
    {
        var item = new TransferItemViewModel(task);
        // 新任务出现在顶部,这样进行中的上传无需滚动即可看到。
        Transfers.Insert(0, item);
        TrimToLimit();
    }

    /// <summary>
    /// 把面板里的行数压在 <see cref="HistoryLimit" /> 条以内。一次传几千个文件时这个列表
    /// 原本会无限增长,而落盘的历史早就只留最近 100 条 —— 多出来的行既进不了历史,还要
    /// 每新增一条就重新布局一次面板,拖窗口与敲命令都跟着卡。
    ///
    /// 只丢弃"用户已经知道结果、也不需要再处理"的旧行:已完成与已取消。
    /// 失败行必须留着 —— 它是用户唯一能看到"哪些文件没传成功"的地方,还挂着重试入口;
    /// 传 5000 个文件挤掉那几条失败记录,用户会以为全部成功。进行中的行同理不能丢。
    /// 代价是失败/进行中的行超过 100 条时列表会突破上限,这是刻意的:
    /// 宁可多渲染几行,也不能把失败悄悄吞掉。
    /// </summary>
    private void TrimToLimit()
    {
        for (int i = Transfers.Count - 1; i >= 0 && Transfers.Count > HistoryLimit; i--)
        {
            if (Transfers[i].Status is TransferStatus.Completed or TransferStatus.Cancelled)
            {
                Transfers.RemoveAt(i);
            }
        }
    }

    /// <summary>按 Id 查找传输项,未找到时返回 <see langword="null" />。</summary>
    public TransferItemViewModel? FindTransfer(Guid transferId) => Transfers.FirstOrDefault(t => t.Id == transferId);

    private void CancelTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item == null)
        {
            return;
        }
        item.Status = TransferStatus.Cancelled;
        _transferManager?.CancelTransferAsync(transferId);
    }

    private void RetryTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item is not { Status: TransferStatus.Failed, RetryAsync: not null })
        {
            return;
        }

        // 移除失败行再执行重试动作:重试会经原浏览器视图模型重新探测续传起点并
        // 以一条新传输行重跑(RunTransferAsync 会重新 AddTransfer),避免同一文件双行并存。
        Transfers.Remove(item);
        _ = GuardedRetryAsync(item);
    }

    private static async Task GuardedRetryAsync(TransferItemViewModel item)
    {
        try
        {
            await item.RetryAsync!();
        }
        catch
        {
            // 重试自身的失败已由 RunTransferAsync 在新行上落定状态(标红 + 错误消息),此处只防未观察异常。
        }
    }

    private void ClearCompleted()
    {
        var completed = Transfers.Where(t =>
            t.Status is TransferStatus.Completed or TransferStatus.Cancelled).ToList();
        foreach (TransferItemViewModel item in completed)
        {
            Transfers.Remove(item);
        }
    }
}

/// <summary>传输历史的持久化文档:单文档保存最近若干条记录(见 <see cref="TransferHistoryRecord" />)。</summary>
public sealed class TransferHistoryDocument
{
    /// <summary>历史记录,新的在前。</summary>
    public List<TransferHistoryRecord> Items { get; set; } = [];
}

/// <summary>一条传输历史记录:恢复展示与"重启后未完成传输不丢失"所需的最小字段。</summary>
public sealed class TransferHistoryRecord
{
    /// <summary>传输任务 Id。</summary>
    public Guid Id { get; set; }

    /// <summary>传输方向。</summary>
    public TransferType Type { get; set; }

    /// <summary>本地路径(远端复制时为远端源路径)。</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>远端路径。</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>落盘时的状态;活动状态在恢复时映射为失败(会话已不存在)。</summary>
    public TransferStatus Status { get; set; }
}

/// <summary>
/// 传输面板拖拽位置的持久化载体(IAppDataStore 需要引用类型)。
/// 存偏移而非绝对坐标,理由见 <see cref="FileTransferViewModel.PanelOffsetX" />。
/// </summary>
public sealed class TransferPanelPosition
{
    /// <summary>相对默认锚点的水平偏移(像素)。</summary>
    public double OffsetX { get; set; }

    /// <summary>相对默认锚点的垂直偏移(像素)。</summary>
    public double OffsetY { get; set; }
}

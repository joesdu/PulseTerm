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
public class FileTransferViewModel : ReactiveObject, IDraggablePanel
{
    /// <summary>面板位置的存储位置(IAppDataStore 的用途之一即 UI 配置)。</summary>
    private const string LayoutCollection = "ui-layout";

    private const string PanelPositionId = "transfer-panel";

    /// <summary>
    /// 旧版传输历史的存储位置。历史恢复已移除(见 <see cref="PurgeLegacyHistory" />),
    /// 这两个常量只用来把遗留文档从存储里清掉。
    /// </summary>
    private const string HistoryCollection = "transfer-history";

    private const string HistoryId = "recent";

    /// <summary>面板里同时存在的行数上限,超出丢弃最旧的已结束行。</summary>
    private const int RowLimit = 100;

    /// <summary>并发传输名额的硬上限,与设置项的取值范围一致(见 <c>AppSettings.Normalize</c>)。</summary>
    private const int MaxTransferSlots = 16;

    /// <summary>
    /// 全窗口共享的并发传输名额。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「设置 → 文件传输 → 最大并发传输数」写的是一个数,没有任何限定词,读起来就是全局的。
    /// 而闸此前是<b>每批次一个</b>:双栏面板左右各拖一个文件夹是 2N,开三个服务器标签同时传
    /// 是 3N,同一面板先后拖两批也是 2N。用户把这个值调小,想要的恰恰是"别把线路占满"
    /// (跳板机、按流量计费的链路、生产机上不想抢 IO),实际并发却在悄悄翻倍。
    /// </para>
    /// <para>
    /// 名额放在这里,是因为传输浮窗本来就是全窗口共享的那一个(各文件面板的
    /// <c>TransferSink</c> 都指向它)—— 它是唯一天然知道"这个窗口一共在传几个"的地方。
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _transferSlots = new(0, MaxTransferSlots);

    private readonly Lock _slotGate = new();

    /// <summary>流通中的名额总数(空闲的 + 已被占用的)。</summary>
    private int _issuedSlots;

    /// <summary>
    /// 待回收的名额数:上限被调小时产生,由后续的归还逐个抵消。
    /// </summary>
    /// <remarks>
    /// 调小上限时不能直接去"抢回"多余的名额 —— 那要等正在传的文件结束,会把调用方
    /// (UI 线程上的批次启动)挂住。记成欠账,等它们各自传完归还时顺手扣掉,一次都不用等。
    /// </remarks>
    private int _slotDebt;

    // 可空:无参构造的宿主(单元测试/无 SFTP 服务的场景)不提供传输管理器。
    private readonly ITransferManager? _transferManager;

    // 可空:同上,无存储时面板位置只在本次运行内保持。
    private readonly IAppDataStore? _dataStore;

    private IDisposable? _autoHide;

    /// <summary>
    /// 当前所有活动批次(一个文件夹/多文件的下载或上传各算一个),按批次 id 索引。
    /// </summary>
    /// <remarks>
    /// <b>这里以前只有一个 <c>_batchCts</c> 和一个 <c>_batchRemaining</c></b>,而传输浮窗是
    /// 全窗口共享的:两台服务器同时传东西,后开的批次直接把前一个的取消源覆盖掉,于是
    /// <list type="bullet">
    /// <item>「全部取消」只真的停掉最后那个批次,别的照传不误 —— 而界面上所有行都被涂成
    /// 「已取消」,这是<b>伪取消</b>:用户以为停了,数据还在往服务器上写;</item>
    /// <item>先结束的批次调 <c>EndBatch()</c>,把另一个还在跑的批次的剩余计数和
    /// 「批次进行中」状态一起清掉,头部徽标随即胡说八道。</item>
    /// </list>
    /// 现在每个批次拿自己的 id,只结算自己那一份。
    /// </remarks>
    private readonly Dictionary<Guid, ActiveBatch> _batches = [];

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
        PurgeLegacyHistory();
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

    /// <summary>当前是否有<b>任何</b>可取消的传输批次在跑。</summary>
    public bool IsBatchActive => _batches.Count > 0;

    /// <summary>
    /// 头部徽标(design 9Ralg):准备阶段随扫描发现的文件数递增;
    /// 批处理期间为尚待传输的文件数(全部批次相加,递减);其余情况为进行中的单文件传输数。
    /// </summary>
    public int PendingCount => IsPreparing
                                   ? _preparingCount
                                   : IsBatchActive
                                       ? _batches.Values.Sum(b => b.Remaining)
                                       : ActiveCount;

    /// <summary>
    /// 取一个并发传输名额;拿到之前一直等。释放返回的对象即归还名额。
    /// </summary>
    /// <param name="limit">当前设置里的上限。每次调用都带上,设置改了下一次就生效。</param>
    /// <param name="cancellationToken">等待期间的取消(取消了就没拿到,不必归还)。</param>
    /// <returns>归还名额用的句柄。</returns>
    public async Task<IDisposable> AcquireTransferSlotAsync(int limit, CancellationToken cancellationToken)
    {
        ApplySlotLimit(limit);
        await _transferSlots.WaitAsync(cancellationToken);
        return new TransferSlot(this);
    }

    /// <summary>当前空闲名额数(回归用例读它)。</summary>
    internal int AvailableTransferSlotsForTest => _transferSlots.CurrentCount;

    /// <summary>当前生效的并发上限(回归用例读它)。</summary>
    internal int TransferSlotLimitForTest
    {
        get
        {
            lock (_slotGate)
            {
                return _issuedSlots - _slotDebt;
            }
        }
    }

    /// <summary>把流通名额数调到 <paramref name="limit" />。<b>绝不阻塞</b>(见 <see cref="_slotDebt" />)。</summary>
    private void ApplySlotLimit(int limit)
    {
        limit = Math.Clamp(limit, 1, MaxTransferSlots);
        lock (_slotGate)
        {
            int effective = _issuedSlots - _slotDebt;
            if (limit > effective)
            {
                // 先抵消欠账(那些名额还在流通,只是记着要收回),不够再发新的。
                int need = limit - effective;
                int fromDebt = Math.Min(_slotDebt, need);
                _slotDebt -= fromDebt;
                for (int i = fromDebt; i < need; i++)
                {
                    _transferSlots.Release();
                    _issuedSlots++;
                }
                return;
            }
            if (limit == effective)
            {
                return;
            }
            _slotDebt += effective - limit;
            // 池子里还闲着的名额可以当场收掉,不用等谁归还。
            while (_slotDebt > 0 && _transferSlots.Wait(0))
            {
                _slotDebt--;
                _issuedSlots--;
            }
        }
    }

    private void ReleaseTransferSlot()
    {
        lock (_slotGate)
        {
            if (_slotDebt > 0)
            {
                // 上限已经调小:这个名额收回,不放回池子。
                _slotDebt--;
                _issuedSlots--;
                return;
            }
        }
        _transferSlots.Release();
    }

    /// <summary>一个已占用的并发名额;释放即归还。</summary>
    private sealed class TransferSlot(FileTransferViewModel owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }
            _released = true;
            owner.ReleaseTransferSlot();
        }
    }

    /// <summary>一个活动批次:剩余文件数 + 取消它自己那一批的令牌源。</summary>
    private sealed class ActiveBatch(CancellationTokenSource cts, int remaining)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public int Remaining { get; set; } = remaining;
    }

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
    /// 开始一个包含 <paramref name="totalFiles" /> 个传输的可取消批次,返回它的 id。
    /// </summary>
    /// <remarks>
    /// 调用方必须把这个 id 一路带到 <see cref="NotifyBatchItemSettled" /> 与
    /// <see cref="EndBatch" />,否则又会回到"谁结束都算大家结束"的老问题上。
    /// </remarks>
    /// <param name="totalFiles">本批次的文件数。</param>
    /// <param name="cts">取消<b>本批次</b>剩余文件(含正在传的那个)的令牌源。</param>
    /// <returns>批次 id。</returns>
    public Guid BeginBatch(int totalFiles, CancellationTokenSource cts)
    {
        // 准备阶段结束,徽标从"已发现"切换为"剩余"。
        _isPreparing = false;
        _preparingCount = 0;
        this.RaisePropertyChanged(nameof(IsPreparing));
        var id = Guid.NewGuid();
        _batches[id] = new ActiveBatch(cts, totalFiles);
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));
        return id;
    }

    /// <summary>把指定批次里的一个文件标记为完成,递减<b>该批次</b>的剩余计数。</summary>
    /// <param name="batchId"><see cref="BeginBatch" /> 返回的批次 id。</param>
    public void NotifyBatchItemSettled(Guid batchId)
    {
        if (_batches.TryGetValue(batchId, out ActiveBatch? batch) && batch.Remaining > 0)
        {
            batch.Remaining--;
        }
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>结束指定批次;全部批次都结束后浮窗才恢复空闲徽标与自动隐藏。</summary>
    /// <param name="batchId"><see cref="BeginBatch" /> 返回的批次 id。</param>
    public void EndBatch(Guid batchId)
    {
        _batches.Remove(batchId);
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));

        // 批次结束后重新评估自动隐藏(批次期间曾抑制了它)。
        NotifyTaskSettled();
    }

    /// <summary>
    /// 取消全部:每一个活动批次都要真的收到取消信号。
    /// </summary>
    /// <remarks>
    /// 之前只取消"最后登记的那一个"却把所有活动行涂成已取消 —— 界面说停了,
    /// 另一台服务器上的传输还在继续写。行的状态只有在**取消确实发出去之后**才可以改。
    /// </remarks>
    private void CancelAll()
    {
        // Cancel() 内联运行传输的取消回调;加保护,使行为异常的回调绝不会从取消按钮处让应用崩溃。
        foreach (ActiveBatch batch in _batches.Values.ToList())
        {
            try
            {
                batch.Cts.Cancel();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                // 尽力而为:这个批次已在拆除中。
            }
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

        // 行状态变化(完成/失败)要刷新徽标计数,故对增删的项挂/摘状态监听。
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
        IsPanelVisible = Transfers.Count > 0;
    }

    private void OnTransferItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferItemViewModel.Status))
        {
            this.RaisePropertyChanged(nameof(ActiveCount));
            this.RaisePropertyChanged(nameof(PendingCount));
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
        var position = new PanelPosition { OffsetX = PanelOffsetX, OffsetY = PanelOffsetY };
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
                PanelPosition? saved = await _dataStore
                                                     .GetAsync<PanelPosition>(LayoutCollection, PanelPositionId)
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

    // ---- 传输历史 ----
    //
    // 曾经把最近 100 条传输落盘、启动时恢复进面板(dcc4ceb),现已整体移除:
    //   1. 这是个浮动 toast,只该反映本次会话正在发生的事;重启后还挂着上次的
    //      已完成/已失败记录不是用户要的。
    //   2. 恢复发生在构造期,而面板此刻还是隐藏的 —— 这 100 行加进集合后占着高度
    //      (面板因此顶到 280px 上限、滚动条也在),却画不出来,于是列表下方一大片空白。
    //      真正渲染出来的只有面板可见之后才加入的那几行。
    // 面板行数上限(RowLimit)保留:它才是传输几千个文件时不卡的那一半。

    /// <summary>一次性清掉旧版落盘的历史文档,免得废数据留在存储里。</summary>
    private void PurgeLegacyHistory()
    {
        if (_dataStore is null)
        {
            return;
        }
        _ = PurgeAsync();

        async Task PurgeAsync()
        {
            try
            {
                await _dataStore.DeleteAsync(HistoryCollection, HistoryId).ConfigureAwait(false);
            }
            catch
            {
                // 清不掉也无所谓:没人再读它了。
            }
        }
    }

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
    /// 把面板里的行数压在 <see cref="RowLimit" /> 条以内。一次传几千个文件时这个列表
    /// 原本会无限增长,每新增一条就要重新布局一次面板,拖窗口与敲命令都跟着卡。
    ///
    /// 只丢弃"用户已经知道结果、也不需要再处理"的旧行:已完成与已取消。
    /// 失败行必须留着 —— 它是用户唯一能看到"哪些文件没传成功"的地方,还挂着重试入口;
    /// 传 5000 个文件挤掉那几条失败记录,用户会以为全部成功。进行中的行同理不能丢。
    /// 代价是失败/进行中的行超过 100 条时列表会突破上限,这是刻意的:
    /// 宁可多渲染几行,也不能把失败悄悄吞掉。
    /// </summary>
    private void TrimToLimit()
    {
        for (int i = Transfers.Count - 1; i >= 0 && Transfers.Count > RowLimit; i--)
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

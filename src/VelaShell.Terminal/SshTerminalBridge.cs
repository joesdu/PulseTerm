using System.Buffers;
using System.Threading.Channels;
using Avalonia.Threading;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal;

/// <summary>
/// SSH ShellStream 与终端模拟器之间的桥接:后台读线程批量拉取主机输出、合并后在 UI 线程一次性喂入,
/// 并把用户输入写回 PTY。同时负责回显抑制与远端关闭通知。
/// </summary>
public class SshTerminalBridge : IDisposable
{
    private readonly CancellationTokenSource _cts;
    /// <summary>
    /// 一块待喂入终端的输出。
    /// </summary>
    /// <param name="Buffer">承载字节的数组。<b>可能比实际数据长</b>(池租的数组只保证 ≥ 请求长度)。</param>
    /// <param name="Length">有效字节数 —— 一律以它为准,绝不能用 <c>Buffer.Length</c>。</param>
    /// <param name="Pooled">
    /// 该数组是否租自 <see cref="ArrayPool{T}" />。为 true 时排空后必须归还;
    /// 转交路由(ZMODEM)产出的数组不是池的,归还就会污染池。
    /// </param>
    private readonly record struct PendingChunk(byte[] Buffer, int Length, bool Pooled);

    private readonly List<PendingChunk> _pending = [];

    /// <summary>
    /// <see cref="FlushPending" /> 从 <see cref="_pending" /> 摘下来的待处理块。
    /// 提出来是为了让拼接与归还都在锁外做(锁只护摘取这一下),同时保住"归还必发生"的时机 ——
    /// 池数组只有在 Feed 同步消费完之后才能还。仅 UI 线程访问。
    /// </summary>
    private readonly List<PendingChunk> _draining = [];

    // 出站写队列:所有发往 PTY 的字节(击键 + SendRaw 注入)先入队,由唯一的写循环按序
    // 逐段 await 后刷出。绝不能对底层流并发 WriteAsync —— Tmds.Ssh 的 SshChannel.WriteAsync
    // 没有任何并发防护:两个写并发时会各自读发送窗口、交错切包,字节以乱序抵达远端;
    // 远端 shell 按收到的顺序回显,屏幕上就是"打 docker status 出来字符拆散跳动"。
    // 打字稍快 + 网络延迟让上一个写挂起 await,下一个按键就会插队,竞态必现。
    // (旧 SSH.NET 的 ShellStream 内部有锁掩盖了这一点,迁移 Tmds.Ssh 后暴露。)
    private readonly Channel<OutboundItem> _writeQueue = Channel.CreateUnbounded<OutboundItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly Task _writeTask;

    /// <summary>
    /// 出站队列元素:待发载荷 + 可选的排空信号(仅测试探针使用,写循环处理到该元素
    /// 且其前所有写都已完成后置位)。
    /// </summary>
    private readonly record struct OutboundItem(byte[] Data, TaskCompletionSource? Drained);

    // 输出合批泵:读线程把原始分块入队,并只请求一次 UI 线程的合并刷新,
    // 而非每次读取都编组并喂入一次。在突发输出(apt/yum、cat、进度条)下,这把
    // 数百次跨线程跳转 + 整屏重绘,压缩成每帧一次 Feed。
    private readonly Lock _pendingLock = new();
    private readonly IShellStreamWrapper _shellStream;
    private readonly ITerminalEmulator _terminal;
    private volatile bool _disposed;

    // 连接初始化命令的回显抑制器(静默执行);仅在 UI 线程读写(Arm 与 FlushPending 同线程)。
    private EchoSuppressor? _echoSuppressor;

    // 同一个抑制针的第二份实例,专供旁路记录(DataReceived → 会话日志 / 会话录制)。
    // 记录挂在读线程的原始流上,拿不到显示路径抑制后的结果 —— 于是注入的初始化脚本
    // 在终端里看不见,回放时却整行冒出来。两条路径看的是同一份字节流,但在不同线程上消费,
    // 共用一个实例会踩坏 EchoSuppressor 的 _held/_hitsLeft 状态,故各持一份。
    // volatile:由 UI 线程装配(SuppressEchoOnce),由读线程消费。
    private volatile EchoSuppressor? _tapEchoSuppressor;
    private int _flushScheduled;
    private Task? _readTask;
    private int _started;

    /// <summary>绑定终端模拟器与 Shell 流,并订阅终端的用户输入事件。</summary>
    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _cts = new();
        _terminal.UserInput += OnUserInput;
        _writeTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>停止读循环、退订输入事件并释放 Shell 流与取消源(可安全重复调用)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _terminal.UserInput -= OnUserInput;
        TransferRouter?.SessionEnded -= OnFileTransferSessionEnded;

        // 封口写队列:写循环排空残余(_disposed 已置位,只弃不写)后自行退出。
        _writeQueue.Writer.TryComplete();

        // 读循环可能正等在背压闸上(积压超高水位)。此刻 UI 再也不会来排空了,
        // 不放行它就会一直挂在那里 —— 下面的 _readTask.Wait 白等满 2 秒才超时返回。
        ReleaseDrainGate();

        // 先释放流、后取消令牌:释放流会以"通道关闭"唤醒挂起的读取,包装层将其吞为 EOF,
        // 读循环无异常退出。若先 Cancel,取消会以 OperationCanceledException 打穿底层库的
        // 整条异步读栈,每次关标签都在调试器里刷一串首次机会异常。令牌保留为兜底:
        // 个别实现的 Dispose 若未能唤醒读取,Cancel 仍能让循环退出。
        try
        {
            _shellStream.Dispose();
        }
        catch
        {
            // 尽力而为:通道可能已被会话断开拆除。
        }
        _cts.Cancel();
        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 吞掉释放期间读任务抛出的异常
        }
        try
        {
            // 流已释放:挂起中的写以 ObjectDisposedException 醒来并被吞掉,循环随即因封口退出。
            _writeTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // 吞掉释放期间写任务抛出的异常
        }
        _cts.Dispose();
        // 闸最后释放:上面两个 Wait 返回之后,读循环已经不可能再碰它。
        _drainGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>读写或喂入终端过程中发生异常时触发。</summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// 主机输出分块,在读线程上触发 —— 供会话日志(设置 → 常规)与会话录制
    /// (设置 → 安全审计)使用。订阅者必须快速返回且绝不抛异常。
    /// 与显示路径一样剥除了连接初始化命令的回显(见 <see cref="SuppressEchoOnce" />):
    /// 注入的脚本在终端里既然是隐形的,记录与回放里也不该冒出来。
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// 当远端关闭通道时触发(例如 shell 执行了 <c>exit</c> 或
    /// 服务器重启):读循环自行结束,而非经由 <see cref="Dispose" />。
    /// 使会话可转为断开状态并就地重连。
    /// 主动拆除期间不会触发。在读取线程上触发——按需封送。
    /// <para>
    /// 参数是「为什么结束」:<c>exit</c> 与掉线在这里必须分得开,否则宿主只能
    /// 把两者一视同仁地自动连回去,用户就退不掉了(#383)。
    /// </para>
    /// </summary>
    public event Action<ShellCloseReason>? Closed;

    /// <summary>
    /// 可选的 ZMODEM 路由器。非 null 时,读循环会先经它路由每一段输出字节
    /// (检测并接管 ZMODEM 会话),其余字节才嗂入终端。由宿主在启动前装配。
    /// 赋值时自动订阅其会话结束事件,以便在会话收尾后把终端复位到干净状态。
    /// </summary>
    public FileTransfer.TerminalTransferRouter? TransferRouter
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field?.SessionEnded -= OnFileTransferSessionEnded;
            field = value;
            field?.SessionEnded += OnFileTransferSessionEnded;
        }
    }

    // 退出备用屏幕缓冲区的控制序列(DECRST 1049)。ZMODEM 传输对 VT 终端本应完全透明,
    // 任何会话都不该把终端切到备用屏;每次会话收尾补发一次以自愈,防止杂散协议字节把主屏内容
    // 挡在空白的备用屏后面(表现为"整屏内容消失、只能重开会话")。
    private static readonly byte[] AltScreenExit = "\x1b[?1049l"u8.ToArray();

    /// <summary>
    /// 传输会话结束(成功 / 失败 / 取消)后的终端复位:在 UI 线程补发一次 DECRST 1049,
    /// 再把路由器回收的残余字节(协议帧之后紧跟的 shell 输出,典型就是提示符)喂回终端。
    /// 若终端确实被杂散字节卡在备用屏,DECRST 会切回主屏、恢复可见内容;若本就在主屏(正常情况),
    /// 模拟器会短路返回,是无副作用的空操作。事件在后台线程触发,故必须编组到 UI 线程再喂入。
    /// </summary>
    private void OnFileTransferSessionEnded(Core.FileTransfer.Model.FileTransferSession session)
    {
        _ = session;
        if (_disposed)
        {
            return;
        }
        // 必须在这里(而非 UI 线程闭包里)取:路由器已经复位,下一次会话会覆盖这份缓存。
        byte[] tail = TransferRouter?.TakeRecoveredBytes() ?? [];
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _terminal.Feed(AltScreenExit);
                if (tail.Length > 0)
                {
                    _terminal.Feed(tail);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        });
    }

    /// <summary>
    /// 在输出流上剥除即将注入的命令回显(见 <see cref="EchoSuppressor" />)。
    /// 回显最多出现两次(内核规范模式 + readline 预输入重绘),窗口过后自动失效。
    /// 显示路径与旁路记录路径(<see cref="DataReceived" />)各装一份实例,理由见字段注释。
    /// </summary>
    public void SuppressEchoOnce(byte[] needle)
    {
        _echoSuppressor = new(needle, 2, TimeSpan.FromSeconds(10));
        _tapEchoSuppressor = new(needle, 2, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 程序化注入:直写 PTY,不经终端控件的输入事件。连接初始化命令(用户配置的
    /// 启动命令)必须走这里——若走 WriteInput,注入里的 ESC 字节会把命令补全的行跟踪器
    /// (plan.md #16)打进未知态,SSH 标签的智能建议从连接起就全灭(实测取证)。
    /// </summary>
    public void SendRaw(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }
        _writeQueue.Writer.TryWrite(new(data, null));
    }

    /// <summary>
    /// 测试探针:返回的任务在"此刻已入队的所有写全部落到底层流"后完成。
    /// 借道队列本身实现(入队一个空载荷哨兵),因此对时序零假设——替代测试里的 Thread.Sleep。
    /// </summary>
    internal Task DrainWritesAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new([], tcs)))
        {
            tcs.SetResult(); // 队列已封口(Dispose 后):没有在途写可等。
        }
        return tcs.Task;
    }

    /// <summary>启动后台读循环;仅允许调用一次,重复调用会抛出异常。</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("Bridge already started");
        }

        // 只启动读取,不要用换行符预热 shell —— 服务器在连接时本就会发送标语和提示符,
        // 多余的 '\n' 会制造出重复的提示符行。令牌在此处快照,因为 Dispose 会在 2 秒宽限后
        // 释放 _cts —— 仍在排空的循环此后不得再触碰 CTS 属性(令牌读取仍有效)。
        CancellationToken token = _cts.Token;
        _readTask = Task.Run(() => ReadLoopAsync(token));
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        // 更大的读取缓冲意味着更少的 await 与更大的自然批次。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        bool remoteClosed = false;

        // 结束的原因由流给出(它才知道是干净的 EOF 还是抛出来的),读循环只负责转述。
        ShellCloseReason closeReason = ShellCloseReason.Unknown;
        try
        {
            while (!token.IsCancellationRequested && _shellStream.CanRead)
            {
                int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // EOF:远端已关闭通道(exit / 重启 / 连接断开)。到底是哪一种,问流。
                    remoteClosed = true;
                    closeReason = _shellStream.CloseReason;
                    break;
                }

                // 每块仍要一份自己的副本(读缓冲下一轮就被覆写,而 UI 线程晚一点才排空),
                // 但副本租自池:这里原先是 `new byte[bytesRead]`,cat 一个大文件时它就是
                // 整条输出链路上最大的一笔分配(每秒上千块、每块至多 16KB)。
                // 归还发生在 FlushPending 把它喂完之后,见 PendingChunk.Pooled。
                byte[] data = ArrayPool<byte>.Shared.Rent(bytesRead);
                buffer.AsSpan(0, bytesRead).CopyTo(data);

                // 记录/回放拿到的流与屏幕一致:注入的初始化脚本回显在此剥除。
                // 无论有无订阅者都要跑,否则抑制器的跨块状态会与实际流脱节。
                // 整块被剥光(或被扣下等下一块续判)时无事可记。
                // 抑制器与订阅者的签名都是精确长度的 byte[],故此处才物化 —— 两者都是
                // 冷路径(抑制器只活在连接后的最初几秒,日志/录制默认关闭),稳态不经过。
                if (_tapEchoSuppressor is not null || DataReceived is not null)
                {
                    byte[] logged = SuppressTapEcho(data.AsSpan(0, bytesRead).ToArray());
                    if (logged.Length > 0)
                    {
                        try
                        {
                            DataReceived?.Invoke(logged);
                        }
                        catch
                        {
                            // 日志订阅者异常不允许打断读循环。
                        }
                    }
                }

                // 不要为每次读取都 await 一次 UI 跳转。把分块入队并合并;读线程
                // 跟得上网络节奏,而 UI 以帧率排空。
                // ZMODEM 路由优先:会话期间返回空终端字节(全部转交引擎),
                // 命中时仅把引导前的字节嗂终端;未启用时原样嗂入。
                FileTransfer.TerminalTransferRouter? router = TransferRouter;
                if (router is null || router.CanPassThrough(data.AsSpan(0, bytesRead)))
                {
                    // 常态直通:无 ZMODEM 引导迹象时原始块零拷贝进合批队列(所有权移交队列)。
                    EnqueueForFeed(new(data, bytesRead, Pooled: true));
                }
                else
                {
                    FileTransfer.TransferRouteResult route = router.ProcessIncoming(data.AsMemory(0, bytesRead));

                    // 路由产出的是它自己的数组,不属于池;本块的池数组到此为止,当场归还。
                    ArrayPool<byte>.Shared.Return(data);
                    if (route.TerminalBytes.Length > 0)
                    {
                        EnqueueForFeed(new(route.TerminalBytes, route.TerminalBytes.Length, Pooled: false));
                    }
                }

                // 入队之后再看积压:UI 排不过来就在这里等一等,让 SSH 流控把压力回传给远端。
                // 放在循环末尾而不是开头,是为了让本轮读到的数据先落进队列 —— 否则
                // 高水位时读到的那一块会在等待期间一直占着读缓冲。
                await ApplyBackpressureAsync(token).ConfigureAwait(false);
            }

            // 当流报告自身不再可读时,循环也会退出。
            if (!token.IsCancellationRequested)
            {
                remoteClosed = true;
                closeReason = _shellStream.CloseReason;
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭过程中预期会出现,不算错误
        }
        catch (ObjectDisposedException)
        {
            // 关闭过程中流已被释放,不算错误
        }
        catch (Exception ex)
        {
            // 抛到这里的都不是「远端 shell 正常退出」—— 那条路是干净的 EOF,不经过 catch。
            remoteClosed = true;
            closeReason = ShellCloseReason.ConnectionLost;
            Error?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 表示远端主动关闭,但不包括我们自身 Dispose() 驱动的拆除。
        if (remoteClosed && !_disposed)
        {
            Closed?.Invoke(closeReason);
        }
    }

    /// <summary>
    /// 旁路记录路径的回显抑制(读线程独占该实例)。未装配或已失效时原样返回,零开销。
    /// </summary>
    private byte[] SuppressTapEcho(byte[] data)
    {
        if (_tapEchoSuppressor is not { } suppressor)
        {
            return data;
        }
        byte[] result = suppressor.Process(data);
        if (suppressor.Expired)
        {
            result = AppendHeldTail(result, suppressor);
            _tapEchoSuppressor = null;
        }
        return result;
    }

    /// <summary>
    /// 弃用抑制器前把它扣住的块尾交还输出流。扣住的字节本该在下一次 Process 里放出来,
    /// 实例一弃用就没有下一次了——不接回来就是永久吞字节(与 #291 的 ZMODEM 扣留同源)。
    /// 扣住的必然是本块的尾巴,故追加在后面。
    /// </summary>
    private static byte[] AppendHeldTail(byte[] head, EchoSuppressor suppressor)
    {
        byte[] tail = suppressor.TakeHeld();
        if (tail.Length == 0)
        {
            return head;
        }
        byte[] merged = new byte[head.Length + tail.Length];
        head.CopyTo(merged, 0);
        tail.CopyTo(merged, head.Length);
        return merged;
    }

    private void EnqueueForFeed(PendingChunk chunk)
    {
        lock (_pendingLock)
        {
            _pending.Add(chunk);
        }
        Interlocked.Add(ref _pendingBytes, chunk.Length);

        // 最多只调度一次待处理的 UI 刷新;后续分块搭它的便车。
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(FlushPending);
        }
    }

    /// <summary>
    /// 积压过高时把读线程按住,等 UI 把它排到低水位以下再继续。
    /// </summary>
    /// <remarks>
    /// 读线程一停,SSH 接收窗口不再推进,压力顺着流控回传到远端 —— 远端的 `cat` 自己会慢下来。
    /// 这是 OpenSSH 客户端的行为,也是"内存有上限"的唯一可靠办法:
    /// 只在本地丢弃或无限攒着,都是把问题留给用户。
    /// </remarks>
    private async Task ApplyBackpressureAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Read(ref _pendingBytes) <= HighWaterBytes)
        {
            return;
        }
        try
        {
            await _drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose 与本次等待撞上了:读循环随即会因取消而退出。
        }
    }

    // 多 chunk 合批的复用缓冲:只增不缩,仅在 UI 线程的 FlushPending 内访问,
    // Feed 同步消费不留引用,因此跨帧复用安全。
    private byte[] _combineBuffer = [];

    // ---- 洪流控制:每帧解析预算 + 读线程背压 ----
    //
    // 合批本身是对的(它把上百次跨线程跳转压成每帧一次),但原先没有上限:
    // `cat` 一个几百 MB 的文件、或 `tail -f` 一个刷得很猛的日志,两帧之间能攒下几十 MB,
    // UI 线程在**一个** Dispatcher 回调里把它们全解析完 —— 期间界面冻结、滚动条不响应、
    // 别的标签也不刷新;内存则随读取速度无限增长(每块租自 ArrayPool,但池只是延迟归还,
    // 不限制总量)。
    //
    // 两道闸:
    //   ① 每帧最多解析 FeedBudgetBytes,剩下的以 Background 优先级续帧 —— 界面始终可交互;
    //   ② 积压超过 HighWaterBytes 时读线程等在 _drainGate 上,降到 LowWaterBytes 再放行。
    //      读线程一停,SSH 的接收窗口就不再推进,压力顺着流控自然回传到远端 ——
    //      远端的 `cat` 会自己慢下来。这正是 OpenSSH 客户端的行为。

    /// <summary>每帧最多交给模拟器解析的字节数。</summary>
    private const int FeedBudgetBytes = 1 << 20; // 1 MB

    /// <summary>积压高水位:超过它读线程就等。</summary>
    private const long HighWaterBytes = 8L << 20; // 8 MB

    /// <summary>积压低水位:降到它以下才放读线程继续。</summary>
    private const long LowWaterBytes = 2L << 20; // 2 MB

    /// <summary>当前 <see cref="_pending" /> 里积压的字节数。</summary>
    private long _pendingBytes;

    /// <summary>
    /// 读线程的等待闸。初值 0 = 关着;<see cref="ReleaseDrainGate" /> 放一次行。
    /// </summary>
    /// <remarks>
    /// 上限 1:重复放行不该攒出配额,否则读线程能连着冲过好几轮高水位。
    /// <para>
    /// <b>积压的实际上界是 <see cref="HighWaterBytes" /> + 两块。</b>一块来自"越界的那一次入队"
    /// (高水位是入队之后才判的);另一块来自一张陈旧许可 —— 积压跌到低水位时读线程可能
    /// 并没有在等,那次 <c>Release</c> 就留在信号量里,下一次 <c>WaitAsync</c> 会立刻拿到它、
    /// 不真的等,于是多放过一块。再下一轮就会真等住。以 16 KB 的读块算,超出上限约 32 KB,
    /// 不值得为它引入"有没有人在等"的额外记账。
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _drainGate = new(0, 1);

    /// <summary>当前积压字节数(背压回归用例读它)。</summary>
    internal long PendingBytesForTest => Interlocked.Read(ref _pendingBytes);

    /// <summary>最近一次 Feed 交出去的字节数(预算回归用例读它)。</summary>
    internal int LastFeedBytesForTest { get; private set; }

    /// <summary>放行等在闸上的读线程;没人等就是空操作。</summary>
    private void ReleaseDrainGate()
    {
        // CurrentCount 已经是 1 时再 Release 会抛 SemaphoreFullException。
        if (_drainGate.CurrentCount == 0)
        {
            try
            {
                _drainGate.Release();
            }
            catch (SemaphoreFullException)
            {
                // 与另一个放行者撞上了:闸已经开着,正是想要的结果。
            }
            catch (ObjectDisposedException)
            {
                // 已 Dispose:读循环也已经在退了。
            }
        }
    }

    private void FlushPending()
    {
        // 先重置,使排空期间到达的分块能调度一次全新刷新。
        Interlocked.Exchange(ref _flushScheduled, 0);

        bool more;
        int taken = 0;
        // 只在锁内摘取,拼接/喂入/归还都在锁外做 —— 读线程不会被 UI 的这段活儿挡住。
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            // 每帧只摘 FeedBudgetBytes,剩下的留到下一帧 —— 见 FeedBudgetBytes 的说明。
            // 「至少摘一块」是必须的:单块本身就超预算时若一块不摘,这里会空转成死循环。
            int index = 0;
            while (index < _pending.Count
                   && (taken == 0 || taken + _pending[index].Length <= FeedBudgetBytes))
            {
                taken += _pending[index].Length;
                _draining.Add(_pending[index]);
                index++;
            }
            _pending.RemoveRange(0, index);
            more = _pending.Count > 0;
        }
        long pendingNow = Interlocked.Add(ref _pendingBytes, -taken);
        // 降到低水位以下就放读线程继续跑(见 _drainGate)。
        if (pendingNow <= LowWaterBytes)
        {
            ReleaseDrainGate();
        }
        if (more)
        {
            // 续帧用 Background 而不是默认的 Normal:Avalonia 的 Render 优先级高于
            // Background、低于 Normal。用 Normal 续帧会把渲染饿死 —— 界面照样冻住,
            // 分片就等于白做。用 Background 则是"渲染完这一帧,再解析下一批"。
            Interlocked.Exchange(ref _flushScheduled, 1);
            Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background);
        }
        try
        {
            if (_disposed)
            {
                return;
            }
            byte[] buffer;
            int length;
            if (_draining.Count == 1)
            {
                (buffer, length, _) = _draining[0];
            }
            else
            {
                int total = 0;
                for (int i = 0; i < _draining.Count; i++)
                {
                    total += _draining[i].Length;
                }
                if (_combineBuffer.Length < total)
                {
                    // 2 倍步进摊平增长成本,避免突发行情下反复重分配。
                    _combineBuffer = new byte[Math.Max(total, _combineBuffer.Length * 2)];
                }
                int offset = 0;
                for (int i = 0; i < _draining.Count; i++)
                {
                    PendingChunk chunk = _draining[i];
                    Array.Copy(chunk.Buffer, 0, _combineBuffer, offset, chunk.Length);
                    offset += chunk.Length;
                }
                buffer = _combineBuffer;
                length = total;
            }
            if (_echoSuppressor is { } suppressor)
            {
                // 抑制窗只覆盖连接后的最初几秒:此路径物化精确数组无妨,稳态热路径不经过。
                byte[] exact = buffer.AsSpan(0, length).ToArray();
                exact = suppressor.Process(exact);
                if (suppressor.Expired)
                {
                    exact = AppendHeldTail(exact, suppressor);
                    _echoSuppressor = null;
                }
                if (exact.Length == 0)
                {
                    return;
                }
                buffer = exact;
                length = exact.Length;
            }
            try
            {
                // 每次刷新只 Feed 一次 => 一次 Updated => 一次重绘,与分块数量无关。
                LastFeedBytesForTest = length;
                FeedTerminal(buffer, length);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
        finally
        {
            // 归还必须发生在 Feed 之后(它同步消费,不留引用),且每条退出路径都要走到 ——
            // 提前 return(已 Dispose、抑制器把整块吃光)同样得还,否则就是池泄漏。
            for (int i = 0; i < _draining.Count; i++)
            {
                PendingChunk chunk = _draining[i];
                if (chunk.Pooled)
                {
                    ArrayPool<byte>.Shared.Return(chunk.Buffer);
                }
            }
            _draining.Clear();
        }
    }

    /// <summary>
    /// 把合批结果喂给模拟器:生产中的具体实现(VelaTerminalControl)走 span 直喂
    /// (复用缓冲零物化);其它 ITerminalEmulator 实现(测试替身)回退 byte[] 语义——
    /// 接口不宜引入 span 成员,ref struct 参数无法被常规 mock 框架替身化。
    /// </summary>
    private void FeedTerminal(byte[] buffer, int length)
    {
        if (_terminal is Rendering.VelaTerminalControl control)
        {
            control.Feed(buffer.AsSpan(0, length));
        }
        else
        {
            _terminal.Feed(length == buffer.Length ? buffer : buffer[..length]);
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
        {
            return;
        }

        // ZMODEM 会话期间击键不得混进协议流:字节会被对端当帧内容解析,轻则 CRC 错重传,
        // 重则整笔传输失败。只识别用户的中止意图 —— Ctrl+X(CAN,ZMODEM 规范取消键)与
        // Ctrl+C(用户本能)都转成会话取消,由引擎发出规范的取消序列;其余击键丢弃。
        if (TransferRouter is { IsInSession: true } router)
        {
            if (Array.IndexOf(data, (byte)0x18) >= 0 || Array.IndexOf(data, (byte)0x03) >= 0)
            {
                router.CancelActiveSession();
            }
            return;
        }

        // 只入队不直写:击键与 SendRaw 都在 UI 线程触发,TryWrite 保序;真正的发送
        // 由唯一的写循环按序完成,杜绝对底层通道的并发 WriteAsync(见 _writeQueue 注释)。
        _writeQueue.Writer.TryWrite(new(data, null));
    }

    /// <summary>
    /// 唯一的出站写者:按入队顺序逐段写入并等待完成,一段未落盘绝不开始下一段。
    /// 上一段写入挂起期间(发送窗口收紧、网络延迟)攒下的后续段合并为一次写出——
    /// 语义上等价于按序逐段发送,只是少切几个 SSH 包。
    /// </summary>
    private async Task WriteLoopAsync()
    {
        while (await _writeQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_writeQueue.Reader.TryRead(out OutboundItem item))
            {
                byte[] payload = item.Data;
                List<TaskCompletionSource>? drains = null;
                CollectDrain(ref drains, item);

                // 排空本轮已积压的元素。只有攒到第二段非空载荷才物化合并缓冲:
                // 常态(单段 + 至多一个哨兵)保持零拷贝直传原数组。
                MemoryStream? merged = null;
                while (_writeQueue.Reader.TryRead(out OutboundItem more))
                {
                    CollectDrain(ref drains, more);
                    if (more.Data.Length == 0)
                    {
                        continue; // 哨兵:只收信号,不参与载荷。
                    }
                    if (payload.Length == 0)
                    {
                        payload = more.Data;
                        continue;
                    }
                    if (merged is null)
                    {
                        merged = new MemoryStream(payload.Length + more.Data.Length + 64);
                        merged.Write(payload);
                    }
                    merged.Write(more.Data);
                }
                if (merged is not null)
                {
                    payload = merged.ToArray();
                }
                if (payload.Length > 0 && !_disposed && _shellStream.CanWrite)
                {
                    try
                    {
                        await _shellStream.WriteAsync(payload, 0, payload.Length, CancellationToken.None).ConfigureAwait(false);
                        _shellStream.Flush();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 流已释放——拆除期间属正常情况
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(ex);
                    }
                }
                // 写成功、写失败、拆除弃段——排空信号一律置位:探针等的是"处理完",不是"送达"。
                drains?.ForEach(d => d.TrySetResult());
            }
        }
    }

    private static void CollectDrain(ref List<TaskCompletionSource>? drains, in OutboundItem item)
    {
        if (item.Drained is { } tcs)
        {
            (drains ??= []).Add(tcs);
        }
    }
}

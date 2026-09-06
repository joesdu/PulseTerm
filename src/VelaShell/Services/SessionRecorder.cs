using System.Diagnostics;
using System.Threading.Channels;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>
/// 单个会话的录制器(设置 → 安全审计 → 会话录制):订阅桥的原始输出,
/// 按 600ms/64KB 缓冲后成块写入 SonnetDB 时序存储,块偏移即回放时间轴。
/// </summary>
/// <remarks>
/// <para>
/// <b>持久化走一条有界队列 + 单消费者</b>,不是"每次刷盘起一个不等待的任务"。
/// 旧写法有两个问题:一是待写数据没有上限 —— 存储一慢,每 600ms 就多攒一份 payload 在
/// 内存里,而录制本身是"后台悄悄跑"的功能,涨到几百 MB 也没人看得见;二是收尾那次
/// <c>SaveRecordingAsync</c> 同样不等待,应用退出时它和数据库释放赛跑,输的那次录制
/// 就只剩没有时长、没有结束时间的半条元数据。
/// </para>
/// <para>
/// <b>失败必须看得见。</b>以前任何异常都只是把 <c>_failed</c> 置上就没下文了 ——
/// 用户以为整场生产操作都录着,事后去回放才发现只有开头几秒。现在改为把停止原因
/// 报给宿主(消息中心),已经写进去的部分仍然可以回放。
/// </para>
/// </remarks>
public sealed class SessionRecorder : IAsyncDisposable, IDisposable
{
    private const int FlushIntervalMs = 600;
    private const int FlushThresholdBytes = 64 * 1024;

    /// <summary>待写队列的字节上限。超过就停止录制并报告原因,而不是无声地继续涨。</summary>
    private const long MaxQueuedBytes = 32L * 1024 * 1024;

    /// <summary>待写队列的条目上限(与字节上限任一触顶即停)。</summary>
    private const int MaxQueuedItems = 512;

    /// <summary>收尾时最多等这么久把队列排空。</summary>
    private static readonly TimeSpan FinalDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly SessionRecording _meta;
    private readonly ISessionRecordingStore _store;
    private readonly Action<string>? _onStopped;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _flushTimer;

    /// <summary>待持久化的写入。单消费者读取,因此块的先后顺序天然保持。</summary>
    private readonly Channel<PendingWrite> _writes = Channel.CreateBounded<PendingWrite>(
        new BoundedChannelOptions(MaxQueuedItems)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Task _drain;

    /// <summary>
    /// 攒批缓冲。<b>跨刷盘复用同一个实例</b>(<see cref="Flush" /> 里 <c>SetLength(0)</c> 而非
    /// 换新):MemoryStream 靠倍增扩容,从 0 长到 64KB 阈值要经过约 9 个中间数组(合计 ~128KB),
    /// 每次刷盘都重来一遍就是每 600ms 白扔一轮。复用后容量稳定在阈值附近,不再扩容。
    /// </summary>
    private readonly MemoryStream _buffer = new(FlushThresholdBytes);
    private long _bufferStartOffsetMs;
    private long _lastFlushedOffsetMs = -1;
    private long _queuedBytes;
    private bool _disposed;
    private bool _failed;

    /// <summary>创建会话录制器,立即持久化录制元数据并启动周期性刷盘定时器。</summary>
    /// <param name="store">承载录制元数据与数据块的时序存储。</param>
    /// <param name="sessionLabel">用于在录制列表中标识该会话的显示名称。</param>
    /// <param name="columns">录制开始时终端的列数;≤ 0 时用默认值。导出 asciicast 的头部要它。</param>
    /// <param name="rows">录制开始时终端的行数;≤ 0 时用默认值。</param>
    /// <param name="onStopped">
    /// 录制因故停止时的回调,参数是可直接展示给用户的原因。宿主据此告知用户 ——
    /// 录制悄悄停掉而用户一无所知,是这个功能最糟的失败方式。
    /// </param>
    public SessionRecorder(
        ISessionRecordingStore store,
        string sessionLabel,
        int columns = SessionRecording.DefaultColumns,
        int rows = SessionRecording.DefaultRows,
        Action<string>? onStopped = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onStopped = onStopped;
        _meta = new()
        {
            SessionLabel = sessionLabel,
            Columns = columns > 0 ? columns : SessionRecording.DefaultColumns,
            Rows = rows > 0 ? rows : SessionRecording.DefaultRows
        };
        _drain = Task.Run(DrainAsync);
        Enqueue(new PendingWrite(0, null)); // 先落一份元数据,列表里立刻能看到"正在录制"。
        _flushTimer = new(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    /// <summary>录制是否已因故停止(回归用例读它)。</summary>
    internal bool IsStoppedForTest
    {
        get
        {
            lock (_gate)
            {
                return _failed;
            }
        }
    }

    /// <summary>当前排队待写的字节数(回归用例读它:必须有上限)。</summary>
    internal long QueuedBytesForTest => Interlocked.Read(ref _queuedBytes);

    /// <summary>由桥的 DataReceived(读线程)调用;仅入缓冲,不做 I/O。</summary>
    public void Write(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            if (_disposed || _failed)
            {
                return;
            }
            if (_buffer.Length == 0)
            {
                _bufferStartOffsetMs = _clock.ElapsedMilliseconds;
            }
            _buffer.Write(data, 0, data.Length);
            if (_buffer.Length < FlushThresholdBytes)
            {
                return;
            }
        }
        Flush();
    }

    /// <summary>
    /// 停止录制并<b>等待</b>收尾落盘:刷出残留缓冲、补全时长与结束时间。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        Flush();

        // 收尾:补全元数据(时长/结束时间),让列表能显示完整条目。
        _meta.EndedAtUtc = DateTime.UtcNow;
        _meta.DurationMs = _clock.ElapsedMilliseconds;
        Enqueue(new PendingWrite(0, null));
        _writes.Writer.TryComplete();
        try
        {
            await _drain.WaitAsync(FinalDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 说清楚:最后那一小段可能没落盘,而不是让它看起来一切正常。
            Fail(Strings.Get("Recorder_StopTimedOut"));
        }
    }

    /// <summary>
    /// 同步收尾入口(供只能同步释放的调用点)。
    /// </summary>
    /// <remarks>
    /// 在线程池上等,不在调用线程上 <c>GetResult()</c> —— 这个方法会从 UI 线程调用,
    /// 而存储那边的续体没有承诺不回 UI 线程。等不到就按超时处理,不把关闭流程卡死。
    /// </remarks>
    public void Dispose()
    {
        try
        {
            Task.Run(async () => await DisposeAsync().ConfigureAwait(false))
                .Wait(FinalDrainTimeout + TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 收尾失败不该阻止会话关闭。
        }
    }

    private void Flush()
    {
        byte[] payload;
        long offset;
        lock (_gate)
        {
            if (_failed || _buffer.Length == 0)
            {
                return;
            }
            // payload 必须是独立副本:它要交给后台的异步写入,而 _buffer 紧接着就被清空复用。
            payload = _buffer.ToArray();

            // 块的存储时间 = 开始时刻 + 偏移,而同一录制同一毫秒只存得下一个点(后写覆盖先写)。
            // 满 64KB 会立刻触发刷盘,爆发输出下两次刷盘落在同一毫秒完全可能 —— 偏移必须严格
            // 递增,否则前一块被后一块悄悄顶掉,回放时那段输出凭空消失。
            offset = Math.Max(_bufferStartOffsetMs, _lastFlushedOffsetMs + 1);
            _lastFlushedOffsetMs = offset;

            // 清空复用而非换新实例:保住已经长到 64KB 的容量,免掉下一轮的倍增扩容链。
            // Position 也要归零 —— SetLength 只截长度,写指针留在原处会在开头留下一段空洞。
            _buffer.SetLength(0);
            _buffer.Position = 0;
        }
        Enqueue(new PendingWrite(offset, payload));
    }

    /// <summary>
    /// 把一次写入排进队列。队列触顶即停止录制并报告 —— 无声地攒下去才是更糟的选择。
    /// </summary>
    private void Enqueue(PendingWrite write)
    {
        int size = write.Payload?.Length ?? 0;
        if (Interlocked.Add(ref _queuedBytes, size) > MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -size);
            Fail(Strings.Format("Recorder_StorageBacklog", MaxQueuedBytes / (1024 * 1024)));
            return;
        }
        if (_writes.Writer.TryWrite(write))
        {
            return;
        }
        Interlocked.Add(ref _queuedBytes, -size);
        // 通道满(条目数触顶)或已完成:前者同样是"存储跟不上",后者是收尾之后的迟到写入。
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }
        Fail(Strings.Format("Recorder_StorageBacklog", MaxQueuedBytes / (1024 * 1024)));
    }

    /// <summary>单消费者:按入队顺序落盘。任何失败都停录制并报告,不重试、不打扰会话。</summary>
    private async Task DrainAsync()
    {
        await foreach (PendingWrite write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (IsStoppedForTest)
                {
                    continue; // 已经停了:把队列排空释放内存即可,不再往存储里写。
                }
                if (write.Payload is { } payload)
                {
                    await _store.AppendChunkAsync(_meta.Id, _meta.StartedAtUtc, write.OffsetMs, payload)
                        .ConfigureAwait(false);
                    _meta.ByteSize += payload.Length;
                    _meta.ChunkCount++;
                    _meta.DurationMs = Math.Max(_meta.DurationMs, write.OffsetMs);
                }
                await _store.SaveRecordingAsync(_meta).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -(write.Payload?.Length ?? 0));
            }
        }
    }

    /// <summary>标记录制已停止,并把原因报上去(只报一次)。</summary>
    private void Fail(string reason)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }
            _failed = true;
        }
        try
        {
            _onStopped?.Invoke(reason);
        }
        catch (Exception ex)
        {
            // 通知这一步失败不该反过来把录制器搞成不确定状态。
            Trace.WriteLine($"[SessionRecorder] failure callback threw: {ex}");
        }
    }

    /// <summary>一次待持久化的写入;<see cref="Payload" /> 为 null 表示只保存元数据。</summary>
    private readonly record struct PendingWrite(long OffsetMs, byte[]? Payload);
}

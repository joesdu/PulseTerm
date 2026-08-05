using System.Diagnostics;
using VelaShell.Core.Recording;

namespace VelaShell.Services;

/// <summary>
/// 单个会话的录制器(设置 → 安全审计 → 会话录制):订阅桥的原始输出,
/// 按 600ms/64KB 缓冲后成块写入 SonnetDB 时序存储,块偏移即回放时间轴。
/// 写入在后台串行执行,失败即自禁用,绝不影响会话本身。
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private const int FlushIntervalMs = 600;
    private const int FlushThresholdBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly SessionRecording _meta;
    private readonly ISessionRecordingStore _store;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private MemoryStream _buffer = new();
    private long _bufferStartOffsetMs;
    private bool _disposed;
    private bool _failed;

    /// <summary>创建会话录制器,立即持久化录制元数据并启动周期性刷盘定时器。</summary>
    /// <param name="store">承载录制元数据与数据块的时序存储。</param>
    /// <param name="sessionLabel">用于在录制列表中标识该会话的显示名称。</param>
    public SessionRecorder(ISessionRecordingStore store, string sessionLabel)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _meta = new() { SessionLabel = sessionLabel };
        _ = PersistAsync(() => _store.SaveRecordingAsync(_meta));
        _flushTimer = new(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

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

    /// <summary>停止录制:关闭定时器、刷出残留缓冲,并补全时长与结束时间等收尾元数据。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _flushTimer.Dispose();
        Flush();

        // 收尾:补全元数据(时长/结束时间),让列表能显示完整条目。
        _meta.EndedAtUtc = DateTime.UtcNow;
        _meta.DurationMs = _clock.ElapsedMilliseconds;
        _ = PersistAsync(() => _store.SaveRecordingAsync(_meta));
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
            payload = _buffer.ToArray();
            offset = _bufferStartOffsetMs;
            _buffer = new();
        }
        _ = PersistAsync(async () =>
        {
            await _store.AppendChunkAsync(_meta.Id, _meta.StartedAtUtc, offset, payload);
            _meta.ByteSize += payload.Length;
            _meta.ChunkCount++;
            _meta.DurationMs = Math.Max(_meta.DurationMs, offset);
            await _store.SaveRecordingAsync(_meta);
        });
    }

    /// <summary>后台串行持久化;任何失败都让录制器自禁用(不重试、不打扰会话)。</summary>
    private async Task PersistAsync(Func<Task> operation)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_failed)
            {
                return;
            }
            await operation().ConfigureAwait(false);
        }
        catch
        {
            _failed = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

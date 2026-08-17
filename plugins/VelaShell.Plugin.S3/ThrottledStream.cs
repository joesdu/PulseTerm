namespace VelaShell.Plugin.S3;

/// <summary>
/// 带宽限制用的包装流(设置 → 文件传输 → 带宽限制):对读/写按字节速率整形。
/// 上传时包装本地读流(限读),下载时包装本地写流(限写),对 AWSSDK 完全透明。
/// 采用简单的滑动窗口:每消耗满一个配额窗口就 sleep 到窗口结束。
/// <para>
/// 与宿主 <c>VelaShell.Core.Sftp.ThrottledStream</c> 是同一份实现的副本。插件够不到 Core,
/// 而限速这件事**不该因为协议换成插件承载就消失** —— 上限值仍由宿主统一给
/// (<c>IProtocolsApi.GetTransferOptionsAsync</c>),这里只负责按它整形。
/// </para>
/// </summary>
internal sealed class ThrottledStream(Stream inner, long bytesPerSecond) : Stream
{
    private readonly long _bytesPerSecond = Math.Max(1, bytesPerSecond);
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private long _windowBytes;
    private long _windowStartTicks = Environment.TickCount64;

    /// <summary>内部流是否支持读取。</summary>
    public override bool CanRead => _inner.CanRead;

    /// <summary>内部流是否支持定位。</summary>
    public override bool CanSeek => _inner.CanSeek;

    /// <summary>内部流是否支持写入。</summary>
    public override bool CanWrite => _inner.CanWrite;

    /// <summary>内部流的长度(字节)。</summary>
    public override long Length => _inner.Length;

    /// <summary>内部流的当前读写位置。</summary>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>计算本次传输后还需等待多少毫秒;无需等待返回 0,并顺带滚动配额窗口。</summary>
    private int NextDelayMs(int justTransferred)
    {
        _windowBytes += justTransferred;
        if (_windowBytes < _bytesPerSecond)
        {
            return 0;
        }
        long elapsed = Environment.TickCount64 - _windowStartTicks;
        long expectedMs = (_windowBytes * 1000) / _bytesPerSecond;
        int delay = expectedMs > elapsed ? (int)Math.Min(expectedMs - elapsed, 1000) : 0;
        _windowBytes = 0;
        _windowStartTicks = Environment.TickCount64 + delay;
        return delay;
    }

    /// <summary>同步路径只能阻塞等待。</summary>
    private void Throttle(int justTransferred)
    {
        int delay = NextDelayMs(justTransferred);
        if (delay > 0)
        {
            Thread.Sleep(delay);
        }
    }

    /// <summary>
    /// 异步路径必须让出线程:限速本质是"等待",用 <see cref="Thread.Sleep(int)" /> 会把线程池线程
    /// 按限速时长整段占死,并发传输下足以拖垮整个线程池。
    /// </summary>
    private async ValueTask ThrottleAsync(int justTransferred, CancellationToken cancellationToken)
    {
        int delay = NextDelayMs(justTransferred);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>从内部流同步读取,并按带宽上限对读取速率整形。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        Throttle(n);
        return n;
    }

    /// <summary>
    /// 从内部流异步读取,并按带宽上限对读取速率整形。
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        await ThrottleAsync(n, cancellationToken).ConfigureAwait(false);
        return n;
    }

    /// <summary>从内部流异步读取,并按带宽上限对读取速率整形。</summary>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        await ThrottleAsync(n, cancellationToken).ConfigureAwait(false);
        return n;
    }

    /// <summary>向内部流同步写入,并按带宽上限对写入速率整形。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Throttle(count);
    }

    /// <summary>
    /// 向内部流异步写入,并按带宽上限对写入速率整形。
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await ThrottleAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>向内部流异步写入,并按带宽上限对写入速率整形。</summary>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        await ThrottleAsync(count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>刷新内部流的缓冲。</summary>
    public override void Flush() => _inner.Flush();
    /// <summary>异步刷新内部流的缓冲。</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    /// <summary>在内部流中定位读写位置。</summary>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <summary>设置内部流的长度。</summary>
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <summary>释放包装的内部流。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>异步释放包装的内部流。</summary>
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

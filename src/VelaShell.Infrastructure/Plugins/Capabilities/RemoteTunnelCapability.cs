using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteTunnel;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteTunnelApi" /> 的桥接实现:复用宿主既有连接,开一条
/// <c>direct-streamlocal@openssh.com</c> / <c>direct-tcpip</c> 通道,把裸流交给插件。
/// <para>
/// 实例是**按插件**创建的(见 <c>PluginManager.CreateContext</c>),因此
/// <see cref="IRemoteTunnelApi.MaxConcurrentTunnels" /> 这个并发上限天然按插件计。
/// </para>
/// </summary>
internal sealed class RemoteTunnelCapability(ISshConnectionService connections) : IRemoteTunnelApi
{
    private static readonly TimeSpan MaxConnectTimeout = TimeSpan.FromMinutes(2);

    /// <summary>当前该插件已打开、尚未释放的隧道条数。</summary>
    private int _activeTunnels;

    public int ActiveTunnels => Volatile.Read(ref _activeTunnels);

    public Task<Stream> OpenUnixSocketAsync(string sessionId, string socketPath, TunnelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        return OpenAsync(sessionId, options, cancellationToken,
            (client, ct) => client.OpenUnixConnectionAsync(socketPath, ct));
    }

    public Task<Stream> OpenTcpAsync(string sessionId, string host, int port, TunnelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return OpenAsync(sessionId, options, cancellationToken,
            (client, ct) => client.OpenTcpConnectionAsync(host, port, ct));
    }

    private async Task<Stream> OpenAsync(string sessionId, TunnelOptions? options,
        CancellationToken cancellationToken, Func<ISshClientWrapper, CancellationToken, Task<Stream>> open)
    {
        ISshClientWrapper client = Resolve(sessionId);
        // 先占坑再干活:隧道不限时,每条占一个 SSH 通道。一个漏掉 Dispose 的插件
        // 不该有能力把对端的通道数吃干净 —— 那时坏掉的是用户的连接,不只是这个插件。
        if (Interlocked.Increment(ref _activeTunnels) > IRemoteTunnelApi.MaxConcurrentTunnels)
        {
            Interlocked.Decrement(ref _activeTunnels);
            throw new InvalidOperationException(
                $"This plugin already has {IRemoteTunnelApi.MaxConcurrentTunnels} tunnels open; dispose one before opening another.");
        }
        TimeSpan connectTimeout = options?.ConnectTimeout is { } t && t > TimeSpan.Zero && t <= MaxConnectTimeout
            ? t
            : TimeSpan.FromSeconds(15);
        try
        {
            // 超时只夹**建立**阶段:链接源不能传进流的生命周期,否则第一个到点的
            // CancelAfter 会在半小时后无声掐掉一条正在跟随的日志流。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(connectTimeout);
            Stream stream;
            try
            {
                stream = await open(client, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Opening the tunnel timed out after {connectTimeout.TotalSeconds:0}s.");
            }
            return new CountedStream(stream, () => Interlocked.Decrement(ref _activeTunnels));
        }
        catch
        {
            Interlocked.Decrement(ref _activeTunnels);
            throw;
        }
    }

    private ISshClientWrapper Resolve(string sessionId) =>
        Guid.TryParse(sessionId, out Guid id) && connections.GetClient(id) is { IsConnected: true } client
            ? client
            : throw new PluginSessionNotFoundException(sessionId);

    /// <summary>
    /// 把配额的归还钉在流的释放上。配额只有在流真正关掉时才回来,而不是"调用返回时" ——
    /// 后者会让上限形同虚设:插件拿着 100 条活流,计数却是 0。
    /// </summary>
    private sealed class CountedStream(Stream inner, Action onDisposed) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanTimeout => inner.CanTimeout;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int ReadTimeout
        {
            get => inner.ReadTimeout;
            set => inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => inner.WriteTimeout;
            set => inner.WriteTimeout = value;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { inner.Dispose(); }
                finally { onDisposed(); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { onDisposed(); }
            }
            GC.SuppressFinalize(this);
        }
    }
}

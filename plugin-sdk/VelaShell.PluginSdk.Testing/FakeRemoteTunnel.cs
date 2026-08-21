using VelaShell.PluginSdk.RemoteTunnel;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IRemoteTunnelApi" /> 的测试替身:优先走 <see cref="Handler" />,
/// 其次按顺序吐出 <see cref="Responses" /> 队列;都没有时抛
/// <see cref="InvalidOperationException" />。
/// <para>
/// 刻意**不**在没有脚本时返回一条空流:空流对 <c>HttpClient</c> 而言是"对端立刻断开",
/// 测试看到的会是一个和被测逻辑无关的 <c>HttpRequestException</c>,
/// 而不是"你忘了给这条隧道写应答"。
/// </para>
/// </summary>
public sealed class FakeRemoteTunnel : IRemoteTunnelApi
{
    private int _active;

    /// <summary>脚本化应答:(sessionId, endpoint) → 双工流。endpoint 形如 <c>unix:/var/run/docker.sock</c> 或 <c>tcp:host:2375</c>。</summary>
    public Func<string, string, Stream>? Handler { get; set; }

    /// <summary>顺序应答队列(无 <see cref="Handler" /> 时使用)。</summary>
    public Queue<Stream> Responses { get; } = new();

    /// <summary>全部已打开的 (sessionId, endpoint) 记录。</summary>
    public List<(string SessionId, string Endpoint)> Opened { get; } = [];

    /// <inheritdoc />
    public int ActiveTunnels => Volatile.Read(ref _active);

    /// <inheritdoc />
    public Task<Stream> OpenUnixSocketAsync(string sessionId, string socketPath, TunnelOptions? options = null,
        CancellationToken cancellationToken = default) => OpenAsync(sessionId, $"unix:{socketPath}");

    /// <inheritdoc />
    public Task<Stream> OpenTcpAsync(string sessionId, string host, int port, TunnelOptions? options = null,
        CancellationToken cancellationToken = default) => OpenAsync(sessionId, $"tcp:{host}:{port}");

    private Task<Stream> OpenAsync(string sessionId, string endpoint)
    {
        Opened.Add((sessionId, endpoint));
        Stream inner = Handler?.Invoke(sessionId, endpoint)
                       ?? (Responses.Count > 0
                           ? Responses.Dequeue()
                           : throw new InvalidOperationException(
                               $"FakeRemoteTunnel has no scripted response for '{endpoint}'. Set Handler or enqueue Responses."));
        Interlocked.Increment(ref _active);
        return Task.FromResult<Stream>(new TrackedStream(inner, () => Interlocked.Decrement(ref _active)));
    }

    /// <summary>
    /// 造一条"读出固定字节、把写入的字节记下来"的双工流,用于把测试当成一个假 daemon。
    /// </summary>
    /// <param name="responseBytes">被测代码读到的字节(读完即 EOF)。</param>
    public static ScriptedTunnelStream Script(byte[] responseBytes) => new(responseBytes);

    private sealed class TrackedStream(Stream inner, Action onDisposed) : DelegatingStream(inner)
    {
        private int _disposed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { base.Dispose(disposing); }
                finally { onDisposed(); }
                return;
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// 一条脚本化的双工流:读出预置字节,写入的内容累积在 <see cref="Written" />。
/// </summary>
public sealed class ScriptedTunnelStream(byte[] responseBytes) : Stream
{
    private readonly MemoryStream _read = new(responseBytes, writable: false);
    private readonly MemoryStream _written = new();

    /// <summary>被测代码写进这条隧道的全部字节(即"发给 daemon 的请求")。</summary>
    public byte[] Written => _written.ToArray();

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _read.Read(buffer);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _written.Write(buffer);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>把一切转发给内层流的基类(测试替身用)。</summary>
public abstract class DelegatingStream(Stream inner) : Stream
{
    /// <summary>内层流。</summary>
    protected Stream Inner { get; } = inner;

    /// <inheritdoc />
    public override bool CanRead => Inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => Inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => Inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => Inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

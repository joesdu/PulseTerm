using System.Threading.Channels;
using VelaShell.Core.FileTransfer.Abstractions;

namespace VelaShell.Core.Tests.FileTransfer;

/// <summary>
/// 测试用内存双工通道:写入本端即出现在对端的读队列,反之亦然。
/// 用于在无真实传输的情况下把发送方与接收方在进程内对接(ZMODEM / XMODEM / YMODEM 通用),
/// 或喂入预置字节序列。
/// </summary>
public sealed class InMemoryByteDuplex : IByteDuplex
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;

    // 被引擎退回的、其实不属于本次传输的字节(见 Unread);排在 Channel 之前被读出。
    private readonly List<ReadOnlyMemory<byte>> _unread = [];
    private readonly Lock _unreadGate = new();

    private InMemoryByteDuplex(
        Channel<ReadOnlyMemory<byte>> inbound,
        Channel<ReadOnlyMemory<byte>> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>创建一对相互连接的双工端点。</summary>
    public static (InMemoryByteDuplex A, InMemoryByteDuplex B) CreatePair()
    {
        var toA = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var toB = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var a = new InMemoryByteDuplex(toA, toB);
        var b = new InMemoryByteDuplex(toB, toA);
        return (a, b);
    }

    /// <summary>创建一个只读端点,预先灌入固定的入站字节(用于喂协议帧给解析器)。</summary>
    public static InMemoryByteDuplex FromInbound(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        var inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        foreach (ReadOnlyMemory<byte> chunk in chunks)
        {
            inbound.Writer.TryWrite(chunk);
        }
        inbound.Writer.TryComplete();
        var outbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        return new(inbound, outbound);
    }

    /// <summary>把本端已写出的全部出站字节拼接读出(供断言)。</summary>
    public async Task<byte[]> DrainOutboundAsync()
    {
        _outbound.Writer.TryComplete();
        var all = new List<byte>();
        await foreach (ReadOnlyMemory<byte> chunk in _outbound.Reader.ReadAllAsync())
        {
            all.AddRange(chunk.ToArray());
        }
        return [.. all];
    }

    /// <summary>
    /// 入站是否已有排队字节。真实通道(ShellStreamByteDuplex)据此让发送端在流式推数据的
    /// 间隙探测对端插话,测试里必须给出同样的语义,否则那条分支在测试中永远走不到。
    /// </summary>
    public bool HasPendingInbound => _inbound.Reader.Count > 0;

    /// <inheritdoc />
    /// <remarks>
    /// 与真实通道(ShellStreamByteDuplex)一致:退回的字节排在队首、且不受"入站已封口"影响 ——
    /// 收尾阶段的退回本来就发生在通道结束之后,写回 Channel 会被静默丢掉。
    /// </remarks>
    public void Unread(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }
        lock (_unreadGate)
        {
            _unread.Add(data);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_unreadGate)
        {
            if (_unread.Count > 0)
            {
                ReadOnlyMemory<byte> pending = _unread[0];
                _unread.RemoveAt(0);
                return pending;
            }
        }
        try
        {
            if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && _inbound.Reader.TryRead(out ReadOnlyMemory<byte> chunk))
            {
                return chunk;
            }
        }
        catch (ChannelClosedException)
        {
            // 归一化为 EOF。
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _outbound.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

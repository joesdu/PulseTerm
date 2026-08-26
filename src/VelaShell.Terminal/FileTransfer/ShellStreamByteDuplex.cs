using System.Threading.Channels;
using VelaShell.Core.Ssh;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;

namespace VelaShell.Terminal.FileTransfer;

/// <summary>
/// 把 <see cref="IShellStreamWrapper" />(SSH / ConPTY / 未来串口 · Telnet)适配为 ZMODEM 引擎
/// 所需的 <see cref="IByteDuplex" />。入站字节由路由器从桥的读循环经 <see cref="Push" /> 喂入
/// (而非直接读传输,以复用桥已有的单一读循环);出站字节直写传输。
/// </summary>
public sealed class ShellStreamByteDuplex(IShellStreamWrapper shellStream) : IByteDuplex
{
    private readonly IShellStreamWrapper _shellStream =
        shellStream ?? throw new ArgumentNullException(nameof(shellStream));

    private readonly Channel<ReadOnlyMemory<byte>> _inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    // 被引擎退回的、其实不属于本次传输的字节(见 Unread)。会话收尾时与通道里的残余一起交还终端。
    private readonly List<ReadOnlyMemory<byte>> _unread = [];
    private readonly Lock _unreadGate = new();

    /// <inheritdoc />
    public bool HasPendingInbound => _inbound.Reader.Count > 0;

    /// <summary>由路由器喂入一段截获的入站字节(读循环线程调用)。</summary>
    /// <param name="data">属于本次传输会话的入站字节。</param>
    public void Push(ReadOnlyMemory<byte> data)
    {
        if (!data.IsEmpty)
        {
            _inbound.Writer.TryWrite(data);
        }
    }

    /// <inheritdoc />
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

    /// <summary>
    /// 取走会话结束时通道里还没被引擎消费的全部字节(含被 <see cref="Unread" /> 退回的部分)。
    /// 这些字节属于 shell 而不属于协议 —— 路由器把它们交还终端,提示符才不会凭空少一行。
    /// </summary>
    /// <returns>尚未消费的入站字节;没有则为空数组。</returns>
    public byte[] DrainPending()
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        lock (_unreadGate)
        {
            chunks.AddRange(_unread);
            _unread.Clear();
        }
        while (_inbound.Reader.TryRead(out ReadOnlyMemory<byte> chunk))
        {
            chunks.Add(chunk);
        }
        int total = chunks.Sum(c => c.Length);
        if (total == 0)
        {
            return [];
        }
        byte[] merged = new byte[total];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> chunk in chunks)
        {
            chunk.Span.CopyTo(merged.AsSpan(offset));
            offset += chunk.Length;
        }
        return merged;
    }

    /// <summary>标记入站结束(会话终止 / 传输关闭),使引擎的读取得到 EOF。</summary>
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
    {
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

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }
        if (!_shellStream.CanWrite)
        {
            // 静默丢弃出站帧 = 对端永远等不到我们的应答。这条日志能立刻把它揪出来。
            TransferTrace.Log($"TX DROPPED ({data.Length}B): shellStream.CanWrite == false");
            return;
        }
        TransferTrace.LogBytes("TX", data.Span);
        try
        {
            // IShellStreamWrapper 只接受 byte[]+offset+count;若底层是数组段则零拷贝复用。
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> seg)
                && seg.Array is not null)
            {
                await _shellStream.WriteAsync(seg.Array, seg.Offset, seg.Count, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte[] copy = data.ToArray();
                await _shellStream.WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            TransferTrace.Log($"TX FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        _shellStream.Flush();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

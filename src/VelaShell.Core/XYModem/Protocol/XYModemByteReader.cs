using VelaShell.Core.FileTransfer.Abstractions;

namespace VelaShell.Core.XYModem.Protocol;

/// <summary>
/// XMODEM / YMODEM 的增量字节读取器。这一族协议不做转义,读取只有两种需求:
/// 「读一个控制字节」和「读满 N 个字节」,因此比 ZMODEM 的帧读取器简单得多。
/// 内部按块缓冲,对「一个数据块被切分到多个网络分片」天然免疫。
/// </summary>
/// <param name="duplex">底层双工通道。</param>
public sealed class XYModemByteReader(IByteDuplex duplex)
{
    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private byte[] _buffer = [];
    private int _pos;
    private bool _eof;

    /// <summary>把一段初始字节(如握手阶段截获的字节)预置到缓冲区最前。</summary>
    /// <param name="seed">要预置的字节。</param>
    public void Seed(ReadOnlySpan<byte> seed)
    {
        if (seed.IsEmpty)
        {
            return;
        }
        int remaining = _buffer.Length - _pos;
        byte[] merged = new byte[remaining + seed.Length];
        seed.CopyTo(merged);
        _buffer.AsSpan(_pos, remaining).CopyTo(merged.AsSpan(seed.Length));
        _buffer = merged;
        _pos = 0;
    }

    /// <summary>
    /// 取走并清空当前缓冲里尚未消费的字节。会话收尾时由引擎调用,把「跟在协议块后面、
    /// 其实属于 shell 的字节」(对端 sb/rb 退出后紧跟的提示符)退还出去。
    /// </summary>
    /// <returns>尚未消费的缓冲字节;没有则为空。</returns>
    public ReadOnlyMemory<byte> DrainBuffered()
    {
        if (_pos >= _buffer.Length)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        byte[] rest = _buffer.AsSpan(_pos).ToArray();
        _pos = _buffer.Length;
        return rest;
    }

    /// <summary>读取下一个字节;缓冲耗尽时从通道补充。通道结束(EOF)返回 <c>-1</c>。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>字节值,或 <c>-1</c> 表示 EOF。</returns>
    public async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        while (_pos >= _buffer.Length)
        {
            if (_eof)
            {
                return -1;
            }
            ReadOnlyMemory<byte> chunk = await _duplex.ReadAsync(ct).ConfigureAwait(false);
            if (chunk.IsEmpty)
            {
                _eof = true;
                return -1;
            }
            _buffer = chunk.ToArray();
            _pos = 0;
        }
        return _buffer[_pos++];
    }

    /// <summary>读满 <paramref name="destination" />;中途 EOF 返回 <c>false</c>。</summary>
    /// <param name="destination">接收字节的缓冲。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读满返回 <c>true</c>;通道提前结束返回 <c>false</c>。</returns>
    public async ValueTask<bool> ReadExactAsync(Memory<byte> destination, CancellationToken ct)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            // 先把已缓冲的部分整段搬走(常态:一个网络分片里就有整块),搬不动了再去拉新分片。
            if (_pos < _buffer.Length)
            {
                int take = Math.Min(destination.Length - filled, _buffer.Length - _pos);
                _buffer.AsSpan(_pos, take).CopyTo(destination.Span[filled..]);
                _pos += take;
                filled += take;
                continue;
            }
            if (_eof)
            {
                return false;
            }
            ReadOnlyMemory<byte> chunk = await _duplex.ReadAsync(ct).ConfigureAwait(false);
            if (chunk.IsEmpty)
            {
                _eof = true;
                return false;
            }
            _buffer = chunk.ToArray();
            _pos = 0;
        }
        return true;
    }
}

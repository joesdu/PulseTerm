using System.Buffers;
using VelaShell.Core.FileTransfer.Protocol;

namespace VelaShell.Core.ZModem.Protocol;

/// <summary>数据子包结束后对端应采取的动作(由帧结束符 ZCRCE/G/Q/W 决定)。</summary>
public enum ZModemSubpacketEnd
{
    /// <summary>ZCRCE:此帧结束,发送方不再续发,无需应答。</summary>
    EndNoAck,

    /// <summary>ZCRCG:帧继续(还有后续子包),无需应答。</summary>
    MoreNoAck,

    /// <summary>ZCRCQ:帧继续,需要接收方回 ZACK。</summary>
    MoreAck,

    /// <summary>ZCRCW:此帧结束,需要接收方回 ZACK。</summary>
    EndAck
}

/// <summary>数据子包读取结果的分类。</summary>
public enum ZModemSubpacketStatus
{
    /// <summary>成功读到一个校验通过的数据子包。</summary>
    Ok,

    /// <summary>子包 CRC 校验失败。</summary>
    CrcError,

    /// <summary>读取途中检测到取消序列。</summary>
    Cancelled,

    /// <summary>底层通道结束(EOF)。</summary>
    EndOfStream,

    /// <summary>等待子包字节期间超时。</summary>
    Timeout
}

/// <summary>一次数据子包读取的结果。</summary>
/// <param name="Status">读取状态。</param>
/// <param name="Data">子包负载(已反转义),仅在 <see cref="Status" /> 为 <see cref="ZModemSubpacketStatus.Ok" /> 时有效。</param>
/// <param name="End">帧结束语义,决定是否续读子包 / 是否需要应答。</param>
public readonly record struct ZModemSubpacketResult(
    ZModemSubpacketStatus Status,
    byte[] Data,
    ZModemSubpacketEnd End = ZModemSubpacketEnd.EndNoAck);

/// <summary>
/// ZMODEM 数据子包的编解码。子包格式为:[转义后的数据字节…] ZDLE 帧结束符(ZCRCE/G/Q/W) CRC。
/// CRC 覆盖「原始数据字节 + 帧结束符字节」,随后 CRC 字节本身也参与 ZDLE 转义。
/// CRC16 走 <see cref="Crc16Xmodem" />(大端上链),CRC32 走 <see cref="Crc32ZModem" />(小端上链)。
/// </summary>
public static class ZModemSubpacket
{
    /// <summary>读取端一次批量搬运的原始字节上限(仅为减少 memcpy 次数,与协议无关)。</summary>
    private const int DrainChunk = 4096;

    private static byte FrameEndByte(ZModemSubpacketEnd end) =>
        end switch
        {
            ZModemSubpacketEnd.EndNoAck => ZModemConstants.ZCRCE,
            ZModemSubpacketEnd.MoreNoAck => ZModemConstants.ZCRCG,
            ZModemSubpacketEnd.MoreAck => ZModemConstants.ZCRCQ,
            ZModemSubpacketEnd.EndAck => ZModemConstants.ZCRCW,
            _ => throw new ArgumentOutOfRangeException(nameof(end))
        };

    private static ZModemSubpacketEnd EndFromToken(ZdleTokenKind kind) =>
        kind switch
        {
            ZdleTokenKind.SubpacketEndNoAck => ZModemSubpacketEnd.EndNoAck,
            ZdleTokenKind.SubpacketMoreNoAck => ZModemSubpacketEnd.MoreNoAck,
            ZdleTokenKind.SubpacketMoreAck => ZModemSubpacketEnd.MoreAck,
            ZdleTokenKind.SubpacketEndAck => ZModemSubpacketEnd.EndAck,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    /// <summary>
    /// 编码 <paramref name="dataLength" /> 字节负载所需的最坏情况缓冲长度:
    /// 每个数据字节最多膨胀成 2 字节转义序列,再加 ZDLE + 结束符 + 最多 8 字节转义后的 CRC32。
    /// </summary>
    /// <param name="dataLength">负载字节数。</param>
    /// <returns>足以容纳编码结果的缓冲长度。</returns>
    public static int MaxEncodedLength(int dataLength) => (dataLength * 2) + 16;

    /// <summary>
    /// 把一段数据编码为链路上的数据子包(含 ZDLE 转义与 CRC),写入调用方提供的缓冲。
    /// 发送端的热路径走这条:缓冲可由 <see cref="ArrayPool{T}" /> 复用,整条链路零堆分配 ——
    /// 旧实现每个子包新建一个 <c>List&lt;byte&gt;</c> 再 <c>ToArray</c>,100MB 文件就是十几万次分配。
    /// </summary>
    /// <param name="data">子包负载(原始未转义字节)。</param>
    /// <param name="end">帧结束语义(决定帧结束符)。</param>
    /// <param name="useCrc32">true 用 CRC32,false 用 CRC16。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    /// <param name="destination">接收编码结果的缓冲,长度须不小于 <see cref="MaxEncodedLength" />。</param>
    /// <returns>实际写入的字节数。</returns>
    public static int Write(
        ReadOnlySpan<byte> data,
        ZModemSubpacketEnd end,
        bool useCrc32,
        bool escapeAllControl,
        Span<byte> destination)
    {
        if (destination.Length < MaxEncodedLength(data.Length))
        {
            throw new ArgumentException("目标缓冲不足以容纳编码后的子包。", nameof(destination));
        }

        byte frameEnd = FrameEndByte(end);
        int written = 0;

        // 1) 转义后的负载。
        foreach (byte b in data)
        {
            written += ZdleCodec.EscapeByte(b, destination[written..], escapeAllControl);
        }

        // 2) ZDLE + 帧结束符(不转义;帧结束符本身即为转义序列的一部分)。
        destination[written++] = ZModemConstants.ZDLE;
        destination[written++] = frameEnd;

        // 3) CRC 覆盖 (原始数据 + 帧结束符字节),CRC 字节再做 ZDLE 转义。
        if (useCrc32)
        {
            uint running = Crc32ZModem.Initial;
            running = Crc32ZModem.UpdateRunning(running, data);
            running = Crc32ZModem.UpdateRunning(running, frameEnd);
            uint crc = running ^ 0xFFFFFFFF;
            written += ZdleCodec.EscapeByte((byte)(crc & 0xFF), destination[written..], escapeAllControl);
            written += ZdleCodec.EscapeByte((byte)((crc >> 8) & 0xFF), destination[written..], escapeAllControl);
            written += ZdleCodec.EscapeByte((byte)((crc >> 16) & 0xFF), destination[written..], escapeAllControl);
            written += ZdleCodec.EscapeByte((byte)((crc >> 24) & 0xFF), destination[written..], escapeAllControl);
        }
        else
        {
            ushort crc = 0;
            crc = Crc16Xmodem.Update(crc, data);
            crc = Crc16Xmodem.Update(crc, frameEnd);
            written += ZdleCodec.EscapeByte((byte)(crc >> 8), destination[written..], escapeAllControl);
            written += ZdleCodec.EscapeByte((byte)(crc & 0xFF), destination[written..], escapeAllControl);
        }
        return written;
    }

    /// <summary>把一段数据编码为链路上的数据子包(含 ZDLE 转义与 CRC),返回新数组。</summary>
    /// <param name="data">子包负载(原始未转义字节)。</param>
    /// <param name="end">帧结束语义(决定帧结束符)。</param>
    /// <param name="useCrc32">true 用 CRC32,false 用 CRC16。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    /// <returns>可直接写入传输的子包字节。</returns>
    public static byte[] Write(
        ReadOnlySpan<byte> data,
        ZModemSubpacketEnd end,
        bool useCrc32,
        bool escapeAllControl = false)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxEncodedLength(data.Length));
        try
        {
            int written = Write(data, end, useCrc32, escapeAllControl, buffer);
            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 从帧读取器增量读取一个数据子包:先尽量批量搬走「不含 ZDLE 的连续段」,
    /// 只在真正遇到 ZDLE 时才退回逐字节反转义,直到遇到帧结束符,再读入并校验 CRC。
    /// CRC 边收边算(单遍),不再先攒完整包再重扫一遍。
    /// </summary>
    /// <param name="reader">已定位在数据子包起点的帧读取器。</param>
    /// <param name="useCrc32">当前帧是否使用 CRC32(由 ZDATA 帧头形态决定)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>子包读取结果。</returns>
    public static async ValueTask<ZModemSubpacketResult> ReadAsync(
        ZModemFrameReader reader,
        bool useCrc32,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte[] scratch = ArrayPool<byte>.Shared.Rent(DrainChunk);
        var accumulator = new ArrayBufferWriter<byte>(DrainChunk);
        ushort crc16 = 0;
        uint crc32 = Crc32ZModem.Initial;
        ZModemSubpacketEnd end;

        // 子包 CRC 覆盖「原始数据 + 帧结束符字节」,故必须捕获终止符原始字节并并入 CRC ——
        // 传 0 会让读侧算出 CRC(data+0) 而写侧是 CRC(data+frameEnd),每个子包都判 CRC 错、
        // 触发无休止重传(表现为文件传输永久卡死)。
        byte frameEnd;
        try
        {
            // 步骤 1:累积负载,直到遇到子包终止符。
            while (true)
            {
                // 快路径:把已缓冲、且不含 ZDLE 的连续段整段搬走并一次性喂 CRC。
                int drained = reader.DrainPlainRun(scratch);
                if (drained > 0)
                {
                    ReadOnlySpan<byte> run = scratch.AsSpan(0, drained);
                    accumulator.Write(run);
                    if (useCrc32)
                    {
                        crc32 = Crc32ZModem.UpdateRunning(crc32, run);
                    }
                    else
                    {
                        crc16 = Crc16Xmodem.Update(crc16, run);
                    }
                    continue;
                }

                (ZdleToken token, bool eof) = await reader.ReadEscapedByteAsync(ct).ConfigureAwait(false);
                if (eof)
                {
                    return new(ZModemSubpacketStatus.EndOfStream, []);
                }
                switch (token.Kind)
                {
                    case ZdleTokenKind.DataByte:
                    case ZdleTokenKind.Rub0:
                    case ZdleTokenKind.Rub1:
                        // ZRUB0/1 在数据子包语境下作为普通数据字节处理。
                        accumulator.GetSpan(1)[0] = token.Value;
                        accumulator.Advance(1);
                        if (useCrc32)
                        {
                            crc32 = Crc32ZModem.UpdateRunning(crc32, token.Value);
                        }
                        else
                        {
                            crc16 = Crc16Xmodem.Update(crc16, token.Value);
                        }
                        continue;
                    case ZdleTokenKind.SubpacketEndNoAck:
                    case ZdleTokenKind.SubpacketMoreNoAck:
                    case ZdleTokenKind.SubpacketMoreAck:
                    case ZdleTokenKind.SubpacketEndAck:
                        frameEnd = token.Value; // 终止符字节本身参与 CRC。
                        end = EndFromToken(token.Kind);
                        break;
                    case ZdleTokenKind.Cancel:
                        return new(ZModemSubpacketStatus.Cancelled, []);
                    default:
                        return new(ZModemSubpacketStatus.CrcError, []);
                }
                break;
            }

            // 步骤 2:读入并校验 CRC(CRC 字节亦经 ZDLE 转义)。
            int crcLength = useCrc32 ? 4 : 2;
            byte[]? crcBytes = await ReadCrcBytesAsync(reader, crcLength, ct).ConfigureAwait(false);
            if (crcBytes is null)
            {
                return new(ZModemSubpacketStatus.EndOfStream, []);
            }

            bool ok;
            if (useCrc32)
            {
                uint expected = (uint)(crcBytes[0] | (crcBytes[1] << 8) | (crcBytes[2] << 16) | (crcBytes[3] << 24));
                uint actual = Crc32ZModem.UpdateRunning(crc32, frameEnd) ^ 0xFFFFFFFF;
                ok = expected == actual;
            }
            else
            {
                ushort expected = (ushort)((crcBytes[0] << 8) | crcBytes[1]);
                ushort actual = Crc16Xmodem.Update(crc16, frameEnd);
                ok = expected == actual;
            }

            return ok
                ? new(ZModemSubpacketStatus.Ok, accumulator.WrittenSpan.ToArray(), end)
                : new(ZModemSubpacketStatus.CrcError, []);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static async ValueTask<byte[]?> ReadCrcBytesAsync(ZModemFrameReader reader, int count, CancellationToken ct)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            (ZdleToken token, bool eof) = await reader.ReadEscapedByteAsync(ct).ConfigureAwait(false);
            if (eof || token.Kind != ZdleTokenKind.DataByte)
            {
                return null;
            }
            bytes[i] = token.Value;
        }
        return bytes;
    }
}

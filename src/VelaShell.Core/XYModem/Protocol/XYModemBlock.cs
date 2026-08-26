using VelaShell.Core.FileTransfer.Protocol;

namespace VelaShell.Core.XYModem.Protocol;

/// <summary>
/// XMODEM / YMODEM 数据块的编解码。块结构为:
/// <c>&lt;SOH|STX&gt; &lt;块号&gt; &lt;块号取反&gt; &lt;定长负载&gt; &lt;校验&gt;</c>,
/// 校验为 CRC16/XMODEM(两字节大端)或 8 位算术校验和(一字节)。
/// 块号从 1 开始、按 256 回绕;YMODEM 的 0 号块承载文件名与大小。
/// </summary>
public static class XYModemBlock
{
    /// <summary>块头长度:引导字节 + 块号 + 块号取反。</summary>
    public const int HeaderLength = 3;

    /// <summary>编码一个数据块所需的缓冲长度。</summary>
    /// <param name="payloadLength">负载长度(128 或 1024)。</param>
    /// <param name="useCrc">true 用 CRC16(2 字节),false 用 8 位校验和(1 字节)。</param>
    /// <returns>整块字节数。</returns>
    public static int EncodedLength(int payloadLength, bool useCrc) =>
        HeaderLength + payloadLength + (useCrc ? 2 : 1);

    /// <summary>
    /// 把一段负载编码成一个完整数据块。负载长度必须正好是 128 或 1024;
    /// 不足定长的尾块由调用方先用 <see cref="XYModemConstants.SUB" /> 补齐。
    /// </summary>
    /// <param name="payload">定长负载。</param>
    /// <param name="blockNumber">块号(取低 8 位上链)。</param>
    /// <param name="useCrc">true 用 CRC16,false 用 8 位校验和。</param>
    /// <param name="destination">接收编码结果的缓冲。</param>
    /// <returns>写入的字节数。</returns>
    public static int Write(ReadOnlySpan<byte> payload, int blockNumber, bool useCrc, Span<byte> destination)
    {
        byte lead = payload.Length switch
        {
            XYModemConstants.SmallPayload => XYModemConstants.SOH,
            XYModemConstants.LargePayload => XYModemConstants.STX,
            _ => throw new ArgumentException("XMODEM/YMODEM 负载必须是 128 或 1024 字节。", nameof(payload))
        };
        int total = EncodedLength(payload.Length, useCrc);
        if (destination.Length < total)
        {
            throw new ArgumentException("目标缓冲不足以容纳整个数据块。", nameof(destination));
        }

        byte seq = (byte)(blockNumber & 0xFF);
        destination[0] = lead;
        destination[1] = seq;
        destination[2] = (byte)~seq;
        payload.CopyTo(destination[HeaderLength..]);

        Span<byte> checksum = destination[(HeaderLength + payload.Length)..];
        if (useCrc)
        {
            ushort crc = Crc16Xmodem.Compute(payload);
            checksum[0] = (byte)(crc >> 8);
            checksum[1] = (byte)(crc & 0xFF);
        }
        else
        {
            checksum[0] = Checksum(payload);
        }
        return total;
    }

    /// <summary>
    /// 校验一个已收齐的数据块的校验字段。
    /// </summary>
    /// <param name="payload">块负载。</param>
    /// <param name="checksumBytes">块尾的校验字节(CRC16 为 2 字节大端,校验和为 1 字节)。</param>
    /// <param name="useCrc">当前是否为 CRC16 模式。</param>
    /// <returns>校验通过返回 <c>true</c>。</returns>
    public static bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> checksumBytes, bool useCrc)
    {
        if (useCrc)
        {
            ushort expected = (ushort)((checksumBytes[0] << 8) | checksumBytes[1]);
            return expected == Crc16Xmodem.Compute(payload);
        }
        return checksumBytes[0] == Checksum(payload);
    }

    /// <summary>XMODEM 的原始校验方式:负载所有字节按 8 位无符号相加(自然溢出)。</summary>
    /// <param name="payload">块负载。</param>
    /// <returns>8 位校验和。</returns>
    public static byte Checksum(ReadOnlySpan<byte> payload)
    {
        byte sum = 0;
        foreach (byte b in payload)
        {
            unchecked
            {
                sum += b;
            }
        }
        return sum;
    }
}

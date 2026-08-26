using VelaShell.Core.FileTransfer.Protocol;

namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// 把 <see cref="ZModemHeader" /> 序列化为链路字节。十六进制帧头用于启动 / 控制帧,
/// 二进制帧头(CRC16 / CRC32)用于数据类帧。CRC16 以大端(高字节在前)传输,
/// CRC32 以小端(低字节在前)传输,均与 lrzsz 一致。
/// </summary>
public static class ZModemFrameWriter
{
    private static readonly byte[] HexDigits = "0123456789abcdef"u8.ToArray();

    /// <summary>把帧头序列化为指定编码形态的链路字节。</summary>
    /// <param name="header">要发送的帧头。</param>
    /// <param name="format">帧头编码形态。</param>
    /// <returns>可直接写入传输的字节序列。</returns>
    public static byte[] Write(ZModemHeader header, ZModemHeaderFormat format) =>
        format switch
        {
            ZModemHeaderFormat.Hex => WriteHex(header),
            ZModemHeaderFormat.Binary16 => WriteBinary16(header),
            ZModemHeaderFormat.Binary32 => WriteBinary32(header),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    private static byte[] WriteHex(ZModemHeader header)
    {
        // 帧头 5 字节(type + 4 参数)参与 CRC16。裸 Compute 即 lrzsz 的上链值(见 Crc16Xmodem 注释)。
        Span<byte> core = [(byte)header.Type, header.P0, header.P1, header.P2, header.P3];
        ushort crc = Crc16Xmodem.Compute(core);

        var output = new List<byte>(24)
        {
            ZModemConstants.ZPAD,
            ZModemConstants.ZPAD,
            ZModemConstants.ZDLE,
            ZModemConstants.ZHEX
        };
        foreach (byte b in core)
        {
            AppendHexByte(output, b);
        }
        AppendHexByte(output, (byte)(crc >> 8));
        AppendHexByte(output, (byte)(crc & 0xFF));

        // 十六进制帧头以 CR LF 收尾;lrzsz 额外补一个 XON(0x11) 释放可能的流控暂停。
        output.Add(0x0D);
        output.Add(0x0A);
        if (header.Type is not ZModemFrameType.ZACK and not ZModemFrameType.ZFIN)
        {
            output.Add(ZModemConstants.XON);
        }
        return [.. output];
    }

    private static byte[] WriteBinary16(ZModemHeader header)
    {
        Span<byte> core = [(byte)header.Type, header.P0, header.P1, header.P2, header.P3];
        ushort crc = Crc16Xmodem.Compute(core);

        var output = new List<byte>(24)
        {
            ZModemConstants.ZPAD,
            ZModemConstants.ZDLE,
            ZModemConstants.ZBIN
        };
        foreach (byte b in core)
        {
            ZdleCodec.EscapeByte(b, output);
        }
        ZdleCodec.EscapeByte((byte)(crc >> 8), output);
        ZdleCodec.EscapeByte((byte)(crc & 0xFF), output);
        return [.. output];
    }

    private static byte[] WriteBinary32(ZModemHeader header)
    {
        Span<byte> core = [(byte)header.Type, header.P0, header.P1, header.P2, header.P3];
        uint crc = Crc32ZModem.Compute(core);

        var output = new List<byte>(28)
        {
            ZModemConstants.ZPAD,
            ZModemConstants.ZDLE,
            ZModemConstants.ZBIN32
        };
        foreach (byte b in core)
        {
            ZdleCodec.EscapeByte(b, output);
        }
        // CRC32 小端(低字节在前)。
        ZdleCodec.EscapeByte((byte)(crc & 0xFF), output);
        ZdleCodec.EscapeByte((byte)((crc >> 8) & 0xFF), output);
        ZdleCodec.EscapeByte((byte)((crc >> 16) & 0xFF), output);
        ZdleCodec.EscapeByte((byte)((crc >> 24) & 0xFF), output);
        return [.. output];
    }

    private static void AppendHexByte(List<byte> output, byte value)
    {
        output.Add(HexDigits[(value >> 4) & 0x0F]);
        output.Add(HexDigits[value & 0x0F]);
    }

    /// <summary>解析两个 ASCII 十六进制字符为一个字节(供帧读取器复用)。</summary>
    /// <param name="high">高位十六进制字符。</param>
    /// <param name="low">低位十六进制字符。</param>
    /// <param name="value">解析出的字节。</param>
    /// <returns>两个字符均为合法十六进制时返回 <c>true</c>。</returns>
    internal static bool TryParseHexByte(byte high, byte low, out byte value)
    {
        value = 0;
        if (!TryHexNibble(high, out int hi) || !TryHexNibble(low, out int lo))
        {
            return false;
        }
        value = (byte)((hi << 4) | lo);
        return true;
    }

    private static bool TryHexNibble(byte c, out int nibble)
    {
        char ch = char.ToLowerInvariant((char)c);
        if (ch is >= '0' and <= '9')
        {
            nibble = ch - '0';
            return true;
        }
        if (ch is >= 'a' and <= 'f')
        {
            nibble = 10 + (ch - 'a');
            return true;
        }
        nibble = 0;
        return false;
    }
}

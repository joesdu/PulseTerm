namespace VelaShell.Core.ZModem.Protocol;

/// <summary>ZDLE 转义序列中,紧跟 <see cref="ZModemConstants.ZDLE" /> 的字节所表示的语义。</summary>
public enum ZdleTokenKind
{
    /// <summary>普通被转义的数据字节(值为 <c>following ^ 0x40</c>)。</summary>
    DataByte,

    /// <summary>被转义的 0x7F(ZRUB0,<c>'l'</c>)。</summary>
    Rub0,

    /// <summary>被转义的 0xFF(ZRUB1,<c>'m'</c>)。</summary>
    Rub1,

    /// <summary>数据子包结束、无需应答(ZCRCE)。</summary>
    SubpacketEndNoAck,

    /// <summary>数据子包继续、无需应答(ZCRCG)。</summary>
    SubpacketMoreNoAck,

    /// <summary>数据子包继续、需要应答(ZCRCQ)。</summary>
    SubpacketMoreAck,

    /// <summary>数据子包结束、需要应答(ZCRCW)。</summary>
    SubpacketEndAck,

    /// <summary>检测到取消:<c>ZDLE</c> 之后又是 <c>ZDLE/CAN</c>(0x18),属中止序列。</summary>
    Cancel,

    /// <summary><c>ZDLE</c> 之后跟随非法字节,协议错误。</summary>
    Invalid
}

/// <summary>解析 <see cref="ZModemConstants.ZDLE" /> 后一字节得到的语义标记。</summary>
/// <param name="Kind">该转义序列的语义分类。</param>
/// <param name="Value">
/// 对 <see cref="ZdleTokenKind.DataByte" /> 为反转义后的数据字节;对子包终止符为原始终止字节
/// (需并入 CRC 计算);其它情形无意义。
/// </param>
public readonly record struct ZdleToken(ZdleTokenKind Kind, byte Value);

/// <summary>
/// ZMODEM 的 ZDLE(0x18)转义编解码。编码遵循 lrzsz <c>zsendline</c> 的默认转义集
/// (ZDLE、DLE 0x10、XON 0x11、XOFF 0x13 及其高位变体 0x90/0x91/0x93),
/// 可选「转义全部控制字符」模式(对应 <c>Zctlesc</c>)。所有操作均为原始字节级,不做字符编码。
/// </summary>
public static class ZdleCodec
{
    /// <summary>被转义的 0x7F 所用的跟随字节 <c>'l'</c>(ZRUB0)。</summary>
    public const byte ZRUB0 = 0x6C;

    /// <summary>被转义的 0xFF 所用的跟随字节 <c>'m'</c>(ZRUB1)。</summary>
    public const byte ZRUB1 = 0x6D;

    /// <summary>判断某字节在数据流中是否必须经 ZDLE 转义。</summary>
    /// <param name="value">待发送的原始字节。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>);默认仅转义必需集合。</param>
    /// <returns>需要转义返回 <c>true</c>。</returns>
    public static bool NeedsEscape(byte value, bool escapeAllControl = false)
    {
        return value switch
        {
            // 0x18
            ZModemConstants.ZDLE or 0x10 or ZModemConstants.XON or ZModemConstants.XOFF or 0x90 or 0x91 or 0x93 => true,
            _ => escapeAllControl && (value & 0x60) == 0,// Zctlesc:转义所有 (b & 0x60) == 0 的控制字节(0x00–0x1F 与 0x80–0x9F)。
        };
    }

    /// <summary>把单个字节按需转义后追加到输出缓冲。</summary>
    /// <param name="value">原始数据字节。</param>
    /// <param name="output">接收编码结果的缓冲。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    public static void EscapeByte(byte value, List<byte> output, bool escapeAllControl = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (NeedsEscape(value, escapeAllControl))
        {
            output.Add(ZModemConstants.ZDLE);
            output.Add((byte)(value ^ 0x40));
        }
        else
        {
            output.Add(value);
        }
    }

    /// <summary>
    /// 把单个字节按需转义后写入 <paramref name="destination" />,返回写入的字节数(1 或 2)。
    /// 发送端热路径用它直接写进池化缓冲,避免 <c>List&lt;byte&gt;</c> 的逐字节 Add 与扩容拷贝。
    /// </summary>
    /// <param name="value">原始数据字节。</param>
    /// <param name="destination">接收编码结果的缓冲(至少 2 字节)。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    /// <returns>写入的字节数。</returns>
    public static int EscapeByte(byte value, Span<byte> destination, bool escapeAllControl = false)
    {
        if (!NeedsEscape(value, escapeAllControl))
        {
            destination[0] = value;
            return 1;
        }
        destination[0] = ZModemConstants.ZDLE;
        destination[1] = (byte)(value ^ 0x40);
        return 2;
    }

    /// <summary>把一段数据按 ZDLE 规则转义后追加到输出缓冲。</summary>
    /// <param name="data">原始数据。</param>
    /// <param name="output">接收编码结果的缓冲。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    public static void Escape(ReadOnlySpan<byte> data, List<byte> output, bool escapeAllControl = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (byte b in data)
        {
            EscapeByte(b, output, escapeAllControl);
        }
    }

    /// <summary>把一段数据按 ZDLE 规则转义并返回新数组。</summary>
    /// <param name="data">原始数据。</param>
    /// <param name="escapeAllControl">是否转义全部控制字符(<c>Zctlesc</c>)。</param>
    /// <returns>转义后的字节数组。</returns>
    public static byte[] Escape(ReadOnlySpan<byte> data, bool escapeAllControl = false)
    {
        var output = new List<byte>(data.Length + 8);
        Escape(data, output, escapeAllControl);
        return [.. output];
    }

    /// <summary>
    /// 解析紧跟 <see cref="ZModemConstants.ZDLE" /> 的一字节,给出其语义标记。
    /// 调用方已消费了 ZDLE 本身,此处只处理其后的 <paramref name="following" />。
    /// </summary>
    /// <param name="following">ZDLE 之后的那一字节。</param>
    /// <returns>该转义序列的语义标记。</returns>
    public static ZdleToken ClassifyEscaped(byte following) =>
        following switch
        {
            ZModemConstants.ZCRCE => new(ZdleTokenKind.SubpacketEndNoAck, following),
            ZModemConstants.ZCRCG => new(ZdleTokenKind.SubpacketMoreNoAck, following),
            ZModemConstants.ZCRCQ => new(ZdleTokenKind.SubpacketMoreAck, following),
            ZModemConstants.ZCRCW => new(ZdleTokenKind.SubpacketEndAck, following),
            ZRUB0 => new(ZdleTokenKind.Rub0, 0x7F),
            ZRUB1 => new(ZdleTokenKind.Rub1, 0xFF),
            ZModemConstants.ZDLE => new(ZdleTokenKind.Cancel, following),
            // 合法转义字节:高 3 位形如 010x_xxxx / 110x_xxxx,即 (b & 0x60) == 0x40。
            _ when (following & 0x60) == 0x40 => new(ZdleTokenKind.DataByte, (byte)(following ^ 0x40)),
            _ => new(ZdleTokenKind.Invalid, following)
        };
}

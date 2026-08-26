namespace VelaShell.Core.XYModem.Protocol;

/// <summary>
/// XMODEM / XMODEM-1K / YMODEM / YMODEM-G 的字节级常量。取值遵循 Chuck Forsberg 的
/// <c>ymodem.txt</c> 与 lrzsz 的 <c>sb</c>/<c>rb</c>、<c>sx</c>/<c>rx</c> 实现。
/// 与 ZMODEM 不同,这一族协议不做任何转义:数据块定长、原样上链,靠块号 + CRC 保证完整性。
/// </summary>
public static class XYModemConstants
{
    /// <summary>128 字节数据块的引导字节(Start Of Header,0x01)。</summary>
    public const byte SOH = 0x01;

    /// <summary>1024 字节数据块的引导字节(0x02),XMODEM-1K / YMODEM 用。</summary>
    public const byte STX = 0x02;

    /// <summary>传输结束(End Of Transmission,0x04):单个文件的数据发完了。</summary>
    public const byte EOT = 0x04;

    /// <summary>肯定应答(0x06):上一块收妥,继续发下一块。</summary>
    public const byte ACK = 0x06;

    /// <summary>否定应答(0x15):上一块校验失败,重发;握手阶段还表示「请用 8 位校验和」。</summary>
    public const byte NAK = 0x15;

    /// <summary>取消(0x18):连续两个及以上表示中止传输。</summary>
    public const byte CAN = 0x18;

    /// <summary>填充字节(Ctrl-Z / CP-M EOF,0x1A):最后一块不足定长时用它补齐。</summary>
    public const byte SUB = 0x1A;

    /// <summary>接收方握手字符 <c>'C'</c>(0x43):请求 CRC16 校验模式(而非 8 位校验和)。</summary>
    public const byte CrcRequest = 0x43;

    /// <summary>接收方握手字符 <c>'G'</c>(0x47):请求 YMODEM-G 流式模式(发送方不等逐块应答)。</summary>
    public const byte StreamRequest = 0x47;

    /// <summary>128 字节块的负载长度。</summary>
    public const int SmallPayload = 128;

    /// <summary>1024 字节块的负载长度。</summary>
    public const int LargePayload = 1024;

    /// <summary>
    /// 中止序列:连续 8 个 CAN。规范要求接收方见到连续 2 个及以上 CAN 即中止,
    /// 这里多发几个以穿过对端可能的行缓冲。
    /// </summary>
    public static ReadOnlySpan<byte> CancelSequence => [CAN, CAN, CAN, CAN, CAN, CAN, CAN, CAN];
}

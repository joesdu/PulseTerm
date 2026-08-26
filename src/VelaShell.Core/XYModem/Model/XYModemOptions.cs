using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.XYModem.Model;

/// <summary>
/// XMODEM / YMODEM 引擎的可调参数。默认值针对与 lrzsz(<c>sb</c>/<c>rb</c>、<c>sx</c>/<c>rx</c>)
/// 互操作的稳健性而设。这一族协议靠固定块 + 逐块应答推进,没有 ZMODEM 的能力协商,
/// 因此「用哪个变体」必须由调用方在开始前就定下来。
/// </summary>
public sealed class XYModemOptions
{
    /// <summary>本次会话使用的协议变体。默认 <see cref="TerminalTransferProtocol.YModem" />。</summary>
    public TerminalTransferProtocol Protocol { get; init; } = TerminalTransferProtocol.YModem;

    /// <summary>
    /// 是否使用 CRC16 校验(而非 XMODEM 最初的 8 位算术校验和)。默认 <c>true</c> ——
    /// YMODEM 强制要求 CRC,只有对接非常古老的 <c>rx</c> 实现时才需要关掉。
    /// </summary>
    public bool UseCrc { get; init; } = true;

    /// <summary>
    /// 接收方握手时每隔多久重发一次 <c>'C'</c>(或 <c>'G'</c>)。默认 3 秒 —— 这是
    /// XMODEM 沿用至今的惯例值,发送方按它来判断「对方还在等」。
    /// </summary>
    public TimeSpan HandshakeInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 握手阶段最多重试几次(接收方重发 <c>'C'</c>、发送方等 <c>'C'</c>)。默认 10 次,
    /// 与 <see cref="HandshakeInterval" /> 相乘约 30 秒 —— 用户在远端敲完 <c>sb</c>/<c>rb</c>
    /// 再切回来点菜单,这段时间要够宽裕。
    /// </summary>
    public int HandshakeRetries { get; init; } = 10;

    /// <summary>
    /// 传输已经跑起来之后,等待对端下一个块 / 下一个应答的超时。默认 20 秒。
    /// </summary>
    public TimeSpan BlockTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>单个块连续重传 / 超时的最大次数,超过即中止会话。默认 10。</summary>
    public int MaxRetries { get; init; } = 10;

    /// <summary>
    /// XMODEM 接收时使用的落地文件名。XMODEM 协议本身不传文件名(这正是 YMODEM 的由来),
    /// 只能由调用方指定;YMODEM 会用 0 号块里的真实文件名覆盖它。
    /// </summary>
    public string DefaultReceiveFileName { get; init; } = "xmodem-received.bin";

    /// <summary>默认选项实例(YMODEM + CRC16)。</summary>
    public static XYModemOptions Default { get; } = new();

    /// <summary>发送时的数据块负载长度:经典 XMODEM 为 128,其余变体为 1024。</summary>
    public int PayloadSize => Protocol == TerminalTransferProtocol.XModem ? 128 : 1024;

    /// <summary>是否为批量协议(带 0 号文件信息块、可连发多个文件)。</summary>
    public bool IsBatch => Protocol is TerminalTransferProtocol.YModem or TerminalTransferProtocol.YModemG;

    /// <summary>是否为流式变体(YMODEM-G:发送方不等逐块 ACK)。</summary>
    public bool IsStreaming => Protocol == TerminalTransferProtocol.YModemG;
}

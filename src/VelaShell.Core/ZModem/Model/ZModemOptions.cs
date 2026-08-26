namespace VelaShell.Core.ZModem.Model;

/// <summary>ZMODEM 引擎的可调参数。默认值针对与 lrzsz 互操作的稳健性而设,而非极致吞吐。</summary>
public sealed class ZModemOptions
{
    /// <summary>是否优先使用 CRC32(接收方在 ZRINIT 中通告能力,发送方据此选择)。默认 <c>true</c>。</summary>
    public bool PreferCrc32 { get; init; } = true;

    /// <summary>是否转义全部控制字符(<c>Zctlesc</c>),用于对控制字符敏感的链路。默认 <c>false</c>。</summary>
    public bool EscapeAllControl { get; init; }

    /// <summary>
    /// 发送方每个数据子包的最大负载字节数。默认 8192 —— lrzsz 的 <c>rz</c> 是纯字节流解析,
    /// 对子包大小没有上限,而 SSH 这类高延迟链路上 1KB 子包意味着每 MB 要多做上千次
    /// 「转义 + CRC + 写通道」的固定开销。8K 与 SecureCRT / Xshell 的默认值一致。
    /// </summary>
    public int SubpacketSize { get; init; } = 8192;

    /// <summary>
    /// 流式推数据的间隙,发现对端插话后读取那一帧的时间预算。默认 500 毫秒。
    /// 对端在流式阶段只会为出错 / 中止插话(ZRPOS / ZSKIP / ZCAN),这类帧都是几十字节、
    /// 一到就是完整的,给半秒足够;给太长会让偶发噪声把整条数据流卡住。
    /// </summary>
    public TimeSpan StreamInterjectTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 传输已经跑起来之后,等待对端下一帧 / 下一个子包的超时。超时后引擎按协议重试(补发 ZRPOS),
    /// 连续超时超过 <see cref="MaxRetries" /> 即中止会话。默认 30 秒 —— 数据在途时给得宽松些,
    /// 避免把一个只是慢的传输误杀。
    /// </summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 连续超时 / CRC 失败的最大重试次数,超过即判定失败。默认 10。
    /// 与 <see cref="FrameTimeout" /> 共同决定「传输中途断流多久放弃」的上限。
    /// </summary>
    public int MaxRetries { get; init; } = 10;

    /// <summary>
    /// 握手阶段(还没收到任何文件帧时)等待对端的超时。默认 5 秒。
    /// 这里必须比 <see cref="FrameTimeout" /> 短得多:握手谈不拢时终端是全黑的(字节都被路由器接管),
    /// 用户只能干等,所以要尽快放弃并把终端还回去。
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 握手阶段的最大重试次数。默认 3 —— 与 <see cref="HandshakeTimeout" /> 相乘,
    /// 最坏情况约 15 秒后终端就会恢复可用。
    /// </summary>
    public int HandshakeRetries { get; init; } = 3;

    /// <summary>
    /// 取消 / 中止后清场时,连续多久无新字节即认为对端已停发。默认 700 毫秒。
    /// 用户取消后对端(sz/rz)在收到 CAN 前可能还在重传协议帧,这些字节若回流终端就是满屏乱码;
    /// 清场把它们吞掉,等对端安静后再交还终端。
    /// </summary>
    public TimeSpan PostCancelDrainIdle { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>取消 / 中止后清场的最长时间(对端持续猛发时的兜底)。默认 3 秒。</summary>
    public TimeSpan PostCancelDrainMax { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>默认选项实例。</summary>
    public static ZModemOptions Default { get; } = new();
}

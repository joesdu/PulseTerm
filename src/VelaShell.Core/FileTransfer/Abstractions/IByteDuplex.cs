namespace VelaShell.Core.FileTransfer.Abstractions;

/// <summary>
/// 终端内文件传输引擎(ZMODEM / XMODEM / YMODEM)面向的最小双工字节通道:与具体传输
/// (SSH Shell、本地 ConPTY、未来的串口 / Telnet)解耦。终端侧把截获的输出字节送入通道,
/// 引擎从 <see cref="ReadAsync" /> 拉取,并经 <see cref="WriteAsync" /> 回写协议帧。
/// </summary>
/// <remarks>
/// 该接口只搬运原始字节,绝不做任何字符编码 / 换行归一化,以保证传输的二进制安全。
/// </remarks>
public interface IByteDuplex : IAsyncDisposable
{
    /// <summary>
    /// 入站方向当前是否已有字节在排队等待读取。用于发送端在流式推数据的间隙做零阻塞探测:
    /// 对端只在出错 / 中止时才插话(ZMODEM 的 ZRPOS / ZCAN、XMODEM 的 NAK / CAN),
    /// 因此「有入站字节」本身就是「该停下来听对端说话」的信号。
    /// 默认实现返回 <c>false</c>,即退化为「从不主动探测」的旧行为。
    /// </summary>
    bool HasPendingInbound => false;

    /// <summary>
    /// 拉取下一段已到达的入站字节。无数据时异步等待;通道结束(EOF)时返回长度为 0 的内存。
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>到达的字节段;EOF 时为空。</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>把一段字节写入底层传输(发送给对端)。</summary>
    /// <param name="data">要发送的字节。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>刷新底层传输的写缓冲。</summary>
    /// <param name="cancellationToken">取消刷新的令牌。</param>
    ValueTask FlushAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 把已经读进引擎、但其实不属于本次传输的字节退回通道头部。会话收尾时调用:
    /// 协议帧和它后面的 shell 输出(<c>sz</c> 退出后紧跟的提示符最典型)常常在同一个网络分片里,
    /// 不退回去就会被引擎连同读取器一起丢掉,用户看到的是"传完了但提示符没了"。
    /// 默认实现丢弃(退化为旧行为)。
    /// </summary>
    /// <param name="data">要退回的字节。</param>
    void Unread(ReadOnlyMemory<byte> data) { }
}

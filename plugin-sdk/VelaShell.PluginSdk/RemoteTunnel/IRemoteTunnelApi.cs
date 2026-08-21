namespace VelaShell.PluginSdk.RemoteTunnel;

/// <summary>打开一条隧道的选项。</summary>
public sealed record TunnelOptions
{
    /// <summary>
    /// 建立通道的超时,默认 15 秒,上限 2 分钟(宿主强制夹取)。
    /// <para>
    /// 只管**建立**:通道一旦建成,读写就由调用方的取消令牌与远端决定,不再有死线 ——
    /// 隧道的正常形态是 <c>docker events</c> 这种挂着不动的长连接,给它一个总时限
    /// 只会让界面在第 N 分钟莫名其妙地断流。
    /// </para>
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 远程隧道能力:在既有 SSH 会话上开一条到远端 <b>unix socket</b> 或 <b>TCP 端口</b> 的
/// 双工字节流(SSH 的 <c>direct-streamlocal@openssh.com</c> / <c>direct-tcpip</c> 通道)。
/// 会话无效时抛 <see cref="PluginSessionNotFoundException" />。
/// <para>
/// <b>为什么不能用 <see cref="RemoteExec.IRemoteExecApi" /> 凑合。</b>
/// 远程执行的两种形态都是**文本**:<c>RunAsync</c> 把整个输出 UTF-8 解码成一个字符串,
/// <c>StreamAsync</c> 按 <c>\n</c> 切行回调。而这条隧道要承载的东西 —— Docker Engine API 的
/// 分块传输、<c>/containers/{id}/archive</c> 的 tar 流、<c>/exec</c> 的 8 字节多路复用帧 ——
/// 全是二进制:UTF-8 解码会把非法字节换成 U+FFFD(不可逆),按行切分会在 <c>0x0A</c>
/// 处把一帧劈成两半。凑合的结果不是"慢一点",是**数据静默损坏**。
/// </para>
/// <para>
/// <b>为什么不用本地端口转发。</b> 宿主确实有 <c>StartPortForwardAsync</c>,但它在**本机**
/// 开一个监听端口 —— 那意味着同机的任何进程都能连上去,而隧道对面是一个 root 等价的
/// Docker socket。这条 API 只把流交给发起调用的插件,不在本机留下任何可被别人连上的入口。
/// </para>
/// <para>
/// 仅 <c>inProcess</c> 宿主模式可用:返回的是一个活的 <see cref="Stream" />,
/// 跨进程代理一条裸流除了把每个字节多搬一次之外没有别的效果。隔离进程里调用抛
/// <see cref="NotSupportedException" />。
/// </para>
/// </summary>
public interface IRemoteTunnelApi
{
    /// <summary>
    /// 同时在飞的隧道上限(每插件)。超过时抛 <see cref="InvalidOperationException" />。
    /// </summary>
    /// <remarks>
    /// 隧道是不限时的,每条占一个 SSH 通道。一个 Docker 面板正常也就是"列表连接池 2~3 条
    /// + 事件流 1 条 + 每个跟随中的日志/统计各 1 条",16 足够宽松;
    /// 而没有上限的话,一个漏掉 <c>Dispose</c> 的插件能把对端的通道数吃干净 ——
    /// 那时坏掉的不是插件,是用户的连接。
    /// </remarks>
    public const int MaxConcurrentTunnels = 16;

    /// <summary>当前该插件已打开、尚未释放的隧道条数。</summary>
    int ActiveTunnels { get; }

    /// <summary>
    /// 打开一条到远端 unix 域套接字的双工字节流。
    /// <para>
    /// 返回的 <see cref="Stream" /> 是**可读可写**的;<see cref="Stream.Dispose()" />
    /// 关闭 SSH 通道并归还配额 —— 调用方**必须**释放它。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="socketPath">远端 socket 的绝对路径,如 <c>/var/run/docker.sock</c>。</param>
    /// <param name="options">选项;为 null 用默认值。</param>
    /// <param name="cancellationToken">取消令牌(只作用于**建立**阶段)。</param>
    /// <returns>双工字节流。</returns>
    /// <exception cref="NotSupportedException">宿主实现不支持隧道(隔离进程模式)时。</exception>
    Task<Stream> OpenUnixSocketAsync(
        string sessionId,
        string socketPath,
        TunnelOptions? options = null,
        CancellationToken cancellationToken = default)
        // 默认实现只为**源兼容**:第三方的测试替身在 SDK 加了这个能力之后仍然能编译。
        => throw new NotSupportedException("This host does not implement remote tunnels.");

    /// <summary>
    /// 打开一条到远端 TCP 端点的双工字节流(从**远端主机**的角度解析地址,
    /// 因此 <c>localhost</c> 指的是远端的环回)。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="host">远端可解析的主机名或 IP。</param>
    /// <param name="port">端口。</param>
    /// <param name="options">选项;为 null 用默认值。</param>
    /// <param name="cancellationToken">取消令牌(只作用于**建立**阶段)。</param>
    /// <returns>双工字节流。</returns>
    /// <exception cref="NotSupportedException">宿主实现不支持隧道(隔离进程模式)时。</exception>
    Task<Stream> OpenTcpAsync(
        string sessionId,
        string host,
        int port,
        TunnelOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This host does not implement remote tunnels.");
}

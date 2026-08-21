namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH 客户端的库中立抽象:Core/App 只依赖此接口与本命名空间的中立类型
/// (<see cref="SftpEntry" />、<see cref="PortForwardRequest" />、SshClientException 层级),
/// 具体 SSH 库(当前为 SSH.NET)被隔离在 Infrastructure 的实现里,更换底层库时
/// 只需提供新的实现与异常翻译。
/// </summary>
public interface ISshClientWrapper : IDisposable
{
    /// <summary>当前是否已与远程主机建立连接。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 当底层 SSH 连接丢失时被取消的令牌(远端关闭/网络中断)。
    /// 提供快速断线检测,无需轮询或定时器。
    /// </summary>
    CancellationToken Disconnected { get; }

    /// <summary>建立连接时的超时时长。</summary>
    TimeSpan ConnectionTimeout { get; set; }

    /// <summary>异步连接到远程主机。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>断开与远程主机的连接。</summary>
    void Disconnect();

    /// <summary>
    /// 在当前连接上异步创建一条交互式 shell 流(打开通道 + pty-req + shell,2~3 个网络往返),
    /// 使用给定的终端类型、行列尺寸、像素尺寸、缓冲区大小及可选的终端模式参数。
    /// </summary>
    Task<IShellStreamWrapper> CreateShellStreamAsync(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null,
        CancellationToken cancellationToken = default);

    /// <summary>在远端主机上执行一次性命令并返回其标准输出。</summary>
    Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在远端主机上执行一次性命令,取回**标准输出、标准错误与退出码**。
    /// <para>
    /// 与 <see cref="RunCommandAsync" /> 的区别只在于不丢东西:后者为了调用点简单
    /// 把标准错误与退出码都扔了,而任何要**如实报告失败**的调用方(插件的远程执行能力
    /// 就是其一)都需要这两样 —— 没有它们,"命令失败了"和"命令没有输出"长得一模一样。
    /// </para>
    /// </summary>
    /// <param name="commandText">命令行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出、标准错误与退出码。</returns>
    Task<RemoteCommandResult> RunCommandDetailedAsync(string commandText, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在远端主机上执行命令,并**边跑边按行**回调输出;进程结束后返回退出码。
    /// <para>
    /// 用于长驻命令(<c>docker logs -f</c>、<c>tail -F</c>、<c>docker events</c>)。
    /// 取消令牌触发时向远端进程发 <c>TERM</c> 再关闭通道,而不是干等它自己结束。
    /// </para>
    /// </summary>
    /// <param name="commandText">命令行。</param>
    /// <param name="includeStandardError">是否也回调标准错误。</param>
    /// <param name="onLine">逐行回调(参数为「是否来自标准错误」与「行文本」);在 I/O 线程上调用,应快速返回。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码与行数。</returns>
    Task<RemoteCommandStreamResult> StreamCommandAsync(
        string commandText,
        bool includeStandardError,
        Action<bool, string> onLine,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步建立并启动一条端口转发;返回的句柄负责其停止与清理。
    /// 启动失败时抛出且不留下半挂的监听。
    /// </summary>
    Task<IPortForwardHandle> StartPortForwardAsync(PortForwardRequest request, CancellationToken cancellationToken = default);
}

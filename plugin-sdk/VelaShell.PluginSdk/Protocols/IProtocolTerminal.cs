namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 宿主打开一条终端会话时给出的初始参数。之后窗口大小变化经
/// <see cref="IProtocolTerminalSession.ResizeAsync" /> 通知。
/// </summary>
/// <param name="TerminalType">终端类型(<c>TERM</c>,如 <c>xterm-256color</c>);
/// 协议若有自己的上报机制(Telnet 的 TERMINAL-TYPE 子协商)应上报这个值。</param>
/// <param name="Columns">初始列数。</param>
/// <param name="Rows">初始行数。</param>
public readonly record struct ProtocolTerminalOptions(string TerminalType, int Columns, int Rows);

/// <summary>
/// 一条已建立的终端会话:**只搬字节**的双工通道,外加一个尺寸通知。
/// <para>
/// 刻意做成裸字节而不是"行"或"命令":VT 解析、回滚、搜索、ZMODEM 检测全在宿主侧,
/// 插件多做一层解释只会与宿主的终端引擎打架。
/// </para>
/// <para>
/// 线程约束:读循环独占 <see cref="ReadAsync" />,而 <see cref="WriteAsync" /> 与
/// <see cref="ResizeAsync" /> 由界面线程与后台任务并发调用 —— 实现须自行序列化写侧
/// (Telnet 的 NAWS 子协商与用户按键都是写,交织会把帧撕开)。
/// </para>
/// </summary>
public interface IProtocolTerminalSession : IAsyncDisposable
{
    /// <summary>
    /// 读取远端输出。返回 0 表示会话结束(EOF),宿主据此把标签置为已断开 ——
    /// 连接掉线**不要抛异常**,归一化成 EOF 才能走到"可重连"那条路上。
    /// </summary>
    /// <param name="buffer">接收缓冲区。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读到的字节数;0 = 会话结束。</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>把用户输入写给远端(按键、粘贴、ZMODEM 帧都走这里)。</summary>
    /// <param name="data">待写字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 终端尺寸变化(用户拉窗口)。协议没有对应机制时实现成空操作 ——
    /// 抛异常会让每一次窗口缩放都在日志里刷一条。
    /// </summary>
    /// <param name="columns">列数。</param>
    /// <param name="rows">行数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件实现的**终端**协议(Telnet、串口、裸 TCP…)。与
/// <see cref="IProtocolFileSystem" /> 是协议能力的两条腿:实现前者接进双栏文件浏览器,
/// 实现这个则接进终端标签 —— 桥、VT 引擎、自绘控件、回滚、搜索、会话日志、会话录制、
/// ZMODEM 全部零改动复用。
/// <para>
/// 注册方式与文件协议一致(见 <see cref="IProtocolsApi.Register(ProtocolDescriptor, IProtocolTerminal)" />),
/// 页签、连接表单、字段折叠也都由同一份 <see cref="ProtocolDescriptor" /> 声明,
/// 因此终端协议插件同样**不需要写一行界面代码**。
/// </para>
/// <para>
/// 一种协议注册为终端协议后,用它建的会话打开的是终端标签而不是文件面板;
/// 两者都实现也是允许的(宿主优先开终端)。
/// </para>
/// </summary>
public interface IProtocolTerminal
{
    /// <summary>
    /// 建立一条终端会话。地址写错、端口不通、凭据不对都应在这一步暴露出来 ——
    /// 宿主据此在标签页内画失败覆盖层,而不是开一个永远不出字的黑屏。
    /// </summary>
    /// <param name="request">连接参数(主机、端口、凭据、协议专属设置)。</param>
    /// <param name="options">终端初始参数(TERM 与初始行列)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已建立的会话。</returns>
    /// <exception cref="ProtocolAuthenticationException">凭据无效;宿主会重新弹登录框。</exception>
    /// <exception cref="ProtocolConnectionException">端点不可达或握手失败。</exception>
    Task<IProtocolTerminalSession> ConnectAsync(
        ProtocolConnectRequest request,
        ProtocolTerminalOptions options,
        CancellationToken cancellationToken = default);
}

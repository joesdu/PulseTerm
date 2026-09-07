namespace VelaShell.Core.Ssh;

/// <summary>
/// 一条终端流为什么读到了头 —— 自动重连据此判断该不该把会话拉回来(#383)。
/// </summary>
/// <remarks>
/// <para>
/// 关键的分野是「远端 shell 自己退了」与「连接被打断了」。前者是用户意图
/// (在远端敲 <c>exit</c> / <c>logout</c>),自动连回去等于跟用户较劲 ——
/// 这正是 #383 报的现象:exit 之后标签自己又连上了,退不掉。
/// 后者才是自动重连要救的场景。
/// </para>
/// <para>
/// 这两件事在 SSH 协议层本就是分开的:远端 shell 退出会发
/// <c>SSH_MSG_CHANNEL_CLOSE</c>,读端因此干净地收到 EOF;而连接中断
/// (RST / 超时 / 服务端进程没了)在通道读上抛异常。丢失这条信息的是适配层 ——
/// 它把两者都归一成了「返回 0」。本枚举就是把它补回来。
/// </para>
/// </remarks>
public enum ShellCloseReason
{
    /// <summary>尚未结束,或该实现无法区分(按「连接中断」处理,即维持自动重连)。</summary>
    Unknown = 0,

    /// <summary>远端 shell 自己退出了(<c>exit</c> / <c>logout</c> / 被 kill),通道被对端正常关闭。</summary>
    RemoteExited,

    /// <summary>连接异常中断(网络断开、超时、服务端崩溃),会话并非按用户意图结束。</summary>
    ConnectionLost,

    /// <summary>本地主动拆除(关闭标签、断开按钮、释放),不是远端给出的结论。</summary>
    LocalTeardown
}

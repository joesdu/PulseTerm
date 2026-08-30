namespace VelaShell.Core.Tunnels;

/// <summary>
/// 本地监听端口已被占用。由创建转发前的预检抛出,而不是等底层套接字抛一个
/// 「An attempt was made to access a socket in a way forbidden by its access permissions」
/// 之类只有写网络代码的人看得懂的 <see cref="System.Net.Sockets.SocketException" />。
/// </summary>
/// <param name="message">面向用户的说明(已本地化)。</param>
/// <param name="port">被占用的本地端口。</param>
public sealed class TunnelPortInUseException(string message, uint port) : Exception(message)
{
    /// <summary>被占用的本地端口。</summary>
    public uint Port { get; } = port;
}

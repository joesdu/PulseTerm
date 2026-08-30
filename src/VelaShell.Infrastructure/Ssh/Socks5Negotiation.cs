using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 动态转发(<c>ssh -D</c>)监听端上的 SOCKS5 服务端握手(RFC 1928,仅 CONNECT + 无认证)。
/// <para>
/// 宿主自己接管动态转发的监听端,是为了能逐连接统计连接数与字节数 —— 底层 SSH 库
/// 把 SOCKS 服务端做在内部,不暴露任何计数。握手字节不计入隧道流量:用户关心的是
/// 业务数据量,几十字节的协议寒暄记进去只会让数字失真。
/// </para>
/// </summary>
internal static class Socks5Negotiation
{
    private const byte Version = 0x05;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodNone = 0xFF;
    private const byte CmdConnect = 0x01;

    private const byte AddrIPv4 = 0x01;
    private const byte AddrDomain = 0x03;
    private const byte AddrIPv6 = 0x04;

    /// <summary>回复码:成功。</summary>
    internal const byte ReplySucceeded = 0x00;

    /// <summary>回复码:一般性失败(目标不可达等兜底)。</summary>
    internal const byte ReplyGeneralFailure = 0x01;

    /// <summary>回复码:连接被拒绝。</summary>
    internal const byte ReplyConnectionRefused = 0x05;

    /// <summary>回复码:不支持的命令(仅实现 CONNECT)。</summary>
    internal const byte ReplyCommandNotSupported = 0x07;

    /// <summary>回复码:不支持的地址类型。</summary>
    internal const byte ReplyAddressNotSupported = 0x08;

    /// <summary>
    /// 完成方法协商与 CONNECT 请求解析,返回客户端要求连到的目标。
    /// 协议层面谈不拢时(版本不符、只给了需认证的方法、非 CONNECT 命令)已向客户端
    /// 回过相应错误码,再抛出 <see cref="Socks5ProtocolException" />。
    /// </summary>
    public static async Task<(string Host, int Port)> AcceptRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        // 方法协商:VER NMETHODS METHODS[NMETHODS]
        byte[] greeting = new byte[2];
        await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != Version)
        {
            throw new Socks5ProtocolException($"Unsupported SOCKS version 0x{greeting[0]:X2}; only SOCKS5 is supported.");
        }
        byte[] methods = new byte[greeting[1]];
        await ReadExactlyAsync(stream, methods, cancellationToken).ConfigureAwait(false);
        if (Array.IndexOf(methods, MethodNoAuth) < 0)
        {
            await stream.WriteAsync(new byte[] { Version, MethodNone }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            throw new Socks5ProtocolException("Client offered no acceptable SOCKS5 authentication method (no-auth required).");
        }
        await stream.WriteAsync(new byte[] { Version, MethodNoAuth }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 请求:VER CMD RSV ATYP DST.ADDR DST.PORT
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (header[0] != Version)
        {
            throw new Socks5ProtocolException($"Unsupported SOCKS version 0x{header[0]:X2} in request.");
        }
        if (header[1] != CmdConnect)
        {
            await WriteReplyAsync(stream, ReplyCommandNotSupported, cancellationToken).ConfigureAwait(false);
            throw new Socks5ProtocolException($"Unsupported SOCKS5 command 0x{header[1]:X2}; only CONNECT is supported.");
        }
        string host;
        switch (header[3])
        {
            case AddrIPv4:
                {
                    byte[] addr = new byte[4];
                    await ReadExactlyAsync(stream, addr, cancellationToken).ConfigureAwait(false);
                    host = new IPAddress(addr).ToString();
                    break;
                }
            case AddrIPv6:
                {
                    byte[] addr = new byte[16];
                    await ReadExactlyAsync(stream, addr, cancellationToken).ConfigureAwait(false);
                    host = new IPAddress(addr).ToString();
                    break;
                }
            case AddrDomain:
                {
                    byte[] length = new byte[1];
                    await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
                    byte[] name = new byte[length[0]];
                    await ReadExactlyAsync(stream, name, cancellationToken).ConfigureAwait(false);
                    host = System.Text.Encoding.ASCII.GetString(name);
                    break;
                }
            default:
                await WriteReplyAsync(stream, ReplyAddressNotSupported, cancellationToken).ConfigureAwait(false);
                throw new Socks5ProtocolException($"Unsupported SOCKS5 address type 0x{header[3]:X2}.");
        }
        byte[] portBytes = new byte[2];
        await ReadExactlyAsync(stream, portBytes, cancellationToken).ConfigureAwait(false);
        int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        if (host.Length == 0 || port == 0)
        {
            await WriteReplyAsync(stream, ReplyGeneralFailure, cancellationToken).ConfigureAwait(false);
            throw new Socks5ProtocolException("SOCKS5 request carried an empty destination.");
        }
        return (host, port);
    }

    /// <summary>
    /// 回一条 SOCKS5 应答。BND.ADDR/BND.PORT 填全零 IPv4:该字段只对 BIND/UDP 有意义,
    /// CONNECT 场景下客户端(浏览器、curl、各类 SOCKS 库)一律忽略。
    /// </summary>
    public static async Task WriteReplyAsync(Stream stream, byte reply, CancellationToken cancellationToken)
    {
        byte[] response = [Version, reply, 0x00, AddrIPv4, 0, 0, 0, 0, 0, 0];
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把打开目标通道时的异常翻译成对应的 SOCKS5 回复码。</summary>
    public static byte ReplyCodeFor(Exception ex)
    {
        SocketException? socket = ex as SocketException ?? ex.InnerException as SocketException;
        return socket?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => ReplyConnectionRefused,
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => 0x04, // Host unreachable
            _ => ReplyGeneralFailure
        };
    }

    /// <summary>
    /// 读满整个缓冲区;对端在中途关闭时抛 <see cref="Socks5ProtocolException" />。
    /// (Stream.ReadExactlyAsync 在 EOF 时抛 EndOfStreamException,这里统一成协议异常,
    /// 让调用方只需要认一种失败。)
    /// </summary>
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0)
        {
            return;
        }
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new Socks5ProtocolException("Client closed the connection during the SOCKS5 handshake.");
        }
    }
}

/// <summary>SOCKS5 握手阶段的协议错误(版本、方法或命令不受支持,或对端中途断开)。</summary>
internal sealed class Socks5ProtocolException(string message) : Exception(message);

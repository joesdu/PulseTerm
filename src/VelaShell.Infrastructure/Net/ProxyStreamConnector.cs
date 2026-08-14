using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 按代理路由建立到目标的隧道流:HTTP 走 CONNECT(RFC 9110 §9.3.6),SOCKS5 走 RFC 1928,
/// 用户名密码认证走 RFC 1929。ProxyDns 开 = 把主机名交给代理端解析(CONNECT 行 / ATYP=DOMAIN);
/// 关 = 本地先解析成 IP 再交给代理。返回的流关闭时连带关闭底层套接字。
/// </summary>
public static class ProxyStreamConnector
{
    /// <summary>经 <paramref name="route" /> 指定的代理连接到目标,返回已打通的双向流。</summary>
    public static async Task<Stream> ConnectAsync(ProxyRoute route, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Kind == ProxyKind.None)
        {
            throw new ArgumentException("Direct route has no proxy to connect through.", nameof(route));
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(route.Host, route.Port, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            string host = route.ProxyDns ? targetHost : await ResolveLocallyAsync(targetHost, cancellationToken).ConfigureAwait(false);
            if (route.Kind == ProxyKind.Http)
            {
                await HttpConnectAsync(stream, route, host, targetPort, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Socks5ConnectAsync(stream, route, host, targetPort, cancellationToken).ConfigureAwait(false);
            }
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>本地 DNS:目标已是 IP 字面量则原样返回;否则解析并优先取 IPv4。</summary>
    internal static async Task<string> ResolveLocallyAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress? pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return pick?.ToString()
            ?? throw new IOException(Strings.Format("Msg_ProxyConnectFailed", $"DNS lookup for '{host}' returned no addresses"));
    }

    // ———— HTTP CONNECT ————

    private static async Task HttpConnectAsync(Stream stream, ProxyRoute route, string host, int port, CancellationToken cancellationToken)
    {
        string authority = $"{ProxyResolver.FormatHost(host)}:{port}";
        StringBuilder request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n");
        if (route.HasCredentials)
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{route.Username}:{route.Password}"));
            request.Append("Proxy-Authorization: Basic ").Append(basic).Append("\r\n");
        }
        request.Append("\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        string response = await ReadHttpResponseHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        int status = ParseHttpStatus(response);
        if (status == 407)
        {
            throw new IOException(Strings.Get("Msg_ProxyAuthFailed"));
        }
        if (status is < 200 or > 299)
        {
            string statusLine = response[..response.IndexOf('\r')];
            throw new IOException(Strings.Format("Msg_ProxyConnectFailed", statusLine));
        }
    }

    /// <summary>
    /// 逐字节读到首个 CRLFCRLF 为止,绝不多读:2xx 之后隧道即刻透明,
    /// 对端(如 SSH 服务器的版本横幅)可能先于我们发数据,多读一个字节就吞掉了隧道流量。
    /// </summary>
    private static async Task<string> ReadHttpResponseHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>(256);
        byte[] one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "connection closed during HTTP CONNECT handshake"));
            }
            head.Add(one[0]);
            if (head.Count >= 4
                && head[^4] == (byte)'\r' && head[^3] == (byte)'\n'
                && head[^2] == (byte)'\r' && head[^1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString([.. head]);
            }
            if (head.Count > 64 * 1024)
            {
                throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "HTTP CONNECT response headers exceed 64 KB"));
            }
        }
    }

    private static int ParseHttpStatus(string response)
    {
        // "HTTP/1.1 200 Connection established"
        string[] parts = response.Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out int status)
            ? status
            : throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "malformed HTTP CONNECT response"));
    }

    // ———— SOCKS5(RFC 1928;用户名密码子协商 RFC 1929) ————

    private static async Task Socks5ConnectAsync(Stream stream, ProxyRoute route, string host, int port, CancellationToken cancellationToken)
    {
        // 方法协商:0x00 无认证;带凭据时另提供 0x02 用户名密码。
        byte[] greeting = route.HasCredentials ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
        byte[] choice = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (choice[0] != 0x05)
        {
            throw new IOException(Strings.Format("Msg_ProxyConnectFailed", $"not a SOCKS5 server (version byte 0x{choice[0]:X2})"));
        }
        switch (choice[1])
        {
            case 0x00:
                break;
            case 0x02 when route.HasCredentials:
                await Socks5AuthenticateAsync(stream, route, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new IOException(choice[1] == 0xFF && !route.HasCredentials
                    ? Strings.Get("Msg_ProxyAuthFailed")
                    : Strings.Format("Msg_ProxyConnectFailed", $"SOCKS5 server selected unsupported auth method 0x{choice[1]:X2}"));
        }

        byte[] request = BuildSocks5ConnectRequest(host, port);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        byte[] head = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (head[1] != 0x00)
        {
            throw new IOException(Strings.Format("Msg_ProxyConnectFailed", Socks5ReplyMessage(head[1])));
        }
        // 读完 BND.ADDR + BND.PORT,恰好耗尽应答、不越界进隧道数据。
        int remaining = head[3] switch
        {
            0x01 => 4 + 2,
            0x04 => 16 + 2,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0] + 2,
            _ => throw new IOException(Strings.Format("Msg_ProxyConnectFailed", $"SOCKS5 reply has unknown address type 0x{head[3]:X2}")),
        };
        await ReadExactAsync(stream, remaining, cancellationToken).ConfigureAwait(false);
    }

    private static async Task Socks5AuthenticateAsync(Stream stream, ProxyRoute route, CancellationToken cancellationToken)
    {
        byte[] user = Encoding.UTF8.GetBytes(route.Username);
        byte[] pass = Encoding.UTF8.GetBytes(route.Password);
        if (user.Length > 255 || pass.Length > 255)
        {
            throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "SOCKS5 username/password exceeds 255 bytes"));
        }
        byte[] message = new byte[3 + user.Length + pass.Length];
        message[0] = 0x01;
        message[1] = (byte)user.Length;
        user.CopyTo(message, 2);
        message[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(message, 3 + user.Length);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        byte[] reply = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (reply[1] != 0x00)
        {
            throw new IOException(Strings.Get("Msg_ProxyAuthFailed"));
        }
    }

    internal static byte[] BuildSocks5ConnectRequest(string host, int port)
    {
        byte[] address;
        byte addressType;
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            address = ip.GetAddressBytes();
            addressType = ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01;
        }
        else
        {
            byte[] name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255)
            {
                throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "target hostname exceeds 255 bytes"));
            }
            address = new byte[1 + name.Length];
            address[0] = (byte)name.Length;
            name.CopyTo(address, 1);
            addressType = 0x03;
        }
        byte[] request = new byte[4 + address.Length + 2];
        request[0] = 0x05; // VER
        request[1] = 0x01; // CMD = CONNECT
        request[2] = 0x00; // RSV
        request[3] = addressType;
        address.CopyTo(request, 4);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        return request;
    }

    private static string Socks5ReplyMessage(byte rep) =>
        rep switch
        {
            0x01 => "SOCKS5: general server failure",
            0x02 => "SOCKS5: connection not allowed by ruleset",
            0x03 => "SOCKS5: network unreachable",
            0x04 => "SOCKS5: host unreachable",
            0x05 => "SOCKS5: connection refused by destination",
            0x06 => "SOCKS5: TTL expired",
            0x07 => "SOCKS5: command not supported",
            0x08 => "SOCKS5: address type not supported",
            _ => $"SOCKS5: reply code 0x{rep:X2}",
        };

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException(Strings.Format("Msg_ProxyConnectFailed", "connection closed during SOCKS5 handshake"));
            }
            read += n;
        }
        return buffer;
    }
}

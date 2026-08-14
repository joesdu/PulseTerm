using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Infrastructure.Net;

namespace VelaShell.Infrastructure.Tests.Net;

/// <summary>
/// 统一代理层:HTTP CONNECT 与 SOCKS5 握手对着 RFC 字节序列断言(期望值为地面真值,
/// 不用被测代码自证),环回中继与解析器覆盖直连/环回豁免/配置校验/系统代理折算。
/// </summary>
[TestClass]
[TestCategory("Proxy")]
public class ProxySupportTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    // ———— SOCKS5 请求字节的地面真值(RFC 1928 §4) ————

    /// <summary>域名目标:VER=05 CMD=01 RSV=00 ATYP=03 LEN 域名 PORT(网络序)。</summary>
    [TestMethod]
    public void Socks5ConnectRequest_DomainTarget_MatchesRfc1928Bytes()
    {
        byte[] request = ProxyStreamConnector.BuildSocks5ConnectRequest("example.com", 22);
        byte[] expected =
        [
            0x05, 0x01, 0x00, 0x03, 0x0B,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l',
            (byte)'e', (byte)'.', (byte)'c', (byte)'o', (byte)'m',
            0x00, 0x16,
        ];
        Assert.AreSequenceEqual(expected, request);
    }

    /// <summary>IPv4 目标:ATYP=01 + 4 字节地址。192.0.2.1:8080 = C0 00 02 01 / 1F 90。</summary>
    [TestMethod]
    public void Socks5ConnectRequest_IPv4Target_MatchesRfc1928Bytes()
    {
        byte[] request = ProxyStreamConnector.BuildSocks5ConnectRequest("192.0.2.1", 8080);
        byte[] expected = [0x05, 0x01, 0x00, 0x01, 0xC0, 0x00, 0x02, 0x01, 0x1F, 0x90];
        Assert.AreSequenceEqual(expected, request);
    }

    // ———— SOCKS5 完整握手(假代理服务器) ————

    /// <summary>无认证 + 远端 DNS:方法协商只报 0x00,请求发域名(ATYP=03),隧道立通(横幅可读)。</summary>
    [TestMethod]
    public async Task Socks5_NoAuth_RemoteDns_TunnelDelivensBanner()
    {
        using CancellationTokenSource cts = Deadline();
        var greeting = new TaskCompletionSource<byte[]>();
        var request = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            greeting.SetResult(await ReadAsync(s, 3, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            request.SetResult(await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-Fake\r\n"), cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port);
        await using Stream tunnel = await ProxyStreamConnector.ConnectAsync(route, "target.example", 2222, cts.Token);

        Assert.AreSequenceEqual(new byte[] { 0x05, 0x01, 0x00 }, await greeting.Task);
        byte[] expectedRequest =
        [
            0x05, 0x01, 0x00, 0x03, 0x0E,
            .. Encoding.ASCII.GetBytes("target.example"),
            0x08, 0xAE, // 2222
        ];
        Assert.AreSequenceEqual(expectedRequest, await request.Task);
        Assert.AreEqual("SSH-2.0-Fake\r\n", Encoding.ASCII.GetString(await ReadAsync(tunnel, 14, cts.Token)));
    }

    /// <summary>带凭据:方法列表含 0x02,子协商按 RFC 1929 发 [01 ulen user plen pass]。</summary>
    [TestMethod]
    public async Task Socks5_WithCredentials_SendsRfc1929Subnegotiation()
    {
        using CancellationTokenSource cts = Deadline();
        var greeting = new TaskCompletionSource<byte[]>();
        var auth = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            greeting.SetResult(await ReadAsync(s, 4, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x02 }, cts.Token);
            auth.SetResult(await ReadAsync(s, 3 + 2 + 6, cts.Token));
            await s.WriteAsync(new byte[] { 0x01, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, "us", "secret");
        await using Stream tunnel = await ProxyStreamConnector.ConnectAsync(route, "target.example", 22, cts.Token);

        Assert.AreSequenceEqual(new byte[] { 0x05, 0x02, 0x00, 0x02 }, await greeting.Task);
        byte[] expectedAuth =
        [
            0x01, 0x02, (byte)'u', (byte)'s',
            0x06, .. Encoding.ASCII.GetBytes("secret"),
        ];
        Assert.AreSequenceEqual(expectedAuth, await auth.Task);
    }

    /// <summary>认证被拒(RFC 1929 status != 0)必须抛错,不得带着未认证的链路继续。</summary>
    [TestMethod]
    public async Task Socks5_CredentialsRejected_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 4, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x02 }, cts.Token);
            await ReadAsync(s, 3 + 1 + 5, cts.Token);
            await s.WriteAsync(new byte[] { 0x01, 0x01 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, "u", "wrong");
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            ProxyStreamConnector.ConnectAsync(route, "target.example", 22, cts.Token));
    }

    /// <summary>关闭「使用代理执行 DNS 查找」:本地解析后发 IP(localhost → ATYP=01 127.0.0.1)。</summary>
    [TestMethod]
    public async Task Socks5_LocalDns_SendsResolvedAddressInsteadOfHostname()
    {
        using CancellationTokenSource cts = Deadline();
        var request = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            request.SetResult(await ReadAsync(s, 4 + 4 + 2, cts.Token));
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port, ProxyDns: false);
        await using Stream tunnel = await ProxyStreamConnector.ConnectAsync(route, "localhost", 2222, cts.Token);

        Assert.AreSequenceEqual(
            new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0x08, 0xAE },
            await request.Task);
    }

    /// <summary>代理拒绝连接(REP != 0)必须抛错。</summary>
    [TestMethod]
    public async Task Socks5_ConnectRefusedByProxy_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            ProxyStreamConnector.ConnectAsync(route, "target.example", 22, cts.Token));
    }

    // ———— HTTP CONNECT ————

    /// <summary>
    /// 带认证的 CONNECT 请求头正确;应答头与紧随其后的隧道首包在同一次发送里到达时,
    /// 握手读取不得越界吞掉隧道数据(SSH 横幅必须能从返回的流里完整读出)。
    /// </summary>
    [TestMethod]
    public async Task HttpConnect_SendsAuthHeader_AndDoesNotOverreadTunnelBytes()
    {
        using CancellationTokenSource cts = Deadline();
        var request = new TaskCompletionSource<string>();
        await using var server = new FakeServer(async s =>
        {
            request.SetResult(await ReadHttpHeadAsync(s, cts.Token));
            byte[] burst = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 Connection established\r\nProxy-Agent: Fake\r\n\r\nSSH-2.0-Fake\r\n");
            await s.WriteAsync(burst, cts.Token); // 应答与横幅一次写出,专门制造越界读的机会
        });

        var route = new ProxyRoute(ProxyKind.Http, "127.0.0.1", server.Port, "user", "pa:ss");
        await using Stream tunnel = await ProxyStreamConnector.ConnectAsync(route, "target.example", 22, cts.Token);

        string head = await request.Task;
        Assert.StartsWith("CONNECT target.example:22 HTTP/1.1\r\n", head);
        Assert.Contains("Host: target.example:22\r\n", head);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pa:ss"));
        Assert.Contains($"Proxy-Authorization: Basic {basic}\r\n", head);
        Assert.AreEqual("SSH-2.0-Fake\r\n", Encoding.ASCII.GetString(await ReadAsync(tunnel, 14, cts.Token)));
    }

    /// <summary>非 2xx 应答(如 502)必须抛错。</summary>
    [TestMethod]
    public async Task HttpConnect_NonSuccessStatus_Throws()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadHttpHeadAsync(s, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n"), cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Http, "127.0.0.1", server.Port);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            ProxyStreamConnector.ConnectAsync(route, "target.example", 22, cts.Token));
    }

    // ———— 环回中继 ————

    /// <summary>客户端连中继端口 → 中继经 SOCKS5 打通目标 → 双向转发原样传输。</summary>
    [TestMethod]
    public async Task LoopbackRelay_ForwardsBothDirectionsThroughProxy()
    {
        using CancellationTokenSource cts = Deadline();
        await using var server = new FakeServer(async s =>
        {
            await ReadAsync(s, 3, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);
            await ReadAsync(s, 4 + 1 + 14 + 2, cts.Token);
            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
            await s.WriteAsync(Encoding.ASCII.GetBytes("hello"), cts.Token);
            byte[] echo = await ReadAsync(s, 4, cts.Token);
            await s.WriteAsync(echo, cts.Token);
        });

        var route = new ProxyRoute(ProxyKind.Socks5, "127.0.0.1", server.Port);
        using var relay = LoopbackProxyRelay.Start(route, "target.example", 2222);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.Port, cts.Token);
        NetworkStream stream = client.GetStream();

        Assert.AreEqual("hello", Encoding.ASCII.GetString(await ReadAsync(stream, 5, cts.Token)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"), cts.Token);
        Assert.AreEqual("ping", Encoding.ASCII.GetString(await ReadAsync(stream, 4, cts.Token)));
        Assert.IsNull(relay.Error);
    }

    // ———— 解析器 ————

    [TestMethod]
    public void Resolver_NoneType_ReturnsDirect()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions());
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("example.com", 22).Kind);
    }

    [TestMethod]
    public void Resolver_ExplicitHttp_CarriesEndpointCredentialsAndDnsFlag()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions
        {
            Type = "http", Host = " proxy.example ", Port = 3128,
            Username = "u", Password = "p", ProxyDns = false,
        });
        ProxyRoute route = resolver.Resolve("example.com", 22);
        Assert.AreEqual(new ProxyRoute(ProxyKind.Http, "proxy.example", 3128, "u", "p", false), route);
    }

    /// <summary>环回目标永不走代理(代理自身的中继与本机实验环境依赖这一点)。</summary>
    [TestMethod]
    public void Resolver_LoopbackTarget_BypassesEvenWithExplicitProxy()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("127.0.0.1", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("localhost", 22).Kind);
        Assert.AreEqual(ProxyKind.None, resolver.Resolve("::1", 22).Kind);
    }

    /// <summary>用户显式开代理但配置不完整:必须抛错,绝不静默直连泄漏流量。</summary>
    [TestMethod]
    public void Resolver_EnabledButIncomplete_Throws()
    {
        ProxyResolver noHost = CreateResolver(new ProxyOptions { Type = "http", Host = "", Port = 8080 });
        Assert.ThrowsExactly<InvalidOperationException>(() => noHost.Resolve("example.com", 22));
        ProxyResolver badPort = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 0 });
        Assert.ThrowsExactly<InvalidOperationException>(() => badPort.Resolve("example.com", 22));
    }

    /// <summary>system 档按系统代理折算;命中 bypass 列表时直连。</summary>
    [TestMethod]
    public void Resolver_SystemType_FollowsCapturedSystemProxy()
    {
        try
        {
            ProxyResolver.SystemProxySource = new WebProxy("http://sysproxy.example:8080")
            {
                // WebProxy 的 bypass 正则匹配的是完整 URI(如 https://bypassed.example:443/),不能锚定纯主机名。
                BypassList = [@"bypassed\.example"],
            };
            ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "system", ProxyDns = false });

            ProxyRoute route = resolver.Resolve("example.com", 443);
            Assert.AreEqual(ProxyKind.Http, route.Kind);
            Assert.AreEqual("sysproxy.example", route.Host);
            Assert.AreEqual(8080, route.Port);
            Assert.IsFalse(route.ProxyDns);

            Assert.AreEqual(ProxyKind.None, resolver.Resolve("bypassed.example", 443).Kind);
        }
        finally
        {
            ProxyResolver.SystemProxySource = null;
        }
    }

    /// <summary>HttpClient 适配:socks5/http 产出对应 scheme 的代理 URI,直连目标报告绕过。</summary>
    [TestMethod]
    public void VelaWebProxy_MapsRouteToProxyUri()
    {
        ProxyResolver resolver = CreateResolver(new ProxyOptions { Type = "socks5", Host = "proxy.example", Port = 1080 });
        var webProxy = new VelaWebProxy(resolver);
        Assert.AreEqual(new Uri("socks5://proxy.example:1080"), webProxy.GetProxy(new Uri("https://api.github.com/")));
        Assert.IsFalse(webProxy.IsBypassed(new Uri("https://api.github.com/")));
        Assert.IsTrue(webProxy.IsBypassed(new Uri("http://127.0.0.1:8384/")));
    }

    // ———— 基建 ————

    private static ProxyResolver CreateResolver(ProxyOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { Proxy = options });
        return new ProxyResolver(settings);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                throw new IOException($"connection closed after {read}/{count} bytes");
            }
            read += n;
        }
        return buffer;
    }

    private static async Task<string> ReadHttpHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        byte[] one = new byte[1];
        while (head.Count < 4
               || !(head[^4] == (byte)'\r' && head[^3] == (byte)'\n' && head[^2] == (byte)'\r' && head[^1] == (byte)'\n'))
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (n == 0)
            {
                throw new IOException("connection closed before end of HTTP headers");
            }
            head.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. head]);
    }

    /// <summary>环回假代理服务器:接受一条连接并执行脚本;脚本异常在 Dispose 时抛回测试。</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _run;

        public int Port { get; }

        public FakeServer(Func<NetworkStream, Task> script)
        {
            _listener.Start(1);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(script);
        }

        private async Task RunAsync(Func<NetworkStream, Task> script)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();
            await script(stream);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            {
                // 客户端因断言失败提前断开时的正常收尾噪声。
            }
            catch (TimeoutException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tmds.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 协商失败后回探对端算法名单。
/// </summary>
/// <remarks>
/// 这里的期望值不是编出来的:名单取自一台真实的国产堡垒机网关(<c>SSH-2.0-9.9.9</c>)——
/// 它只提供 <c>aes128-ctr</c> 与 <c>ssh-rsa</c>,正是 Tmds.Ssh 一个都不支持、用户只看到
/// 一句 <c>KeyExchangeFailed</c> 的那种对端。假服务端按 RFC 4253 §6 的分组格式回放这份名单,
/// 断言落在"解出来的算法名"和"诊断文案里到底提了哪几类",而不是解析器自己的中间状态。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SshAlgorithmProbeTests
{
    private const string BastionVersion = "SSH-2.0-9.9.9";
    private static readonly string[] BastionKex =
        ["ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521"];
    private static readonly string[] BastionHostKey = ["ssh-rsa"];
    private static readonly string[] BastionEncryption = ["aes128-ctr"];
    private static readonly string[] BastionMac = ["hmac-sha2-256", "hmac-sha2-512"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Probe_ReadsEveryListTheServerAdvertises()
    {
        await using FakeSshServer server = FakeSshServer.Start((stream, ct) => ServeBastionKexInitAsync(stream, ct));

        SshPeerAlgorithms peer = (await SshAlgorithmProbe.TryProbeAsync(
            IPAddress.Loopback.ToString(), server.Port, ProbeTimeout, TestContext.CancellationTokenSource.Token))!;

        Assert.AreEqual(BastionVersion, peer.ServerVersion);
        CollectionAssert.AreEqual(BastionKex, peer.KeyExchange.ToArray());
        CollectionAssert.AreEqual(BastionHostKey, peer.HostKey.ToArray());
        CollectionAssert.AreEqual(BastionEncryption, peer.EncryptionServerToClient.ToArray());
        CollectionAssert.AreEqual(BastionEncryption, peer.EncryptionClientToServer.ToArray());
        CollectionAssert.AreEqual(BastionMac, peer.MacServerToClient.ToArray());
    }

    [TestMethod]
    public async Task Probe_SkipsTheBannerLinesBeforeTheVersionString()
    {
        // 堡垒机与政企设备普遍在版本串之前先甩一段法律声明;认死第一行就什么也探不到了。
        await using FakeSshServer server = FakeSshServer.Start(
            (stream, ct) => ServeBastionKexInitAsync(stream, ct, banner: "*** 授权用户专用,操作全程审计 ***"));

        SshPeerAlgorithms peer = (await SshAlgorithmProbe.TryProbeAsync(
            IPAddress.Loopback.ToString(), server.Port, ProbeTimeout, TestContext.CancellationTokenSource.Token))!;

        Assert.AreEqual(BastionVersion, peer.ServerVersion);
        CollectionAssert.AreEqual(BastionEncryption, peer.EncryptionServerToClient.ToArray());
    }

    [TestMethod]
    public async Task Probe_EndpointThatIsNotSsh_ReturnsNullInsteadOfThrowing()
    {
        // 端口填错(填到 HTTP 或 TLS 上)时诊断只该沉默,不能再抛一个异常盖掉真正的失败。
        await using FakeSshServer server = FakeSshServer.Start(async (stream, ct) =>
        {
            await WriteLineAsync(stream, "HTTP/1.1 400 Bad Request", ct);
            await WriteLineAsync(stream, "Content-Length: 0", ct);
        });

        Assert.IsNull(await SshAlgorithmProbe.TryProbeAsync(
                          IPAddress.Loopback.ToString(), server.Port, ProbeTimeout,
                          TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task Probe_PacketCutShort_ReturnsNullInsteadOfThrowing()
    {
        await using FakeSshServer server = FakeSshServer.Start(async (stream, ct) =>
        {
            await WriteLineAsync(stream, BastionVersion, ct);
            await ReadLineAsync(stream, ct);
            // 声称有 1000 字节却只发了包头就断开。
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, 1000);
            await stream.WriteAsync(header, ct);
        });

        Assert.IsNull(await SshAlgorithmProbe.TryProbeAsync(
                          IPAddress.Loopback.ToString(), server.Port, ProbeTimeout,
                          TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task Probe_NothingListening_ReturnsNullInsteadOfThrowing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.IsNull(await SshAlgorithmProbe.TryProbeAsync(
                          IPAddress.Loopback.ToString(), port, ProbeTimeout,
                          TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task Describe_ReportsOnlyTheAlgorithmClassesWithNoOverlap()
    {
        await using FakeSshServer server = FakeSshServer.Start((stream, ct) => ServeBastionKexInitAsync(stream, ct));
        var settings = new SshClientSettings("probe@127.0.0.1")
        {
            HostName = IPAddress.Loopback.ToString(),
            Port = server.Port
        };

        string message = (await SshAlgorithmDiagnostics.TryDescribeAsync(
            settings, TestContext.CancellationTokenSource.Token))!;

        Assert.Contains(BastionVersion, message, "对端版本要摆出来 —— 用户和厂商对线时就靠这一句。");
        Assert.Contains("aes128-ctr", message, "对端提供的加密算法必须原样列出。");
        Assert.Contains("ssh-rsa", message, "主机密钥同样没有交集(我们只认 rsa-sha2-*),该报。");
        Assert.Contains("chacha20-poly1305@openssh.com", message,
                        "只说对端提供什么没用,得同时告诉用户本客户端支持什么才知道该改成什么。");
        // 这两类是有交集的:kex 双方都有 ecdh-sha2-nistp256,MAC 双方都有 hmac-sha2-256。
        // 把它们也列进去,用户就会去改本来没问题的配置。
        Assert.DoesNotContain("ecdh-", message, "密钥交换有交集,不该出现在失败原因里。");
        Assert.DoesNotContain("hmac-", message, "MAC 有交集,不该出现在失败原因里。");
        // 证书变体只在对端出示 OpenSSH 证书时才用得上,列出来纯粹是把六个可用算法淹掉。
        Assert.DoesNotContain("-cert-", message, "证书变体是噪音,不该出现在「本客户端支持」里。");
        // 双方名单各占一行:挤在一行里换行之后就成了一团,对不上"我这边有没有对端要的那个"。
        Assert.IsGreaterThanOrEqualTo(5, message.Split('\n').Length,
                                      "标题 + 两类各三行:名单必须分行,不能挤成一坨。");
    }

    [TestMethod]
    public async Task Describe_PeerThatShareseverything_ReturnsNull()
    {
        // 协商失败另有原因(比如对端在 KEXINIT 之后才出问题)时不能硬凑一段说明:
        // 指着一堆其实能用的算法说"没有交集"比不说更糟。
        await using FakeSshServer server = FakeSshServer.Start((stream, ct) =>
            ServeKexInitAsync(stream, ct, BastionVersion, BastionKex,
                              ["rsa-sha2-256"], ["chacha20-poly1305@openssh.com"], ["hmac-sha2-256"]));
        var settings = new SshClientSettings("probe@127.0.0.1")
        {
            HostName = IPAddress.Loopback.ToString(),
            Port = server.Port
        };

        Assert.IsNull(await SshAlgorithmDiagnostics.TryDescribeAsync(
                          settings, TestContext.CancellationTokenSource.Token));
    }

    public TestContext TestContext { get; set; } = null!;

    private static Task ServeBastionKexInitAsync(NetworkStream stream, CancellationToken ct, string? banner = null) =>
        ServeKexInitAsync(stream, ct, BastionVersion, BastionKex, BastionHostKey, BastionEncryption, BastionMac, banner);

    /// <summary>假服务端:banner(可选) → 版本串 → 等客户端的版本串 → 一个 KEXINIT。</summary>
    /// <remarks>
    /// 刻意在发 KEXINIT 之前先读一行:探针若不发自己的版本串,这里就永远等下去,
    /// 测试会以超时而不是"碰巧通过"的方式失败。
    /// </remarks>
    private static async Task ServeKexInitAsync(
        NetworkStream stream, CancellationToken ct, string version,
        string[] kex, string[] hostKey, string[] encryption, string[] mac, string? banner = null)
    {
        if (banner is not null)
        {
            await WriteLineAsync(stream, banner, ct);
        }
        await WriteLineAsync(stream, version, ct);
        await ReadLineAsync(stream, ct);
        await stream.WriteAsync(BuildKexInit(kex, hostKey, encryption, mac), ct);
    }

    /// <summary>按 RFC 4253 §6 拼一个明文分组包,载荷是 §7.1 的 SSH_MSG_KEXINIT。</summary>
    private static byte[] BuildKexInit(string[] kex, string[] hostKey, string[] encryption, string[] mac)
    {
        List<byte> payload = [20];
        payload.AddRange(new byte[16]); // cookie
        foreach (string[] list in new[] { kex, hostKey, encryption, encryption, mac, mac })
        {
            AddNameList(payload, string.Join(',', list));
        }
        AddNameList(payload, "none");   // 压缩 c2s
        AddNameList(payload, "none");   // 压缩 s2c
        AddNameList(payload, "");       // 语言 c2s
        AddNameList(payload, "");       // 语言 s2c
        payload.Add(0);                 // first_kex_packet_follows
        payload.AddRange(new byte[4]);  // reserved

        // 4(长度) + 1(填充长) + 载荷 + 填充 必须是 8 的倍数,且填充至少 4 字节。
        int padding = (8 - ((5 + payload.Count) % 8)) % 8;
        if (padding < 4)
        {
            padding += 8;
        }
        byte[] packet = new byte[5 + payload.Count + padding];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)(1 + payload.Count + padding));
        packet[4] = (byte)padding;
        payload.CopyTo(packet, 5);
        return packet;
    }

    private static void AddNameList(List<byte> payload, string nameList)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nameList.Length);
        payload.AddRange(length);
        payload.AddRange(Encoding.ASCII.GetBytes(nameList));
    }

    private static Task WriteLineAsync(Stream stream, string line, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct).AsTask();

    private static async Task ReadLineAsync(Stream stream, CancellationToken ct)
    {
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one, ct) == 1 && one[0] != (byte)'\n')
        {
            // 逐字节读到换行为止:版本串阶段还没有分组格式可依。
        }
    }

    /// <summary>只接一条连接的假 SSH 服务端。</summary>
    private sealed class FakeSshServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeSshServer(TcpListener listener, Func<NetworkStream, CancellationToken, Task> handle)
        {
            _listener = listener;
            _loop = RunAsync(handle);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeSshServer Start(Func<NetworkStream, CancellationToken, Task> handle)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeSshServer(listener, handle);
        }

        private async Task RunAsync(Func<NetworkStream, CancellationToken, Task> handle)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using NetworkStream stream = client.GetStream();
                await handle(stream, _cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
                // 测试结束时监听器被关掉,或探针提前断开:都属正常收尾。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // 同上。
            }
            _cts.Dispose();
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Telnet.Tests;

/// <summary>
/// 会话级测试:跑在真实的环回 TCP 之上(<see cref="TcpListener" />),
/// 不是把套接字替换成替身 —— Telnet 的问题多半出在"线上真实字节"这一层
/// (协商时序、IAC 边界、写侧交织),对着内存替身跑等于没测。
/// </summary>
[TestClass]
public sealed class TelnetSessionTests
{
    /// <summary>一台只说 Telnet 的迷你服务端:接一个连接,原样记录收到的字节,按需回写。</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<TcpClient> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte> _received = [];
        private readonly Lock _gate = new();
        private TcpClient? _client;
        private NetworkStream? _stream;

        public LoopbackServer()
        {
            _listener = new(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _client = client;
                _stream = client.GetStream();
                _accepted.TrySetResult(client);
                byte[] buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        int read = await _stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }
                        lock (_gate)
                        {
                            _received.AddRange(buffer.AsSpan(0, read).ToArray());
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                {
                    // 客户端关闭:正常收尾。
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task WaitForClientAsync() => await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        public byte[] Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public async Task SendAsync(params byte[] bytes)
        {
            await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _stream!.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>等到服务端收到的字节里出现某个子序列(协商是异步的,不能靠 sleep 赌)。</summary>
        public async Task WaitForBytesAsync(byte[] needle, string because)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (IndexOf(Received, needle) >= 0)
                {
                    return;
                }
                await Task.Delay(15).ConfigureAwait(false);
            }
            Assert.Fail($"{because};实际收到:{BitConverter.ToString(Received)}");
        }

        public static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int index = 0; index + needle.Length <= haystack.Length; index++)
            {
                if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
                {
                    return index;
                }
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            _listener.Stop();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static async Task<IProtocolTerminalSession> ConnectAsync(
        LoopbackServer server,
        Dictionary<string, string>? settings = null)
    {
        var context = new TestPluginContext { PluginId = "velashell.telnet" };
        var terminal = new TelnetTerminal(context);
        var request = new ProtocolConnectRequest
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Settings = settings ?? [with(StringComparer.Ordinal)]
        };
        return await terminal.ConnectAsync(request, new("xterm-256color", 120, 32), CancellationToken.None);
    }

    private static async Task<string> ReadTextAsync(IProtocolTerminalSession session, int expectedLength)
    {
        var text = new StringBuilder();
        byte[] buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (text.Length < expectedLength)
        {
            int read = await session.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    [TestMethod]
    public async Task Connect_SendsInitialNegotiation_AndAnswersTerminalTypeAndNaws()
    {
        await using var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server);
        await server.WaitForClientAsync();

        // 连上就该主动发出第一批协商(而不是等对端先开口 —— 有的设备一直不开口)。
        await server.WaitForBytesAsync([255, 251, 24], "连接后应主动 WILL TERMINAL-TYPE");
        await server.WaitForBytesAsync([255, 251, 31], "连接后应主动 WILL NAWS");

        // 对端 DO NAWS → 立刻回 WILL + 当前尺寸(120x32)。
        await server.SendAsync(255, 253, 31);
        await server.WaitForBytesAsync([255, 250, 31, 0, 120, 0, 32, 255, 240], "DO NAWS 后应立即上报当前窗口尺寸");

        // 对端 DO TERMINAL-TYPE 然后 SEND → 回 IS xterm-256color。
        await server.SendAsync(255, 253, 24);
        await server.SendAsync(255, 250, 24, 1, 255, 240);
        byte[] expected = [255, 250, 24, 0, .. Encoding.ASCII.GetBytes("xterm-256color"), 255, 240];
        await server.WaitForBytesAsync(expected, "TERMINAL-TYPE SEND 应回 IS <term>");
    }

    [TestMethod]
    public async Task Read_StripsProtocolBytes_AndRestoresDoubledIac()
    {
        await using var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server);
        await server.WaitForClientAsync();

        // 协商字节夹在数据中间:必须被吃掉,而 IAC IAC 还原成一个 0xFF。
        await server.SendAsync((byte)'h', 255, 251, 3, (byte)'i', 255, 255, (byte)'!');
        byte[] buffer = new byte[16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<byte>();
        while (received.Count < 4)
        {
            int read = await session.ReadAsync(buffer, timeout.Token);
            Assert.IsGreaterThan(0, read);
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        Assert.AreSequenceEqual(new byte[] { (byte)'h', (byte)'i', 0xFF, (byte)'!' }, [.. received]);
    }

    [TestMethod]
    public async Task Write_WithoutRemoteEcho_EchoesLocally_SoTypingIsNeverInvisible()
    {
        await using var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server);
        await server.WaitForClientAsync();

        await session.WriteAsync(Encoding.ASCII.GetBytes("ls"));
        Assert.AreEqual("ls", await ReadTextAsync(session, 2), "对端未接管回显时,输入必须由本地回显出来。");
        await server.WaitForBytesAsync(Encoding.ASCII.GetBytes("ls"), "输入仍要真的发到线上");
    }

    [TestMethod]
    public async Task Write_AfterServerTakesOverEcho_DoesNotEchoLocally()
    {
        await using var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server);
        await server.WaitForClientAsync();

        // 对端 WILL ECHO 之后本地不能再回显,否则用户看到的是双份字符("llss")。
        await server.SendAsync(255, 251, 1);
        await server.WaitForBytesAsync([255, 253, 1], "对端 WILL ECHO 后应回 DO ECHO");
        await session.WriteAsync(Encoding.ASCII.GetBytes("ls"));

        await server.SendAsync("X"u8.ToArray());
        Assert.AreEqual("X", await ReadTextAsync(session, 1), "读到的第一个字符应是对端的回显,而不是本地回显的 'l'。");
    }

    [TestMethod]
    public async Task Read_ReturnsZero_WhenTheServerClosesTheConnection()
    {
        // 掉线必须归一化成 EOF:宿主据此把标签置为"已断开、可重连",
        // 抛异常只会让读循环带着异常收尾。
        var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server);
        await server.WaitForClientAsync();
        await server.DisposeAsync();

        byte[] buffer = new byte[16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await session.ReadAsync(buffer, timeout.Token);
        Assert.AreEqual(0, read);
    }

    [TestMethod]
    public async Task EnterKey_IsRewrittenPerSettings_WhenBinaryIsRefused()
    {
        await using var server = new LoopbackServer();
        await using IProtocolTerminalSession session = await ConnectAsync(server, new(StringComparer.Ordinal)
        {
            [TelnetFields.Binary] = "false",
            [TelnetFields.EnterMode] = "crnul"
        });
        await server.WaitForClientAsync();
        await session.WriteAsync("\r"u8.ToArray());
        await server.WaitForBytesAsync([0x0D, 0x00], "非 BINARY + CR NUL 模式下回车应发 CR NUL");
    }
}

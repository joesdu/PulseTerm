using System.Buffers.Binary;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 动态转发监听端的 SOCKS5 服务端握手(RFC 1928)。用例喂的是**客户端侧**
/// <see cref="ProxyStreamConnector.BuildSocks5ConnectRequest" /> 生成的字节:那份实现另有
/// 对着 RFC 逐字节的断言(ProxySupportTests),拿它当地面真值,服务端就不会自说自话地
/// 与客户端一起跑偏。
/// </summary>
[TestClass]
[TestCategory("Tunnel")]
public class Socks5NegotiationTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));

    /// <summary>域名目标解析:握手完成后拿到客户端要求连的主机与端口。</summary>
    [TestMethod]
    public async Task AcceptRequest_ParsesDomainTarget()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x05, 0x01, 0x00]); // VER, NMETHODS=1, NO-AUTH
        stream.WriteInbound(ProxyStreamConnector.BuildSocks5ConnectRequest("db.internal", 5432));

        (string host, int port) = await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token);

        Assert.AreEqual("db.internal", host);
        Assert.AreEqual(5432, port);
        Assert.AreSequenceEqual(new byte[] { 0x05, 0x00 }, stream.ReadOutbound(2), "应先回一条选定 NO-AUTH 的方法应答。");
    }

    /// <summary>IPv4 目标解析:地址按四字节网络序还原。</summary>
    [TestMethod]
    public async Task AcceptRequest_ParsesIPv4Target()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x05, 0x02, 0x00, 0x02]); // 同时提供 NO-AUTH 与用户名口令
        stream.WriteInbound(ProxyStreamConnector.BuildSocks5ConnectRequest("10.0.0.7", 8080));

        (string host, int port) = await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token);

        Assert.AreEqual("10.0.0.7", host);
        Assert.AreEqual(8080, port);
    }

    /// <summary>客户端一个可用方法都不给时回 0xFF 并中止,而不是硬着头皮往下走。</summary>
    [TestMethod]
    public async Task AcceptRequest_RejectsWhenNoAcceptableMethod()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x05, 0x01, 0x02]); // 只提供用户名/口令认证

        await Assert.ThrowsExactlyAsync<Socks5ProtocolException>(
            async () => await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token));

        Assert.AreSequenceEqual(new byte[] { 0x05, 0xFF }, stream.ReadOutbound(2));
    }

    /// <summary>非 CONNECT 命令(BIND / UDP ASSOCIATE)回 0x07,并把请求判死。</summary>
    [TestMethod]
    public async Task AcceptRequest_RejectsNonConnectCommand()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x05, 0x01, 0x00]);
        stream.WriteInbound([0x05, 0x02, 0x00, 0x01, 10, 0, 0, 7, 0x1F, 0x90]); // CMD=BIND

        await Assert.ThrowsExactlyAsync<Socks5ProtocolException>(
            async () => await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token));

        stream.ReadOutbound(2); // 方法应答
        byte[] reply = stream.ReadOutbound(10);
        Assert.AreEqual(0x07, reply[1], "应回「命令不受支持」。");
    }

    /// <summary>SOCKS4 之类的旧版本直接拒绝,不去猜它想干什么。</summary>
    [TestMethod]
    public async Task AcceptRequest_RejectsUnsupportedVersion()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x04, 0x01, 0x00]);

        await Assert.ThrowsExactlyAsync<Socks5ProtocolException>(
            async () => await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token));
    }

    /// <summary>客户端握手到一半就走了,报协议异常而不是让调用方对着 EndOfStreamException 猜。</summary>
    [TestMethod]
    public async Task AcceptRequest_RejectsTruncatedHandshake()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();
        stream.WriteInbound([0x05, 0x03, 0x00]); // 声称有 3 个方法,只给了 1 个

        await Assert.ThrowsExactlyAsync<Socks5ProtocolException>(
            async () => await Socks5Negotiation.AcceptRequestAsync(stream, deadline.Token));
    }

    /// <summary>成功应答是 10 字节的 VER/REP/RSV/ATYP + 全零 IPv4 端点。</summary>
    [TestMethod]
    public async Task WriteReply_EmitsTenByteSuccess()
    {
        using CancellationTokenSource deadline = Deadline();
        var stream = new DuplexBuffer();

        await Socks5Negotiation.WriteReplyAsync(stream, Socks5Negotiation.ReplySucceeded, deadline.Token);

        byte[] reply = stream.ReadOutbound(10);
        Assert.AreSequenceEqual(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, reply);
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)));
    }

    /// <summary>目标拒绝连接要翻译成 0x05(connection refused),而不是笼统的一般性失败。</summary>
    [TestMethod]
    public void ReplyCodeFor_MapsRefusedToConnectionRefused()
    {
        var refused = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        Assert.AreEqual(Socks5Negotiation.ReplyConnectionRefused, Socks5Negotiation.ReplyCodeFor(refused));
        Assert.AreEqual(Socks5Negotiation.ReplyGeneralFailure, Socks5Negotiation.ReplyCodeFor(new InvalidOperationException()));
    }

    /// <summary>把「客户端写入」与「服务端回写」分成两条独立缓冲的双工流。</summary>
    private sealed class DuplexBuffer : Stream
    {
        private readonly MemoryStream _inbound = new();
        private readonly MemoryStream _outbound = new();
        private long _readPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>放入客户端将要发来的字节。</summary>
        public void WriteInbound(byte[] bytes)
        {
            long resume = _inbound.Position;
            _inbound.Position = _inbound.Length;
            _inbound.Write(bytes);
            _inbound.Position = resume;
        }

        /// <summary>取出服务端回写的下 <paramref name="count" /> 个字节。</summary>
        public byte[] ReadOutbound(int count)
        {
            byte[] all = _outbound.ToArray();
            Assert.IsGreaterThanOrEqualTo((int)_readPosition + count, all.Length, "回写的字节不够读。");
            byte[] slice = all.AsSpan((int)_readPosition, count).ToArray();
            _readPosition += count;
            return slice;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inbound.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_inbound.Read(buffer.Span));

        public override void Write(byte[] buffer, int offset, int count) => _outbound.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _outbound.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

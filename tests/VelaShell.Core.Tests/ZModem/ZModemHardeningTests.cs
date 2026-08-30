using System.Text;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.Tests.FileTransfer;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.ZModem;

/// <summary>
/// 2026-08 一轮 ZMODEM 加固的回归:文件名编码、ZEOF 长度校验、ZSINIT 应答、会话尾字节交还、
/// 大文件保护、以及批量反转义解码路径的正确性。每条用例都对应一个真实会造成用户可见损坏的缺陷。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class ZModemHardeningTests
{
    private static ZModemOptions FastOptions => new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(2),
        HandshakeRetries = 2,
        FrameTimeout = TimeSpan.FromSeconds(3),
        MaxRetries = 2,
        PostCancelDrainIdle = TimeSpan.FromMilliseconds(100),
        PostCancelDrainMax = TimeSpan.FromMilliseconds(300)
    };

    /// <summary>
    /// 远端 <c>sz 中文名.txt</c> 上链的是 UTF-8 字节。手工按 lrzsz 的 ZFILE 布局拼出这段负载,
    /// 解析结果必须是原文件名 —— 曾一律按 Latin1 解,落盘名变成 "ä¸­æ..." 这样的乱码。
    /// </summary>
    [TestMethod]
    public void ParseFileMetadata_DecodesUtf8FileName()
    {
        var payload = new List<byte>();
        payload.AddRange(Encoding.UTF8.GetBytes("中文名 with spaces.txt"));
        payload.Add(0);
        payload.AddRange(Encoding.ASCII.GetBytes("1234 0 644 0 0 0"));
        payload.Add(0);

        TransferFileMetadata metadata = ZModemReceiver.ParseFileMetadata([.. payload]);

        Assert.AreEqual("中文名 with spaces.txt", metadata.FileName);
        Assert.AreEqual(1234L, metadata.Size);
        Assert.AreEqual(0b110_100_100, metadata.UnixMode, "模式字段是八进制 644");
    }

    /// <summary>
    /// 对端用的不是 UTF-8(老系统的 GBK 之类)时,严格 UTF-8 解码会失败,
    /// 必须回退到按字节保真的 Latin1,而不是抛异常或吐一串 U+FFFD 替换符。
    /// </summary>
    [TestMethod]
    public void ParseFileMetadata_FallsBackToByteFaithfulDecodeForNonUtf8()
    {
        // 0xB2 0xE2 是 "测" 的 GBK 编码,单独看不是合法 UTF-8 序列。
        byte[] payload = [0xB2, 0xE2, (byte)'.', (byte)'t', (byte)'x', (byte)'t', 0, (byte)'1', 0];

        TransferFileMetadata metadata = ZModemReceiver.ParseFileMetadata(payload);

        Assert.AreEqual(6, metadata.FileName.Length, "回退解码必须字节对字符一一对应,不能丢字节");
        Assert.AreEqual(0xB2, metadata.FileName[0]);
        Assert.AreEqual(0xE2, metadata.FileName[1]);
        Assert.AreEqual(".txt", metadata.FileName[2..]);
    }

    /// <summary>非 ASCII 文件名要能在我们自己的发送端 → 接收端之间原样往返。</summary>
    [TestMethod]
    public async Task RoundTrip_PreservesNonAsciiFileName()
    {
        byte[] data = Encoding.UTF8.GetBytes("内容");
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receive = new ZModemReceiver(b, sink).ReceiveAsync(cts.Token);
        Task<FileTransferSession> send =
            new ZModemSender(a, new InMemoryFileSource([("测试文件.txt", data)])).SendAsync(cts.Token);
        await Task.WhenAll(receive, send);

        Assert.AreSequenceEqual(["测试文件.txt"], sink.OfferedNames);
        Assert.AreSequenceEqual(data, sink.Completed["测试文件.txt"]);
    }

    /// <summary>
    /// ZEOF 声明的长度与实际收到的字节数不符 = 中间丢了子包。接收方必须回 ZRPOS 要求续发,
    /// 而不是直接收尾 —— 那等于把一个被截断的文件当成功交付,用户毫无察觉。
    /// </summary>
    [TestMethod]
    public async Task ZeofWithWrongLength_TriggersZrposInsteadOfSilentTruncation()
    {
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving = new ZModemReceiver(ours, sink, FastOptions).ReceiveAsync(cts.Token);
        var reader = new ZModemFrameReader(peer);

        await ExpectFrameAsync(reader, ZModemFrameType.ZRINIT, cts.Token);

        // ZFILE + 文件信息子包:声称有 40 字节。
        await WriteHeaderAsync(peer, ZModemHeader.Empty(ZModemFrameType.ZFILE), ZModemHeaderFormat.Binary16, cts.Token);
        byte[] info = TransferFileInfoCodec.Encode("truncated.bin", 40, null, 0, 40);
        await peer.WriteAsync(ZModemSubpacket.Write(info, ZModemSubpacketEnd.EndNoAck, useCrc32: false), cts.Token);

        ZModemHeaderResult rpos = await ExpectFrameAsync(reader, ZModemFrameType.ZRPOS, cts.Token);
        Assert.AreEqual(0u, rpos.Header.Position);

        // 只发 10 字节数据,却在 ZEOF 里声称发了 40 字节。
        await WriteHeaderAsync(
            peer, ZModemHeader.WithPosition(ZModemFrameType.ZDATA, 0), ZModemHeaderFormat.Binary16, cts.Token);
        byte[] chunk = Encoding.ASCII.GetBytes("0123456789");
        await peer.WriteAsync(ZModemSubpacket.Write(chunk, ZModemSubpacketEnd.EndNoAck, useCrc32: false), cts.Token);
        await WriteHeaderAsync(
            peer, ZModemHeader.WithPosition(ZModemFrameType.ZEOF, 40), ZModemHeaderFormat.Binary16, cts.Token);

        ZModemHeaderResult reply = await ExpectFrameAsync(reader, ZModemFrameType.ZRPOS, cts.Token);
        Assert.AreEqual(10u, reply.Header.Position, "应从我们真正收到的偏移续发,而不是当作收完");
        Assert.IsFalse(sink.Completed.ContainsKey("truncated.bin"), "长度对不上的文件不能被标记为完成");

        // 让接收方自己超时收摊,别把测试挂死。
        await peer.DisposeAsync();
        await receiving;
    }

    /// <summary>
    /// <c>sz -e</c> 会先发 ZSINIT 声明转义策略并等一个 ZACK。不应答会让它一路重发到超时放弃,
    /// 表现为「传输死活起不来」。
    /// </summary>
    [TestMethod]
    public async Task Zsinit_IsAcknowledged()
    {
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving = new ZModemReceiver(ours, sink, FastOptions).ReceiveAsync(cts.Token);
        var reader = new ZModemFrameReader(peer);

        await ExpectFrameAsync(reader, ZModemFrameType.ZRINIT, cts.Token);

        // ZSINIT 帧头 + Attn 序列子包(以 ZCRCW 收尾,按规范要求应答)。
        await WriteHeaderAsync(peer, ZModemHeader.Empty(ZModemFrameType.ZSINIT), ZModemHeaderFormat.Binary16, cts.Token);
        await peer.WriteAsync(
            ZModemSubpacket.Write([0x00], ZModemSubpacketEnd.EndAck, useCrc32: false), cts.Token);

        await ExpectFrameAsync(reader, ZModemFrameType.ZACK, cts.Token);

        await peer.DisposeAsync();
        await receiving;
    }

    /// <summary>
    /// 会话结束时,读取器缓冲里剩下的字节(<c>sz</c> 退出后紧跟的 shell 提示符)必须退回通道,
    /// 由路由器交还终端 —— 丢掉它们的表现是「传完了但提示符没了,要按一下回车才回来」。
    /// </summary>
    [TestMethod]
    public async Task SessionEnd_HandsTrailingShellBytesBackToChannel()
    {
        byte[] tail = Encoding.ASCII.GetBytes("\r\nuser@host:~$ ");

        // 把 ZFIN + "OO" + 提示符拼在同一个分片里 —— 真实链路上就是这样一起到达的。
        var inbound = new List<byte>();
        inbound.AddRange(ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZFIN), ZModemHeaderFormat.Hex));
        inbound.AddRange("OO"u8.ToArray());
        inbound.AddRange(tail);

        var duplex = InMemoryByteDuplex.FromInbound([inbound.ToArray()]);
        var sink = new InMemoryFileSink();

        await new ZModemReceiver(duplex, sink, FastOptions).ReceiveAsync(CancellationToken.None);

        // 引擎在收尾时把没消费的字节 Unread 回通道,这里应能重新读出来。
        ReadOnlyMemory<byte> returned = await duplex.ReadAsync(CancellationToken.None);
        Assert.AreSequenceEqual(tail, returned.ToArray(), "提示符字节必须原样退回,而不是随读取器一起丢掉");
    }

    /// <summary>
    /// 数据子包解码走了「批量搬运 + 遇 ZDLE 才逐字节」的快路径,必须对满是转义字节的负载
    /// 依然字节保真 —— 快路径的边界(段首/段尾恰好是 ZDLE)最容易出错。
    /// </summary>
    [TestMethod]
    public async Task SubpacketDecode_IsByteFaithfulForEscapeHeavyPayload()
    {
        // 全部由需要转义的字节构成,并夹杂普通字节,覆盖快路径与逐字节路径的来回切换。
        byte[] payload =
        [
            0x18, 0x10, 0x11, 0x13, 0x90, 0x91, 0x93,
            (byte)'a', (byte)'b',
            0x18, 0x18, 0x11,
            (byte)'c',
            0x7F, 0xFF, 0x00,
            0x13, 0x13, 0x13
        ];

        foreach (bool useCrc32 in (bool[])[false, true])
        {
            byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndNoAck, useCrc32);
            var duplex = InMemoryByteDuplex.FromInbound([wire]);
            var reader = new ZModemFrameReader(duplex);

            ZModemSubpacketResult result = await ZModemSubpacket.ReadAsync(reader, useCrc32, CancellationToken.None);

            Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status, $"crc32={useCrc32}");
            Assert.AreSequenceEqual(payload, result.Data, $"crc32={useCrc32} 时负载不保真");
        }
    }

    /// <summary>负载被链路改了一个字节时,子包 CRC 必须判失败(否则损坏数据会静默落盘)。</summary>
    [TestMethod]
    public async Task SubpacketDecode_DetectsCorruption()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('x', 300));
        byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndNoAck, useCrc32: true);
        wire[100] ^= 0x01;

        var duplex = InMemoryByteDuplex.FromInbound([wire]);
        ZModemSubpacketResult result =
            await ZModemSubpacket.ReadAsync(new ZModemFrameReader(duplex), useCrc32: true, CancellationToken.None);

        Assert.AreEqual(ZModemSubpacketStatus.CrcError, result.Status);
    }

    /// <summary>
    /// ZMODEM 的位置字段是 32 位,&gt;4GiB 的文件表达不了。必须显式跳过并给出理由,
    /// 而不是让偏移悄悄回绕、交付一份被打乱的文件却全程"成功"。
    /// </summary>
    [TestMethod]
    public async Task FileLargerThanFourGiB_IsSkippedWithReason()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receive = new ZModemReceiver(b, sink, FastOptions).ReceiveAsync(cts.Token);
        Task<FileTransferSession> send = new ZModemSender(
            a,
            new OversizedFileSource(),
            FastOptions).SendAsync(cts.Token);
        await Task.WhenAll(receive, send);

        FileTransferItem item = send.Result.Items.Single();
        Assert.AreEqual(FileTransferState.Failed, item.Status);
        StringAssert.Contains(item.ErrorMessage ?? string.Empty, "4 GiB");
        Assert.IsEmpty(sink.OfferedNames, "超限文件根本不该被提供给对端");
    }

    /// <summary>谎称自己有 5 GiB 的文件来源(不会真的被读取,发送端应在打开前就拦下)。</summary>
    private sealed class OversizedFileSource : IFileTransferSource
    {
        public ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OutgoingTransferFile>>(
                [new("/tmp/huge.bin", "huge.bin", 5L * 1024 * 1024 * 1024, null)]);

        public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("超限文件不该被打开");
    }

    private static async Task WriteHeaderAsync(
        InMemoryByteDuplex duplex,
        ZModemHeader header,
        ZModemHeaderFormat format,
        CancellationToken ct)
    {
        await duplex.WriteAsync(ZModemFrameWriter.Write(header, format), ct);
        await duplex.FlushAsync(ct);
    }

    private static async Task<ZModemHeaderResult> ExpectFrameAsync(
        ZModemFrameReader reader,
        ZModemFrameType expected,
        CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            ZModemHeaderResult frame = await reader.ReadHeaderAsync(ct);
            Assert.AreEqual(ZModemReadStatus.Header, frame.Status, $"等 {expected} 时读到 {frame.Status}");
            if (frame.Header.Type == expected)
            {
                return frame;
            }
        }
        Assert.Fail($"始终没等到 {expected} 帧");
        return default;
    }
}

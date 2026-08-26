using System.Text;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.Tests.FileTransfer;
using VelaShell.Core.XYModem.Model;
using VelaShell.Core.XYModem.Protocol;

namespace VelaShell.Core.Tests.XYModem;

/// <summary>
/// 与真实 lrzsz(<c>sb</c>/<c>rb</c>、<c>sx</c>/<c>rx</c>)的互操作回归。对端那一侧的字节是按
/// <c>ymodem.txt</c> 的块布局手工拼出来的,不经过我们自己的编码器 —— 这样编码器和解码器一起错的
/// 时候测试才会红。断言同时覆盖我们发出去的每一个应答字节。
/// </summary>
[TestClass]
[TestCategory("XYModem")]
public class XYModemInteropTests
{
    private const byte SOH = 0x01;
    private const byte STX = 0x02;
    private const byte EOT = 0x04;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;
    private const byte CAN = 0x18;
    private const byte SUB = 0x1A;
    private const byte C = 0x43;
    private const byte G = 0x47;

    /// <summary>按 ymodem.txt 手工拼一个数据块:引导 + 块号 + 块号取反 + 定长负载 + CRC16(大端)。</summary>
    private static byte[] HandBuiltBlock(int blockNumber, ReadOnlySpan<byte> content, int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        payload.AsSpan().Fill(blockNumber == 0 ? (byte)0x00 : SUB); // 0 号块补 NUL,数据块补 SUB。
        content.CopyTo(payload);

        var wire = new List<byte>(payloadSize + 5)
        {
            payloadSize == 1024 ? STX : SOH,
            (byte)(blockNumber & 0xFF),
            (byte)~(byte)(blockNumber & 0xFF)
        };
        wire.AddRange(payload);
        ushort crc = Crc16Xmodem.Compute(payload);
        wire.Add((byte)(crc >> 8));
        wire.Add((byte)(crc & 0xFF));
        return [.. wire];
    }

    /// <summary>按 sb 的写法手工拼 0 号块内容:<c>文件名 NUL 大小 修改时间八进制 模式八进制 …</c>。</summary>
    private static byte[] HandBuiltBlockZero(string name, long size)
    {
        var content = new List<byte>();
        content.AddRange(Encoding.UTF8.GetBytes(name));
        content.Add(0);
        content.AddRange(Encoding.ASCII.GetBytes($"{size} 0 644 0 0 0"));
        content.Add(0);
        return HandBuiltBlock(0, [.. content], 128);
    }

    /// <summary>从对端读一个字节,超时即断言失败(比一直挂着更好排障)。</summary>
    private static async Task<byte> ReadByteAsync(XYModemByteReader reader, CancellationToken ct)
    {
        int b = await reader.ReadByteAsync(ct);
        Assert.IsTrue(b >= 0, "对端提前关闭了通道");
        return (byte)b;
    }

    private static async Task ExpectAsync(XYModemByteReader reader, byte expected, string because, CancellationToken ct)
    {
        byte actual = await ReadByteAsync(reader, ct);
        Assert.AreEqual(expected, actual, $"{because}(期望 0x{expected:x2},实际 0x{actual:x2})");
    }

    /// <summary>
    /// 完整的 YMODEM 下行:扮演 <c>sb</c> 手工发出 0 号块 → 数据块 → EOT → 空 0 号块,
    /// 并逐个断言我们回的应答字节。这条用例同时钉住三件事:握手字符是 <c>'C'</c>、
    /// 0 号块被正确解析出文件名与大小、末块的 SUB 填充按声明大小被裁掉。
    /// </summary>
    [TestMethod]
    public async Task YModemReceive_MatchesHandBuiltSbExchange()
    {
        byte[] content = Encoding.ASCII.GetBytes("hello ymodem world");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving =
            new XYModemReceiver(ours, sink, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);

        // 1) 接收方先招呼:CRC 模式的 'C'。
        await ExpectAsync(reader, C, "YMODEM 接收方必须先发 'C' 请求 CRC 模式", cts.Token);

        // 2) sb 发 0 号块(文件名 + 大小),接收方应答 ACK,再发一次 'C' 索要数据。
        await peer.WriteAsync(HandBuiltBlockZero("中文名.txt", content.Length), cts.Token);
        await ExpectAsync(reader, ACK, "0 号块应被 ACK", cts.Token);
        await ExpectAsync(reader, C, "ACK 完 0 号块后要再发一次 'C' 才开始收数据", cts.Token);

        // 3) 一个 128 字节数据块(尾部 SUB 填充),接收方应答 ACK。
        await peer.WriteAsync(HandBuiltBlock(1, content, 128), cts.Token);
        await ExpectAsync(reader, ACK, "数据块 1 应被 ACK", cts.Token);

        // 4) EOT 收束本文件。
        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT 应被 ACK", cts.Token);

        // 5) 批结束:接收方再发 'C',sb 用空的 0 号块收摊。
        await ExpectAsync(reader, C, "收完一个文件后应再发 'C' 等下一个", cts.Token);
        await peer.WriteAsync(HandBuiltBlock(0, [], 128), cts.Token);
        await ExpectAsync(reader, ACK, "空 0 号块应被 ACK", cts.Token);

        FileTransferSession session = await receiving;

        Assert.AreEqual(FileTransferState.Completed, session.Status);
        CollectionAssert.AreEqual(new[] { "中文名.txt" }, sink.OfferedNames, "0 号块的 UTF-8 文件名应原样解出");
        Assert.AreEqual((long)content.Length, sink.OfferedSizes[0], "0 号块声明的大小应被解析");
        CollectionAssert.AreEqual(content, sink.Completed["中文名.txt"], "末块的 SUB 填充必须按声明大小裁掉");
    }

    /// <summary>
    /// XMODEM 下行没有 0 号块:第一个到达的就是 1 号数据块,落地名走配置的默认值。
    /// 大小未知,末块的 SUB 填充只能靠裁尾部启发式去掉。
    /// </summary>
    [TestMethod]
    public async Task XModemReceive_HasNoBlockZero_AndTrimsPadding()
    {
        byte[] content = Encoding.ASCII.GetBytes("xmodem payload");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var options = new XYModemOptions
        {
            Protocol = TerminalTransferProtocol.XModem,
            DefaultReceiveFileName = "download.bin"
        };
        Task<FileTransferSession> receiving = new XYModemReceiver(ours, sink, options).ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, C, "XMODEM 接收方同样先发 'C'", cts.Token);

        await peer.WriteAsync(HandBuiltBlock(1, content, 128), cts.Token);
        await ExpectAsync(reader, ACK, "数据块 1 应被 ACK", cts.Token);

        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT 应被 ACK", cts.Token);

        FileTransferSession session = await receiving;

        Assert.AreEqual(FileTransferState.Completed, session.Status);
        CollectionAssert.AreEqual(new[] { "download.bin" }, sink.OfferedNames);
        CollectionAssert.AreEqual(content, sink.Completed["download.bin"]);
    }

    /// <summary>XMODEM-1K 的 1024 字节块由 STX 引导,接收方应按引导字节自动识别块长。</summary>
    [TestMethod]
    public async Task XModemReceive_AcceptsStxThousandByteBlocks()
    {
        byte[] content = new byte[1000];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 注意:配置的是普通 XMODEM,块长仍由链路上的 STX 决定 —— 接收侧不该被配置写死。
        var options = new XYModemOptions
        {
            Protocol = TerminalTransferProtocol.XModem,
            DefaultReceiveFileName = "big.bin"
        };
        Task<FileTransferSession> receiving = new XYModemReceiver(ours, sink, options).ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, C, "先发 'C'", cts.Token);
        await peer.WriteAsync(HandBuiltBlock(1, content, 1024), cts.Token);
        await ExpectAsync(reader, ACK, "STX 大块也应被正常 ACK", cts.Token);
        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT 应被 ACK", cts.Token);

        FileTransferSession session = await receiving;

        Assert.AreEqual(FileTransferState.Completed, session.Status);
        // 尾部是 24 个 SUB 填充,裁掉后应正好还原 1000 字节 —— 但内容末尾恰为 SUB 时会多裁,
        // 这是 XMODEM 不传大小的固有局限,此处的测试数据刻意避开了那种情形。
        CollectionAssert.AreEqual(content, sink.Completed["big.bin"]);
    }

    /// <summary>块校验失败时必须回 NAK 要求重发,重发正确后照常继续 —— 这是 XMODEM 的全部纠错手段。</summary>
    [TestMethod]
    public async Task CorruptBlock_IsNakedThenRecovered()
    {
        byte[] content = Encoding.ASCII.GetBytes("retry me");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var options = new XYModemOptions
        {
            Protocol = TerminalTransferProtocol.XModem,
            DefaultReceiveFileName = "retry.bin"
        };
        Task<FileTransferSession> receiving = new XYModemReceiver(ours, sink, options).ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, C, "先发 'C'", cts.Token);

        byte[] corrupted = HandBuiltBlock(1, content, 128);
        corrupted[3] ^= 0xFF; // 负载首字节翻转,CRC 必然对不上。
        await peer.WriteAsync(corrupted, cts.Token);
        await ExpectAsync(reader, NAK, "坏块必须回 NAK", cts.Token);

        await peer.WriteAsync(HandBuiltBlock(1, content, 128), cts.Token);
        await ExpectAsync(reader, ACK, "重发的好块应被 ACK", cts.Token);

        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT 应被 ACK", cts.Token);

        FileTransferSession session = await receiving;
        Assert.AreEqual(FileTransferState.Completed, session.Status);
        CollectionAssert.AreEqual(content, sink.Completed["retry.bin"]);
    }

    /// <summary>
    /// 重复块(我们上一个 ACK 在链路上丢了、对端重发同一块)必须补发 ACK 而<b>不能</b>重复写盘 ——
    /// 写重了文件就会多出一整块内容,而且没有任何报错。
    /// </summary>
    [TestMethod]
    public async Task DuplicateBlock_IsReAckedButNotWrittenTwice()
    {
        byte[] first = Encoding.ASCII.GetBytes("AAAA");
        byte[] second = Encoding.ASCII.GetBytes("BBBB");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving =
            new XYModemReceiver(ours, sink, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, C, "先发 'C'", cts.Token);
        await peer.WriteAsync(HandBuiltBlockZero("dup.bin", 256), cts.Token);
        await ExpectAsync(reader, ACK, "0 号块 ACK", cts.Token);
        await ExpectAsync(reader, C, "再发 'C'", cts.Token);

        await peer.WriteAsync(HandBuiltBlock(1, first, 128), cts.Token);
        await ExpectAsync(reader, ACK, "块 1 ACK", cts.Token);
        // 同一块再来一次(模拟 ACK 丢失后的重传)。
        await peer.WriteAsync(HandBuiltBlock(1, first, 128), cts.Token);
        await ExpectAsync(reader, ACK, "重复块应补发 ACK", cts.Token);
        await peer.WriteAsync(HandBuiltBlock(2, second, 128), cts.Token);
        await ExpectAsync(reader, ACK, "块 2 ACK", cts.Token);

        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT ACK", cts.Token);
        await ExpectAsync(reader, C, "批循环的下一次 'C'", cts.Token);
        await peer.WriteAsync(HandBuiltBlock(0, [], 128), cts.Token);
        await ExpectAsync(reader, ACK, "空 0 号块 ACK", cts.Token);

        await receiving;

        byte[] landed = sink.Completed["dup.bin"];
        Assert.AreEqual(256, landed.Length, "重复块被写了两遍就会超过声明的 256 字节");
        CollectionAssert.AreEqual(first, landed[..4]);
        CollectionAssert.AreEqual(second, landed[128..132]);
    }

    /// <summary>YMODEM-G 的握手字符是 <c>'G'</c> 而不是 <c>'C'</c>,且收到块后不逐块应答。</summary>
    [TestMethod]
    public async Task YModemG_UsesGHandshake_AndDoesNotAckEachBlock()
    {
        byte[] content = Encoding.ASCII.GetBytes("streamed");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving =
            new XYModemReceiver(ours, sink, new XYModemOptions { Protocol = TerminalTransferProtocol.YModemG })
                .ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, G, "YMODEM-G 的握手字符必须是 'G'", cts.Token);

        // 0 号块仍然要 ACK(YMODEM-G 只是数据块不逐块应答)。
        await peer.WriteAsync(HandBuiltBlockZero("g.bin", content.Length), cts.Token);
        await ExpectAsync(reader, ACK, "0 号块在 YMODEM-G 下仍需 ACK", cts.Token);
        await ExpectAsync(reader, G, "再发一次 'G' 开始流式收数据", cts.Token);

        // 数据块 + EOT 连着发,中间不给接收方应答的机会 —— 这正是 YMODEM-G 的定义。
        await peer.WriteAsync(HandBuiltBlock(1, content, 128), cts.Token);
        await peer.WriteAsync(new byte[] { EOT }, cts.Token);
        await ExpectAsync(reader, ACK, "EOT 仍需 ACK(只有数据块不应答)", cts.Token);
        await ExpectAsync(reader, G, "批循环的下一次 'G'", cts.Token);
        await peer.WriteAsync(HandBuiltBlock(0, [], 128), cts.Token);
        await ExpectAsync(reader, ACK, "空 0 号块 ACK", cts.Token);

        FileTransferSession session = await receiving;
        Assert.AreEqual(FileTransferState.Completed, session.Status);
        CollectionAssert.AreEqual(content, sink.Completed["g.bin"]);
    }

    /// <summary>对端发来连续 CAN 时必须立刻中止,而不是把 CAN 当数据吞下去。</summary>
    [TestMethod]
    public async Task PeerCancel_AbortsSession()
    {
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<FileTransferSession> receiving =
            new XYModemReceiver(ours, sink, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .ReceiveAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await ExpectAsync(reader, C, "先发 'C'", cts.Token);
        await peer.WriteAsync(new byte[] { CAN, CAN, CAN, CAN }, cts.Token);

        FileTransferSession session = await receiving;
        Assert.AreEqual(FileTransferState.Cancelled, session.Status);
        Assert.AreEqual(0, sink.Completed.Count);
    }

    /// <summary>
    /// 上行方向:扮演 <c>rb</c> 发 <c>'C'</c>,逐块校验我们发出来的字节确实符合 ymodem.txt 的布局,
    /// 并确认批结束块是「128 个零 + CRC 0000」。
    /// </summary>
    [TestMethod]
    public async Task YModemSend_ProducesSpecCompliantWireBytes()
    {
        byte[] content = Encoding.ASCII.GetBytes("upload me");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var source = new InMemoryFileSource([("up.txt", content)]);
        Task<FileTransferSession> sending =
            new XYModemSender(ours, source, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .SendAsync(cts.Token);

        var reader = new XYModemByteReader(peer);

        // 我们扮演 rb:先发 'C' 招呼。
        await peer.WriteAsync(new byte[] { C }, cts.Token);

        // 0 号块:SOH 00 FF <文件名 NUL 大小 …> CRC。
        byte[] blockZero = await ReadBlockAsync(reader, cts.Token);
        Assert.AreEqual(SOH, blockZero[0], "0 号块必须是 128 字节的 SOH 块");
        Assert.AreEqual(0x00, blockZero[1]);
        Assert.AreEqual(0xFF, blockZero[2]);
        TransferFileMetadata parsed = TransferFileInfoCodec.Parse(blockZero.AsSpan(3, 128));
        Assert.AreEqual("up.txt", parsed.FileName);
        Assert.AreEqual((long)content.Length, parsed.Size);
        await peer.WriteAsync(new byte[] { ACK, C }, cts.Token);

        // 数据块:内容不足 128 时用 SOH 小块,尾部 SUB 填充。
        byte[] dataBlock = await ReadBlockAsync(reader, cts.Token);
        Assert.AreEqual(SOH, dataBlock[0], "不足 128 字节的尾块应降到 SOH 小块,不该发整整 1KB 填充");
        Assert.AreEqual(0x01, dataBlock[1]);
        Assert.AreEqual(0xFE, dataBlock[2]);
        CollectionAssert.AreEqual(content, dataBlock[3..(3 + content.Length)]);
        Assert.AreEqual(SUB, dataBlock[3 + content.Length], "尾部必须用 SUB(0x1A)填充");
        Assert.IsTrue(
            XYModemBlock.Verify(dataBlock.AsSpan(3, 128), dataBlock.AsSpan(131, 2), useCrc: true),
            "数据块 CRC 必须自洽");
        await peer.WriteAsync(new byte[] { ACK }, cts.Token);

        // EOT → ACK。
        await ExpectAsync(reader, EOT, "数据发完应发 EOT", cts.Token);
        await peer.WriteAsync(new byte[] { ACK }, cts.Token);

        // 批结束:我们再发 'C',对方应回全零的 0 号块(CRC 也必然是 0000)。
        await peer.WriteAsync(new byte[] { C }, cts.Token);
        byte[] terminator = await ReadBlockAsync(reader, cts.Token);
        Assert.AreEqual(SOH, terminator[0]);
        Assert.AreEqual(0x00, terminator[1]);
        Assert.AreEqual(0xFF, terminator[2]);
        CollectionAssert.AreEqual(new byte[128], terminator[3..131], "批结束块的负载必须是 128 个零");
        Assert.AreEqual(0x00, terminator[131]);
        Assert.AreEqual(0x00, terminator[132]);
        await peer.WriteAsync(new byte[] { ACK }, cts.Token);

        FileTransferSession session = await sending;
        Assert.AreEqual(FileTransferState.Completed, session.Status);
    }

    /// <summary>发送方应当听对端的:对端发 NAK 就退回 8 位校验和模式(块尾只有一个字节)。</summary>
    [TestMethod]
    public async Task Sender_FallsBackToChecksumWhenPeerSendsNak()
    {
        byte[] content = Encoding.ASCII.GetBytes("checksum mode");
        (InMemoryByteDuplex ours, InMemoryByteDuplex peer) = InMemoryByteDuplex.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var source = new InMemoryFileSource([("cs.bin", content)]);
        Task<FileTransferSession> sending =
            new XYModemSender(ours, source, new XYModemOptions { Protocol = TerminalTransferProtocol.XModem })
                .SendAsync(cts.Token);

        var reader = new XYModemByteReader(peer);
        await peer.WriteAsync(new byte[] { NAK }, cts.Token); // 老式 rx:请求校验和模式。

        byte[] header = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            header[i] = await ReadByteAsync(reader, cts.Token);
        }
        Assert.AreEqual(SOH, header[0]);
        byte[] payload = new byte[128];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = await ReadByteAsync(reader, cts.Token);
        }
        byte checksum = await ReadByteAsync(reader, cts.Token);
        Assert.AreEqual(
            XYModemBlock.Checksum(payload),
            checksum,
            "对端要校验和模式时,块尾必须是一个 8 位算术和而不是两字节 CRC");
        await peer.WriteAsync(new byte[] { ACK }, cts.Token);

        await ExpectAsync(reader, EOT, "单块文件发完就该 EOT", cts.Token);
        await peer.WriteAsync(new byte[] { ACK }, cts.Token);

        FileTransferSession session = await sending;
        Assert.AreEqual(FileTransferState.Completed, session.Status);
    }

    /// <summary>读一个完整数据块(按引导字节判块长),返回含块头与校验的整块字节。</summary>
    private static async Task<byte[]> ReadBlockAsync(XYModemByteReader reader, CancellationToken ct)
    {
        byte lead = await ReadByteAsync(reader, ct);
        Assert.IsTrue(lead is SOH or STX, $"期望块引导 SOH/STX,实际 0x{lead:x2}");
        int payloadSize = lead == STX ? 1024 : 128;
        byte[] block = new byte[3 + payloadSize + 2];
        block[0] = lead;
        for (int i = 1; i < block.Length; i++)
        {
            block[i] = await ReadByteAsync(reader, ct);
        }
        return block;
    }
}

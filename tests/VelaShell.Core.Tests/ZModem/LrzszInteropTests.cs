using System.Text;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.Tests.FileTransfer;

namespace VelaShell.Core.Tests.ZModem;

/// <summary>
/// 与真实 lrzsz(<c>sz</c>/<c>rz</c>)的互操作回归。这里的期望值不是用我们自己的编码器生成的,
/// 而是按 lrzsz <c>zm.c</c>/<c>zmodem.h</c> 的定义手工构造 —— 否则测试只是在自证,
/// 编码器和解码器一起错的时候依然全绿(这正是这批 bug 当初能溜进来的原因)。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class LrzszInteropTests
{
    /// <summary>按 lrzsz <c>zshhdr()</c> 手工拼一个十六进制帧头(含其 0x8A 换行怪癖与 XON 收尾)。</summary>
    private static byte[] LrzszHexHeader(byte type, byte[] flags)
    {
        var wire = new List<byte> { 0x2A, 0x2A, 0x18, 0x42 };
        ushort crc = Crc16Xmodem.Update(0, type);
        wire.AddRange(Encoding.ASCII.GetBytes(type.ToString("x2")));
        foreach (byte f in flags)
        {
            crc = Crc16Xmodem.Update(crc, f);
            wire.AddRange(Encoding.ASCII.GetBytes(f.ToString("x2")));
        }
        wire.AddRange(Encoding.ASCII.GetBytes(((byte)(crc >> 8)).ToString("x2")));
        wire.AddRange(Encoding.ASCII.GetBytes(((byte)(crc & 0xFF)).ToString("x2")));
        wire.Add(0x0D);
        wire.Add(0x8A); // lrzsz 发的是八进制 0212 = LF | 0x80,不是裸 LF。
        if (type is not ((byte)ZModemFrameType.ZFIN or (byte)ZModemFrameType.ZACK))
        {
            wire.Add(ZModemConstants.XON);
        }
        return [.. wire];
    }

    /// <summary>跑一次会话,取回接收方写到链路上的全部字节。</summary>
    private static async Task<byte[]> CaptureReceiverOutboundAsync(byte[] inbound)
    {
        var duplex = InMemoryByteDuplex.FromInbound(inbound.Length > 0 ? [inbound] : []);
        var receiver = new ZModemReceiver(duplex, new InMemoryFileSink());
        await receiver.ReceiveAsync(CancellationToken.None);
        return await duplex.DrainOutboundAsync();
    }

    /// <summary>
    /// ZRINIT 的 ZF0 必须通告 CANFDX|CANOVIO|CANFC32(0x23)。
    /// 回归:曾把 CANOVIO 误写成 0x40 —— 那其实是 ESCCTL,导致我们通告的是
    /// 「请转义所有控制字符」而非「可流式接收」,且从未通告全双工。
    /// </summary>
    [TestMethod]
    public async Task Zrinit_AdvertisesLrzszCapabilityFlags()
    {
        byte[] outbound = await CaptureReceiverOutboundAsync([]);

        var echo = InMemoryByteDuplex.FromInbound([outbound]);
        ZModemHeaderResult frame = await new ZModemFrameReader(echo).ReadHeaderAsync(CancellationToken.None);

        Assert.AreEqual(ZModemReadStatus.Header, frame.Status);
        Assert.AreEqual(ZModemFrameType.ZRINIT, frame.Header.Type);

        // ZF0 是 4 个参数字节的最后一个(P3),不是第一个。
        byte zf0 = frame.Header.P3;
        Assert.AreEqual(
            ZModemCapabilities.CANFDX | ZModemCapabilities.CANOVIO | ZModemCapabilities.CANFC32,
            zf0,
            $"ZF0 应为 0x23,实际 0x{zf0:x2}");

        // ESCCTL 会让 lrzsz 进入 Zctlesc 模式(吞吐骤降),默认不应通告。
        Assert.AreEqual(0, zf0 & ZModemCapabilities.ESCCTL, "默认不应通告 ESCCTL");
        // ZP0/ZP1 = 接收缓冲区大小,0 表示流式不限窗口。
        Assert.AreEqual(0, frame.Header.P0);
        Assert.AreEqual(0, frame.Header.P1);
    }

    /// <summary>我们发出的 ZRINIT 应与真实 lrzsz <c>rz</c> 的字节完全一致(除其 0x8A 换行怪癖)。</summary>
    [TestMethod]
    public async Task Zrinit_MatchesRealLrzszWireBytes()
    {
        byte[] outbound = await CaptureReceiverOutboundAsync([]);
        byte[] expected = LrzszHexHeader(
            (byte)ZModemFrameType.ZRINIT,
            [0, 0, 0, ZModemCapabilities.CANFDX | ZModemCapabilities.CANOVIO | ZModemCapabilities.CANFC32]);

        // lrzsz 发 0x8A(LF|0x80),我们发裸 0x0A;两者对端都按噪声跳过,故比较时归一。
        byte[] normalized = [.. expected.Select(b => b == 0x8A ? (byte)0x0A : b)];
        Assert.AreSequenceEqual(normalized, outbound[..normalized.Length]);
    }

    /// <summary>真实 lrzsz <c>sz</c> 的启动序列("rz\r" + ZRQINIT,含 0x8A)必须能被解析。</summary>
    [TestMethod]
    public async Task FrameReader_ParsesRealLrzszSzStartup()
    {
        var wire = new List<byte>();
        wire.AddRange("rz\r"u8.ToArray());
        wire.AddRange(LrzszHexHeader((byte)ZModemFrameType.ZRQINIT, [0, 0, 0, 0]));

        var duplex = InMemoryByteDuplex.FromInbound([wire.ToArray()]);
        ZModemHeaderResult frame = await new ZModemFrameReader(duplex).ReadHeaderAsync(CancellationToken.None);

        Assert.AreEqual(ZModemReadStatus.Header, frame.Status);
        Assert.AreEqual(ZModemFrameType.ZRQINIT, frame.Header.Type);
        Assert.AreEqual(ZModemHeaderFormat.Hex, frame.Format);
    }

    /// <summary>
    /// 逐字节回放 2026-07-16 对 Ubuntu 22.04 真机 <c>sz</c> 的诊断抓包:它发出的 ZNAK 帧
    /// <c>**\x18B0600000000cd85\r\x8a\x11</c> 必须校验通过。回归:CRC16 曾被双重增广
    /// (旧式 lrzsz 算法的补零收尾 + 现代算法again),我们对这帧报 CrcError、对端对我们的
    /// 每个非全零帧头也报 CrcError,双方无限互发 ZNAK/ZRQINIT,终端全黑直到超时。
    /// </summary>
    [TestMethod]
    public async Task FrameReader_ParsesZnakCapturedFromRealUbuntuSz()
    {
        byte[] captured =
        [
            0x2A, 0x2A, 0x18, 0x42,                         // ** ZDLE ZHEX
            0x30, 0x36,                                     // "06" = ZNAK
            0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, // "00000000"
            0x63, 0x64, 0x38, 0x35,                         // "cd85" — lrzsz 的真实 CRC
            0x0D, 0x8A, 0x11                                // CR LF|0x80 XON
        ];

        var duplex = InMemoryByteDuplex.FromInbound([captured]);
        ZModemHeaderResult frame = await new ZModemFrameReader(duplex).ReadHeaderAsync(CancellationToken.None);

        Assert.AreEqual(ZModemReadStatus.Header, frame.Status);
        Assert.AreEqual(ZModemFrameType.ZNAK, frame.Header.Type);
    }

    /// <summary>反向对照:我们发出的 ZNAK 必须与真实 lrzsz 的抓包字节一致(CRC = cd85)。</summary>
    [TestMethod]
    public void FrameWriter_ZnakMatchesRealUbuntuSzCapture()
    {
        byte[] wire = ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZNAK), ZModemHeaderFormat.Hex);
        string ascii = Encoding.ASCII.GetString(wire, 4, 14);
        Assert.AreEqual("0600000000cd85", ascii);
    }

    /// <summary>真实 lrzsz <c>rz</c> 的启动 ZRINIT 必须能被解析,且能读出其能力位。</summary>
    [TestMethod]
    public async Task FrameReader_ParsesRealLrzszRzStartup()
    {
        byte[] wire = LrzszHexHeader((byte)ZModemFrameType.ZRINIT, [0, 0, 0, 0x23]);

        var duplex = InMemoryByteDuplex.FromInbound([wire]);
        ZModemHeaderResult frame = await new ZModemFrameReader(duplex).ReadHeaderAsync(CancellationToken.None);

        Assert.AreEqual(ZModemReadStatus.Header, frame.Status);
        Assert.AreEqual(ZModemFrameType.ZRINIT, frame.Header.Type);
        Assert.AreEqual(0x23, frame.Header.P3);
        Assert.AreNotEqual(0, frame.Header.P3 & ZModemCapabilities.CANFC32);
    }

    /// <summary>
    /// 对端发了 ZRQINIT 之后就再无音讯时,接收方必须在有限时间内放弃并返回失败,
    /// 而不是永久挂起。回归:超时选项此前从未接线,ReadHeaderAsync 会无限期等待 ——
    /// 路由器因此永远停在会话态,把此后所有输出(含 shell 提示符)全部吞掉,终端再也回不来。
    /// </summary>
    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Receiver_PeerGoesSilent_FailsFastInsteadOfHangingForever()
    {
        // 对端静默属于握手阶段,走的是 HandshakeTimeout/HandshakeRetries 预算。
        var options = new ZModemOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(100),
            HandshakeRetries = 2
        };
        // 入站永远不产出、也永不结束 —— 精确复刻「sz 卡住不发帧」的现场。
        var duplex = new SilentDuplex();
        var receiver = new ZModemReceiver(duplex, new InMemoryFileSink(), options);

        FileTransferSession session = await receiver.ReceiveAsync(CancellationToken.None);

        Assert.AreEqual(FileTransferState.Failed, session.Status);
        // 每次超时都应补发一次 ZRINIT,最后再发取消序列。
        Assert.IsGreaterThan(0, duplex.WrittenBytes, "超时后应向对端发过 ZRINIT / 取消序列");
    }

    /// <summary>
    /// 盯住用户可见的那条保证:默认配置下,握手谈不拢时终端必须在几十秒内回来,而不是几分钟。
    /// 握手期间终端是全黑的(字节全被路由器接管),用户除了干等什么也做不了。
    /// 这里用配置算术断言(而非真跑 20 秒),避免慢且在并行下抖动的挂钟测试 ——
    /// 机制本身由 <see cref="Receiver_PeerGoesSilent_FailsFastInsteadOfHangingForever" /> 用小预算实跑验证。
    /// </summary>
    [TestMethod]
    public void DefaultHandshakeBudget_IsSecondsNotMinutes()
    {
        ZModemOptions o = ZModemOptions.Default;
        // 握手最坏耗时 ≈ (1 次首读 + HandshakeRetries 次重试) × HandshakeTimeout。
        TimeSpan worstCase = o.HandshakeTimeout * (o.HandshakeRetries + 1);
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromSeconds(30), worstCase,
            $"默认握手预算最坏 {worstCase.TotalSeconds:F0}s,应为秒级(旧实现曾是 FrameTimeout×MaxRetries=300s)");
        // 握手预算必须显著短于数据阶段预算,否则等于没区分。
        Assert.IsLessThan(
            o.FrameTimeout, o.HandshakeTimeout,
            "握手超时应远短于数据阶段超时");
    }

    /// <summary>
    /// 用户放弃保存目录选择(sink 返回 Abort)时:接收方必须走优雅路线——对当前文件回 ZSKIP,
    /// 让 sz 跳过并发 ZFIN 干净收尾,而不是发 CAN 中止序列。发 CAN 会让 sz 继续吐协议字节,
    /// 会话结束后这些二进制垃圾流回终端,其中的 ESC 序列会切到备用屏幕缓冲区,表现为
    /// "点击焦点后内容全没、输入无效"。这里用真实发送方状态机扮演 sz,验证它被干净收尾。
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Receiver_UserAbortsFolder_SkipsGracefullySoSenderFinishesClean()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // a 端扮演 sz(发送方状态机),要发一个文件;b 端是接收方,其 sink 一被询问就中止(用户取消目录)。
        Task<FileTransferSession> send = new ZModemSender(
            a, new InMemoryFileSource([("doc.pdf", new byte[4096])])).SendAsync(cts.Token);
        Task<FileTransferSession> receive = new ZModemReceiver(b, new AbortingFileSink()).ReceiveAsync(cts.Token);

        await Task.WhenAll(send, receive);

        // 接收方判为已取消。
        Assert.AreEqual(FileTransferState.Cancelled, receive.Result.Status);
        // 关键:sz(发送方)被干净收尾——它看到 ZSKIP 把文件记为 Skipped,并正常走完 ZFIN。
        // 若接收方发的是 CAN,发送方会读到取消序列而中途中断(Cancelled/Failed),而非干净的 Completed+Skipped。
        Assert.AreEqual(FileTransferState.Completed, send.Result.Status, "sz 应收到 ZSKIP 后干净收尾");
        Assert.AreEqual(FileTransferState.Skipped, send.Result.Items[0].Status, "被取消的文件应记为 Skipped");
    }

    /// <summary>一被询问文件处置就返回 Abort:复刻用户放弃保存目录选择。</summary>
    private sealed class AbortingFileSink : IFileTransferSink
    {
        public ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
            TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken) =>
            ValueTask.FromResult((TransferFileDisposition.Abort, 0L));

        public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>取消令牌应能立刻中止一个正在等待的会话(标签关闭 / 用户中止)。</summary>
    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Receiver_Cancellation_EndsSessionPromptly()
    {
        var duplex = new SilentDuplex();
        var receiver = new ZModemReceiver(duplex, new InMemoryFileSink());
        using var cts = new CancellationTokenSource();

        Task<FileTransferSession> task = receiver.ReceiveAsync(cts.Token);
        await cts.CancelAsync();
        FileTransferSession session = await task;

        Assert.AreEqual(FileTransferState.Cancelled, session.Status);
    }

    /// <summary>入站永不产出数据、也永不 EOF 的双工:用于复现「对端静默」。</summary>
    private sealed class SilentDuplex : IByteDuplex
    {
        private int _written;

        public int WrittenBytes => Volatile.Read(ref _written);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _written, data.Length);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

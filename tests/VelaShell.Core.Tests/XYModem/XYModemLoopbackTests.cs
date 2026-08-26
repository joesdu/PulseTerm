using System.Text;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Tests.FileTransfer;
using VelaShell.Core.XYModem.Model;
using VelaShell.Core.XYModem.Protocol;

namespace VelaShell.Core.Tests.XYModem;

/// <summary>
/// 把我们自己的 XMODEM/YMODEM 发送方与接收方在内存双工上对接跑完整流程,验证字节保真、
/// 多块推进与批量收束。这类回环测试只能证明「两侧自洽」,协议是否符合规范由
/// <see cref="XYModemInteropTests" /> 的手工线上字节把关 —— 两者缺一不可。
/// </summary>
[TestClass]
[TestCategory("XYModem")]
public class XYModemLoopbackTests
{
    private static async Task<(FileTransferSession Send, FileTransferSession Receive, InMemoryFileSink Sink)>
        RoundTripAsync(TerminalTransferProtocol protocol, (string Name, byte[] Data)[] files)
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        var options = new XYModemOptions
        {
            Protocol = protocol,
            DefaultReceiveFileName = files[0].Name
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<FileTransferSession> receive = new XYModemReceiver(b, sink, options).ReceiveAsync(cts.Token);
        Task<FileTransferSession> send = new XYModemSender(a, new InMemoryFileSource(files), options).SendAsync(cts.Token);

        await Task.WhenAll(receive, send);
        return (send.Result, receive.Result, sink);
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // 刻意避开 0x1A(SUB):XMODEM 不传大小,内容结尾恰为 SUB 时会被裁尾启发式多裁掉,
            // 那是协议的固有局限而非实现缺陷,不该让它污染这里的保真断言。
            data[i] = (byte)(((i * 31) + seed) % 251);
        }
        return data;
    }

    /// <summary>YMODEM 单文件:内容与声明大小都要原样过去。</summary>
    [TestMethod]
    public async Task YModem_SingleFile_RoundTripsExactly()
    {
        byte[] data = Pattern(5000, 3);

        (FileTransferSession send, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModem, [("payload.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, send.Status);
        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        Assert.AreEqual((long)data.Length, sink.OfferedSizes[0]);
        CollectionAssert.AreEqual(data, sink.Completed["payload.bin"]);
    }

    /// <summary>YMODEM 的批量能力:一次会话连发三个文件,顺序与内容都不能串。</summary>
    [TestMethod]
    public async Task YModem_Batch_TransfersEveryFileInOrder()
    {
        (string, byte[])[] files =
        [
            ("first.bin", Pattern(100, 1)),
            ("second.bin", Pattern(3000, 2)),
            ("三个.txt", Encoding.UTF8.GetBytes("第三个文件,故意用非 ASCII 文件名"))
        ];

        (FileTransferSession send, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModem, files);

        Assert.AreEqual(FileTransferState.Completed, send.Status);
        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        CollectionAssert.AreEqual(new[] { "first.bin", "second.bin", "三个.txt" }, sink.OfferedNames);
        foreach ((string name, byte[] data) in files)
        {
            CollectionAssert.AreEqual(data, sink.Completed[name], $"{name} 内容不符");
        }
        Assert.AreEqual(3, send.Items.Count);
    }

    /// <summary>块号跨过 255 回绕后仍要正确对齐(1024 字节块 × 300 块 &gt; 256)。</summary>
    [TestMethod]
    public async Task YModem_BlockNumberWrap_StillAligns()
    {
        byte[] data = Pattern(300 * 1024, 7);

        (_, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModem, [("wrap.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        CollectionAssert.AreEqual(data, sink.Completed["wrap.bin"], "块号回绕后内容错位");
    }

    /// <summary>XMODEM 单文件:没有文件名与大小,内容仍要能完整落地。</summary>
    [TestMethod]
    public async Task XModem_SingleFile_RoundTripsExactly()
    {
        byte[] data = Pattern(700, 5);

        (FileTransferSession send, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.XModem, [("classic.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, send.Status);
        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        Assert.IsNull(sink.OfferedSizes[0], "XMODEM 不传大小,元数据里的 Size 应为 null");
        CollectionAssert.AreEqual(data, sink.Completed["classic.bin"]);
    }

    /// <summary>XMODEM-1K:发送端改用 1024 字节块,接收端按引导字节自适应。</summary>
    [TestMethod]
    public async Task XModem1K_UsesLargeBlocks_AndRoundTrips()
    {
        byte[] data = Pattern(4096, 9);

        (_, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.XModem1K, [("big.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        CollectionAssert.AreEqual(data, sink.Completed["big.bin"]);
    }

    /// <summary>YMODEM-G:数据块不逐块应答,内容仍要完整。</summary>
    [TestMethod]
    public async Task YModemG_Streaming_RoundTripsExactly()
    {
        byte[] data = Pattern(20000, 11);

        (FileTransferSession send, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModemG, [("stream.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, send.Status);
        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        CollectionAssert.AreEqual(data, sink.Completed["stream.bin"]);
    }

    /// <summary>正好等于块长整数倍的文件不能多发一个空块、也不能少一块。</summary>
    [TestMethod]
    public async Task YModem_ExactBlockMultiple_HasNoOffByOne()
    {
        byte[] data = Pattern(1024 * 3, 13);

        (_, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModem, [("exact.bin", data)]);

        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        Assert.AreEqual(data.Length, sink.Completed["exact.bin"].Length);
        CollectionAssert.AreEqual(data, sink.Completed["exact.bin"]);
    }

    /// <summary>空文件也要能走完流程(0 号块声明大小 0,随后直接 EOT)。</summary>
    [TestMethod]
    public async Task YModem_EmptyFile_CompletesCleanly()
    {
        (FileTransferSession send, FileTransferSession receive, InMemoryFileSink sink) =
            await RoundTripAsync(TerminalTransferProtocol.YModem, [("empty.bin", [])]);

        Assert.AreEqual(FileTransferState.Completed, send.Status);
        Assert.AreEqual(FileTransferState.Completed, receive.Status);
        Assert.AreEqual(0, sink.Completed["empty.bin"].Length);
    }

    /// <summary>
    /// 接收端拒绝落地(用户取消保存目录)时,整个会话必须中止而不是继续收 ——
    /// 这一族协议没有 ZMODEM 的 ZSKIP,拒绝就只能中止,发送端也应看到取消。
    /// </summary>
    [TestMethod]
    public async Task ReceiverAbort_CancelsBothSides()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink { NextDisposition = TransferFileDisposition.Abort };
        var options = new XYModemOptions { Protocol = TerminalTransferProtocol.YModem };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<FileTransferSession> receive = new XYModemReceiver(b, sink, options).ReceiveAsync(cts.Token);
        Task<FileTransferSession> send = new XYModemSender(
            a,
            new InMemoryFileSource([("nope.bin", Pattern(2000, 17))]),
            options).SendAsync(cts.Token);

        await Task.WhenAll(receive, send);

        Assert.AreEqual(FileTransferState.Cancelled, receive.Result.Status);
        Assert.AreEqual(FileTransferState.Cancelled, send.Result.Status);
        Assert.AreEqual(0, sink.Completed.Count);
    }

    /// <summary>用户在文件选择框里取消(空清单)时,发送端应干净地记为取消而不是失败。</summary>
    [TestMethod]
    public async Task SenderWithNoFiles_ReportsCancelled()
    {
        (InMemoryByteDuplex a, _) = InMemoryByteDuplex.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        FileTransferSession session = await new XYModemSender(
            a,
            new InMemoryFileSource([]),
            new XYModemOptions { Protocol = TerminalTransferProtocol.YModem }).SendAsync(cts.Token);

        Assert.AreEqual(FileTransferState.Cancelled, session.Status);
    }
}

using System.Text;
using NSubstitute;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Ssh;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Terminal.FileTransfer;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("ZModem")]
public class TransferRouterTests
{
    private static readonly byte[] Signature = ZModemConstants.ReceiveInitSignature.ToArray();

    /// <summary>远端 <c>sz</c> 注入的真 ZRQINIT 十六进制帧头(18 字节 + CR LF XON)。</summary>
    private static byte[] Zrqinit() =>
        ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZRQINIT), ZModemHeaderFormat.Hex);

    /// <summary>远端 <c>rz</c> 注入的真 ZRINIT 十六进制帧头。</summary>
    private static byte[] Zrinit() =>
        ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZRINIT), ZModemHeaderFormat.Hex);

    [TestMethod]
    public void Detector_PlainOutput_PassesThroughUnchanged()
    {
        var detector = new ZModemDetector();
        byte[] text = "user@host:~$ ls -la\r\n"u8.ToArray();

        ZModemDetectResult result = detector.Process(text);

        Assert.IsFalse(result.Detected);
        Assert.AreSequenceEqual(text, result.TerminalBytes);
    }

    [TestMethod]
    public void Detector_HeaderInStream_SplitsTerminalAndProtocol()
    {
        var detector = new ZModemDetector();
        byte[] prefix = "rz\r"u8.ToArray();
        byte[] header = Zrqinit();

        ZModemDetectResult result = detector.Process([.. prefix, .. header]);

        Assert.IsTrue(result.Detected);
        Assert.AreEqual(ZModemTrigger.Receive, result.Trigger);
        Assert.AreSequenceEqual(prefix, result.TerminalBytes);
        Assert.AreSequenceEqual(header, result.ProtocolBytes);
    }

    /// <summary>远端 <c>rz</c> 的 ZRINIT 必须被识别为「本地发送」(上传)。</summary>
    [TestMethod]
    public void Detector_RzZrinitHeader_TriggersSendDirection()
    {
        var detector = new ZModemDetector();
        byte[] header = Zrinit();

        ZModemDetectResult result = detector.Process(
            [.. "rz waiting to receive.\b\b\b"u8.ToArray(), .. header]);

        Assert.IsTrue(result.Detected);
        Assert.AreEqual(ZModemTrigger.Send, result.Trigger);
        Assert.AreSequenceEqual(header, result.ProtocolBytes);
    }

    /// <summary>
    /// 引导被分片切开时仍须识别;而且被切在前半段的字节<b>照常喂进终端</b>(零扣留),
    /// 不再等下一分片 —— 那正是 #291 的根因。
    /// </summary>
    [TestMethod]
    public void Detector_HeaderSplitAcrossChunks_StillDetectsAndWithholdsNothing()
    {
        var detector = new ZModemDetector();
        byte[] header = Zrqinit();
        byte[] first = [.. "noise"u8.ToArray(), .. header[..8]];

        ZModemDetectResult r1 = detector.Process(first);
        Assert.IsFalse(r1.Detected);
        Assert.AreSequenceEqual(first, r1.TerminalBytes); // 一个字节都没扣。

        ZModemDetectResult r2 = detector.Process(header.AsSpan()[8..]);
        Assert.IsTrue(r2.Detected);
        Assert.IsEmpty(r2.TerminalBytes);                 // 已喂过的部分不重喂。
        Assert.AreSequenceEqual(header, r2.ProtocolBytes); // 协议种子仍是完整帧头。
    }

    /// <summary>
    /// 回归 #291:用户敲的 <c>*</c> 必须立刻回显。检测器此前把任何与引导前缀重叠的块尾都扣下来,
    /// 而引导前两字节正是 <c>'*' '*'</c> —— SSH 里逐字回显的星号被无限期扣住,表现为
    /// 「输入 * 不显示、光标不动,按方向键才一次冒出来」。连敲三次只应显示三次,一个都不能少。
    /// </summary>
    [TestMethod]
    public void Detector_TypedAsterisksEchoedOneByOne_AreNeverWithheld()
    {
        var detector = new ZModemDetector();
        for (int i = 1; i <= 3; i++)
        {
            ZModemDetectResult r = detector.Process("*"u8);
            Assert.IsFalse(r.Detected);
            Assert.AreSequenceEqual("*"u8.ToArray(), r.TerminalBytes, $"asterisk #{i} was swallowed");
        }
    }

    /// <summary>
    /// 零扣留的总不变量:未命中期间,<b>进来几个字节就吐出几个字节</b>,无论怎么切分片。
    /// 这里刻意逐字节喂一段满是 <c>*</c> 与 ZDLE 的、永远凑不成合法帧头的流。
    /// </summary>
    [TestMethod]
    public void Detector_WhileUndetected_OutputLengthAlwaysEqualsInputLength()
    {
        var detector = new ZModemDetector();
        byte[] stream = [.. "**\x18B0zz ***\x18\x18B00 ls *.txt\r\n**"u8.ToArray()];

        var echoed = new List<byte>();
        foreach (byte b in stream)
        {
            ZModemDetectResult r = detector.Process([b]);
            Assert.IsFalse(r.Detected);
            Assert.HasCount(1, r.TerminalBytes);
            echoed.AddRange(r.TerminalBytes);
        }

        Assert.AreSequenceEqual(stream, echoed);
    }

    /// <summary>
    /// 逐字节喂一个真帧头(最恶劣的分片):每一步都不能吞字节;帧头第 18 字节到齐的那一刻命中,
    /// 且交给引擎的协议种子是完整的 18 字节帧头(含此前已经回显过的部分)。
    /// </summary>
    [TestMethod]
    public void Detector_HeaderFedByteByByte_DetectsWhenHeaderCompletes()
    {
        var detector = new ZModemDetector();
        byte[] header = Zrqinit();
        const int HexHeaderLength = 18;

        for (int i = 0; i < HexHeaderLength - 1; i++)
        {
            ZModemDetectResult step = detector.Process([header[i]]);
            Assert.IsFalse(step.Detected, $"detected too early at byte {i}");
            Assert.AreSequenceEqual([header[i]], step.TerminalBytes);
        }

        ZModemDetectResult last = detector.Process([header[HexHeaderLength - 1]]);

        Assert.IsTrue(last.Detected);
        Assert.AreEqual(ZModemTrigger.Receive, last.Trigger);
        Assert.AreSequenceEqual(header[..HexHeaderLength], last.ProtocolBytes);
    }

    /// <summary>
    /// 判据是<b>完整且格式良好</b>的十六进制帧头,不是 6 字节引导:后 12 位不全是十六进制数字
    /// 的,只是巧合,不得接管终端。
    /// </summary>
    [TestMethod]
    public void Detector_MalformedHexHeader_IsNotDetected()
    {
        var detector = new ZModemDetector();
        byte[] input = [.. Signature, .. "not-hex-here"u8.ToArray()];

        ZModemDetectResult result = detector.Process(input);

        Assert.IsFalse(result.Detected);
        Assert.AreSequenceEqual(input, result.TerminalBytes);
    }

    /// <summary>
    /// 尾锚定(借鉴 zmodem.js):<c>sz</c>/<c>rz</c> 写完帧头就阻塞等应答,真引导后面只会跟
    /// CR/LF/XON。后面还跟着普通输出的,几乎必然是 <c>cat</c> 到二进制里凑出来的巧合 —— 不接管。
    /// </summary>
    [TestMethod]
    public void Detector_HeaderFollowedByShellOutput_IsNotDetected()
    {
        var detector = new ZModemDetector();
        byte[] input = [.. Zrqinit(), .. "user@host:~$ "u8.ToArray()];

        ZModemDetectResult result = detector.Process(input);

        Assert.IsFalse(result.Detected);
        Assert.AreSequenceEqual(input, result.TerminalBytes);
    }

    /// <summary>
    /// 但用户刚敲过 <c>sz</c>/<c>rz</c> 时(路由器置位 <c>AcceptUnanchoredHeader</c>),
    /// 同样的输入必须识别得到:此时误报代价远低于漏检。
    /// </summary>
    [TestMethod]
    public void Detector_UnanchoredHeader_IsDetectedWhenArmed()
    {
        var detector = new ZModemDetector { AcceptUnanchoredHeader = true };
        byte[] header = Zrqinit();

        ZModemDetectResult result = detector.Process([.. header, .. "user@host:~$ "u8.ToArray()]);

        Assert.IsTrue(result.Detected);
        Assert.AreEqual(ZModemTrigger.Receive, result.Trigger);
        Assert.AreSequenceEqual(header, result.ProtocolBytes[..header.Length]);
    }

    /// <summary>
    /// 块尾是星号时下一块不得走零拷贝快路径,否则跨分片的引导会被整块直喂终端而漏检。
    /// </summary>
    [TestMethod]
    public void Detector_AfterTrailingAsterisk_NextChunkTakesSlowPath()
    {
        var detector = new ZModemDetector();
        Assert.IsTrue(detector.CanPassThrough("plain output\r\n"u8));

        _ = detector.Process("echo **"u8);

        Assert.IsFalse(detector.CanPassThrough(Zrqinit().AsSpan(2)));
    }

    /// <summary>复位丢弃跨分片匹配状态,且不交还任何字节(从不扣留 → 交还就是重影)。</summary>
    [TestMethod]
    public void Detector_Reset_DropsCarryWithoutReplayingBytes()
    {
        var detector = new ZModemDetector();
        byte[] header = Zrqinit();

        ZModemDetectResult r = detector.Process(header.AsSpan()[..8]);
        Assert.AreSequenceEqual(header[..8], r.TerminalBytes);

        detector.Reset();

        // 复位后残缺前缀不再参与匹配,后半段自成一段普通输出。
        ZModemDetectResult after = detector.Process(header.AsSpan()[8..]);
        Assert.IsFalse(after.Detected);
        Assert.AreSequenceEqual(header[8..], after.TerminalBytes);
    }

    /// <summary>
    /// 未接线上传选择器时遇到 <c>rz</c>:必须原样把字节喂回终端,而不是接管后卡死。
    /// 宁可让用户看到乱码,也不能把终端永久吞掉。
    /// </summary>
    [TestMethod]
    public void Router_RzWithoutUploadPicker_PassesBytesThroughInsteadOfHijacking()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        var router = new TerminalTransferRouter(shell, () => new RouterTestSink());

        byte[] zrinit = ZModemFrameWriter.Write(
            new ZModemHeader(ZModemFrameType.ZRINIT, 0, 0, 0, 0x23),
            ZModemHeaderFormat.Hex);
        TransferRouteResult route = router.ProcessIncoming(zrinit);

        Assert.IsFalse(route.SessionStarted);
        Assert.IsFalse(router.IsInSession);
        Assert.AreSequenceEqual(zrinit, route.TerminalBytes);
    }

    private static TerminalTransferRouter NewRouter(bool withUploadPicker = false)
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        return new(shell, () => new RouterTestSink(), withUploadPicker ? () => new RouterTestSource() : null);
    }

    /// <summary>
    /// X/YMODEM 在链路上没有任何引导序列,自动触发只能靠命令行这一路信号:用户敲下
    /// <c>sb file</c> 就该直接开会话(WindTerm 对 <c>rx</c>/<c>sx</c>/<c>rb</c>/<c>sb</c> 同此)。
    /// </summary>
    [TestMethod]
    public void Router_UserTypedSb_StartsYModemSessionFromCommandLine()
    {
        TerminalTransferRouter router = NewRouter();
        try
        {
            Assert.IsTrue(router.NoteCommandSubmitted("sb payload.log"));
            Assert.IsTrue(router.IsInSession);

            // 会话已接管:入站字节全部转交引擎,终端不再喂。
            TransferRouteResult route = router.ProcessIncoming("protocol bytes"u8.ToArray());
            Assert.IsEmpty(route.TerminalBytes);
        }
        finally
        {
            router.CancelActiveSession();
        }
    }

    /// <summary>上传方向没接线文件选择器时不得开会话(否则引擎起来就没文件可发)。</summary>
    [TestMethod]
    public void Router_UserTypedRb_WithoutUploadPicker_DoesNotStart()
    {
        TerminalTransferRouter router = NewRouter();

        Assert.IsFalse(router.NoteCommandSubmitted("rb"));
        Assert.IsFalse(router.IsInSession);
    }

    /// <summary>普通命令不得触发任何东西。</summary>
    [TestMethod]
    public void Router_OrdinaryCommand_StartsNothing()
    {
        TerminalTransferRouter router = NewRouter(withUploadPicker: true);

        Assert.IsFalse(router.NoteCommandSubmitted("ls -la *.txt"));
        Assert.IsFalse(router.IsInSession);
    }

    /// <summary>
    /// ZMODEM 不靠命令行启动(输出流里有引导,自动检测更可靠),命令行只用来放宽判据:
    /// 敲过 <c>sz</c> 之后,帧头后面跟着别的输出也照样认。
    /// </summary>
    [TestMethod]
    public void Router_UserTypedSz_RelaxesDetectorWithoutStartingSession()
    {
        TerminalTransferRouter router = NewRouter();
        try
        {
            Assert.IsFalse(router.NoteCommandSubmitted("sz report.pdf"));
            Assert.IsFalse(router.IsInSession);

            byte[] input = [.. Zrqinit(), .. "trailing shell output"u8.ToArray()];
            TransferRouteResult route = router.ProcessIncoming(input);

            Assert.IsTrue(route.SessionStarted);
        }
        finally
        {
            router.CancelActiveSession();
        }
    }

    /// <summary>
    /// 同样的字节,没敲过命令时不认(尾锚定生效)—— 这正是「<c>cat</c> 到 ZMODEM 抓包
    /// 不该劫持终端」那条守卫。
    /// </summary>
    [TestMethod]
    public void Router_WithoutCommandSignal_UnanchoredHeaderIsNotHijacked()
    {
        TerminalTransferRouter router = NewRouter();
        byte[] input = [.. Zrqinit(), .. "trailing shell output"u8.ToArray()];

        TransferRouteResult route = router.ProcessIncoming(input);

        Assert.IsFalse(route.SessionStarted);
        Assert.AreSequenceEqual(input, route.TerminalBytes);
    }

    /// <summary>
    /// 命令行是<b>加分信号而非硬闸门</b>:脚本 / 别名 / <c>make upload</c> 里调起的 <c>sz</c>
    /// 压根不经过命令行这一路,它的引导仍须照常被自动检测到。
    /// </summary>
    [TestMethod]
    public void Router_ScriptInvokedSz_StillDetectedWithoutCommandSignal()
    {
        TerminalTransferRouter router = NewRouter();
        try
        {
            TransferRouteResult route = router.ProcessIncoming(Zrqinit());

            Assert.IsTrue(route.SessionStarted);
        }
        finally
        {
            router.CancelActiveSession();
        }
    }

    [TestMethod]
    public async Task Router_DetectsAndReceivesFile_EndToEnd()
    {
        // A shell stream whose WriteAsync captures the receiver's protocol replies and
        // whose reads are irrelevant (the router feeds the engine via ProcessIncoming).
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        var fromReceiver = new MemoryStream();
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                byte[] buf = callInfo.ArgAt<byte[]>(0);
                int off = callInfo.ArgAt<int>(1);
                int cnt = callInfo.ArgAt<int>(2);
                lock (fromReceiver)
                {
                    fromReceiver.Write(buf, off, cnt);
                }
                return Task.CompletedTask;
            });

        var sink = new RouterTestSink();
        var completed = new TaskCompletionSource<FileTransferSession>();
        var router = new TerminalTransferRouter(shell, () => sink);
        router.SessionEnded += s => completed.TrySetResult(s);

        // Build a full sz-style byte stream: ZRQINIT + ZFILE + ZDATA + ZEOF + ZFIN.
        byte[] content = Encoding.UTF8.GetBytes("router end-to-end payload\n");
        byte[] stream = BuildSenderStream("router.txt", content);

        // Feed it in as terminal output; the router should detect and take over.
        TransferRouteResult route = router.ProcessIncoming("prompt$ ".U8Array());
        Assert.AreSequenceEqual("prompt$ ".U8Array(), route.TerminalBytes);

        TransferRouteResult route2 = router.ProcessIncoming(stream);
        Assert.IsTrue(route2.SessionStarted);
        Assert.IsEmpty(route2.TerminalBytes);

        FileTransferSession session = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(FileTransferState.Completed, session.Status);
        Assert.IsTrue(sink.Completed.ContainsKey("router.txt"));
        Assert.AreSequenceEqual(content, sink.Completed["router.txt"]);
    }

    // Builds a complete non-interactive ZMODEM send stream (receiver drives ZRPOS via ACKs
    // it writes to the shell mock; since our stream is pre-canned we use no-ACK subpackets and
    // a single ZDATA frame, which the receiver handles without needing to gate on ZRPOS).
    private static byte[] BuildSenderStream(string name, byte[] content)
    {
        var wire = new List<byte>();
        wire.AddRange(ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZRQINIT), ZModemHeaderFormat.Hex));
        wire.AddRange(ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZFILE), ZModemHeaderFormat.Binary32));
        var info = new List<byte>();
        info.AddRange(Encoding.ASCII.GetBytes(name));
        info.Add(0);
        info.AddRange(Encoding.ASCII.GetBytes($"{content.Length} 0 0 0 0 {content.Length}"));
        info.Add(0);
        wire.AddRange(ZModemSubpacket.Write(info.ToArray(), ZModemSubpacketEnd.EndNoAck, useCrc32: true));
        wire.AddRange(ZModemFrameWriter.Write(ZModemHeader.WithPosition(ZModemFrameType.ZDATA, 0), ZModemHeaderFormat.Binary32));
        wire.AddRange(ZModemSubpacket.Write(content, ZModemSubpacketEnd.EndNoAck, useCrc32: true));
        wire.AddRange(ZModemFrameWriter.Write(ZModemHeader.WithPosition(ZModemFrameType.ZEOF, (uint)content.Length), ZModemHeaderFormat.Binary32));
        wire.AddRange(ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZFIN), ZModemHeaderFormat.Hex));
        wire.AddRange([0x4F, 0x4F]);
        return [.. wire];
    }

    /// <summary>上传方向的最小桩:只用来证明「接线了就能起会话」,不发任何文件。</summary>
    private sealed class RouterTestSource : IFileTransferSource
    {
        public ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OutgoingTransferFile>>([]);

        public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());
    }

    private sealed class RouterTestSink : IFileTransferSink
    {
        private readonly Dictionary<Guid, MemoryStream> _streams = [];
        public Dictionary<string, byte[]> Completed { get; } = [];

        public ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
            TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken)
        {
            _streams[item.Id] = new MemoryStream();
            return ValueTask.FromResult((TransferFileDisposition.Accept, 0L));
        }

        public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _streams[item.Id].Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken)
        {
            Completed[item.FileName] = _streams[item.Id].ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

internal static class Utf8TestExtensions
{
    public static byte[] U8Array(this string s) => Encoding.UTF8.GetBytes(s);
}

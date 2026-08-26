using System.Text;
using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Terminal.FileTransfer;
using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("ZModem")]
public class TransferRouterTests
{
    private static readonly byte[] Signature = ZModemConstants.ReceiveInitSignature.ToArray();

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
    public void Detector_SignatureInStream_SplitsTerminalAndProtocol()
    {
        var detector = new ZModemDetector();
        byte[] prefix = "rz\r"u8.ToArray();
        byte[] input = [.. prefix, .. Signature, 0x30, 0x31];

        ZModemDetectResult result = detector.Process(input);

        Assert.IsTrue(result.Detected);
        Assert.AreSequenceEqual(prefix, result.TerminalBytes);
        Assert.HasCount(Signature.Length + 2, result.ProtocolBytes);
        Assert.AreSequenceEqual(Signature, result.ProtocolBytes[..Signature.Length]);
    }

    [TestMethod]
    public void Detector_SignatureSplitAcrossChunks_WithholdsThenDetects()
    {
        var detector = new ZModemDetector();
        // First chunk ends mid-signature: "**\x18" then next chunk "B00".
        byte[] first = [.. "noise"u8.ToArray(), Signature[0], Signature[1], Signature[2]];
        ZModemDetectResult r1 = detector.Process(first);

        Assert.IsFalse(r1.Detected);
        // The 3 signature-prefix bytes must be withheld, only "noise" fed.
        Assert.AreSequenceEqual("noise"u8.ToArray(), r1.TerminalBytes);

        byte[] second = [Signature[3], Signature[4], Signature[5], 0x30, 0x30];
        ZModemDetectResult r2 = detector.Process(second);

        Assert.IsTrue(r2.Detected);
        Assert.IsEmpty(r2.TerminalBytes);
        Assert.AreSequenceEqual(Signature, r2.ProtocolBytes[..Signature.Length]);
    }

    [TestMethod]
    public void Detector_FalsePrefixAtEnd_IsReleasedOnFlush()
    {
        var detector = new ZModemDetector();
        // Ends with the first 2 signature bytes ("**") — withheld pending more.
        byte[] input = [.. "data**"u8.ToArray()];
        ZModemDetectResult r = detector.Process(input);
        Assert.IsFalse(r.Detected);
        Assert.AreSequenceEqual("data"u8.ToArray(), r.TerminalBytes);

        // If the session never materializes, Flush returns the withheld bytes.
        byte[] flushed = detector.Flush();
        Assert.AreSequenceEqual("**"u8.ToArray(), flushed);
    }

    /// <summary>
    /// 远端 <c>rz</c> 发的是 ZRINIT(<c>**\x18B01</c>)而不是 ZRQINIT,必须被识别为「本地发送」。
    /// 回归:检测器此前只匹配 ZRQINIT,<c>rz</c> 完全检测不到 —— 它的 ZRINIT 会被当成普通输出
    /// 渲染成满屏乱码,上传功能形同不存在。
    /// </summary>
    [TestMethod]
    public void Detector_RzZrinitSignature_TriggersSendDirection()
    {
        var detector = new ZModemDetector();
        byte[] zrinit = ZModemConstants.SendInitSignature.ToArray();
        byte[] input = [.. "rz waiting to receive.**\b\b\b"u8.ToArray(), .. zrinit, 0x30, 0x30];

        ZModemDetectResult result = detector.Process(input);

        Assert.IsTrue(result.Detected);
        Assert.AreEqual(ZModemTrigger.Send, result.Trigger);
        Assert.AreSequenceEqual(zrinit, result.ProtocolBytes[..zrinit.Length]);
    }

    /// <summary>远端 <c>sz</c> 的 ZRQINIT 必须被识别为「本地接收」。</summary>
    [TestMethod]
    public void Detector_SzZrqinitSignature_TriggersReceiveDirection()
    {
        var detector = new ZModemDetector();
        byte[] input = [.. "rz\r"u8.ToArray(), .. Signature, 0x30, 0x30];

        ZModemDetectResult result = detector.Process(input);

        Assert.IsTrue(result.Detected);
        Assert.AreEqual(ZModemTrigger.Receive, result.Trigger);
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

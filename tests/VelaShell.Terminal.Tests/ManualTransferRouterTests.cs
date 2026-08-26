using System.Text;
using NSubstitute;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.Ssh;
using VelaShell.Terminal.FileTransfer;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// XMODEM / YMODEM 的手动接管路径。这一族协议没有可自动识别的引导序列,只能由用户在远端敲好
/// <c>sb</c>/<c>rb</c> 后从命令面板手动发起,因此这条路径的可用性判定与接管行为必须单独钉住。
/// </summary>
[TestClass]
[TestCategory("XYModem")]
public class ManualTransferRouterTests
{
    private const byte SOH = 0x01;
    private const byte EOT = 0x04;
    private const byte ACK = 0x06;
    private const byte C = 0x43;
    private const byte SUB = 0x1A;

    /// <summary>没接线上传能力时,发送方向的手动传输必须被明确拒绝而不是开一个跑不动的会话。</summary>
    [TestMethod]
    public void StartManualSession_SendWithoutSource_IsRejected()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        var router = new TerminalTransferRouter(shell, () => new CapturingSink());

        TransferStartFailure result =
            router.StartManualSession(TerminalTransferProtocol.YModem, FileTransferDirection.Send);

        Assert.AreEqual(TransferStartFailure.NotWired, result);
        Assert.IsFalse(router.IsInSession);
        Assert.IsFalse(router.CanSend);
    }

    /// <summary>已经有会话在跑时不能再叠一个 —— 两个引擎抢同一条字节流必然互相撕碎。</summary>
    [TestMethod]
    public void StartManualSession_WhileBusy_IsRejected()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        var router = new TerminalTransferRouter(shell, () => new CapturingSink());

        Assert.AreEqual(
            TransferStartFailure.None,
            router.StartManualSession(TerminalTransferProtocol.YModem, FileTransferDirection.Receive));
        Assert.IsTrue(router.IsInSession);
        Assert.AreEqual(
            TransferStartFailure.AlreadyInSession,
            router.StartManualSession(TerminalTransferProtocol.XModem, FileTransferDirection.Receive));

        router.CancelActiveSession();
    }

    /// <summary>
    /// 手动发起 YMODEM 接收的完整链路:路由器接管字节流 → 引擎向 shell 写出 <c>'C'</c> →
    /// 我们扮演 <c>sb</c> 把手工拼的块喂进 ProcessIncoming → 文件落地、会话干净收束,
    /// 且跟在协议块后面的 shell 提示符被交还终端。
    /// </summary>
    [TestMethod]
    public async Task ManualYModemReceive_TakesOverAndLandsFile()
    {
        var outbound = new MemoryStream();
        var outboundSignal = new SemaphoreSlim(0);
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (outbound)
                {
                    outbound.Write(call.ArgAt<byte[]>(0), call.ArgAt<int>(1), call.ArgAt<int>(2));
                }
                outboundSignal.Release();
                return Task.CompletedTask;
            });

        var sink = new CapturingSink();
        var completed = new TaskCompletionSource<FileTransferSession>();
        var router = new TerminalTransferRouter(shell, () => sink);
        router.SessionEnded += s => completed.TrySetResult(s);

        Assert.AreEqual(
            TransferStartFailure.None,
            router.StartManualSession(TerminalTransferProtocol.YModem, FileTransferDirection.Receive));

        // 引擎起步后应主动写出握手字符 'C'。
        await WaitForOutboundAsync(outboundSignal, outbound, 1);
        Assert.AreEqual(C, ReadOutbound(outbound)[0], "YMODEM 接收方必须先发 'C'");

        byte[] content = Encoding.UTF8.GetBytes("manual ymodem 内容");
        router.ProcessIncoming(BlockZero("manual.txt", content.Length));
        await WaitForOutboundAsync(outboundSignal, outbound, 2); // ACK + 'C'

        router.ProcessIncoming(DataBlock(1, content));
        await WaitForOutboundAsync(outboundSignal, outbound, 3); // ACK

        // EOT、批结束块与之后的 shell 提示符在同一个分片里到达 —— 真实链路上就是这样。
        byte[] prompt = "\r\nuser@host:~$ "u8.ToArray();
        router.ProcessIncoming(new byte[] { EOT });
        await WaitForOutboundAsync(outboundSignal, outbound, 4); // ACK(EOT)+ 'C'(下一轮握手)

        var tailChunk = new List<byte>();
        tailChunk.AddRange(TerminatorBlock());
        tailChunk.AddRange(prompt);
        router.ProcessIncoming(tailChunk.ToArray());

        FileTransferSession session = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(FileTransferState.Completed, session.Status);
        Assert.AreEqual(TerminalTransferProtocol.YModem, session.Protocol);
        Assert.IsTrue(sink.Completed.ContainsKey("manual.txt"));
        Assert.AreSequenceEqual(content, sink.Completed["manual.txt"]);
        Assert.AreSequenceEqual(prompt, router.TakeRecoveredBytes(), "协议块之后的提示符必须交还终端");
        Assert.IsFalse(router.IsInSession, "会话结束后路由器必须复位回常态");
    }

    /// <summary>会话进行中,入站字节全部转交引擎,一个也不能漏进终端。</summary>
    [TestMethod]
    public void DuringManualSession_NoBytesReachTerminal()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        var router = new TerminalTransferRouter(shell, () => new CapturingSink());
        router.StartManualSession(TerminalTransferProtocol.XModem, FileTransferDirection.Receive);

        TransferRouteResult route = router.ProcessIncoming("这些字节属于协议"u8.ToArray());

        Assert.IsEmpty(route.TerminalBytes);
        router.CancelActiveSession();
    }

    private static async Task WaitForOutboundAsync(SemaphoreSlim signal, MemoryStream outbound, int expectedBytes)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (outbound)
            {
                if (outbound.Length >= expectedBytes)
                {
                    return;
                }
            }
            await signal.WaitAsync(cts.Token);
        }
    }

    private static byte[] ReadOutbound(MemoryStream outbound)
    {
        lock (outbound)
        {
            return outbound.ToArray();
        }
    }

    /// <summary>按 ymodem.txt 手工拼块:引导 + 块号 + 块号取反 + 定长负载 + CRC16(大端)。</summary>
    private static byte[] Block(int number, ReadOnlySpan<byte> content, byte padding)
    {
        byte[] payload = new byte[128];
        payload.AsSpan().Fill(padding);
        content.CopyTo(payload);
        var wire = new List<byte> { SOH, (byte)(number & 0xFF), (byte)~(byte)(number & 0xFF) };
        wire.AddRange(payload);
        ushort crc = Crc16Xmodem.Compute(payload);
        wire.Add((byte)(crc >> 8));
        wire.Add((byte)(crc & 0xFF));
        return [.. wire];
    }

    private static byte[] BlockZero(string name, long size)
    {
        var content = new List<byte>();
        content.AddRange(Encoding.UTF8.GetBytes(name));
        content.Add(0);
        content.AddRange(Encoding.ASCII.GetBytes($"{size} 0 644 0 0 0"));
        content.Add(0);
        return Block(0, [.. content], 0x00);
    }

    private static byte[] DataBlock(int number, ReadOnlySpan<byte> content) => Block(number, content, SUB);

    private static byte[] TerminatorBlock() => Block(0, [], 0x00);

    private sealed class CapturingSink : IFileTransferSink
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

using System.Buffers;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Ssh;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Terminal.FileTransfer;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("TerminalBridge")]
public class TerminalBridgeTests
{
    private readonly ITerminalEmulator _terminal;
    private readonly IShellStreamWrapper _shellStream;

    public TerminalBridgeTests()
    {
        _terminal = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
    }

    /// <summary>轮询等待条件成立(读循环在后台线程,无确定性探针时用它替代长睡眠)。</summary>
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }
        Assert.IsTrue(condition(), $"条件在 {timeoutMs}ms 内未成立。");
    }

    [TestMethod]
    public void Constructor_NullTerminal_ThrowsArgumentNullException()
    {
        SshTerminalBridge act() => new(null!, _shellStream);
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>((Func<SshTerminalBridge>)act);
        Assert.AreEqual("terminal", ex.ParamName);
    }

    [TestMethod]
    public void Constructor_NullShellStream_ThrowsArgumentNullException()
    {
        SshTerminalBridge act() => new(_terminal, null!);
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>((Func<SshTerminalBridge>)act);
        Assert.AreEqual("shellStream", ex.ParamName);
    }

    [TestMethod]
    public void Start_CalledTwice_ThrowsInvalidOperationException()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        void act() => bridge.Start();
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(act);
        Assert.Contains("already started", ex.Message);
    }

    [TestMethod]
    public async Task UserInput_WritesToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        byte[] testData = Encoding.UTF8.GetBytes("hello");

        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        await bridge.DrainWritesAsync();

        await _shellStream.Received().WriteAsync(
            testData,
            0,
            testData.Length,
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UserInput_FlushesAfterWrite()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        byte[] testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        await bridge.DrainWritesAsync();

        _shellStream.Received().Flush();
    }

    [TestMethod]
    public async Task Start_DoesNotPrimeShell_SoTheInitialPromptIsNotDuplicated()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        await bridge.DrainWritesAsync();

        // The server already emits its banner + prompt on connect; sending an extra newline
        // would produce a duplicate prompt line, so Start must not write anything.
        await _shellStream.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void UserInput_WhenDisposed_DoesNotWriteToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Dispose();

        _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UserInput_WhenStreamCannotWrite_DoesNotWrite()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        byte[] testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        await bridge.DrainWritesAsync();

        await _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void ReadLoop_WhenCanReadFalse_ExitsImmediately()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        // Task.Run in Start() needs time to enter and exit the loop
        Thread.Sleep(200);

        _shellStream.DidNotReceive().ReadAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void ReadLoop_WhenReadReturnsZero_ExitsGracefully()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(200);

        _terminal.DidNotReceive().Feed(Arg.Any<byte[]>());
    }

    [TestMethod]
    public void ReadLoop_WhenExceptionOccurs_FiresErrorEvent()
    {
        var expectedException = new IOException("connection lost");
        Exception? capturedError = null;

        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;
        bridge.Start();

        WaitUntil(() => capturedError is not null);

        Assert.AreSame(expectedException, capturedError);
    }

    [TestMethod]
    public void Dispose_CancelsReadLoopAndDisposesStream()
    {
        _shellStream.CanRead.Returns(true);

        // ReadAsync blocks forever until CancellationToken fires
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                CancellationToken ct = callInfo.ArgAt<CancellationToken>(3);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(100);

        bridge.Dispose();

        _shellStream.Received().Dispose();
    }

    [TestMethod]
    public void ReadLoop_WhenRemoteCloses_FiresClosed()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0)); // EOF => remote closed the channel

        bool closed = false;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += _ => closed = true;
        bridge.Start();

        WaitUntil(() => closed);
    }

    /// <summary>关闭原因原样转述给宿主,不在桥里被抹平(#383)。</summary>
    /// <remarks>
    /// 桥自己无从分辨 exit 与掉线 —— 两者在它眼里都是"读到 0"。知道差别的是流,
    /// 所以桥的职责只有一条:把流给出的结论完整带上去,让宿主据此决定要不要自动重连。
    /// </remarks>
    [TestMethod]
    public void ReadLoop_WhenRemoteShellExits_ReportsThatReason()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.CloseReason.Returns(ShellCloseReason.RemoteExited);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        ShellCloseReason? reported = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += reason => reported = reason;
        bridge.Start();

        WaitUntil(() => reported is not null);
        Assert.AreEqual(ShellCloseReason.RemoteExited, reported);
    }

    /// <summary>读取抛异常的那条路不可能是"shell 正常退出",一律报连接中断。</summary>
    [TestMethod]
    public void ReadLoop_WhenReadThrows_ReportsConnectionLost()
    {
        _shellStream.CanRead.Returns(true);

        // 流可能已经把原因记成别的了;抛出来的这一路不看它,直接下结论。
        _shellStream.CloseReason.Returns(ShellCloseReason.RemoteExited);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new IOException("connection reset"));

        ShellCloseReason? reported = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += reason => reported = reason;
        bridge.Start();

        WaitUntil(() => reported is not null);
        Assert.AreEqual(ShellCloseReason.ConnectionLost, reported);
    }

    [TestMethod]
    public void Dispose_DoesNotFireClosed()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                CancellationToken ct = callInfo.ArgAt<CancellationToken>(3);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });

        bool closed = false;
        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += _ => closed = true;
        bridge.Start();

        Thread.Sleep(100);
        bridge.Dispose(); // intentional teardown must not look like a remote close

        Thread.Sleep(100);

        Assert.IsFalse(closed);
    }

    [TestMethod]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        _shellStream.CanRead.Returns(false);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);

        bridge.Dispose();
        bridge.Dispose();
    }

    [TestMethod]
    public async Task UserInput_RapidKeystrokes_NeverWriteConcurrently_AndPreserveByteOrder()
    {
        // 回归防护:Tmds.Ssh 的通道写没有并发防护,桥必须把击键写串行化。
        // 旧实现对每个按键即发即忘地 WriteAsync,上一个写因网络延迟挂起时下一个按键
        // 就并发插队 → 字节乱序抵达远端 → 回显出"字符拆散跳动"(docker status 事故)。
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);

        object gate = new();
        int inFlight = 0, maxInFlight = 0;
        var received = new MemoryStream();
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                lock (gate)
                {
                    inFlight++;
                    maxInFlight = Math.Max(maxInFlight, inFlight);
                    received.Write(call.ArgAt<byte[]>(0), call.ArgAt<int>(1), call.ArgAt<int>(2));
                }

                // 模拟发送窗口收紧/网络延迟:写挂起期间后续按键持续到达。
                await Task.Delay(30);
                lock (gate)
                {
                    inFlight--;
                }
            });

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        byte[] typed = Encoding.UTF8.GetBytes("docker status");
        foreach (byte b in typed)
        {
            _terminal.UserInput += Raise.Event<Action<byte[]>>(new[] { b });
        }

        await bridge.DrainWritesAsync();

        lock (gate)
        {
            Assert.AreEqual(1, maxInFlight, "出站写必须串行:任意时刻至多一个 WriteAsync 在途。");
            Assert.AreEqual("docker status", Encoding.UTF8.GetString(received.ToArray()), "字节必须按击键顺序完整送达。");
        }
    }

    /// <summary>
    /// 让替身流按序吐出给定分块,吐完后阻塞(模拟"连接还在,只是没有新输出")。
    /// </summary>
    private void ScriptReads(params byte[][] chunks)
    {
        int index = 0;
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(async call =>
                    {
                        if (index >= chunks.Length)
                        {
                            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
                            return 0;
                        }
                        byte[] chunk = chunks[index++];
                        chunk.CopyTo(call.ArgAt<byte[]>(0), call.ArgAt<int>(1));
                        return chunk.Length;
                    });
    }

    [TestMethod]
    public void ReadLoop_PooledChunks_NeverLeakBytesBeyondTheReadLength()
    {
        // 读循环的每块副本改成租自 ArrayPool 之后,拿到的数组几乎总是比请求的长
        // (池按 2 的幂分桶:请求 13 字节给 16 字节),而且带着上一位租客的残留数据。
        // 任何一处用 Buffer.Length 而非实际读到的长度,就会把这些垃圾字节当成服务器输出
        // 送进终端/录制。这里先把池里的桶全填成 0xFF,再断言旁路记录到的字节逐字精确。
        for (int size = 16; size <= 32 * 1024; size *= 2)
        {
            byte[] poison = ArrayPool<byte>.Shared.Rent(size);
            poison.AsSpan().Fill(0xFF);
            ArrayPool<byte>.Shared.Return(poison);
        }

        byte[] first = Encoding.UTF8.GetBytes("hello");        // 5 字节 → 池至少给 16
        byte[] second = Encoding.UTF8.GetBytes("world!");       // 6 字节
        ScriptReads(first, second);

        var logged = new MemoryStream();
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.DataReceived += chunk =>
        {
            lock (logged)
            {
                logged.Write(chunk);
            }
        };
        bridge.Start();

        WaitUntil(() =>
        {
            lock (logged)
            {
                return logged.Length >= first.Length + second.Length;
            }
        });

        lock (logged)
        {
            byte[] actual = logged.ToArray();
            Assert.AreEqual(
                "helloworld!",
                Encoding.UTF8.GetString(actual),
                "旁路记录混进了读长度之外的字节 —— 池租数组的尾部残留被当成服务器输出了。");
            Assert.DoesNotContain(
                (byte)0xFF,
                actual,
                "记录里出现了池毒化字节,说明某处用了 Buffer.Length 而不是实际读到的长度。");
        }
    }

    [TestMethod]
    public void DataReceived_StripsInjectedInitCommandEcho_SoRecordingsAndLogsMatchTheScreen()
    {
        // 会话录制/会话日志挂的是读线程上的原始流,拿不到显示路径抑制后的结果 ——
        // 于是注入的初始化脚本在终端里隐形,回放时却整行冒出来(用户反馈)。
        // 记录到的字节必须与屏幕所见一致。
        const string injected = "prompt_nl() { local c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";
        byte[] needle = Encoding.UTF8.GetBytes(injected + "\r\n");

        ScriptReads(
            Encoding.UTF8.GetBytes("Last login: Mon Jul 27 11:57:35 2026\r\n"),
            Encoding.UTF8.GetBytes(" " + injected + "\r\n"), // tty 回显:前导空格 + 命令 + CRLF
            Encoding.UTF8.GetBytes("[root@192 ~]# "));

        var logged = new MemoryStream();
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.DataReceived += chunk =>
        {
            lock (logged)
            {
                logged.Write(chunk);
            }
        };
        bridge.SuppressEchoOnce(needle);
        bridge.Start();

        WaitUntil(() =>
        {
            lock (logged)
            {
                return Encoding.UTF8.GetString(logged.ToArray()).Contains("[root@192 ~]#", StringComparison.Ordinal);
            }
        });

        lock (logged)
        {
            string text = Encoding.UTF8.GetString(logged.ToArray());
            Assert.DoesNotContain("prompt_nl", text, "注入的初始化脚本不得进入会话录制/日志。");
            Assert.Contains("Last login", text, "真实的服务器输出必须原样保留。");
            Assert.Contains("[root@192 ~]#", text, "提示符必须原样保留。");
        }
    }

    [TestMethod]
    public void DataReceived_EchoSplitAcrossChunks_IsStillStripped()
    {
        // 回显会被网络任意切开;跨块的部分命中必须扣住续判,而不是漏半行进录制。
        const string injected = "prompt_nl() { local c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";
        byte[] needle = Encoding.UTF8.GetBytes(injected + "\r\n");
        string echo = injected + "\r\n";

        ScriptReads(
            Encoding.UTF8.GetBytes(echo[..20]),
            Encoding.UTF8.GetBytes(echo[20..]),
            Encoding.UTF8.GetBytes("[root@192 ~]# "));

        var logged = new MemoryStream();
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.DataReceived += chunk =>
        {
            lock (logged)
            {
                logged.Write(chunk);
            }
        };
        bridge.SuppressEchoOnce(needle);
        bridge.Start();

        WaitUntil(() =>
        {
            lock (logged)
            {
                return Encoding.UTF8.GetString(logged.ToArray()).Contains("[root@192 ~]#", StringComparison.Ordinal);
            }
        });

        lock (logged)
        {
            Assert.AreEqual("[root@192 ~]# ", Encoding.UTF8.GetString(logged.ToArray()));
        }
    }

    /// <summary>构造一个已进入会话态的路由器(喂 ZRQINIT 触发接收会话)。</summary>
    private TerminalTransferRouter StartInSessionRouter()
    {
        var router = new TerminalTransferRouter(_shellStream, () => Substitute.For<IFileTransferSink>());
        byte[] zrqinit = ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZRQINIT), ZModemHeaderFormat.Hex);
        TransferRouteResult route = router.ProcessIncoming(zrqinit);
        Assert.IsTrue(route.SessionStarted);
        Assert.IsTrue(router.IsInSession);
        return router;
    }

    [TestMethod]
    public async Task UserInput_DuringFileTransferSession_IsDroppedNotWritten()
    {
        // 会话期间击键混进协议流会被对端当帧内容解析,必须整段丢弃。
        // 断言字节选 'q':ZMODEM hex 帧只含 0-9a-f 与帧界符,'q' 绝不会由引擎自己写出。
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        var written = new List<byte>();
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (written)
                {
                    written.AddRange(new ArraySegment<byte>(call.ArgAt<byte[]>(0), call.ArgAt<int>(1), call.ArgAt<int>(2)));
                }
                return Task.CompletedTask;
            });

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        TerminalTransferRouter router = StartInSessionRouter();
        bridge.TransferRouter = router;

        _terminal.UserInput += Raise.Event<Action<byte[]>>(Encoding.UTF8.GetBytes("qqq"));
        await bridge.DrainWritesAsync();

        lock (written)
        {
            Assert.DoesNotContain((byte)'q', written, "ZMODEM 会话期间的击键不得写入传输流。");
        }
        router.CancelActiveSession();
    }

    [TestMethod]
    public async Task UserInput_CtrlXDuringFileTransferSession_CancelsSession_ThenInputFlowsAgain()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        TerminalTransferRouter router = StartInSessionRouter();
        bridge.TransferRouter = router;
        var ended = new TaskCompletionSource<FileTransferSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.SessionEnded += s => ended.TrySetResult(s);

        // Ctrl+X(CAN)= 用户中止意图:转成会话取消,而非把裸字节塞进协议流。
        _terminal.UserInput += Raise.Event<Action<byte[]>>(new byte[] { 0x18 });

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        WaitUntil(() => !router.IsInSession);

        // 会话结束后击键恢复正常流动。
        byte[] resumed = Encoding.UTF8.GetBytes("q");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(resumed);
        await bridge.DrainWritesAsync();

        await _shellStream.Received().WriteAsync(resumed, 0, resumed.Length, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UserInput_WriteThrowsObjectDisposed_DoesNotPropagate()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ObjectDisposedException("stream"));

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        byte[] testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        await bridge.DrainWritesAsync();

        // ObjectDisposedException is swallowed per the write loop's contract
        Assert.IsNull(capturedError);
    }

    [TestMethod]
    public async Task UserInput_WriteThrowsGenericException_FiresErrorEvent()
    {
        var expectedException = new InvalidOperationException("write failed");
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        byte[] testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        await bridge.DrainWritesAsync();

        Assert.IsNotNull(capturedError);
        Assert.AreSame(expectedException, capturedError);
    }
}

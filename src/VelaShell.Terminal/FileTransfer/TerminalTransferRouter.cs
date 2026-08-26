using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Ssh;
using VelaShell.Core.XYModem.Model;
using VelaShell.Core.XYModem.Protocol;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Terminal.FileTransfer;

/// <summary>路由器当前所处的状态。</summary>
public enum TransferRoutingState
{
    /// <summary>常态:字节正常喂入 VT 终端,同时监视 ZMODEM 引导。</summary>
    Normal,

    /// <summary>传输会话进行中:字节全部转交引擎,不喂终端。</summary>
    InSession
}

/// <summary>一次字节路由的结果:应喂入终端的字节(会话期间为空)。</summary>
/// <param name="TerminalBytes">应喂入 VT 终端的字节。</param>
/// <param name="SessionStarted">本次调用是否刚触发了一个传输会话。</param>
public readonly record struct TransferRouteResult(byte[] TerminalBytes, bool SessionStarted);

/// <summary>手动启动传输会话失败的原因。</summary>
public enum TransferStartFailure
{
    /// <summary>启动成功。</summary>
    None,

    /// <summary>已经有一个传输会话在跑了。</summary>
    AlreadyInSession,

    /// <summary>宿主没有接线对应方向的文件选择能力(上传缺 source,下载缺 sink)。</summary>
    NotWired
}

/// <summary>
/// 终端侧文件传输路由器:插在桥的读循环与终端喂入之间,是三种协议共用的接管入口。
/// <para>
/// ZMODEM 会<b>自动</b>接管:常态下监视输出流里的 ZMODEM 引导(远端 <c>sz</c> 的 ZRQINIT /
/// 远端 <c>rz</c> 的 ZRINIT),一旦命中就切入会话态。
/// </para>
/// <para>
/// XMODEM / YMODEM 只能<b>手动</b>接管(见 <see cref="StartManualSession" />):它们在链路上
/// 没有可识别的引导序列 —— <c>sb</c>/<c>sx</c> 启动后静默等待接收方发 <c>'C'</c>,而 <c>rb</c>/<c>rx</c>
/// 只吐裸 <c>'C'</c>,在终端输出里与普通字符毫无区别,任何自动检测都必然误触发。
/// </para>
/// 会话期间入站字节改喂 <see cref="ShellStreamByteDuplex" />,由后台任务上的引擎消费,
/// 终端停止喂入;会话结束后自动复位回常态。设计为传输无关,SSH / ConPTY / 串口 / Telnet 通用。
/// </summary>
public sealed class TerminalTransferRouter(
    IShellStreamWrapper shellStream,
    Func<IFileTransferSink> sinkFactory,
    Func<IFileTransferSource>? sourceFactory = null,
    ZModemOptions? options = null,
    IFileTransferObserver? observer = null)
{
    private readonly IShellStreamWrapper _shellStream =
        shellStream ?? throw new ArgumentNullException(nameof(shellStream));
    private readonly Func<IFileTransferSink> _sinkFactory =
        sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
    private readonly Func<IFileTransferSource>? _sourceFactory = sourceFactory;
    private readonly ZModemOptions _options = options ?? ZModemOptions.Default;
    private readonly IFileTransferObserver? _observer = observer;
    private readonly ZModemDetector _detector = new();
    private readonly Lock _gate = new();

    private TransferRoutingState _state = TransferRoutingState.Normal;
    private ShellStreamByteDuplex? _duplex;
    private CancellationTokenSource? _sessionCts;
    private byte[] _recovered = [];

    /// <summary>当前是否处于传输会话中。</summary>
    public bool IsInSession
    {
        get
        {
            lock (_gate)
            {
                return _state == TransferRoutingState.InSession;
            }
        }
    }

    /// <summary>宿主是否接线了上传能力(未接线时遇到远端 <c>rz</c> 不接管,手动上传也不可用)。</summary>
    public bool CanSend => _sourceFactory is not null;

    /// <summary>会话结束(成功 / 失败 / 取消)时触发,便于宿主刷新 UI 与恢复终端焦点。</summary>
    public event Action<FileTransferSession>? SessionEnded;

    /// <summary>
    /// 常态零拷贝快路径:未处于传输会话且检测器判定本块可直通时为 true,
    /// 调用方(读循环)可把原始块原样喂终端,完全绕过 <see cref="ProcessIncoming" />
    /// 的窗口拼接与切片。
    /// </summary>
    /// <remarks>
    /// ZMODEM 会话只会在同一读线程的 <see cref="ProcessIncoming" /> 里启动,判定与直喂之间
    /// 不存在竞态。<see cref="StartManualSession" /> 来自 UI 线程,理论上存在「判定为可直通、
    /// 直喂之前会话开了」的一帧窗口;但 XMODEM/YMODEM 的对端在我们主动写出握手字符之前是沉默的
    /// (接收方向)或只在打印横幅(发送方向,那些字节本就该进终端),这一帧最多让本就属于终端的
    /// 字节进终端,不会吃掉协议字节。
    /// </remarks>
    public bool CanPassThrough(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            return _state != TransferRoutingState.InSession && _detector.CanPassThrough(data);
        }
    }

    /// <summary>
    /// 处理一段来自读循环的原始输出字节,返回应喂入 VT 终端的字节。
    /// 会话期间返回空数组;检测到 ZMODEM 引导时启动会话并把引导及其后字节转交引擎。
    /// </summary>
    /// <param name="data">读循环刚读到的输出字节。</param>
    /// <returns>路由结果(待喂终端字节 + 是否刚启动会话)。</returns>
    public TransferRouteResult ProcessIncoming(ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            if (_state == TransferRoutingState.InSession)
            {
                // 会话进行中:全部转交引擎,终端不喂。
                TransferTrace.LogBytes("RX->engine", data.Span);
                _duplex?.Push(data);
                return new([], false);
            }

            ZModemDetectResult detect = _detector.Process(data.Span);
            if (!detect.Detected)
            {
                return new(detect.TerminalBytes, false);
            }
            TransferTrace.Log($"DETECT trigger={detect.Trigger} terminal={detect.TerminalBytes.Length}B protocol={detect.ProtocolBytes.Length}B");
            TransferTrace.LogBytes("RX->engine(seed)", detect.ProtocolBytes);

            // 远端跑了 rz 但宿主没接线上传能力:不接管,原样喂终端(总比把终端吞掉强)。
            if (detect.Trigger == ZModemTrigger.Send && _sourceFactory is null)
            {
                byte[] passthrough = new byte[detect.TerminalBytes.Length + detect.ProtocolBytes.Length];
                detect.TerminalBytes.CopyTo(passthrough, 0);
                detect.ProtocolBytes.CopyTo(passthrough, detect.TerminalBytes.Length);
                return new(passthrough, false);
            }

            // 命中 ZMODEM 引导:切入会话态,把引导及其后字节喂给引擎。
            FileTransferDirection direction = detect.Trigger == ZModemTrigger.Receive
                ? FileTransferDirection.Receive
                : FileTransferDirection.Send;
            StartSession(TerminalTransferProtocol.ZModem, direction, detect.ProtocolBytes);
            return new(detect.TerminalBytes, true);
        }
    }

    /// <summary>
    /// 手动启动一次传输会话。XMODEM / YMODEM 只能走这条路(它们没有可自动识别的引导);
    /// ZMODEM 也支持手动启动,用于对端已经在等、但引导序列被漏掉的补救场景。
    /// 调用方应先让用户在远端敲好对应命令(下载 <c>sb file</c> / <c>sx file</c>,
    /// 上传 <c>rb</c> / <c>rx</c>),再触发本方法。
    /// </summary>
    /// <param name="protocol">要使用的协议变体。</param>
    /// <param name="direction">传输方向(接收 = 远端发给我们,发送 = 我们上传)。</param>
    /// <returns><see cref="TransferStartFailure.None" /> 表示已启动。</returns>
    public TransferStartFailure StartManualSession(
        TerminalTransferProtocol protocol,
        FileTransferDirection direction)
    {
        lock (_gate)
        {
            if (_state == TransferRoutingState.InSession)
            {
                return TransferStartFailure.AlreadyInSession;
            }
            if (direction == FileTransferDirection.Send && _sourceFactory is null)
            {
                return TransferStartFailure.NotWired;
            }
            // 检测器里可能扣着几个字节(疑似 ZMODEM 引导前缀),它们属于终端而不属于本次会话;
            // 这里主动清掉,避免它们在会话结束时被当成"残余"再喂一次。
            _ = _detector.Flush();
            StartSession(protocol, direction, []);
            return TransferStartFailure.None;
        }
    }

    private void StartSession(
        TerminalTransferProtocol protocol,
        FileTransferDirection direction,
        byte[] initialBytes)
    {
        // 调用方已持有 _gate。
        _state = TransferRoutingState.InSession;
        var duplex = new ShellStreamByteDuplex(_shellStream);
        _duplex = duplex;
        var cts = new CancellationTokenSource();
        _sessionCts = cts;

        if (initialBytes.Length > 0)
        {
            duplex.Push(initialBytes);
        }

        // 引擎跑在后台任务上(读循环线程只负责搬字节,绝不在其上阻塞跑协议)。
        _ = Task.Run(async () =>
        {
            FileTransferSession session;
            try
            {
                session = await RunEngineAsync(protocol, direction, duplex, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TransferTrace.Log($"ENGINE THREW: {ex}");
                session = new FileTransferSession
                {
                    Direction = direction,
                    Protocol = protocol,
                    Status = FileTransferState.Failed
                };
            }
            finally
            {
                await duplex.DisposeAsync().ConfigureAwait(false);
            }
            TransferTrace.Log($"SESSION END protocol={protocol} status={session.Status} items={session.Items.Count}");
            EndSession();
            SessionEnded?.Invoke(session);
        });
    }

    /// <summary>按协议与方向挑选并驱动对应的引擎。</summary>
    private Task<FileTransferSession> RunEngineAsync(
        TerminalTransferProtocol protocol,
        FileTransferDirection direction,
        ShellStreamByteDuplex duplex,
        CancellationToken ct)
    {
        if (protocol == TerminalTransferProtocol.ZModem)
        {
            return direction == FileTransferDirection.Receive
                ? new ZModemReceiver(duplex, _sinkFactory(), _options, _observer).ReceiveAsync(ct)
                : new ZModemSender(duplex, _sourceFactory!(), _options, _observer).SendAsync(ct);
        }

        var xyOptions = new XYModemOptions { Protocol = protocol };
        return direction == FileTransferDirection.Receive
            ? new XYModemReceiver(duplex, _sinkFactory(), xyOptions, _observer).ReceiveAsync(ct)
            : new XYModemSender(duplex, _sourceFactory!(), xyOptions, _observer).SendAsync(ct);
    }

    private void EndSession()
    {
        lock (_gate)
        {
            _state = TransferRoutingState.Normal;
            // 会话结束时通道里可能还压着字节:协议帧和它后面的 shell 输出(sz/rz 退出后的提示符)
            // 常常在同一个网络分片里被一起截走。这些字节不交还终端,用户就会看到"传完了但没提示符"。
            _recovered = _duplex?.DrainPending() ?? [];
            _duplex = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            // 复位检测器,丢弃可能残留的扣留字节(会话字节不应回流终端)。
            _ = _detector.Flush();
        }
    }

    /// <summary>
    /// 取走上一次会话收尾时回收的、应交还终端的字节(见 <see cref="EndSession" />)。
    /// 由宿主在 <see cref="SessionEnded" /> 回调里调用,取一次即清空。
    /// </summary>
    /// <returns>应喂入终端的残余字节;没有则为空数组。</returns>
    public byte[] TakeRecoveredBytes()
    {
        lock (_gate)
        {
            byte[] result = _recovered;
            _recovered = [];
            return result;
        }
    }

    /// <summary>请求取消进行中的会话(用户中止 / 标签关闭)。无会话时为空操作。</summary>
    public void CancelActiveSession()
    {
        lock (_gate)
        {
            _sessionCts?.Cancel();
            _duplex?.CompleteInbound();
        }
    }
}

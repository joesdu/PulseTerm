using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.ZModem.Model;

namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// ZMODEM 接收方状态机:驱动「远端 <c>sz</c> → 本地落地」的完整批量接收流程。
/// 握手 ZRQINIT→ZRINIT,逐文件 ZFILE→(ZRPOS 就绪)→ZDATA 子包→ZEOF,最后 ZFIN 交换 + 消费 <c>OO</c>。
/// 通过 <see cref="IByteDuplex" /> 收发原始字节,经 <see cref="IFileTransferSink" /> 落地,
/// 经 <see cref="IFileTransferObserver" /> 上报进度。全程二进制安全,与 lrzsz 互操作。
/// </summary>
public sealed class ZModemReceiver(
    IByteDuplex duplex,
    IFileTransferSink sink,
    ZModemOptions? options = null,
    IFileTransferObserver? observer = null)
{
    /// <summary>收尾阶段等待发送方 <c>"OO"</c> 的最长时间;等不到也照常结束会话。</summary>
    private static readonly TimeSpan OverAndOutTimeout = TimeSpan.FromSeconds(3);

    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private readonly IFileTransferSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly ZModemOptions _options = options ?? ZModemOptions.Default;
    private readonly IFileTransferObserver? _observer = observer;
    private readonly ZModemFrameReader _reader = new(duplex);

    private bool _useCrc32;

    /// <summary>
    /// 执行完整的接收会话,直到批量结束(ZFIN)、被取消或出错。
    /// </summary>
    /// <param name="cancellationToken">取消整个会话的令牌。</param>
    /// <returns>本次会话的状态与已接收文件清单。</returns>
    public async Task<FileTransferSession> ReceiveAsync(CancellationToken cancellationToken)
    {
        var session = new FileTransferSession { Direction = FileTransferDirection.Receive };
        TransferTrace.Log($"RECEIVER START frameTimeout={_options.FrameTimeout.TotalSeconds}s maxRetries={_options.MaxRetries}");
        _observer?.OnSessionStarted(session);
        session.Status = FileTransferState.Transferring;

        try
        {
            // 主动发送 ZRINIT 触发发送方开始(远端 sz 通常已发 ZRQINIT,收到与否都可回 ZRINIT)。
            await SendZrinitAsync(cancellationToken).ConfigureAwait(false);

            // 握手阶段的超时重试计数:每次超时补发一次 ZRINIT(对端可能漏收),超上限即放弃。
            // 收到第一个文件帧之前都算握手阶段,用更短的预算 —— 此时终端是全黑的,不能让用户干等几分钟。
            int handshakeRetries = 0;
            bool handshakeDone = false;

            while (true)
            {
                ZModemHeaderResult frame = await ReadHeaderAsync(
                        cancellationToken,
                        handshakeDone ? null : _options.HandshakeTimeout)
                    .ConfigureAwait(false);
                TransferTrace.Log(frame.Status == ZModemReadStatus.Header
                    ? $"RECV frame {frame.Header.Type} fmt={frame.Format} pos={frame.Header.Position}"
                    : $"RECV frame status={frame.Status}");
                switch (frame.Status)
                {
                    case ZModemReadStatus.Cancelled:
                        session.Status = FileTransferState.Cancelled;
                        _observer?.OnSessionFailed(session, null);
                        return session;
                    case ZModemReadStatus.EndOfStream:
                        // 通道意外结束:若已收完至少一个文件视作完成,否则失败。
                        Finish(session);
                        return session;
                    case ZModemReadStatus.Timeout:
                        // 对端迟迟不发下一帧:补发 ZRINIT 再等一轮;反复超时即判定握手失败并中止,
                        // 让路由器把终端交还给用户(而不是永久卡在会话态)。
                        if (++handshakeRetries > (handshakeDone ? _options.MaxRetries : _options.HandshakeRetries))
                        {
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM handshake timed out"));
                            await TrySendCancelAsync().ConfigureAwait(false);
                            return session;
                        }
                        await SendZrinitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    case ZModemReadStatus.CrcError:
                        await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZNAK), cancellationToken).ConfigureAwait(false);
                        continue;
                }
                handshakeRetries = 0;

                switch (frame.Header.Type)
                {
                    case ZModemFrameType.ZRQINIT:
                        // 发送方(重)请求:再回一次 ZRINIT。
                        await SendZrinitAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case ZModemFrameType.ZFILE:
                        // 对端开始发文件了:握手完成,后续切到更宽松的数据阶段预算。
                        handshakeDone = true;
                        _useCrc32 = frame.Format == ZModemHeaderFormat.Binary32;
                        bool cont = await ReceiveFileAsync(session, cancellationToken).ConfigureAwait(false);
                        if (!cont)
                        {
                            return session;
                        }
                        break;

                    case ZModemFrameType.ZFIN:
                        // 发送方要结束:回 ZFIN,再消费其 "OO"。
                        await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZFIN), cancellationToken).ConfigureAwait(false);
                        await ConsumeOverAndOutAsync(cancellationToken).ConfigureAwait(false);
                        Finish(session);
                        return session;

                    case ZModemFrameType.ZSINIT:
                        // 发送方声明自己的转义策略与 Attn 序列(sz -e 会发)。它跟着一个数据子包,
                        // 且规范要求接收方回 ZACK —— 不回,sz 会一直重发 ZSINIT 直到超时放弃,
                        // 表现为"传输死活起不来"。这里读掉子包并应答。
                        _useCrc32 = frame.Format == ZModemHeaderFormat.Binary32;
                        await ConsumeZsinitAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case ZModemFrameType.ZCOMPL:
                    case ZModemFrameType.ZSKIP:
                        // 批处理阶段的无害控制帧:继续等待下一帧。
                        break;

                    default:
                        // 其它意外帧:忽略并继续(稳健性)。
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            session.Status = FileTransferState.Cancelled;
            _observer?.OnSessionFailed(session, null);
            await TrySendCancelAsync().ConfigureAwait(false);
            return session;
        }
        catch (Exception ex)
        {
            session.Status = FileTransferState.Failed;
            _observer?.OnSessionFailed(session, ex);
            await TrySendCancelAsync().ConfigureAwait(false);
            return session;
        }
        finally
        {
            // 读取器缓冲里剩下的字节已经不属于本次传输了(多半是 sz 退出后的 shell 提示符),
            // 退回通道让路由器交还终端 —— 否则用户看到的是"传完了但提示符没了"。
            _duplex.Unread(_reader.DrainBuffered());
        }
    }

    /// <summary>读掉 ZSINIT 后面的数据子包(Attn 序列)并回 ZACK,让 <c>sz -e</c> 能继续往下走。</summary>
    private async Task ConsumeZsinitAsync(CancellationToken ct)
    {
        ZModemSubpacketResult attn = await ReadSubpacketAsync(ct).ConfigureAwait(false);
        if (attn.Status is ZModemSubpacketStatus.Cancelled or ZModemSubpacketStatus.EndOfStream)
        {
            return;
        }
        // Attn 序列(接收方在出错时用来打断发送方的字节串)对 SSH 这样的可靠链路没有意义,
        // 我们靠 ZRPOS 就能纠错,所以解析出来也不用;应答本身才是关键。
        await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZACK), ct).ConfigureAwait(false);
    }

    private async Task<bool> ReceiveFileAsync(FileTransferSession session, CancellationToken ct)
    {
        // 读取紧跟 ZFILE 帧头的文件信息数据子包(用当前帧的 CRC 宽度)。
        ZModemSubpacketResult info = await ReadSubpacketAsync(ct).ConfigureAwait(false);
        if (info.Status != ZModemSubpacketStatus.Ok)
        {
            if (info.Status == ZModemSubpacketStatus.Cancelled)
            {
                session.Status = FileTransferState.Cancelled;
                _observer?.OnSessionFailed(session, null);
                return false;
            }
            if (info.Status is ZModemSubpacketStatus.Timeout or ZModemSubpacketStatus.EndOfStream)
            {
                // 文件信息子包始终没到齐:中止,避免无限期占住终端。
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM ZFILE subpacket timed out"));
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
            }
            // 文件信息子包损坏:请求重发该帧。
            await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZNAK), ct).ConfigureAwait(false);
            return true;
        }

        TransferFileMetadata metadata = ParseFileMetadata(info.Data);
        var item = new FileTransferItem { FileName = metadata.FileName, Size = metadata.Size };
        session.AddItem(item);

        // 这两条之间就是「弹保存目录对话框」。只见前者不见后者 = 卡在 UI 提示上,而非协议上。
        TransferTrace.Log($"ZFILE offered name='{metadata.FileName}' size={metadata.Size} -> prompting sink");
        (TransferFileDisposition disposition, long resumeOffset) =
            await _sink.OnFileOfferedAsync(metadata, item, ct).ConfigureAwait(false);
        TransferTrace.Log($"ZFILE disposition={disposition} resumeOffset={resumeOffset}");

        switch (disposition)
        {
            case TransferFileDisposition.Skip:
                item.Status = FileTransferState.Skipped;
                _observer?.OnFileSkipped(item);
                await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZSKIP), ct).ConfigureAwait(false);
                return true;
            case TransferFileDisposition.Abort:
                // 用户中止(如放弃选择保存目录):绝不发 CAN —— CAN 未必能让 sz 停下,它会继续吐协议字节,
                // 会话结束后这些二进制垃圾流回终端,其中的 ESC 序列会把终端切到备用屏幕缓冲区,
                // 表现为"点击焦点后内容全没、输入无效"。改回 ZSKIP 优雅跳过当前文件:sz 收到后跳过并发 ZFIN
                // 干净收尾(根本不发文件数据)。后续文件会被 sink 再次判为 Abort 而继续 ZSKIP,直到 ZFIN。
                item.Status = FileTransferState.Cancelled;
                session.Status = FileTransferState.Cancelled;
                _observer?.OnFileSkipped(item);
                await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZSKIP), ct).ConfigureAwait(false);
                return true; // 继续主循环,等 sz 的 ZFIN 干净收束(会话状态已记为 Cancelled,Finish 不会覆盖)。
        }

        item.Status = FileTransferState.Transferring;
        item.BytesTransferred = resumeOffset;
        _observer?.OnFileStarted(item);

        // 通过 ZRPOS 告知发送方从何处开始发送数据(0 = 从头)。
        await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)resumeOffset), ct).ConfigureAwait(false);

        return await ReceiveFileDataAsync(session, item, ct).ConfigureAwait(false);
    }

    private async Task<bool> ReceiveFileDataAsync(FileTransferSession session, FileTransferItem item, CancellationToken ct)
    {
        int dataRetries = 0;
        // ZEOF 长度对不上的累计次数。不能复用 dataRetries —— 那个每读到一个合法帧就清零,
        // 用它做上限等于没有上限(发送方一直重发同一个 ZEOF 就会把接收方钉在死循环里)。
        int eofMismatches = 0;
        while (true)
        {
            ZModemHeaderResult frame = await ReadHeaderAsync(ct).ConfigureAwait(false);
            switch (frame.Status)
            {
                case ZModemReadStatus.Cancelled:
                    await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                    session.Status = FileTransferState.Cancelled;
                    _observer?.OnSessionFailed(session, null);
                    return false;
                case ZModemReadStatus.EndOfStream:
                    await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                    Finish(session);
                    return false;
                case ZModemReadStatus.Timeout:
                    // 数据阶段停顿:回 ZRPOS 要求从当前偏移续发;反复超时即判定该文件失败。
                    if (++dataRetries > _options.MaxRetries)
                    {
                        await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                        session.Status = FileTransferState.Failed;
                        _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM data transfer timed out"));
                        await TrySendCancelAsync().ConfigureAwait(false);
                        return false;
                    }
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    continue;
                case ZModemReadStatus.CrcError:
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    continue;
            }
            dataRetries = 0;

            switch (frame.Header.Type)
            {
                case ZModemFrameType.ZDATA:
                    _useCrc32 = frame.Format == ZModemHeaderFormat.Binary32;
                    bool dataOk = await ReceiveDataSubpacketsAsync(session, item, ct).ConfigureAwait(false);
                    if (!dataOk)
                    {
                        return false;
                    }
                    break;

                case ZModemFrameType.ZEOF:
                    // ZEOF 的位置字段 = 发送方声称已发的总字节数。它和我们实际收下的字节数对不上,
                    // 说明中间丢了子包(链路吞字节、或一个 ZDATA 帧被整段跳过);此时直接收尾就是
                    // 悄悄交付一个被截断的文件。规范的做法是回 ZRPOS 让发送方从我们的实际位置续发。
                    if (frame.Header.Position != (uint)item.BytesTransferred)
                    {
                        TransferTrace.Log(
                            $"ZEOF length mismatch: peer says {frame.Header.Position}, received {item.BytesTransferred} -> ZRPOS");
                        if (++eofMismatches > _options.MaxRetries)
                        {
                            await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(
                                session,
                                new InvalidDataException("ZMODEM ZEOF 声明的长度与实际接收字节数不一致"));
                            await TrySendCancelAsync().ConfigureAwait(false);
                            return false;
                        }
                        await SendHeaderAsync(
                                ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)item.BytesTransferred),
                                ct)
                            .ConfigureAwait(false);
                        continue;
                    }
                    // 文件数据结束:收尾并回到批循环。
                    await _sink.CompleteAsync(item, ct).ConfigureAwait(false);
                    item.Status = FileTransferState.Completed;
                    _observer?.OnFileCompleted(item);
                    // 回 ZRINIT 表示已就绪接收下一个文件(或后续 ZFIN)。
                    await SendZrinitAsync(ct).ConfigureAwait(false);
                    return true;

                default:
                    // 数据阶段的意外帧:忽略。
                    break;
            }
        }
    }

    private async Task<bool> ReceiveDataSubpacketsAsync(FileTransferSession session, FileTransferItem item, CancellationToken ct)
    {
        while (true)
        {
            ZModemSubpacketResult sub = await ReadSubpacketAsync(ct).ConfigureAwait(false);
            switch (sub.Status)
            {
                case ZModemSubpacketStatus.Cancelled:
                    await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                    session.Status = FileTransferState.Cancelled;
                    _observer?.OnSessionFailed(session, null);
                    return false;
                case ZModemSubpacketStatus.EndOfStream:
                    await FailItemAsync(session, item, null, ct).ConfigureAwait(false);
                    Finish(session);
                    return false;
                case ZModemSubpacketStatus.Timeout:
                    // 子包中途断流:回 ZRPOS 让发送方从已确认偏移续发,由上层帧循环施加重试上限。
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    return true;
                case ZModemSubpacketStatus.CrcError:
                    // 子包损坏:回 ZRPOS 要求从当前偏移重发。
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    return true;
            }

            if (sub.Data.Length > 0)
            {
                try
                {
                    await _sink.WriteAsync(item, sub.Data, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 写盘失败(磁盘满 / 权限变更):只舍弃当前文件,不拖垮整批。
                    await FailItemAsync(session, item, ex, ct).ConfigureAwait(false);
                    return false;
                }
                item.BytesTransferred += sub.Data.Length;
                _observer?.OnFileProgress(item);
            }

            switch (sub.End)
            {
                case ZModemSubpacketEnd.MoreNoAck:
                    continue; // 帧内还有子包。
                case ZModemSubpacketEnd.MoreAck:
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZACK, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    continue;
                case ZModemSubpacketEnd.EndAck:
                    await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZACK, (uint)item.BytesTransferred), ct).ConfigureAwait(false);
                    return true; // 帧结束,回到 ZDATA/ZEOF 等待。
                case ZModemSubpacketEnd.EndNoAck:
                default:
                    return true; // 帧结束,等待下一帧头(通常 ZEOF)。
            }
        }
    }

    private async Task SendZrinitAsync(CancellationToken ct)
    {
        // ZF0 是 4 个参数字节的最后一个(P3),不是第一个 —— 见 ZModemHeader 的字段顺序。
        // 通告全双工 + 可与磁盘 IO 重叠(二者缺一 lrzsz 都会退化:无 CANFDX 时关闭窗口机制)。
        byte flags = ZModemCapabilities.CANFDX | ZModemCapabilities.CANOVIO;
        if (_options.PreferCrc32)
        {
            flags |= ZModemCapabilities.CANFC32;
        }
        if (_options.EscapeAllControl)
        {
            // 仅在链路对控制字符敏感时通告;它会让 lrzsz 进入 Zctlesc 模式,吞吐显著下降。
            flags |= ZModemCapabilities.ESCCTL;
        }
        // ZP0/ZP1 = 接收缓冲区大小,0 表示不限窗口(流式接收)。
        var header = new ZModemHeader(ZModemFrameType.ZRINIT, 0, 0, 0, flags);
        await SendHeaderAsync(header, ct, ZModemHeaderFormat.Hex).ConfigureAwait(false);
    }

    /// <summary>
    /// 读一个帧头,并施加 <see cref="ZModemOptions.FrameTimeout" />。超时返回
    /// <see cref="ZModemReadStatus.Timeout" />,而不是无限期挂起 —— 否则路由器会永久停在会话态,
    /// 把此后所有输出(含 shell 提示符)一并吞掉,终端再也回不来。
    /// </summary>
    private async Task<ZModemHeaderResult> ReadHeaderAsync(CancellationToken ct, TimeSpan? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget ?? _options.FrameTimeout);
        try
        {
            return await _reader.ReadHeaderAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ZModemReadStatus.Timeout);
        }
    }

    /// <summary>读一个数据子包,并施加 <see cref="ZModemOptions.FrameTimeout" />。</summary>
    private async Task<ZModemSubpacketResult> ReadSubpacketAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.FrameTimeout);
        try
        {
            return await ZModemSubpacket.ReadAsync(_reader, _useCrc32, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ZModemSubpacketStatus.Timeout, []);
        }
    }

    private async Task SendHeaderAsync(ZModemHeader header, CancellationToken ct, ZModemHeaderFormat? format = null)
    {
        // 控制帧默认走十六进制头(与 lrzsz 一致,便于对端行缓冲解析)。
        ZModemHeaderFormat fmt = format ?? ZModemHeaderFormat.Hex;
        byte[] wire = ZModemFrameWriter.Write(header, fmt);
        await _duplex.WriteAsync(wire, ct).ConfigureAwait(false);
        await _duplex.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ConsumeOverAndOutAsync(CancellationToken ct)
    {
        // 发送方在最终 ZFIN 后追加 "OO"。尽力读走这两个字节,读不到也不阻塞会话结束 ——
        // 故这里用一个短超时:对端若没发 "OO",不能让会话在收尾阶段永久挂住终端。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OverAndOutTimeout);
        try
        {
            int seen = 0;
            while (seen < 2)
            {
                (ZdleToken token, bool eof) = await _reader.ReadEscapedByteAsync(timeout.Token).ConfigureAwait(false);
                if (eof)
                {
                    return;
                }
                // 十六进制帧头以 CR LF(可能再加 XON)收尾,这些字节留在缓冲里还没被消化。
                // 不跳过它们就会在第一个 CR 上判定"没有 OO"直接返回,把真正的 "OO" 留在缓冲里 ——
                // 会话收尾把残余字节交还终端时,用户屏幕上就会凭空多出两个 "OO"。
                if (token.Value is 0x0D or 0x0A or ZModemConstants.XON)
                {
                    continue;
                }
                if (token.Value == ZModemConstants.OverAndOut)
                {
                    seen++;
                    continue;
                }
                // 不属于收尾序列(多半已经是 shell 的输出了):退回缓冲,交给残余字节回流逻辑。
                _reader.Seed([token.Value]);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 收尾阶段的取消 / 超时均无害:会话已经在协议上结束了。
        }
    }

    private async Task FailItemAsync(FileTransferSession session, FileTransferItem item, Exception? error, CancellationToken ct)
    {
        _ = session;
        try
        {
            await _sink.FailAsync(item, error, ct).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不掩盖原始错误。
        }
        item.Status = FileTransferState.Failed;
    }

    private async Task TrySendCancelAsync()
    {
        try
        {
            await _duplex.WriteAsync(ZModemConstants.CancelSequence.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await _duplex.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 传输可能已断开;取消序列尽力而为。
        }
    }

    private static void Finish(FileTransferSession session)
    {
        if (session.Status is FileTransferState.Cancelled or FileTransferState.Failed)
        {
            return;
        }
        session.Status = FileTransferState.Completed;
    }

    /// <summary>
    /// 解析 ZFILE 数据子包:首段为 NUL 结尾的文件名,其后以空格分隔可选字段
    /// (大小 修改时间(八进制) 模式(八进制) 串行 剩余文件数 剩余字节数)。
    /// 该格式与 YMODEM 的 0 号块完全一致,故实现落在共享的
    /// <see cref="TransferFileInfoCodec" /> 上,两个协议不再各写一遍。
    /// </summary>
    /// <param name="data">ZFILE 数据子包的反转义负载。</param>
    /// <returns>解析出的文件元数据。</returns>
    public static TransferFileMetadata ParseFileMetadata(byte[] data) =>
        TransferFileInfoCodec.Parse(data);
}

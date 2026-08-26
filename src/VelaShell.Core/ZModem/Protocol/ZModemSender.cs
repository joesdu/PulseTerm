using System.Buffers;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.ZModem.Model;

namespace VelaShell.Core.ZModem.Protocol;

/// <summary>
/// ZMODEM 发送方状态机:驱动「本地 → 远端 <c>rz</c>」的完整批量发送流程。
/// 握手(消费对端 ZRINIT 并读取其能力位)→ 逐文件 ZFILE + 信息子包 → 等 ZRPOS → ZDATA 子包流 → ZEOF,
/// 全部发完后 ZFIN 交换 + 追加 <c>"OO"</c>。行为对齐 lrzsz <c>sz</c>,与 Linux <c>rz</c> 互操作。
/// </summary>
public sealed class ZModemSender(
    IByteDuplex duplex,
    IFileTransferSource source,
    ZModemOptions? options = null,
    IFileTransferObserver? observer = null)
{
    /// <summary>ZFILE 的 ZF0 转换请求位:二进制传输,禁止对端做换行/文本转换(lrzsz <c>ZCBIN 1</c>)。</summary>
    private const byte ZCBIN = 0x01;

    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private readonly IFileTransferSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly ZModemOptions _options = options ?? ZModemOptions.Default;
    private readonly IFileTransferObserver? _observer = observer;
    private readonly ZModemFrameReader _reader = new(duplex);

    // 对端 ZRINIT 通告的能力位(ZF0)。决定数据帧用 CRC32 还是 CRC16、是否需要转义全部控制字符。
    private bool _useCrc32;
    private bool _escapeAllControl;

    /// <summary>执行完整的发送会话,直到批量结束(ZFIN)、被取消或出错。</summary>
    /// <param name="cancellationToken">取消整个会话的令牌。</param>
    /// <returns>本次会话的状态与已发送文件清单。</returns>
    public async Task<FileTransferSession> SendAsync(CancellationToken cancellationToken)
    {
        var session = new FileTransferSession { Direction = FileTransferDirection.Send };
        _observer?.OnSessionStarted(session);
        session.Status = FileTransferState.Transferring;

        try
        {
            // 1) 等对端 ZRINIT(远端 rz 启动时即主动发送;路由器已把它喂进读取缓冲)。
            if (!await WaitForZrinitAsync(session, cancellationToken).ConfigureAwait(false))
            {
                return session;
            }

            // 2) 问宿主要发哪些文件(弹文件选择框)。用户取消 => 向对端发取消序列并结束。
            IReadOnlyList<OutgoingTransferFile> files =
                await _source.GetFilesAsync(cancellationToken).ConfigureAwait(false);
            if (files.Count == 0)
            {
                // 用户在文件选择框点了取消(两次):不发 CAN,而是走优雅收尾——告诉 rz "没有文件要发了"。
                // ZFIN 是协议里发送方结束会话的正规方式,rz 收到后会干净退回 shell 提示符,
                // 用户无需再按 Ctrl+C,也不会像 CAN 那样让 rz 打印中止错误或继续 "waiting to receive"。
                session.Status = FileTransferState.Cancelled;
                await FinishSessionAsync(session, cancellationToken).ConfigureAwait(false);
                _observer?.OnSessionFailed(session, null);
                return session;
            }

            // 3) 逐个发送。
            for (int i = 0; i < files.Count; i++)
            {
                if (!await SendFileAsync(session, files[i], files.Count - i - 1, cancellationToken).ConfigureAwait(false))
                {
                    return session;
                }
            }

            // 4) 结束握手:ZFIN ↔ ZFIN,再追加 "OO"。
            await FinishSessionAsync(session, cancellationToken).ConfigureAwait(false);
            return session;
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
            // 读取器缓冲里剩下的字节已经不属于本次传输了(多半是 rz 退出后的 shell 提示符),
            // 退回通道让路由器交还终端 —— 否则用户看到的是"传完了但提示符没了"。
            _duplex.Unread(_reader.DrainBuffered());
        }
    }

    /// <summary>等待并解析对端 ZRINIT,记录其能力位。超时会补发 ZRQINIT 促使对端重发。</summary>
    private async Task<bool> WaitForZrinitAsync(FileTransferSession session, CancellationToken ct)
    {
        int retries = 0;
        while (true)
        {
            // 握手阶段用更短的预算:谈不拢时终端是全黑的,要尽快放弃并还给用户。
            ZModemHeaderResult frame = await ReadHeaderAsync(ct, _options.HandshakeTimeout).ConfigureAwait(false);
            switch (frame.Status)
            {
                case ZModemReadStatus.Cancelled:
                    session.Status = FileTransferState.Cancelled;
                    _observer?.OnSessionFailed(session, null);
                    return false;
                case ZModemReadStatus.EndOfStream:
                    session.Status = FileTransferState.Failed;
                    _observer?.OnSessionFailed(session, null);
                    return false;
                case ZModemReadStatus.Timeout:
                case ZModemReadStatus.CrcError:
                    if (++retries > _options.HandshakeRetries)
                    {
                        session.Status = FileTransferState.Failed;
                        _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM ZRINIT handshake timed out"));
                        await TrySendCancelAsync().ConfigureAwait(false);
                        return false;
                    }
                    // 促使对端(rz)重发 ZRINIT。
                    await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZRQINIT), ct).ConfigureAwait(false);
                    continue;
            }

            if (frame.Header.Type == ZModemFrameType.ZRINIT)
            {
                // ZF0 是 4 个参数字节的最后一个(P3)。
                byte flags = frame.Header.P3;
                _useCrc32 = _options.PreferCrc32 && (flags & ZModemCapabilities.CANFC32) != 0;
                _escapeAllControl = _options.EscapeAllControl || (flags & ZModemCapabilities.ESCCTL) != 0;
                return true;
            }

            // 其它帧(如对端也发了 ZRQINIT):继续等 ZRINIT。
            if (++retries > _options.MaxRetries)
            {
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM peer never sent ZRINIT"));
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
            }
        }
    }

    /// <summary>发送单个文件;返回 <c>false</c> 表示整个会话应终止。</summary>
    private async Task<bool> SendFileAsync(
        FileTransferSession session,
        OutgoingTransferFile file,
        int filesRemaining,
        CancellationToken ct)
    {
        var item = new FileTransferItem
        {
            FileName = file.RemoteName,
            Size = file.Size,
            LocalPath = file.LocalPath
        };
        session.AddItem(item);

        // ZMODEM 的位置字段(ZDATA/ZEOF/ZRPOS 的 ZP0..ZP3)是 32 位,>4GiB 的文件在协议层就表达不了。
        // 旧实现直接 (uint) 截断:偏移悄悄回绕,对端收到的是一份被打乱的文件却全程"成功"。
        // 宁可明确跳过并给出理由,也不要交付静默损坏的数据。
        if (file.Size > uint.MaxValue)
        {
            item.Status = FileTransferState.Failed;
            item.ErrorMessage = "文件超过 4 GiB,超出 ZMODEM 32 位偏移上限;请改用 SFTP 传输。";
            _observer?.OnFileSkipped(item);
            return true;
        }

        Stream stream;
        try
        {
            stream = await _source.OpenReadAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 打不开就跳过这个文件,不拖垮整批。
            item.Status = FileTransferState.Failed;
            item.ErrorMessage = ex.Message;
            _observer?.OnFileSkipped(item);
            return true;
        }

        await using (stream.ConfigureAwait(false))
        {
            int retries = 0;
            while (true)
            {
                // ZFILE 帧头 + 文件信息子包(以 ZCRCW 收尾,等对端 ZRPOS/ZSKIP)。
                await SendZfileAsync(file, filesRemaining, ct).ConfigureAwait(false);

                ZModemHeaderResult frame = await ReadHeaderAsync(ct).ConfigureAwait(false);
                switch (frame.Status)
                {
                    case ZModemReadStatus.Cancelled:
                        item.Status = FileTransferState.Cancelled;
                        session.Status = FileTransferState.Cancelled;
                        _observer?.OnSessionFailed(session, null);
                        return false;
                    case ZModemReadStatus.EndOfStream:
                        item.Status = FileTransferState.Failed;
                        session.Status = FileTransferState.Failed;
                        _observer?.OnSessionFailed(session, null);
                        return false;
                    case ZModemReadStatus.Timeout:
                    case ZModemReadStatus.CrcError:
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM ZFILE was never acknowledged"));
                            await TrySendCancelAsync().ConfigureAwait(false);
                            return false;
                        }
                        continue; // 重发 ZFILE。
                }

                switch (frame.Header.Type)
                {
                    case ZModemFrameType.ZSKIP:
                        // 对端已有该文件 / 拒收:跳过,继续下一个。
                        item.Status = FileTransferState.Skipped;
                        _observer?.OnFileSkipped(item);
                        return true;

                    case ZModemFrameType.ZRPOS:
                        item.Status = FileTransferState.Transferring;
                        _observer?.OnFileStarted(item);
                        return await SendFileDataAsync(session, item, stream, frame.Header.Position, ct)
                            .ConfigureAwait(false);

                    case ZModemFrameType.ZRINIT:
                        // 对端重发 ZRINIT(还没看到我们的 ZFILE):重试。
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        }
                        continue;

                    case ZModemFrameType.ZFIN:
                    case ZModemFrameType.ZABORT:
                    case ZModemFrameType.ZCAN:
                        item.Status = FileTransferState.Cancelled;
                        session.Status = FileTransferState.Cancelled;
                        _observer?.OnSessionFailed(session, null);
                        return false;

                    default:
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        }
                        continue;
                }
            }
        }
    }

    /// <summary>从 <paramref name="startOffset" /> 起发送文件数据,直到 ZEOF 被对端确认(回 ZRINIT)。</summary>
    private async Task<bool> SendFileDataAsync(
        FileTransferSession session,
        FileTransferItem item,
        Stream stream,
        uint startOffset,
        CancellationToken ct)
    {
        int retries = 0;
        long offset = startOffset;

        int payloadSize = Math.Max(64, _options.SubpacketSize);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(payloadSize);
        byte[] wire = ArrayPool<byte>.Shared.Rent(ZModemSubpacket.MaxEncodedLength(payloadSize));
        try
        {
            while (true)
            {
                // ZDATA 帧头 + 子包流:全程 ZCRCG(无需逐包应答),最后一包 ZCRCE 收帧,再发 ZEOF。
                if (!TrySeek(stream, offset, item))
                {
                    _observer?.OnFileSkipped(item);
                    return false;
                }
                await SendHeaderAsync(
                        ZModemHeader.WithPosition(ZModemFrameType.ZDATA, (uint)offset),
                        ct,
                        DataHeaderFormat)
                    .ConfigureAwait(false);

                long pos = offset;
                // 流式推进的三种出路:读到文件尾(发 ZEOF)、对端插话要求回退(重开一帧)、致命错误(收摊)。
                bool restartFrame = false;
                bool endOfFile = false;
                while (!restartFrame && !endOfFile)
                {
                    // ReadAtLeastAsync 只在真正 EOF 时才返回不足量,故「读不满 = 文件读完」。
                    // 不拿 item.Size 判尾:声明大小与实际内容不一致时会截断或多发。
                    int read;
                    try
                    {
                        read = await stream
                            .ReadAtLeastAsync(buffer.AsMemory(0, payloadSize), payloadSize, throwOnEndOfStream: false, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 读取失败(文件被删 / 权限变更):只舍弃当前文件,不拖垮整批。
                        item.Status = FileTransferState.Failed;
                        item.ErrorMessage = ex.Message;
                        _observer?.OnFileSkipped(item);
                        return false;
                    }
                    bool last = read < payloadSize;
                    ZModemSubpacketEnd end = last ? ZModemSubpacketEnd.EndNoAck : ZModemSubpacketEnd.MoreNoAck;
                    int encoded = ZModemSubpacket.Write(buffer.AsSpan(0, read), end, _useCrc32, _escapeAllControl, wire);
                    await _duplex.WriteAsync(wire.AsMemory(0, encoded), ct).ConfigureAwait(false);
                    pos += read;
                    item.BytesTransferred = pos;
                    _observer?.OnFileProgress(item);
                    if (last)
                    {
                        endOfFile = true;
                        break;
                    }

                    // 流式阶段对端本该沉默,一旦有入站字节就只可能是「出事了」——
                    // ZRPOS(要求回退重发)、ZSKIP(不要这个文件了)、ZCAN(整笔中止)。
                    // 旧实现要等整个文件推完、发出 ZEOF 之后才去读这一帧:一个几百 MB 的文件在第
                    // 1MB 处校验失败,剩下的几百 MB 会照发不误,然后再从头重来。这里就地接住。
                    if (!_duplex.HasPendingInbound)
                    {
                        continue;
                    }
                    ZModemHeaderResult interject =
                        await ReadHeaderAsync(ct, _options.StreamInterjectTimeout).ConfigureAwait(false);
                    switch (interject.Status)
                    {
                        case ZModemReadStatus.Cancelled:
                            item.Status = FileTransferState.Cancelled;
                            session.Status = FileTransferState.Cancelled;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        case ZModemReadStatus.EndOfStream:
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        case ZModemReadStatus.Timeout:
                        case ZModemReadStatus.CrcError:
                            // 只是噪声或半个帧:继续推流,对端会按自己的超时再要一次。
                            continue;
                    }

                    switch (interject.Header.Type)
                    {
                        case ZModemFrameType.ZRPOS:
                            if (++retries > _options.MaxRetries)
                            {
                                item.Status = FileTransferState.Failed;
                                session.Status = FileTransferState.Failed;
                                _observer?.OnSessionFailed(session, null);
                                return false;
                            }
                            offset = interject.Header.Position;
                            item.BytesTransferred = offset;
                            TransferTrace.Log($"SEND stream interrupted by ZRPOS -> rewind to {offset}");
                            restartFrame = true;
                            break;

                        case ZModemFrameType.ZSKIP:
                            item.Status = FileTransferState.Skipped;
                            _observer?.OnFileSkipped(item);
                            return true;

                        case ZModemFrameType.ZFIN:
                        case ZModemFrameType.ZABORT:
                        case ZModemFrameType.ZCAN:
                            item.Status = FileTransferState.Cancelled;
                            session.Status = FileTransferState.Cancelled;
                            _observer?.OnSessionFailed(session, null);
                            return false;

                        default:
                            // ZACK 之类的良性帧:不打断流,继续推。
                            break;
                    }
                }

                if (restartFrame)
                {
                    continue;
                }

                await _duplex.FlushAsync(ct).ConfigureAwait(false);

                // ZEOF 的位置字段 = 文件总长。
                await SendHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZEOF, (uint)pos), ct, DataHeaderFormat)
                    .ConfigureAwait(false);

                ZModemHeaderResult frame = await ReadHeaderAsync(ct).ConfigureAwait(false);
                switch (frame.Status)
                {
                    case ZModemReadStatus.Cancelled:
                        item.Status = FileTransferState.Cancelled;
                        session.Status = FileTransferState.Cancelled;
                        _observer?.OnSessionFailed(session, null);
                        return false;
                    case ZModemReadStatus.EndOfStream:
                        item.Status = FileTransferState.Failed;
                        session.Status = FileTransferState.Failed;
                        _observer?.OnSessionFailed(session, null);
                        return false;
                    case ZModemReadStatus.Timeout:
                    case ZModemReadStatus.CrcError:
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, new TimeoutException("ZMODEM ZEOF was never acknowledged"));
                            await TrySendCancelAsync().ConfigureAwait(false);
                            return false;
                        }
                        continue;
                }

                switch (frame.Header.Type)
                {
                    case ZModemFrameType.ZRINIT:
                        // 对端确认收完,准备好接下一个文件。
                        item.Status = FileTransferState.Completed;
                        _observer?.OnFileCompleted(item);
                        return true;

                    case ZModemFrameType.ZRPOS:
                        // 对端要求从指定偏移重发(校验失败 / 丢包)。
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        }
                        offset = frame.Header.Position;
                        item.BytesTransferred = offset;
                        continue;

                    case ZModemFrameType.ZSKIP:
                        item.Status = FileTransferState.Skipped;
                        _observer?.OnFileSkipped(item);
                        return true;

                    case ZModemFrameType.ZFIN:
                    case ZModemFrameType.ZABORT:
                    case ZModemFrameType.ZCAN:
                        item.Status = FileTransferState.Cancelled;
                        session.Status = FileTransferState.Cancelled;
                        _observer?.OnSessionFailed(session, null);
                        return false;

                    default:
                        if (++retries > _options.MaxRetries)
                        {
                            item.Status = FileTransferState.Failed;
                            session.Status = FileTransferState.Failed;
                            _observer?.OnSessionFailed(session, null);
                            return false;
                        }
                        continue;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(wire);
        }
    }

    /// <summary>
    /// 把读取流定位到 <paramref name="offset" />。流不可定位时(理论上不该发生,
    /// <see cref="IFileTransferSource" /> 要求可定位流)标记该文件失败而不是让异常打穿整批。
    /// </summary>
    private static bool TrySeek(Stream stream, long offset, FileTransferItem item)
    {
        if (stream.CanSeek)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return true;
        }
        if (offset == 0)
        {
            return true; // 从头发不需要定位。
        }
        item.Status = FileTransferState.Failed;
        item.ErrorMessage = "文件流不支持定位,无法响应 ZRPOS 续传请求。";
        return false;
    }

    /// <summary>发 ZFILE 帧头 + 文件信息子包(名字 NUL 大小 时间 模式 … NUL,以 ZCRCW 收尾)。</summary>
    private async Task SendZfileAsync(OutgoingTransferFile file, int filesRemaining, CancellationToken ct)
    {
        // ZF0(=P3) = 转换请求;ZCBIN 表示二进制传输,禁止 rz 做文本换行转换。
        await SendHeaderAsync(new(ZModemFrameType.ZFILE, 0, 0, 0, ZCBIN), ct, DataHeaderFormat).ConfigureAwait(false);

        // 文件信息字段的格式与 YMODEM 的 0 号块完全一致,编码走共享的 TransferFileInfoCodec:
        // "<文件名> NUL <大小> <修改时间八进制> <模式八进制> <串行> <剩余文件数> <剩余字节数> NUL"。
        byte[] info = TransferFileInfoCodec.Encode(
            file.RemoteName,
            file.Size,
            file.ModifiedUtc,
            filesRemaining,
            file.Size);

        byte[] wire = ZModemSubpacket.Write(info, ZModemSubpacketEnd.EndAck, _useCrc32, _escapeAllControl);
        await _duplex.WriteAsync(wire, ct).ConfigureAwait(false);
        await _duplex.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ZFIN 交换并追加 <c>"OO"</c>,收束整个批次(正常发完或用户取消都走这里)。</summary>
    private async Task FinishSessionAsync(FileTransferSession session, CancellationToken ct)
    {
        bool peerAcknowledged = false;

        await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZFIN), ct).ConfigureAwait(false);

        // 等对端回 ZFIN。用户取消时 rz 可能在犹豫期间重发过 ZRINIT 等旧帧,需跳过它们直到看到 ZFIN;
        // 有界等待(PostCancelDrainMax),等不到时补发 CAN 中止序列兜底——rz 收到 CAN 会打印
        // "ZMODEM transfer cancelled"并退出(仍好过让用户手动 Ctrl+C)。
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.PostCancelDrainMax;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ZModemHeaderResult frame = await ReadHeaderAsync(ct, _options.PostCancelDrainIdle).ConfigureAwait(false);
            if (frame.Status == ZModemReadStatus.Header && frame.Header.Type == ZModemFrameType.ZFIN)
            {
                peerAcknowledged = true;
                try
                {
                    await _duplex.WriteAsync("OO"u8.ToArray(), ct).ConfigureAwait(false);
                    await _duplex.FlushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // "OO" 只是礼貌收尾,发不出去不影响结果。
                }
                break;
            }
            if (frame.Status == ZModemReadStatus.EndOfStream)
            {
                peerAcknowledged = true;
                break; // 对端已退出。
            }
            if (frame.Status == ZModemReadStatus.Timeout)
            {
                // 空闲一轮:rz 可能没收到我们的 ZFIN,补发一次再等。
                await SendHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZFIN), ct).ConfigureAwait(false);
            }
            // 其它旧帧(ZRINIT 等):忽略,继续等 ZFIN。
        }

        if (!peerAcknowledged)
        {
            // ZFIN 未获对端确认:发送 CAN 中止序列强制 rz 退出。
            // rz 会打印 "ZMODEM transfer cancelled" 然后退出,总比一直卡着好。
            await TrySendCancelAsync().ConfigureAwait(false);
        }

        if (session.Status is not (FileTransferState.Cancelled or FileTransferState.Failed))
        {
            session.Status = FileTransferState.Completed;
            _observer?.OnSessionCompleted(session);
        }
    }

    /// <summary>数据类帧(ZFILE/ZDATA/ZEOF)的帧头形态:对端支持 CRC32 时用 ZBIN32,否则 ZBIN。</summary>
    private ZModemHeaderFormat DataHeaderFormat =>
        _useCrc32 ? ZModemHeaderFormat.Binary32 : ZModemHeaderFormat.Binary16;

    /// <summary>读一个帧头并施加超时(默认 <see cref="ZModemOptions.FrameTimeout" />),避免无限期挂住终端。</summary>
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

    private async Task SendHeaderAsync(ZModemHeader header, CancellationToken ct, ZModemHeaderFormat? format = null)
    {
        // 控制帧默认走十六进制头(与 lrzsz 一致)。
        byte[] wire = ZModemFrameWriter.Write(header, format ?? ZModemHeaderFormat.Hex);
        await _duplex.WriteAsync(wire, ct).ConfigureAwait(false);
        await _duplex.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task TrySendCancelAsync()
    {
        try
        {
            await _duplex.WriteAsync(ZModemConstants.CancelSequence.ToArray(), CancellationToken.None)
                .ConfigureAwait(false);
            await _duplex.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 传输可能已断开;取消序列尽力而为。
        }
    }
}

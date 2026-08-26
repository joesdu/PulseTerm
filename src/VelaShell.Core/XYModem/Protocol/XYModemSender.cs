using System.Buffers;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.XYModem.Model;

namespace VelaShell.Core.XYModem.Protocol;

/// <summary>
/// XMODEM / XMODEM-1K / YMODEM / YMODEM-G 发送方状态机:驱动「本地 → 远端 <c>rb</c>/<c>rx</c>」。
/// 流程由接收方的握手字符起步:收到 <c>'C'</c> 用 CRC16,收到 NAK 退回 8 位校验和,
/// 收到 <c>'G'</c> 则切 YMODEM-G 流式(不等逐块 ACK)。YMODEM 每个文件先发 0 号块(文件名 + 大小),
/// 数据块从 1 号开始按 256 回绕,以 EOT 收束;整批结束时再发一个空的 0 号块。
/// </summary>
public sealed class XYModemSender(
    IByteDuplex duplex,
    IFileTransferSource source,
    XYModemOptions? options = null,
    IFileTransferObserver? observer = null)
{
    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private readonly IFileTransferSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly XYModemOptions _options = options ?? XYModemOptions.Default;
    private readonly IFileTransferObserver? _observer = observer;
    private readonly XYModemByteReader _reader = new(duplex);

    // 接收方握手字符定下来的实际模式(可能与 options 的偏好不同 —— 以对端的要求为准)。
    private bool _useCrc = true;
    private bool _streaming;

    /// <summary>执行完整的发送会话,直到整批结束、被取消或出错。</summary>
    /// <param name="cancellationToken">取消整个会话的令牌。</param>
    /// <returns>本次会话的状态与已发送文件清单。</returns>
    public async Task<FileTransferSession> SendAsync(CancellationToken cancellationToken)
    {
        var session = new FileTransferSession
        {
            Direction = FileTransferDirection.Send,
            Protocol = _options.Protocol
        };
        TransferTrace.Log($"XY SENDER START protocol={_options.Protocol}");
        _observer?.OnSessionStarted(session);
        session.Status = FileTransferState.Transferring;

        try
        {
            IReadOnlyList<OutgoingTransferFile> files =
                await _source.GetFilesAsync(cancellationToken).ConfigureAwait(false);
            if (files.Count == 0)
            {
                // 用户取消了文件选择:发 CAN 让 rb/rx 干净退出(这一族协议没有 ZMODEM 的 ZFIN 那种优雅收尾)。
                session.Status = FileTransferState.Cancelled;
                await TrySendCancelAsync().ConfigureAwait(false);
                _observer?.OnSessionFailed(session, null);
                return session;
            }
            if (!_options.IsBatch && files.Count > 1)
            {
                // XMODEM 一次只能传一个文件(没有文件名、没有批边界),多选时只发第一个并说明原因。
                TransferTrace.Log($"XY sender: XMODEM 只发首个文件,忽略其余 {files.Count - 1} 个");
                files = [files[0]];
            }

            for (int i = 0; i < files.Count; i++)
            {
                if (!await SendFileAsync(session, files[i], files.Count - i - 1, cancellationToken).ConfigureAwait(false))
                {
                    return session;
                }
            }

            if (_options.IsBatch && !await SendBatchTerminatorAsync(cancellationToken).ConfigureAwait(false))
            {
                // 终止块没被确认不影响已经传完的文件,只记一笔诊断。
                TransferTrace.Log("XY sender: 批结束块未获确认");
            }

            session.Status = FileTransferState.Completed;
            _observer?.OnSessionCompleted(session);
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
            _duplex.Unread(_reader.DrainBuffered());
        }
    }

    /// <summary>发送单个文件(YMODEM 含 0 号块);返回 <c>false</c> 表示整个会话应终止。</summary>
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

        // 1) 等接收方招呼('C' / 'G' / NAK),顺便定下校验模式。
        if (!await WaitForHandshakeAsync(session, ct).ConfigureAwait(false))
        {
            item.Status = session.Status;
            return false;
        }

        // 2) YMODEM:先发 0 号块(文件名 + 大小),等 ACK,再等一次握手字符才开始推数据。
        if (_options.IsBatch)
        {
            byte[] info = TransferFileInfoCodec.Encode(
                file.RemoteName,
                file.Size,
                file.ModifiedUtc,
                filesRemaining,
                file.Size);
            if (!await SendPaddedBlockAsync(info, 0, padding: 0, ct).ConfigureAwait(false))
            {
                item.Status = FileTransferState.Failed;
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, new IOException("YMODEM 0 号块未获确认"));
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
            }
            if (!await WaitForHandshakeAsync(session, ct).ConfigureAwait(false))
            {
                item.Status = session.Status;
                return false;
            }
        }

        Stream stream;
        try
        {
            stream = await _source.OpenReadAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            item.Status = FileTransferState.Failed;
            item.ErrorMessage = ex.Message;
            _observer?.OnFileSkipped(item);
            // 文件打不开时无法只跳过这一个(协议里没有 ZSKIP),只能中止整批。
            session.Status = FileTransferState.Failed;
            _observer?.OnSessionFailed(session, ex);
            await TrySendCancelAsync().ConfigureAwait(false);
            return false;
        }

        await using (stream.ConfigureAwait(false))
        {
            item.Status = FileTransferState.Transferring;
            _observer?.OnFileStarted(item);
            return await SendFileDataAsync(session, item, stream, ct).ConfigureAwait(false);
        }
    }

    /// <summary>推送数据块直到文件读完,再发 EOT 并等待确认。</summary>
    private async Task<bool> SendFileDataAsync(
        FileTransferSession session,
        FileTransferItem item,
        Stream stream,
        CancellationToken ct)
    {
        int payloadSize = _options.PayloadSize;
        byte[] payload = ArrayPool<byte>.Shared.Rent(payloadSize);
        try
        {
            int blockNumber = 1;
            long sent = 0;
            while (true)
            {
                int read = await stream
                    .ReadAtLeastAsync(payload.AsMemory(0, payloadSize), payloadSize, throwOnEndOfStream: false, ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break; // 文件读完了,且上一块正好填满。
                }

                // 尾块优化:剩不到 128 字节时改用 SOH 小块,少发近 900 字节的纯填充。
                int blockPayload = read <= XYModemConstants.SmallPayload
                    ? XYModemConstants.SmallPayload
                    : payloadSize;
                payload.AsSpan(read, blockPayload - read).Fill(XYModemConstants.SUB);

                if (!await SendBlockWithRetryAsync(payload.AsMemory(0, blockPayload), blockNumber, ct).ConfigureAwait(false))
                {
                    item.Status = FileTransferState.Failed;
                    session.Status = FileTransferState.Failed;
                    _observer?.OnSessionFailed(session, new IOException($"数据块 {blockNumber} 反复未获确认"));
                    await TrySendCancelAsync().ConfigureAwait(false);
                    return false;
                }

                sent += read;
                blockNumber++;
                item.BytesTransferred = sent;
                _observer?.OnFileProgress(item);

                if (read < payloadSize)
                {
                    break; // 读不满 = 文件到底了。
                }
            }

            if (!await SendEotAsync(ct).ConfigureAwait(false))
            {
                item.Status = FileTransferState.Failed;
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, new IOException("EOT 未获确认"));
                return false;
            }

            item.Status = FileTransferState.Completed;
            _observer?.OnFileCompleted(item);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>整批结束:再等一次握手字符,发一个全零的 0 号块告诉接收方「没有下一个文件了」。</summary>
    private async Task<bool> SendBatchTerminatorAsync(CancellationToken ct)
    {
        int handshake = await ReadHandshakeCharAsync(ct).ConfigureAwait(false);
        if (handshake < 0)
        {
            return false;
        }
        return await SendPaddedBlockAsync(ReadOnlyMemory<byte>.Empty, 0, padding: 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把一段(可能不足 128 字节的)内容补齐成 128 字节块发出并等待 ACK。
    /// 0 号块按规范用 NUL 补齐(<paramref name="padding" /> 传 0),数据块用 SUB。
    /// </summary>
    private async Task<bool> SendPaddedBlockAsync(
        ReadOnlyMemory<byte> content,
        int blockNumber,
        byte padding,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(XYModemConstants.SmallPayload);
        try
        {
            Span<byte> span = buffer.AsSpan(0, XYModemConstants.SmallPayload);
            span.Fill(padding);
            int take = Math.Min(content.Length, span.Length);
            content.Span[..take].CopyTo(span);
            // 0 号块(以及批结束块)即使在 YMODEM-G 下也必须逐块应答 —— 流式只免掉数据块的 ACK。
            return await SendBlockWithRetryAsync(
                    buffer.AsMemory(0, XYModemConstants.SmallPayload),
                    blockNumber,
                    ct,
                    alwaysAwaitAck: true)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 发一个数据块并按需等待 ACK;收到 NAK 就重发,超过重试上限返回 <c>false</c>。
    /// YMODEM-G 不等应答,只在对端主动插话时检查是不是 CAN。
    /// </summary>
    private async Task<bool> SendBlockWithRetryAsync(
        ReadOnlyMemory<byte> blockPayload,
        int blockNumber,
        CancellationToken ct,
        bool alwaysAwaitAck = false)
    {
        int wireLength = XYModemBlock.EncodedLength(blockPayload.Length, _useCrc);
        byte[] wire = ArrayPool<byte>.Shared.Rent(wireLength);
        try
        {
            int written = XYModemBlock.Write(blockPayload.Span, blockNumber, _useCrc, wire);
            for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                await _duplex.WriteAsync(wire.AsMemory(0, written), ct).ConfigureAwait(false);
                await _duplex.FlushAsync(ct).ConfigureAwait(false);

                if (_streaming && !alwaysAwaitAck)
                {
                    // 流式模式:对端沉默即正常,只有它插话时才需要看一眼 —— 那只可能是 CAN。
                    if (!_duplex.HasPendingInbound)
                    {
                        return true;
                    }
                    int peek = await ReadResponseAsync(_options.BlockTimeout, ct).ConfigureAwait(false);
                    return peek != XYModemConstants.CAN;
                }

                int response = await ReadResponseAsync(_options.BlockTimeout, ct).ConfigureAwait(false);
                switch (response)
                {
                    case XYModemConstants.ACK:
                        return true;
                    case XYModemConstants.CAN:
                        return false;
                    default:
                        // NAK / 超时 / 噪声:重发这一块。
                        TransferTrace.Log($"XY block {blockNumber} not acked (resp={response}), retry {attempt + 1}");
                        continue;
                }
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(wire);
        }
    }

    /// <summary>发 EOT 并等 ACK;接收方按老规矩先回一个 NAK 时重发一次 EOT。</summary>
    private async Task<bool> SendEotAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            await _duplex.WriteAsync(new[] { XYModemConstants.EOT }, ct).ConfigureAwait(false);
            await _duplex.FlushAsync(ct).ConfigureAwait(false);
            int response = await ReadResponseAsync(_options.BlockTimeout, ct).ConfigureAwait(false);
            if (response == XYModemConstants.ACK)
            {
                return true;
            }
            if (response == XYModemConstants.CAN)
            {
                return false;
            }
            // NAK 是 XMODEM 规范里接收方对首个 EOT 的正常反应(用来过滤线路噪声),重发即可。
        }
        return false;
    }

    /// <summary>
    /// 等待接收方的握手字符并据此定下校验 / 流式模式。
    /// <c>'C'</c> = CRC16,<c>'G'</c> = YMODEM-G 流式,NAK = 8 位校验和。
    /// </summary>
    private async Task<bool> WaitForHandshakeAsync(FileTransferSession session, CancellationToken ct)
    {
        int c = await ReadHandshakeCharAsync(ct).ConfigureAwait(false);
        switch (c)
        {
            case XYModemConstants.CrcRequest:
                _useCrc = true;
                _streaming = false;
                return true;
            case XYModemConstants.StreamRequest:
                _useCrc = true;
                _streaming = true;
                return true;
            case XYModemConstants.NAK:
                _useCrc = false;
                _streaming = false;
                return true;
            case XYModemConstants.CAN:
                session.Status = FileTransferState.Cancelled;
                _observer?.OnSessionFailed(session, null);
                return false;
            default:
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(
                    session,
                    new TimeoutException("XMODEM/YMODEM 握手超时:接收方没有发出 'C'/'G'/NAK"));
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>按握手节奏等一个 <c>'C'</c>/<c>'G'</c>/NAK/CAN,跳过噪声;等不到返回 <c>-1</c>。</summary>
    private async Task<int> ReadHandshakeCharAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < _options.HandshakeRetries; attempt++)
        {
            int c = await ReadResponseAsync(_options.HandshakeInterval, ct).ConfigureAwait(false);
            if (c is XYModemConstants.CrcRequest
                or XYModemConstants.StreamRequest
                or XYModemConstants.NAK
                or XYModemConstants.CAN)
            {
                return c;
            }
        }
        return -1;
    }

    /// <summary>
    /// 读一个应答字节,跳过 <c>rb</c>/<c>rx</c> 启动时打印的横幅文本与 CR/LF 噪声。
    /// 超时或通道结束返回 <c>-1</c>。
    /// </summary>
    private async Task<int> ReadResponseAsync(TimeSpan budget, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            while (true)
            {
                int b = await _reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
                switch (b)
                {
                    case < 0:
                        return -1;
                    case XYModemConstants.ACK:
                    case XYModemConstants.NAK:
                    case XYModemConstants.CrcRequest:
                    case XYModemConstants.StreamRequest:
                        return b;
                    case XYModemConstants.CAN:
                        // 中止序列至少两个 CAN;单个可能只是噪声。
                        int next = await _reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
                        if (next == XYModemConstants.CAN)
                        {
                            return XYModemConstants.CAN;
                        }
                        continue;
                    default:
                        continue;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
    }

    private async Task TrySendCancelAsync()
    {
        try
        {
            await _duplex.WriteAsync(XYModemConstants.CancelSequence.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await _duplex.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 传输可能已断开;取消序列尽力而为。
        }
    }
}

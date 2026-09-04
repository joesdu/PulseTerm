using System.Buffers;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.XYModem.Model;

namespace VelaShell.Core.XYModem.Protocol;

/// <summary>
/// XMODEM / XMODEM-1K / YMODEM / YMODEM-G 接收方状态机:驱动「远端 <c>sb</c>/<c>sx</c> → 本地落地」。
/// 与 ZMODEM 不同,这一族协议由<b>接收方</b>发起:先反复发 <c>'C'</c>(CRC 模式)或 <c>'G'</c>
/// (YMODEM-G 流式)招呼发送方,收到数据块后逐块校验并应答 ACK / NAK,以 EOT 收束一个文件。
/// YMODEM 的 0 号块携带文件名与大小(格式与 ZMODEM 的 ZFILE 子包一致),空的 0 号块表示整批结束;
/// XMODEM 没有文件名,落地名由 <see cref="XYModemOptions.DefaultReceiveFileName" /> 指定。
/// </summary>
public sealed class XYModemReceiver(
    IByteDuplex duplex,
    IFileTransferSink sink,
    XYModemOptions? options = null,
    IFileTransferObserver? observer = null)
{
    private readonly IByteDuplex _duplex = duplex ?? throw new ArgumentNullException(nameof(duplex));
    private readonly IFileTransferSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly XYModemOptions _options = options ?? XYModemOptions.Default;
    private readonly IFileTransferObserver? _observer = observer;
    private readonly XYModemByteReader _reader = new(duplex);

    /// <summary>握手字符:CRC 模式发 <c>'C'</c>,YMODEM-G 发 <c>'G'</c>,校验和模式发 NAK。</summary>
    private byte HandshakeChar => _options.IsStreaming
        ? XYModemConstants.StreamRequest
        : _options.UseCrc
            ? XYModemConstants.CrcRequest
            : XYModemConstants.NAK;

    /// <summary>执行完整的接收会话,直到整批结束、被取消或出错。</summary>
    /// <param name="cancellationToken">取消整个会话的令牌。</param>
    /// <returns>本次会话的状态与已接收文件清单。</returns>
    public async Task<FileTransferSession> ReceiveAsync(CancellationToken cancellationToken)
    {
        var session = new FileTransferSession
        {
            Direction = FileTransferDirection.Receive,
            Protocol = _options.Protocol
        };
        TransferTrace.Log($"XY RECEIVER START protocol={_options.Protocol} crc={_options.UseCrc}");
        _observer?.OnSessionStarted(session);
        session.Status = FileTransferState.Transferring;

        try
        {
            while (true)
            {
                // 每个文件都从一次握手开始:反复发 'C'/'G' 直到看到块引导。
                int lead = await WaitForLeadAsync(handshaking: true, cancellationToken).ConfigureAwait(false);
                if (lead < 0)
                {
                    session.Status = FileTransferState.Failed;
                    _observer?.OnSessionFailed(session, new TimeoutException("XMODEM/YMODEM 握手超时:对端没有开始发送"));
                    await TrySendCancelAsync().ConfigureAwait(false);
                    return session;
                }
                if (lead == XYModemConstants.CAN)
                {
                    session.Status = FileTransferState.Cancelled;
                    _observer?.OnSessionFailed(session, null);
                    return session;
                }
                if (lead == XYModemConstants.EOT)
                {
                    // 对端直接给了 EOT(没有文件可发):应答后干净收束。
                    await WriteAsync([XYModemConstants.ACK], cancellationToken).ConfigureAwait(false);
                    break;
                }

                TransferFileMetadata? metadata =
                    await ReadFileHeaderAsync(session, lead, cancellationToken).ConfigureAwait(false);
                if (metadata is null)
                {
                    // YMODEM 的空 0 号块 = 整批结束;或握手 / 校验彻底失败(状态已在内部落定)。
                    break;
                }

                bool more = await ReceiveOneFileAsync(session, metadata, cancellationToken).ConfigureAwait(false);
                if (!more)
                {
                    return session;
                }
                if (!_options.IsBatch)
                {
                    break; // XMODEM 一次只传一个文件。
                }
            }

            if (session.Status == FileTransferState.Transferring)
            {
                session.Status = FileTransferState.Completed;
                _observer?.OnSessionCompleted(session);
            }
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
            // 协议块之后紧跟的字节属于 shell(sb/sx 退出后的提示符),退回通道交还终端。
            _duplex.Unread(_reader.DrainBuffered());
        }
    }

    /// <summary>
    /// 取得当前文件的元数据。YMODEM 读 0 号块并解析(空块表示整批结束,返回 <c>null</c>);
    /// XMODEM 没有文件信息块,直接用配置的默认文件名合成一份,并把已读到的引导字节退回读取器。
    /// </summary>
    private async Task<TransferFileMetadata?> ReadFileHeaderAsync(
        FileTransferSession session,
        int lead,
        CancellationToken ct)
    {
        if (!_options.IsBatch)
        {
            // 这个引导字节属于第 1 个数据块,退回去让数据循环重新读。
            _reader.Seed([(byte)lead]);
            return new TransferFileMetadata { FileName = _options.DefaultReceiveFileName };
        }

        int retries = 0;
        int currentLead = lead;
        while (true)
        {
            XYModemBlockResult block = await ReadBlockAsync(currentLead, ct).ConfigureAwait(false);
            if (block.Status == XYModemBlockStatus.Ok && block.Number == 0)
            {
                if (block.Payload.Length == 0 || block.Payload[0] == 0)
                {
                    // 空的 0 号块:整批结束。
                    await WriteAsync([XYModemConstants.ACK], ct).ConfigureAwait(false);
                    return null;
                }
                TransferFileMetadata metadata = TransferFileInfoCodec.Parse(block.Payload);
                await WriteAsync([XYModemConstants.ACK], ct).ConfigureAwait(false);
                TransferTrace.Log($"XY block0 name='{metadata.FileName}' size={metadata.Size}");
                return metadata;
            }
            if (block.Status == XYModemBlockStatus.EndOfStream || ++retries > _options.MaxRetries)
            {
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, new InvalidDataException("YMODEM 0 号块反复校验失败"));
                await TrySendCancelAsync().ConfigureAwait(false);
                return null;
            }
            // 0 号块坏了:NAK 请求重发,再等下一个引导。
            await WriteAsync([XYModemConstants.NAK], ct).ConfigureAwait(false);
            currentLead = await WaitForLeadAsync(handshaking: false, ct).ConfigureAwait(false);
            if (currentLead is < 0 or XYModemConstants.CAN)
            {
                session.Status = currentLead == XYModemConstants.CAN
                    ? FileTransferState.Cancelled
                    : FileTransferState.Failed;
                _observer?.OnSessionFailed(session, null);
                return null;
            }
        }
    }

    /// <summary>接收一个文件的全部数据块直到 EOT。返回 <c>false</c> 表示整个会话应终止。</summary>
    private async Task<bool> ReceiveOneFileAsync(
        FileTransferSession session,
        TransferFileMetadata metadata,
        CancellationToken ct)
    {
        var item = new FileTransferItem { FileName = metadata.FileName, Size = metadata.Size };
        session.AddItem(item);

        (TransferFileDisposition disposition, _) =
            await _sink.OnFileOfferedAsync(metadata, item, ct).ConfigureAwait(false);
        if (disposition != TransferFileDisposition.Accept)
        {
            // XMODEM / YMODEM 没有「跳过单个文件」的语义(那是 ZMODEM 的 ZSKIP),
            // 接收方唯一能表达的拒绝就是中止整批 —— 发 CAN 让 sb/sx 干净退出。
            item.Status = disposition == TransferFileDisposition.Skip
                ? FileTransferState.Skipped
                : FileTransferState.Cancelled;
            session.Status = FileTransferState.Cancelled;
            _observer?.OnFileSkipped(item);
            _observer?.OnSessionFailed(session, null);
            await TrySendCancelAsync().ConfigureAwait(false);
            return false;
        }

        item.Status = FileTransferState.Transferring;
        _observer?.OnFileStarted(item);

        if (_options.IsBatch)
        {
            // 0 号块已应答,再发一次握手字符,发送方据此开始推数据块。
            await WriteAsync([HandshakeChar], ct).ConfigureAwait(false);
        }

        int expected = 1;
        int retries = 0;
        long written = 0;
        // 大小未知(XMODEM)时延后一块写盘:末块的尾部是 SUB 填充,必须等确认它是末块才能裁掉。
        byte[]? deferred = null;

        while (true)
        {
            int lead = await WaitForLeadAsync(handshaking: false, ct).ConfigureAwait(false);
            if (lead < 0)
            {
                if (++retries > _options.MaxRetries)
                {
                    await FailItemAsync(item, new TimeoutException("XMODEM/YMODEM 数据块等待超时"), ct).ConfigureAwait(false);
                    session.Status = FileTransferState.Failed;
                    _observer?.OnSessionFailed(session, null);
                    await TrySendCancelAsync().ConfigureAwait(false);
                    return false;
                }
                // 催一下:重发上一个应答(流式模式重发握手字符)。
                await WriteAsync([_options.IsStreaming ? HandshakeChar : XYModemConstants.NAK], ct).ConfigureAwait(false);
                continue;
            }
            if (lead == XYModemConstants.CAN)
            {
                await FailItemAsync(item, null, ct).ConfigureAwait(false);
                session.Status = FileTransferState.Cancelled;
                _observer?.OnSessionFailed(session, null);
                return false;
            }
            if (lead == XYModemConstants.EOT)
            {
                if (deferred is not null)
                {
                    // 末块:裁掉尾部的 SUB(0x1A)填充再落盘。这是 XMODEM 唯一能表达「文件到此为止」
                    // 的方式 —— 它不传大小,所以对二进制文件而言结尾恰好是 0x1A 的情形无法区分,
                    // 这是协议本身的局限(YMODEM 正是为解决它才引入 0 号块的大小字段)。
                    int trimmed = TrimTrailingPadding(deferred);
                    if (trimmed > 0)
                    {
                        await _sink.WriteAsync(item, deferred.AsMemory(0, trimmed), ct).ConfigureAwait(false);
                        written += trimmed;
                    }
                }
                await WriteAsync([XYModemConstants.ACK], ct).ConfigureAwait(false);
                item.BytesTransferred = written;
                await _sink.CompleteAsync(item, ct).ConfigureAwait(false);
                item.Status = FileTransferState.Completed;
                _observer?.OnFileCompleted(item);
                return true;
            }

            XYModemBlockResult block = await ReadBlockAsync(lead, ct).ConfigureAwait(false);
            if (block.Status == XYModemBlockStatus.EndOfStream)
            {
                await FailItemAsync(item, null, ct).ConfigureAwait(false);
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, null);
                return false;
            }
            if (block.Status != XYModemBlockStatus.Ok)
            {
                if (_options.IsStreaming || ++retries > _options.MaxRetries)
                {
                    // YMODEM-G 没有重传机制:一旦出错就只能整批中止(这正是它换吞吐的代价)。
                    await FailItemAsync(item, new InvalidDataException("数据块校验失败"), ct).ConfigureAwait(false);
                    session.Status = FileTransferState.Failed;
                    _observer?.OnSessionFailed(session, null);
                    await TrySendCancelAsync().ConfigureAwait(false);
                    return false;
                }
                await WriteAsync([XYModemConstants.NAK], ct).ConfigureAwait(false);
                continue;
            }

            if (block.Number == ((expected - 1) & 0xFF))
            {
                // 重复块:说明我们上一个 ACK 丢了,补发一次,但不能重复写盘。
                await WriteAsync([XYModemConstants.ACK], ct).ConfigureAwait(false);
                continue;
            }
            if (block.Number != (expected & 0xFF))
            {
                // 块号错位意味着中间整块丢失,XMODEM 无法定位续传,只能中止。
                await FailItemAsync(item, new InvalidDataException($"块号错位:期望 {expected & 0xFF},收到 {block.Number}"), ct)
                    .ConfigureAwait(false);
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, null);
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
            }

            retries = 0;
            try
            {
                if (metadata.Size is { } size)
                {
                    // 大小已知(YMODEM):按声明长度截断,末块的填充自然被丢掉。
                    int take = (int)Math.Clamp(size - written, 0, block.Payload.Length);
                    if (take > 0)
                    {
                        await _sink.WriteAsync(item, block.Payload.AsMemory(0, take), ct).ConfigureAwait(false);
                        written += take;
                    }
                }
                else
                {
                    if (deferred is not null)
                    {
                        await _sink.WriteAsync(item, deferred, ct).ConfigureAwait(false);
                        written += deferred.Length;
                    }
                    deferred = block.Payload;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailItemAsync(item, ex, ct).ConfigureAwait(false);
                session.Status = FileTransferState.Failed;
                _observer?.OnSessionFailed(session, ex);
                await TrySendCancelAsync().ConfigureAwait(false);
                return false;
            }

            expected++;
            item.BytesTransferred = written + (deferred?.Length ?? 0);
            _observer?.OnFileProgress(item);
            if (!_options.IsStreaming)
            {
                await WriteAsync([XYModemConstants.ACK], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 等待下一个块引导字节(SOH / STX / EOT / CAN),跳过途中的噪声(<c>sb</c> 启动横幅之类)。
    /// <paramref name="handshaking" /> 为 true 时按 <see cref="XYModemOptions.HandshakeInterval" />
    /// 周期性重发握手字符招呼对端;否则只按 <see cref="XYModemOptions.BlockTimeout" /> 等一轮。
    /// </summary>
    /// <returns>引导字节;超时或通道结束返回 <c>-1</c>。</returns>
    private async Task<int> WaitForLeadAsync(bool handshaking, CancellationToken ct)
    {
        int attempts = handshaking ? _options.HandshakeRetries : 1;
        TimeSpan budget = handshaking ? _options.HandshakeInterval : _options.BlockTimeout;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (handshaking)
            {
                await WriteAsync([HandshakeChar], ct).ConfigureAwait(false);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget);
            try
            {
                while (true)
                {
                    int b = await _reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
                    if (b < 0)
                    {
                        return -1; // 通道结束。
                    }
                    switch (b)
                    {
                        case XYModemConstants.SOH:
                        case XYModemConstants.STX:
                        case XYModemConstants.EOT:
                            return b;
                        case XYModemConstants.CAN:
                            // 中止序列至少两个 CAN;单个 CAN 可能只是噪声,再确认一个。
                            int next = await _reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
                            if (next == XYModemConstants.CAN)
                            {
                                return XYModemConstants.CAN;
                            }
                            continue;
                        default:
                            continue; // 横幅文本 / 回车换行等噪声:跳过。
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 本轮超时:握手模式下再招呼一次,数据模式下交给调用方决定重试。
            }
        }
        return -1;
    }

    /// <summary>读取一个数据块的块号与负载并校验(引导字节已由调用方消费)。</summary>
    private async Task<XYModemBlockResult> ReadBlockAsync(int lead, CancellationToken ct)
    {
        int payloadLength = lead == XYModemConstants.STX
            ? XYModemConstants.LargePayload
            : XYModemConstants.SmallPayload;
        int checksumLength = _options.UseCrc ? 2 : 1;

        byte[] rented = ArrayPool<byte>.Shared.Rent(2 + payloadLength + checksumLength);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.BlockTimeout);
            Memory<byte> frame = rented.AsMemory(0, 2 + payloadLength + checksumLength);
            bool complete;
            try
            {
                complete = await _reader.ReadExactAsync(frame, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(XYModemBlockStatus.Timeout, 0, []);
            }
            if (!complete)
            {
                return new(XYModemBlockStatus.EndOfStream, 0, []);
            }

            byte seq = rented[0];
            byte inverse = rented[1];
            if ((byte)~seq != inverse)
            {
                return new(XYModemBlockStatus.BadBlockNumber, seq, []);
            }
            ReadOnlySpan<byte> payload = rented.AsSpan(2, payloadLength);
            ReadOnlySpan<byte> checksum = rented.AsSpan(2 + payloadLength, checksumLength);
            return XYModemBlock.Verify(payload, checksum, _options.UseCrc)
                ? new(XYModemBlockStatus.Ok, seq, payload.ToArray())
                : new(XYModemBlockStatus.ChecksumError, seq, []);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>去掉块尾连续的 <see cref="XYModemConstants.SUB" /> 填充,返回有效长度。</summary>
    private static int TrimTrailingPadding(ReadOnlySpan<byte> payload)
    {
        int end = payload.Length;
        while (end > 0 && payload[end - 1] == XYModemConstants.SUB)
        {
            end--;
        }
        return end;
    }

    private async Task FailItemAsync(FileTransferItem item, Exception? error, CancellationToken ct)
    {
        try
        {
            await _sink.FailAsync(item, error, ct).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不掩盖原始错误。
        }
        item.Status = FileTransferState.Failed;
        item.ErrorMessage ??= error?.Message;
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        TransferTrace.LogBytes("XY TX", data);
        await _duplex.WriteAsync(data, ct).ConfigureAwait(false);
        await _duplex.FlushAsync(ct).ConfigureAwait(false);
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

/// <summary>一个数据块读取结果的分类。</summary>
public enum XYModemBlockStatus
{
    /// <summary>块号与校验都通过。</summary>
    Ok,

    /// <summary>校验字段(CRC16 / 校验和)不匹配。</summary>
    ChecksumError,

    /// <summary>块号与其取反副本对不上(块头损坏)。</summary>
    BadBlockNumber,

    /// <summary>等待块字节超时。</summary>
    Timeout,

    /// <summary>底层通道结束(EOF)。</summary>
    EndOfStream
}

/// <summary>一次数据块读取的结果。</summary>
/// <param name="Status">读取状态。</param>
/// <param name="Number">块号(低 8 位)。</param>
/// <param name="Payload">块负载,仅在 <see cref="Status" /> 为 <see cref="XYModemBlockStatus.Ok" /> 时有效。</param>
public readonly record struct XYModemBlockResult(XYModemBlockStatus Status, byte Number, byte[] Payload);

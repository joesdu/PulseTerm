using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.FileTransfer.Abstractions;

/// <summary>
/// 接收方文件落地目标:引擎每收到一个文件声明(ZMODEM 的 ZFILE / YMODEM 的 0 号块 /
/// XMODEM 的合成声明)就询问处置(接收 / 跳过 / 中止),随后把校验通过的数据写入,
/// 并在文件结束或失败时收尾。实现方负责路径解析、覆盖策略与真正的磁盘 IO。
/// </summary>
public interface IFileTransferSink
{
    /// <summary>
    /// 发送方提供了一个文件。返回处置决定;若接受,可一并给出续传起始偏移
    /// (0 表示从头接收,&gt;0 表示崩溃恢复续传 —— 仅 ZMODEM 支持,其余协议会忽略)。
    /// </summary>
    /// <param name="metadata">解析出的文件元数据。</param>
    /// <param name="item">对应的传输项(实现可在此写入解析后的 LocalPath)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处置决定与续传偏移。</returns>
    ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
        TransferFileMetadata metadata,
        FileTransferItem item,
        CancellationToken cancellationToken);

    /// <summary>把一段已校验的文件数据写入目标。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="data">已反转义并通过 CRC 校验的数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>当前文件全部数据接收完毕,收尾并落盘。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken);

    /// <summary>当前文件失败(协议错误 / IO 错误 / 取消),清理半成品。</summary>
    /// <param name="item">当前文件项。</param>
    /// <param name="error">导致失败的异常;取消时可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken);
}

using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.FileTransfer.Abstractions;

/// <summary>
/// 发送方文件来源:远端运行 <c>rz</c> / <c>rb</c> / <c>rx</c> 时,引擎经此询问「要上传哪些文件」
/// 并逐个打开读取流。实现方负责文件选择 UI、路径解析与真正的磁盘 IO。
/// </summary>
public interface IFileTransferSource
{
    /// <summary>
    /// 会话开始时询问本批要发送的文件清单。返回空清单表示用户取消(引擎将优雅收尾或发取消序列)。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>要发送的文件清单(按发送顺序)。</returns>
    ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken);

    /// <summary>打开某个待发送文件的只读流(引擎负责释放)。</summary>
    /// <param name="file">待发送的文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可读、可定位的流(ZMODEM 的 <c>ZRPOS</c> 续传需要 <see cref="Stream.Seek" />)。</returns>
    ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken);
}

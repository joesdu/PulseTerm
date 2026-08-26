namespace VelaShell.Core.FileTransfer.Model;

/// <summary>
/// 一个待上传(本地 → 远端)的文件。<see cref="RemoteName" /> 是写进 ZMODEM 的 ZFILE 信息子包
/// 或 YMODEM 的 0 号块的名字,必须是纯文件名(不含目录),否则对端会按相对路径落地甚至拒收。
/// </summary>
/// <param name="LocalPath">本地文件的绝对路径。</param>
/// <param name="RemoteName">告知远端的文件名(纯文件名)。</param>
/// <param name="Size">文件字节大小。</param>
/// <param name="ModifiedUtc">文件修改时间(UTC);未知时为 <c>null</c>。</param>
public sealed record OutgoingTransferFile(
    string LocalPath,
    string RemoteName,
    long Size,
    DateTimeOffset? ModifiedUtc);

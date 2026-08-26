namespace VelaShell.Core.FileTransfer.Model;

/// <summary>
/// 发送方在传输开始前声明的文件元数据。ZMODEM 来自 ZFILE 数据子包、YMODEM 来自 0 号块,
/// 二者格式一致:首段为 NUL 结尾的文件名,其后是以空格分隔的可选字段
/// (字节大小、修改时间(八进制 Unix 时间)、文件模式、串行号、批中剩余文件数、剩余字节数)。
/// XMODEM 无此声明,引擎会合成一个只有本地占位文件名的实例。缺省字段以 <c>null</c> 表示。
/// </summary>
public sealed class TransferFileMetadata
{
    /// <summary>发送方声明的文件名(可能含相对路径,使用 <c>/</c> 分隔)。</summary>
    public required string FileName { get; init; }

    /// <summary>文件字节大小;发送方未提供时为 <c>null</c>。</summary>
    public long? Size { get; init; }

    /// <summary>文件修改时间(UTC);发送方未提供或为 0 时为 <c>null</c>。</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Unix 文件权限模式(八进制原值);发送方未提供时为 <c>null</c>。</summary>
    public int? UnixMode { get; init; }

    /// <summary>本批传输中当前文件之后仍剩余的文件数;未提供时为 <c>null</c>。</summary>
    public int? FilesRemaining { get; init; }

    /// <summary>解析所依据的原始元数据文本(便于诊断)。</summary>
    public string? RawMetadata { get; init; }
}

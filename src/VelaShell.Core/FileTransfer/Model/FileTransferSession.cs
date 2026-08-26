using System.Collections.ObjectModel;

namespace VelaShell.Core.FileTransfer.Model;

/// <summary>终端内文件传输会话中单个文件的进度与状态。</summary>
public sealed class FileTransferItem
{
    /// <summary>该文件项的唯一标识。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>发送方声明的文件名(接收侧)或本地文件名(发送侧)。</summary>
    public required string FileName { get; init; }

    /// <summary>该文件实际落地 / 读取的本地绝对路径;接收方在接受时填入。</summary>
    public string? LocalPath { get; set; }

    /// <summary>文件总字节数;未知时为 <c>null</c>(XMODEM 无大小声明)。</summary>
    public long? Size { get; set; }

    /// <summary>已传输字节数。</summary>
    public long BytesTransferred { get; set; }

    /// <summary>当前状态。</summary>
    public FileTransferState Status { get; set; } = FileTransferState.Pending;

    /// <summary>失败时的错误描述。</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>一次终端内文件传输会话:可含批量多个文件,方向固定,承载整体进度。</summary>
public sealed class FileTransferSession
{
    private readonly ObservableCollection<FileTransferItem> _items = [];

    /// <summary>会话唯一标识。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>传输方向。</summary>
    public required FileTransferDirection Direction { get; init; }

    /// <summary>本次会话所用的协议。</summary>
    public TerminalTransferProtocol Protocol { get; init; } = TerminalTransferProtocol.ZModem;

    /// <summary>会话整体状态。</summary>
    public FileTransferState Status { get; set; } = FileTransferState.Pending;

    /// <summary>本会话包含的文件项(按出现顺序)。</summary>
    public ReadOnlyObservableCollection<FileTransferItem> Items { get; }

    /// <summary>会话开始时刻(UTC)。</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>初始化一个会话。</summary>
    public FileTransferSession() => Items = new(_items);

    /// <summary>向会话追加一个文件项(在 UI 观察线程上下文中调用)。</summary>
    /// <param name="item">要追加的文件项。</param>
    public void AddItem(FileTransferItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }
}

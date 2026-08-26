using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.Tests.FileTransfer;

/// <summary>把收到的文件数据累积在内存中的测试用 sink(ZMODEM / XMODEM / YMODEM 通用)。</summary>
internal sealed class InMemoryFileSink : IFileTransferSink
{
    private readonly Dictionary<Guid, MemoryStream> _streams = [];

    /// <summary>下一次 <see cref="OnFileOfferedAsync" /> 要返回的处置决定。</summary>
    public TransferFileDisposition NextDisposition { get; set; } = TransferFileDisposition.Accept;

    /// <summary>已成功收完的文件:文件名 → 内容。</summary>
    public Dictionary<string, byte[]> Completed { get; } = [];

    /// <summary>发送方声明过的全部文件名(按出现顺序)。</summary>
    public List<string> OfferedNames { get; } = [];

    /// <summary>发送方声明过的全部文件大小(与 <see cref="OfferedNames" /> 对齐)。</summary>
    public List<long?> OfferedSizes { get; } = [];

    public ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
        TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken)
    {
        OfferedNames.Add(metadata.FileName);
        OfferedSizes.Add(metadata.Size);
        item.LocalPath = metadata.FileName;
        if (NextDisposition == TransferFileDisposition.Accept)
        {
            _streams[item.Id] = new MemoryStream();
        }
        return ValueTask.FromResult((NextDisposition, 0L));
    }

    public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _streams[item.Id].Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken)
    {
        Completed[item.FileName] = _streams[item.Id].ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>内存文件来源:直接给出待发送内容,不碰磁盘。</summary>
internal sealed class InMemoryFileSource((string Name, byte[] Data)[] files) : IFileTransferSource
{
    private readonly List<(string Name, byte[] Data)> _files = [.. files];

    public ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<OutgoingTransferFile>>(
            [.. _files.Select(f => new OutgoingTransferFile($"/tmp/{f.Name}", f.Name, f.Data.Length, null))]);

    public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(
            new MemoryStream(_files.First(f => f.Name == file.RemoteName).Data, writable: false));
}

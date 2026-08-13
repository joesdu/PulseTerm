using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteFsApi" /> 的桥接实现:复用宿主既有会话的 SFTP 通道(不重复认证)。
/// 进度回调做 ≥100ms 节流 —— 插件流量不允许灌爆调用方的调度器
/// (吸取大文件传输卡顿的历史教训)。
/// </summary>
internal sealed class RemoteFsCapability(ISftpService sftp, ISshConnectionService connections) : IRemoteFsApi
{
    private static RemoteFileEntry Map(RemoteFileInfo info) => new(
        info.Name, info.FullPath, info.IsDirectory, info.Size,
        new DateTimeOffset(DateTime.SpecifyKind(info.LastModified, DateTimeKind.Utc), TimeSpan.Zero),
        info.Permissions, info.Owner, info.Group);

    /// <summary>解析并校验会话 id;无效或未连接时抛 <see cref="PluginSessionNotFoundException" />。</summary>
    private Guid Resolve(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out Guid id)
            || connections.GetSession(id) is not { Status: SessionStatus.Connected })
        {
            throw new PluginSessionNotFoundException(sessionId);
        }
        return id;
    }

    /// <summary>把 SDK 进度包成宿主进度并节流:至少间隔 100ms 才转发一次,末帧(完成)始终放行。</summary>
    private static Progress<TransferProgress>? Throttle(IProgress<RemoteTransferProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }
        long lastTicks = 0;
        return new Progress<TransferProgress>(p =>
        {
            long now = Environment.TickCount64;
            bool final = p.TotalBytes > 0 && p.BytesTransferred >= p.TotalBytes;
            if (!final && now - Interlocked.Read(ref lastTicks) < 100)
            {
                return;
            }
            Interlocked.Exchange(ref lastTicks, now);
            progress.Report(new(p.BytesTransferred, p.TotalBytes));
        });
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path,
        CancellationToken cancellationToken = default)
    {
        List<RemoteFileInfo> entries = await sftp.ListDirectoryAsync(Resolve(sessionId), path, cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(Map)];
    }

    public async Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return Map(await sftp.GetFileInfoAsync(Resolve(sessionId), path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return null; // 语义约定:不存在返回 null,不以异常判存在。
        }
    }

    public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => sftp.ExistsAsync(Resolve(sessionId), path, cancellationToken);

    public Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => sftp.GetWorkingDirectoryAsync(Resolve(sessionId), cancellationToken);

    public Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => sftp.DownloadFileAsync(Resolve(sessionId), remotePath, localPath, Throttle(progress), 0, cancellationToken);

    public Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => sftp.UploadFileAsync(Resolve(sessionId), localPath, remotePath, Throttle(progress), 0, cancellationToken);

    public Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.OpenReadAsync(Resolve(sessionId), remotePath, cancellationToken);

    public async Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        RemoteFileInfo info = await sftp.GetFileInfoAsync(id, remotePath, cancellationToken).ConfigureAwait(false);
        if (info.Size > maxBytes)
        {
            throw new InvalidOperationException(
                $"Remote file '{remotePath}' is {info.Size} bytes (limit {maxBytes}); use OpenReadAsync/DownloadFileAsync for large files.");
        }
        await using Stream stream = await sftp.OpenReadAsync(id, remotePath, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(info.Size, maxBytes));
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        Guid id = Resolve(sessionId);
        string tmp = Path.Combine(Path.GetTempPath(), $"velashell-plugin-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, content.ToArray(), cancellationToken).ConfigureAwait(false);
            await sftp.UploadFileAsync(id, tmp, remotePath, null, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.DeleteAsync(Resolve(sessionId), remotePath, null, cancellationToken);

    public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.CreateDirectoryAsync(Resolve(sessionId), remotePath, cancellationToken);

    public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => sftp.EnsureDirectoryAsync(Resolve(sessionId), remotePath, cancellationToken);

    public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
        => sftp.RenameAsync(Resolve(sessionId), oldPath, newPath, cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理尽力而为。
        }
    }
}

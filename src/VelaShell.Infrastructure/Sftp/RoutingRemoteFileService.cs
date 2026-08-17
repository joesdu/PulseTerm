using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;

namespace VelaShell.Infrastructure.Sftp;

/// <summary>
/// 按会话归属把远程文件操作分派到对应后端的路由:FTP 会话交给 <see cref="Ftp.FtpFileService" />,
/// 插件协议(S3、WebDAV…)的会话交给
/// <see cref="Plugins.Protocols.PluginProtocolFileService" />,其余(SSH 上的 SFTP)走原本的实现。
/// <para>
/// 之所以能这么干,是因为 <see cref="ISftpService" /> 的每个成员都以 <c>Guid sessionId</c> 为键、
/// 返回协议无关的 <see cref="RemoteFileInfo" /> —— 文件浏览器、传输管理器、限速、拖放
/// 全部只认这个接口,因此新增一种协议对它们是零改动。
/// </para>
/// <para>
/// 注意这里的后端数量已经**封顶**:插件协议那一路是一个通用出口,再多几十种协议也只是
/// 注册表里多几行,不会再回来改这个类。
/// </para>
/// </summary>
public sealed class RoutingRemoteFileService(
    ISftpService sftp,
    ISftpService ftp,
    IFtpSessionService ftpSessions,
    ISftpService plugin,
    IPluginProtocolSessionService pluginSessions) : ISftpService
{
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
    private readonly ISftpService _ftp = ftp ?? throw new ArgumentNullException(nameof(ftp));
    private readonly IFtpSessionService _ftpSessions = ftpSessions ?? throw new ArgumentNullException(nameof(ftpSessions));
    private readonly ISftpService _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    private readonly IPluginProtocolSessionService _pluginSessions = pluginSessions ?? throw new ArgumentNullException(nameof(pluginSessions));

    /// <inheritdoc />
    public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).ListDirectoryAsync(sessionId, path, cancellationToken);

    /// <inheritdoc />
    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).UploadFileAsync(sessionId, localPath, remotePath, progress, resumeOffset, cancellationToken);

    /// <inheritdoc />
    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).DownloadFileAsync(sessionId, remotePath, localPath, progress, resumeOffset, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).DeleteAsync(sessionId, remotePath, progress, cancellationToken);

    /// <inheritdoc />
    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).CreateDirectoryAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).CreateFileAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).EnsureDirectoryAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).RenameAsync(sessionId, oldPath, newPath, cancellationToken);

    /// <inheritdoc />
    public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).CopyAsync(sessionId, sourcePath, destPath, progress, cancellationToken);

    /// <inheritdoc />
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).SetPermissionsAsync(sessionId, remotePath, octalMode, cancellationToken);

    /// <inheritdoc />
    public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).GetFileInfoAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).OpenReadAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).ExistsAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).GetWorkingDirectoryAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Resolve(sessionId).CloseSessionAsync(sessionId, cancellationToken);

    /// <summary>释放全部后端。</summary>
    public async ValueTask DisposeAsync()
    {
        await _plugin.DisposeAsync().ConfigureAwait(false);
        await _ftp.DisposeAsync().ConfigureAwait(false);
        await _sftp.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 会话归属判定:只有某个后端真正持有该会话时才走它,其余一律走 SFTP ——
    /// 未知会话的报错也就仍由原本的 SFTP 实现给出,行为与加这些协议之前一致。
    /// </summary>
    private ISftpService Resolve(Guid sessionId)
    {
        if (_ftpSessions.OwnsSession(sessionId))
        {
            return _ftp;
        }
        return _pluginSessions.OwnsSession(sessionId) ? _plugin : _sftp;
    }
}

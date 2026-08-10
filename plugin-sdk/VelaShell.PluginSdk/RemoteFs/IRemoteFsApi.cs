namespace VelaShell.PluginSdk.RemoteFs;

/// <summary>远端文件或目录的元数据。</summary>
/// <param name="Name">名称(不含路径)。</param>
/// <param name="FullPath">完整远端路径。</param>
/// <param name="IsDirectory">是否为目录。</param>
/// <param name="Size">大小(字节);目录为 0 或实现定义值。</param>
/// <param name="LastModified">最后修改时间。</param>
/// <param name="Permissions">权限字符串(如 <c>rwxr-xr-x</c>)。</param>
/// <param name="Owner">属主。</param>
/// <param name="Group">属组。</param>
public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModified,
    string Permissions,
    string Owner,
    string Group);

/// <summary>文件传输进度。宿主对回调做了节流(≥100ms 一次),回调频率不代表 I/O 频率。</summary>
/// <param name="TransferredBytes">已传输字节数。</param>
/// <param name="TotalBytes">总字节数;未知时为 -1。</param>
public readonly record struct RemoteTransferProgress(long TransferredBytes, long TotalBytes);

/// <summary>
/// 远程文件能力:复用宿主既有 SSH 会话的 SFTP 通道(不重复认证)。
/// 会话不存在或未连接时抛 <see cref="PluginSessionNotFoundException" />。
/// 语义约定:<see cref="StatAsync" /> 对不存在的路径返回 <see langword="null" />,不以异常判存在。
/// </summary>
public interface IRemoteFsApi
{
    /// <summary>列举远端目录。</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>获取单个远端条目的元数据;路径不存在时返回 <see langword="null" />。</summary>
    Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>远端路径是否存在(文件或目录)。</summary>
    Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>会话的 SFTP 工作目录(通常为登录账户的 home 目录)。</summary>
    Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>下载远端文件到本地路径。</summary>
    Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>上传本地文件到远端路径。</summary>
    Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开远端文件的**只读顺序流**(边读边处理,不经本地临时文件;调用方负责释放)。
    /// 大文件解析(日志扫描、逐行处理)优先用它;隔离模式下流经 RPC 分块拉取,
    /// 不支持 Seek。
    /// </summary>
    Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取远端小文件内容。文件大于 <paramref name="maxBytes" />(默认 16MB)时抛
    /// <see cref="InvalidOperationException" /> —— 大文件请用 <see cref="DownloadFileAsync" />。
    /// </summary>
    Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default);

    /// <summary>将内容写入远端文件(覆盖)。适合小文件;大文件请用 <see cref="UploadFileAsync" />。</summary>
    Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>删除远端文件,或递归删除目录。</summary>
    Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>创建远端目录(已存在时报错;幂等版见 <see cref="EnsureDirectoryAsync" />)。</summary>
    Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>确保远端目录存在(幂等)。</summary>
    Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>重命名或移动远端条目。</summary>
    Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);
}

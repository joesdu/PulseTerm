using System.Buffers;
using Tmds.Ssh;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Wraps a <see cref="SftpClient" /> to provide a common interface for SFTP operations, with exception translation and progress reporting.
/// </summary>
/// <param name="clientFactory"></param>
public sealed class TmdsSftpClientWrapper(Func<Task<SftpClient>> clientFactory) : ISftpClientWrapper
{
    /// <summary>续传上传每次写入的块大小;高延迟链路上块越大往返越少。</summary>
    private const int ResumeChunkSize = 256 * 1024;

    /// <inheritdoc />
    /// <remarks>
    /// Tmds.Ssh 单文件传输时最多有 <c>SftpChannel.MaxConcurrentBuffers</c>(64)个写缓冲区在途,
    /// 每个上限 <c>Constants.MaxDataPacketSize</c>(32KB)。断开时最高偏移已被确认、而其之前
    /// 尚未确认的缓冲区就成了空洞,所以回退 64 × 32KB = 2MB 才能保证起点之前连续。
    /// 对 GB 级文件而言重传 2MB 可忽略不计,换来的是"续传出来的文件一定是对的"。
    /// </remarks>
    public long ResumeSafetyMargin => 64L * 32 * 1024;

    private readonly Func<Task<SftpClient>> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private SftpClient? _client;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the SFTP client is currently connected and not disposed.
    /// </summary>
    public bool IsConnected => !_disposed && _client is not null;

    /// <summary>
    /// Gets or sets the connection timeout for the SFTP client. The default value is 10 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return field; }
        set { ObjectDisposedException.ThrowIf(_disposed, this); field = value; }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the current working directory of the SFTP client. The default value is "/".
    /// </summary>
    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }

        private set;
    } = "/";

    /// <summary>
    /// Connects to the SFTP server using the provided client factory. If already connected, this method does nothing.
    /// If an exception occurs during connection, it is translated to a more meaningful exception using <see cref="TmdsSshInterop.Translate" />.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        try
        {
            _client = await _clientFactory().ConfigureAwait(false);
            WorkingDirectory = _client.WorkingDirectory.Path;
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated) { throw translated; }
    }

    /// <summary>
    /// Disconnects from the SFTP server and disposes of the underlying client. If already disconnected or disposed, this method does nothing.
    /// </summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client?.Dispose(); _client = null;
    }

    /// <summary>
    /// Lists the entries in the specified directory on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<IEnumerable<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(() => ListEntriesAsync(EnsureClient(), path, ct), ct);
    }

    /// <summary>
    /// Uploads a file to the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="path"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task UploadAsync(Stream input, string path, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct)
            .ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Downloads a file from the specified path on the SFTP server to the provided output stream. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="output"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DownloadAsync(string path, Stream output, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient()
            .DownloadFileAsync(path, output, ToProgress(cb), ct)
            .ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Deletes the file at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteFileAsync(path, ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Deletes the directory at the specified path on the SFTP server, including all its contents. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().DeleteDirectoryAsync(path, true, null, ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Creates a directory at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().CreateDirectoryAsync(path, cancellationToken: ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Renames a file from the old path to the new path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="o"></param>
    /// <param name="n"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task RenameFileAsync(string o, string n, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () => await EnsureClient().RenameAsync(o, n, ct).ConfigureAwait(false), ct);
    }

    // Tmds.Ssh 未单独暴露 posix-rename 扩展;RenameAsync 即其现有的重命名路径。
    /// <summary>
    /// Renames a file from the old path to the new path on the SFTP server using POSIX semantics. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="o"></param>
    /// <param name="n"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task PosixRenameFileAsync(string o, string n, CancellationToken ct = default) => RenameFileAsync(o, n, ct);

    /// <summary>
    /// Checks if a file or directory exists at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // Tmds.Ssh 的 GetAttributesAsync 对不存在的路径返回 null 而非抛异常
            // (SSH.NET 是抛 SftpPathNotFoundException):判空即存在性,零异常控制流。
            FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs is not null;
        }, ct);
    }

    /// <summary>
    /// Changes the permissions of a file or directory at the specified path on the SFTP server.
    /// The mode is specified as a short integer representing the Unix file mode (e.g., 0o755).
    /// If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="mode"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            var unixMode = (UnixFileMode)Convert.ToInt32(mode.ToString(), 8);
            var perms = UnixFilePermissionsExtensions.ToUnixFilePermissions(unixMode);
            await EnsureClient().SetAttributesAsync(path, permissions: perms, cancellationToken: ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>设置远端条目的最后修改时间(SFTP setstat;协议要求 atime/mtime 成对下发,atime 取同值)。</summary>
    public Task SetLastWriteTimeAsync(string path, DateTimeOffset lastWriteTime, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            await EnsureClient()
                .SetAttributesAsync(path, times: (lastWriteTime, lastWriteTime), cancellationToken: ct)
                .ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Opens a file at the specified path on the SFTP server with the given mode and access. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="mode"></param>
    /// <param name="access"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="VelaSftpPathNotFoundException"></exception>
    public Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync<Stream>(async () =>
        {
            SftpClient c = EnsureClient();
            SftpFile? file = mode switch
            {
                FileMode.CreateNew => await c.CreateNewFileAsync(path, access, SeekableOptions(), ct).ConfigureAwait(false),
                // Create/Truncate 语义要求截断旧内容,否则新内容比旧文件短时会残留旧尾部数据
                FileMode.Create => await c.OpenOrCreateFileAsync(path, access, SeekableOptions(OpenMode.Truncate), ct).ConfigureAwait(false),
                FileMode.Truncate => await c.OpenFileAsync(path, access, SeekableOptions(OpenMode.Truncate), ct).ConfigureAwait(false),
                FileMode.Append => await c.OpenOrCreateFileAsync(path, access, SeekableOptions(OpenMode.Append), ct).ConfigureAwait(false),
                FileMode.OpenOrCreate => await c.OpenOrCreateFileAsync(path, access, SeekableOptions(), ct).ConfigureAwait(false),
                _ => await c.OpenFileAsync(path, access, SeekableOptions(), ct).ConfigureAwait(false),
            };
            // Tmds.Ssh 对不存在的文件返回 null 而非抛异常
            return file ?? throw new VelaSftpPathNotFoundException($"File not found: {path}");
        }, ct);
    }

    /// <summary>
    /// Gets the size of a file at the specified path on the SFTP server. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            // 不存在返回 null → 契约 -1,零异常控制流。
            FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs?.Length ?? -1L;
        }, ct);
    }

    /// <summary>
    /// 直接 stat 单个条目;不存在返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// followLinks 传 true,与 <see cref="ListEntriesAsync" /> 所用的
    /// <see cref="Tmds.Ssh.EnumerationOptions" /> 默认值(FollowFileLinks / FollowDirectoryLinks 均为 true)
    /// 保持一致 —— 否则"指向目录的符号链接"会被判成非目录,删除/复制的递归分支就会走错。
    /// </remarks>
    public Task<SftpEntry?> GetEntryAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            FileEntryAttributes? attrs = await EnsureClient()
                .GetAttributesAsync(path, true, null, ct).ConfigureAwait(false);
            return attrs is null ? null : MapEntry(path, attrs);
        }, ct);
    }

    /// <summary>
    /// Uploads a file to the specified path on the SFTP server, resuming from the given offset. If the client is not connected, an <see cref="InvalidOperationException" /> is thrown.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="path"></param>
    /// <param name="resumeOffset"></param>
    /// <param name="cb"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="VelaSftpPathNotFoundException"></exception>
    public Task UploadAsync(Stream input, string path, long resumeOffset, Action<ulong>? cb = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GuardedAsync(async () =>
        {
            SftpClient c = EnsureClient();
            if (resumeOffset <= 0)
            {
                await c.UploadFileAsync(input, path, overwrite: true, progress: ToProgress(cb), cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            input.Seek(resumeOffset, SeekOrigin.Begin);
            await using SftpFile remote = await c.OpenFileAsync(path, FileAccess.Write, new FileOpenOptions(), ct).ConfigureAwait(false)
                ?? throw new VelaSftpPathNotFoundException($"File not found: {path}");

            // 32KB 一块在高延迟链路上往返次数过多;256KB 显著减少 SFTP 往返。
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ResumeChunkSize);
            try
            {
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, ResumeChunkSize), ct).ConfigureAwait(false)) > 0)
                {
                    // 用绝对偏移写(WriteAtAsync),不依赖文件内部位置。
                    // 原先是 Seek(offset) + 顺序 WriteAsync:位置是隐式状态,一旦被扰动就静默错位,
                    // 而且"写到哪"与"从哪读"是两处独立推进的,对不上也没人发现。
                    // 续传起点本身由 Core 的 SftpService.ResolveUploadResumeAsync 负责核实。
                    await remote.WriteAtAsync(buffer.AsMemory(0, read), resumeOffset + written, ct).ConfigureAwait(false);
                    written += read;
                    cb?.Invoke((ulong)(resumeOffset + written));
                }

                // 必须显式异步关闭:同步 Dispose 不保证挂起的写入已经落地、也不保证把失败抛出来,
                // 表现就是"上传显示完成,远端文件尾部却缺字节"。
                await remote.CloseAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, ct);
    }

    /// <summary>
    /// Disposes the SFTP client and releases all resources.
    /// </summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _client?.Dispose(); _client = null; GC.SuppressFinalize(this); }

    /// <summary>
    /// 构造可 Seek 的打开选项。
    /// <para>
    /// <see cref="ISftpClientWrapper.OpenAsync" /> 的契约要求返回的流支持 Seek(续传起点校验、
    /// 断点续传下载都要按偏移定位)。而 <see cref="FileOpenOptions" /> 的默认值是
    /// <c>Seekable = false</c> 且 <c>CacheLength = false</c>,此时 <c>SftpFile.Seek</c> 会直接抛出
    /// <see cref="NotSupportedException" /> —— 症状就是"上传一半取消后再传同一个文件必报错"。
    /// </para>
    /// <para>
    /// 两个开关互相独立、缺一不可:<c>CanSeek</c> 由 <c>Seekable</c> 决定,而 <c>Seek</c> 内部还要求
    /// 长度已缓存(<c>CacheLength</c>)。代价是打开时多一次取属性的往返,相对传输本身可忽略。
    /// </para>
    /// </summary>
    private static FileOpenOptions SeekableOptions(OpenMode mode = OpenMode.Default) => new()
    {
        OpenMode = mode,
        Seekable = true,
        CacheLength = true,
    };

    private SftpClient EnsureClient() => _client ?? throw new InvalidOperationException("Not connected.");

    private static async Task<IEnumerable<SftpEntry>> ListEntriesAsync(SftpClient client, string dir, CancellationToken ct)
    {
        var entries = new List<SftpEntry>();
        await foreach ((string Path, FileEntryAttributes Attributes) result in SftpDirectoryExtensions.GetDirectoryEntriesAsync(
            client, dir, new Tmds.Ssh.EnumerationOptions())
            .WithCancellation(ct).ConfigureAwait(false))
        {
            entries.Add(MapEntry(result.Path, result.Attributes));
        }
        return entries;
    }

    internal static SftpEntry MapEntry(string fullPath, FileEntryAttributes attrs)
    {
        string fn = Path.GetFileName(fullPath);
        UnixFilePermissions p = attrs.Permissions;
        return new SftpEntry
        {
            Name = fn,
            FullName = fullPath,
            Length = attrs.Length,
            IsDirectory = attrs.FileType == UnixFileType.Directory,
            // 必须 LocalDateTime 而非 .DateTime:后者把 DateTimeOffset 的偏移剥掉,留下
            // "UTC 墙钟数 + Kind=Unspecified" —— 文件浏览器显示成 +0 时区,下载保留时间戳
            // (File.SetLastWriteTime 按本地解读)还会再错一次时差。SFTP mtime 是 Unix 纪元秒,
            // 换算本地时间展示才与 ls -l / date 一致。
            LastWriteTime = attrs.LastWriteTime.LocalDateTime,
            UserId = attrs.Uid,
            GroupId = attrs.Gid,
            OwnerCanRead = (p & UnixFilePermissions.UserRead) != 0,
            OwnerCanWrite = (p & UnixFilePermissions.UserWrite) != 0,
            OwnerCanExecute = (p & UnixFilePermissions.UserExecute) != 0,
            GroupCanRead = (p & UnixFilePermissions.GroupRead) != 0,
            GroupCanWrite = (p & UnixFilePermissions.GroupWrite) != 0,
            GroupCanExecute = (p & UnixFilePermissions.GroupExecute) != 0,
            OthersCanRead = (p & UnixFilePermissions.OtherRead) != 0,
            OthersCanWrite = (p & UnixFilePermissions.OtherWrite) != 0,
            OthersCanExecute = (p & UnixFilePermissions.OtherExecute) != 0,
        };
    }

    /// <summary>
    /// 统一异常翻译:释放竞态导致的 NRE 归一为 ObjectDisposedException,
    /// 其余库异常经 <see cref="TmdsSshInterop.Translate" /> 翻译为 Core 中立异常。
    /// </summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> op, CancellationToken ct = default)
    {
        try { return await op().ConfigureAwait(false); }
        catch (NullReferenceException) when (IsTornDown()) { throw new ObjectDisposedException(nameof(TmdsSftpClientWrapper)); }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, ct) is { } t) { throw t; }
    }

    private async Task GuardedAsync(Func<Task> op, CancellationToken ct = default) => await GuardedAsync(async () => { await op().ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);

    private bool IsTornDown() { if (_disposed) return true; try { return _client is null; } catch { return true; } }

    private static ProgressAdapter? ToProgress(Action<ulong>? cb) => cb is null ? null : new ProgressAdapter(cb);

    /// <summary>
    /// 把 Tmds.Ssh 的分块传输进度桥接为累计字节回调:
    /// offset 是每块传输完成后在远端(上传)/本地目标(下载)文件中的位置,即累计已传字节数。
    /// </summary>
    private sealed class ProgressAdapter(Action<ulong> callback) : SftpProgressHandler
    {
        protected override void DataTransferred(int index, long bytesTransferred, long offset) => callback((ulong)offset);
    }
}

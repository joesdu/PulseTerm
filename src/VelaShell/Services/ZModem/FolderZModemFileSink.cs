using VelaShell.Core.Models;
using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Model;

namespace VelaShell.Services.ZModem;

/// <summary>
/// 基于「一次会话选一次目录」的 ZMODEM 接收落地实现:首个文件触发一次原生文件夹选择框,
/// 之后本会话所有文件都写入该目录。文件名经 <see cref="Path.GetFileName(string)" /> 归一
/// (防止 <c>../</c> 路径穿越)并替换非法字符;重名按设置里的冲突策略处理;取消选择即中止会话。
/// 由 <c>ZModemTerminalRouter</c> 每会话经 sinkFactory 新建一个实例,因此不跨会话缓存目录。
/// </summary>
internal sealed class FolderZModemFileSink(
    Func<ZModemFolderPromptRequest, CancellationToken, Task<string?>> pickFolderAsync,
    Func<Task<AppSettings>> getSettingsAsync) : IZModemFileSink, IAsyncDisposable
{
    private readonly Func<ZModemFolderPromptRequest, CancellationToken, Task<string?>> _pickFolderAsync =
        pickFolderAsync ?? throw new ArgumentNullException(nameof(pickFolderAsync));
    private readonly Func<Task<AppSettings>> _getSettingsAsync =
        getSettingsAsync ?? throw new ArgumentNullException(nameof(getSettingsAsync));

    private readonly Dictionary<Guid, FileStream> _streams = [];

    // 本会话选定的目录:首个文件解析一次,后续复用。null = 尚未选择;空串 = 用户已取消。
    private string? _sessionFolder;
    private bool _folderResolved;

    /// <inheritdoc />
    public async ValueTask<(ZModemFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
        ZModemFileMetadata metadata,
        ZModemTransferItem item,
        CancellationToken cancellationToken)
    {
        AppSettings settings = await _getSettingsAsync().ConfigureAwait(false);

        // 首个文件:弹目录选择框,结果缓存在本会话 sink 内。
        if (!_folderResolved)
        {
            string suggested = ExpandDownloadDirectory(settings.Transfer.LocalDownloadDirectory);
            var request = new ZModemFolderPromptRequest(suggested, metadata.FileName, metadata.Size);
            _sessionFolder = await _pickFolderAsync(request, cancellationToken).ConfigureAwait(false);

            // 防误触:首次取消可能是不小心点了关闭/取消,再给一次机会;二次取消即视为确认中止。
            if (string.IsNullOrWhiteSpace(_sessionFolder))
            {
                _sessionFolder = await _pickFolderAsync(
                    request with { IsRetryAfterCancel = true },
                    cancellationToken).ConfigureAwait(false);
            }
            _folderResolved = true;
        }

        // 两次都取消了目录选择:中止整个会话(而非跳过单个文件)。
        if (string.IsNullOrWhiteSpace(_sessionFolder))
        {
            return (ZModemFileDisposition.Abort, 0);
        }

        // 路径穿越防护:只取文件名部分,再替换文件名中的非法字符。
        string safeName = SanitizeFileName(metadata.FileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return (ZModemFileDisposition.Skip, 0);
        }

        try
        {
            Directory.CreateDirectory(_sessionFolder);
        }
        catch (Exception)
        {
            // 目录不可创建 / 不可写:中止会话,避免逐个文件反复失败。
            return (ZModemFileDisposition.Abort, 0);
        }

        string? targetPath = ResolveConflictPath(_sessionFolder, safeName, settings.Transfer.ConflictPolicy);
        if (targetPath is null)
        {
            // 冲突策略为 skip 且文件已存在。
            return (ZModemFileDisposition.Skip, 0);
        }

        FileStream stream;
        try
        {
            stream = new(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception)
        {
            return (ZModemFileDisposition.Abort, 0);
        }

        item.LocalPath = targetPath;
        item.Size = metadata.Size;
        _streams[item.Id] = stream;
        return (ZModemFileDisposition.Accept, 0);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ZModemTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_streams.TryGetValue(item.Id, out FileStream? stream))
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(ZModemTransferItem item, CancellationToken cancellationToken)
    {
        if (_streams.Remove(item.Id, out FileStream? stream))
        {
            try
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 刷盘失败(磁盘满 / 权限变更):尽力而为,错误不可恢复。
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask FailAsync(ZModemTransferItem item, Exception? error, CancellationToken cancellationToken)
    {
        _ = error;
        _ = cancellationToken;
        if (_streams.Remove(item.Id, out FileStream? stream))
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放失败不影响会话。
            }
        }
        // 保留半成品文件(不删除),为将来的断点续传留出空间。
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (FileStream stream in _streams.Values)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放阶段忽略流异常。
            }
        }
        _streams.Clear();
    }

    /// <summary>
    /// 把配置的下载目录解析成绝对路径:<c>~</c> 与相对路径一律以用户主目录为基准,
    /// 空值回退到系统"下载"文件夹(见 <see cref="UserPathResolver" />)。
    /// </summary>
    private static string ExpandDownloadDirectory(string path) => UserPathResolver.ResolveOrDownloads(path);

    /// <summary>把远端文件名归一为安全的纯文件名:剥离任何目录成分并替换非法字符。</summary>
    private static string SanitizeFileName(string remoteFileName)
    {
        // 先按两种分隔符取尾段,规避 Windows 上 GetFileName 不识别 '/' 的边角情况。
        string name = remoteFileName.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }
        name = Path.GetFileName(name);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        // 拒绝 "." / ".." 这类无实体名。
        return name is "." or ".." ? string.Empty : name;
    }

    /// <summary>按冲突策略解析实际写入路径;返回 <c>null</c> 表示应跳过该文件。</summary>
    private static string? ResolveConflictPath(string folder, string safeName, string conflictPolicy)
    {
        string path = Path.Combine(folder, safeName);
        if (!File.Exists(path))
        {
            return path;
        }
        // ConflictPolicy: ask / overwrite / skip / rename。无交互 UI 时 ask 退化为 rename(不覆盖、不丢数据)。
        return conflictPolicy switch
        {
            "overwrite" => path,
            "skip" => null,
            _ => CreateUniquePath(folder, safeName) // rename / ask / 未知
        };
    }

    /// <summary>为重名文件生成不冲突的路径,形如 <c>name (1).ext</c>。</summary>
    private static string CreateUniquePath(string folder, string safeName)
    {
        string stem = Path.GetFileNameWithoutExtension(safeName);
        string ext = Path.GetExtension(safeName);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

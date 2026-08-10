using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IRemoteFsApi" /> 的内存实现:每会话一棵以 <c>/</c> 分隔的路径树。
/// 语义与真实实现对齐:Stat 对不存在路径返回 <see langword="null" />;
/// ReadAllBytes 超限抛 <see cref="InvalidOperationException" />。
/// </summary>
public sealed class FakeRemoteFs : IRemoteFsApi
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _dirs = new(StringComparer.Ordinal);

    /// <summary>预置一个远端文件(自动创建父目录)。</summary>
    public void AddFile(string sessionId, string path, byte[] content)
    {
        FilesOf(sessionId)[Normalize(path)] = content;
        EnsureParents(sessionId, path);
    }

    /// <summary>预置一个远端目录。</summary>
    public void AddDirectory(string sessionId, string path)
    {
        DirsOf(sessionId).Add(Normalize(path));
        EnsureParents(sessionId, path);
    }

    /// <summary>读取当前文件内容(测试断言用);不存在时返回 <see langword="null" />。</summary>
    public byte[]? GetFile(string sessionId, string path)
        => FilesOf(sessionId).GetValueOrDefault(Normalize(path));

    private Dictionary<string, byte[]> FilesOf(string sessionId)
        => _files.TryGetValue(sessionId, out Dictionary<string, byte[]>? files) ? files : _files[sessionId] = new(StringComparer.Ordinal);

    private HashSet<string> DirsOf(string sessionId)
        => _dirs.TryGetValue(sessionId, out HashSet<string>? dirs) ? dirs : _dirs[sessionId] = new(StringComparer.Ordinal);

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private void EnsureParents(string sessionId, string path)
    {
        string current = Normalize(path);
        HashSet<string> dirs = DirsOf(sessionId);
        while (current.LastIndexOf('/') is > 0 and var slash)
        {
            current = current[..slash];
            dirs.Add(current);
        }
        dirs.Add("/");
    }

    private RemoteFileEntry? StatCore(string sessionId, string path)
    {
        string normalized = Normalize(path);
        string name = normalized == "/" ? "/" : normalized[(normalized.LastIndexOf('/') + 1)..];
        if (FilesOf(sessionId).TryGetValue(normalized, out byte[]? content))
        {
            return new(name, normalized, false, content.Length, DateTimeOffset.UtcNow, "rw-r--r--", "tester", "tester");
        }
        return DirsOf(sessionId).Contains(normalized)
            ? new(name, normalized, true, 0, DateTimeOffset.UtcNow, "rwxr-xr-x", "tester", "tester")
            : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = Normalize(path);
        if (StatCore(sessionId, normalized) is not { IsDirectory: true })
        {
            return Task.FromException<IReadOnlyList<RemoteFileEntry>>(
                new DirectoryNotFoundException($"Remote directory not found: {path}"));
        }
        string prefix = normalized == "/" ? "/" : normalized + "/";
        IEnumerable<string> children = FilesOf(sessionId).Keys.Concat(DirsOf(sessionId))
            .Where(p => p.Length > prefix.Length && p.StartsWith(prefix, StringComparison.Ordinal)
                        && !p[prefix.Length..].Contains('/'));
        IReadOnlyList<RemoteFileEntry> entries = [.. children.Distinct().Order(StringComparer.Ordinal).Select(p => StatCore(sessionId, p)!)];
        return Task.FromResult(entries);
    }

    /// <inheritdoc />
    public Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(StatCore(sessionId, path));

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(StatCore(sessionId, path) is not null);

    /// <inheritdoc />
    public Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult("/home/tester");

    /// <inheritdoc />
    public async Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] content = await ReadAllBytesAsync(sessionId, remotePath, int.MaxValue, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(localPath, content, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(content.Length, content.Length));
    }

    /// <inheritdoc />
    public async Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] content = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        AddFile(sessionId, remotePath, content);
        progress?.Report(new(content.Length, content.Length));
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        byte[] content = await ReadAllBytesAsync(sessionId, remotePath, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(content, writable: false);
    }

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (FilesOf(sessionId).TryGetValue(Normalize(remotePath), out byte[]? content))
        {
            return content.Length > maxBytes
                ? Task.FromException<byte[]>(new InvalidOperationException(
                    $"Remote file '{remotePath}' is {content.Length} bytes (limit {maxBytes})."))
                : Task.FromResult(content);
        }
        return Task.FromException<byte[]>(new FileNotFoundException($"Remote file not found: {remotePath}"));
    }

    /// <inheritdoc />
    public Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        AddFile(sessionId, remotePath, content.ToArray());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        string normalized = Normalize(remotePath);
        string prefix = normalized + "/";
        FilesOf(sessionId).Remove(normalized);
        FilesOf(sessionId).Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList()
            .ForEach(p => FilesOf(sessionId).Remove(p));
        DirsOf(sessionId).RemoveWhere(p => p == normalized || p.StartsWith(prefix, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        if (StatCore(sessionId, remotePath) is not null)
        {
            return Task.FromException(new IOException($"Remote path already exists: {remotePath}"));
        }
        AddDirectory(sessionId, remotePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        AddDirectory(sessionId, remotePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        string from = Normalize(oldPath);
        string to = Normalize(newPath);
        Dictionary<string, byte[]> files = FilesOf(sessionId);
        if (files.Remove(from, out byte[]? content))
        {
            AddFile(sessionId, to, content);
            return Task.CompletedTask;
        }
        if (DirsOf(sessionId).Contains(from))
        {
            string prefix = from + "/";
            foreach (string path in files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                files.Remove(path, out byte[]? moved);
                AddFile(sessionId, to + path[from.Length..], moved!);
            }
            DirsOf(sessionId).RemoveWhere(p => p == from || p.StartsWith(prefix, StringComparison.Ordinal));
            AddDirectory(sessionId, to);
            return Task.CompletedTask;
        }
        return Task.FromException(new FileNotFoundException($"Remote path not found: {oldPath}"));
    }
}

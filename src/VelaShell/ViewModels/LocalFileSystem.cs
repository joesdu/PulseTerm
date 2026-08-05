namespace VelaShell.ViewModels;

/// <summary>一个可选中的本地文件系统根。</summary>
public sealed record LocalRootEntry(
    string DisplayName,
    string FullPath,
    bool IsAccessible,
    string Tooltip
);

/// <summary>本地面板窄删除缝所用的文件系统元数据。</summary>
internal sealed record LocalFileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    bool IsReparsePoint = false
);

/// <summary>用于确定性本地面板遍历与删除测试的最小文件系统接缝。</summary>
internal interface ILocalFileSystem
{
    Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(
        string path,
        CancellationToken cancellationToken
    );

    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>将本地文件或目录移动(重命名)到同一卷内的新路径。</summary>
    Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken);

    /// <summary>在给定路径创建目录(幂等)。</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>返回文件大小(字节);若路径不存在或是一个目录则返回 -1。</summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken);

    /// <summary>以只读方式打开文件。</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>以给定模式(如 Append 用于续传)打开文件写入。</summary>
    Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken);
}

/// <summary>枚举平台本地根,避免面板与 DriveInfo 耦合。</summary>
internal interface ILocalRootProvider
{
    Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken);
}

internal sealed class PhysicalLocalFileSystem : ILocalFileSystem
{
    /// <summary>
    /// 列举选项。
    /// <para>
    /// <c>IgnoreInaccessible</c>:用户主目录里散落着 Templates、"我的文档"这类
    /// Hidden+System+ReparsePoint 的兼容性联接点,它们带拒绝 ACL。无参的
    /// <c>EnumerateFileSystemInfos()</c> 用的是 <c>EnumerationOptions.Compatible</c>
    /// (IgnoreInaccessible = false),碰到任何一个进不去的子项就让整张列表抛
    /// <see cref="UnauthorizedAccessException" /> —— 一个打不开的子项不该连累整个目录。
    /// </para>
    /// <para>
    /// <c>AttributesToSkip = 0</c>:必须显式写,否则默认会跳过 Hidden|System,
    /// 悄悄改掉"隐藏文件照常显示"的既有行为(无参重载是不跳过的)。
    /// </para>
    /// </summary>
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchType = MatchType.Win32,
    };

    public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(
        string path,
        CancellationToken cancellationToken
    ) =>
        Task.Run<IReadOnlyList<LocalFileSystemEntry>>(
            () =>
            {
                var entries = new List<LocalFileSystemEntry>();
                foreach (
                    FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos(
                        "*",
                        ListingOptions
                    )
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        bool reparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                        bool directory = info.Attributes.HasFlag(FileAttributes.Directory);
                        long size = directory || reparse ? 0 : ((FileInfo)info).Length;
                        entries.Add(
                            new(
                                info.Name,
                                info.FullName,
                                directory,
                                size,
                                info.LastWriteTime,
                                reparse
                            )
                        );
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // 条目在列举过程中被删除,或读属性被拒:跳过这一条,不要连累整张列表。
                    }
                }

                // IgnoreInaccessible 也会把"没权限进入这个目录本身"变成"空目录",那是对用户撒谎。
                // 只在空结果时再用严格模式探一次,把拒绝访问如实抛出来(真的空目录则安然通过)。
                if (entries.Count == 0)
                {
                    using IEnumerator<FileSystemInfo> probe = new DirectoryInfo(path)
                        .EnumerateFileSystemInfos()
                        .GetEnumerator();
                    probe.MoveNext();
                }
                return entries;
            },
            cancellationToken
        );

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
            },
            cancellationToken
        );

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(path);
            },
            cancellationToken
        );

    public Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(sourcePath))
                {
                    Directory.Move(sourcePath, destPath);
                }
                else
                {
                    File.Move(sourcePath, destPath);
                }
            },
            cancellationToken
        );

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(path);
            },
            cancellationToken
        );

    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return File.Exists(path) ? new FileInfo(path).Length : -1;
            },
            cancellationToken
        );

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<Stream>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            },
            cancellationToken
        );

    public Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken) =>
        Task.Run<Stream>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileStream(path, mode, FileAccess.Write, FileShare.None);
            },
            cancellationToken
        );
}

internal sealed class PhysicalLocalRootProvider : ILocalRootProvider
{
    public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<LocalRootEntry>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string home = Canonicalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var candidates = new List<(string DisplayName, string FullPath)> { ("~", home) };

                if (OperatingSystem.IsWindows())
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path;
                        try
                        {
                            path = Canonicalize(drive.Name);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException)
                        {
                            continue;
                        }
                        candidates.Add((path, path));
                    }
                }
                else
                {
                    candidates.Add(("/", "/"));
                }

                return [.. candidates
                    .DistinctBy(candidate => candidate.FullPath, PathComparer)
                    .Select(candidate =>
                    {
                        bool accessible = IsAccessible(candidate.FullPath);
                        return new LocalRootEntry(
                            candidate.DisplayName,
                            candidate.FullPath,
                            accessible,
                            candidate.FullPath
                        );
                    })];
            },
            cancellationToken
        );

    private static string Canonicalize(string path) => Path.GetFullPath(path);

    private static bool IsAccessible(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

using System.Security.Cryptography;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>把旧 LocalApplicationData 数据根一次性迁入 ~/.velashell。</summary>
public static class VelaShellDataMigration
{
    internal const string CompletionMarkerFileName = ".localappdata-migration-complete";
    internal const string BackupDirectoryName = ".migration-backup";

    /// <summary>
    /// 在任何持久化服务打开文件前执行迁移。完成标记存在时直接返回，不再访问旧目录。
    /// </summary>
    public static void MigrateIfNeeded(VelaShellStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        MigrateDirectory(paths.LegacyLocalAppDataDirectory, paths.RootDirectory);
    }

    /// <summary>
    /// 将 <paramref name="sourceRoot" /> 的全部普通文件复制并校验到目标目录。目标同名文件先备份，
    /// 全部成功后删除源目录并写完成标记；中途失败不写标记，下次启动可安全重试。
    /// </summary>
    internal static void MigrateDirectory(string sourceRoot, string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        string source = Normalize(sourceRoot);
        string target = Normalize(targetRoot);
        if (PathsEqual(source, target) || IsUnder(source, target) || IsUnder(target, source))
        {
            throw new InvalidOperationException("The legacy and current VelaShell data directories must be separate.");
        }

        string marker = Path.Combine(target, CompletionMarkerFileName);
        if (File.Exists(marker))
        {
            return;
        }

        Directory.CreateDirectory(target);
        if (Directory.Exists(source))
        {
            EnsureNoReparsePoints(source);
            CopyAndVerify(source, target);
            DeleteSourceTree(source);
        }

        File.WriteAllText(marker,
            $"Migrated from {source}{Environment.NewLine}{DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }

    private static void CopyAndVerify(string source, string target)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        foreach (string directory in Directory.EnumerateDirectories(source, "*", options))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        string backupRoot = Path.Combine(target, BackupDirectoryName, "localappdata");
        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", options))
        {
            string relative = Path.GetRelativePath(source, sourceFile);
            string targetFile = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            if (File.Exists(targetFile))
            {
                string backupFile = Path.Combine(backupRoot, relative);
                if (!File.Exists(backupFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(targetFile, backupFile, overwrite: false);
                }
                File.SetAttributes(targetFile, FileAttributes.Normal);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));
            if (!FilesEqual(sourceFile, targetFile))
            {
                throw new IOException($"VelaShell data migration verification failed for '{relative}'.");
            }
        }
    }

    private static void EnsureNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"VelaShell data migration cannot safely move the symbolic link '{entry}'. Remove it and retry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void DeleteSourceTree(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (string directory in Directory.EnumerateDirectories(root, "*", options)
                     .OrderByDescending(path => path.Length))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }
        File.SetAttributes(root, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }
        using FileStream leftStream = File.OpenRead(left);
        using FileStream rightStream = File.OpenRead(right);
        byte[] leftHash = SHA256.HashData(leftStream);
        byte[] rightHash = SHA256.HashData(rightStream);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

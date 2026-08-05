namespace VelaShell.ViewModels;

/// <summary>在构造本地路径前,校验派生自远端列举的名称。</summary>
public static class LocalPathSafety
{
    /// <summary>在当前目录之下解析一个派生自远端的单文件名。</summary>
    public static bool TryResolveDestination(
        string currentDirectory,
        string remoteName,
        out string destination
    )
    {
        destination = string.Empty;
        if (string.IsNullOrWhiteSpace(currentDirectory) || !IsSafeLeafName(remoteName))
        {
            return false;
        }

        try
        {
            string current = Path.GetFullPath(currentDirectory);
            string candidate = Path.GetFullPath(Path.Combine(current, remoteName));
            string prefix = current.EndsWith(Path.DirectorySeparatorChar)
                ? current
                : current + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(prefix, comparison))
            {
                return false;
            }

            if (!HasNoReparsePointBelowRoot(current, candidate))
            {
                return false;
            }

            destination = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasNoReparsePointBelowRoot(string root, string candidate)
    {
        try
        {
            string relative = Path.GetRelativePath(root, candidate);
            string current = root;
            foreach (
                string component in relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
            )
            {
                current = Path.Combine(current, component);

                // 下载目标通常尚不存在:先用不抛异常的 Exists 探测,避免 GetAttributes
                // 为每个新文件刷一次 FileNotFoundException(首个不存在的组件之下不必再查)。
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    return true;
                }
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                catch (FileNotFoundException)
                {
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>返回安全的规范目标路径;名称不安全时返回 null。</summary>
    public static string? ResolveDestination(string currentDirectory, string remoteName) =>
        TryResolveDestination(currentDirectory, remoteName, out string destination)
            ? destination
            : null;

    /// <summary>判断一个派生自远端的值是否为单一安全的叶子名称。</summary>
    public static bool IsSafeLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string deviceName = name.TrimEnd(' ', '.').Split('.')[0].ToUpperInvariant();
        return deviceName is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(deviceName.StartsWith("COM", StringComparison.Ordinal) && IsDeviceNumber(deviceName[3..]))
            && !(deviceName.StartsWith("LPT", StringComparison.Ordinal) && IsDeviceNumber(deviceName[3..]));
    }

    private static bool IsDeviceNumber(string suffix) =>
        suffix.Length == 1 && suffix[0] is >= '1' and <= '9';
}

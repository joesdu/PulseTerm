using System.Globalization;

namespace VelaShell.ViewModels;

/// <summary>SFTP 文档本地面板所展示的单个本地文件系统条目。</summary>
public sealed record LocalFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    bool IsReparsePoint = false,
    bool IsParentEntry = false
)
{
    /// <summary>显示名称;合成父目录行使用 <c>..</c>。</summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    /// <summary>本项是否为真实目录(而非合成父目录行)。</summary>
    public bool IsRegularDirectory => IsDirectory && !IsParentEntry;

    /// <summary>本项是否为真实文件(而非合成父目录行)。</summary>
    public bool IsRegularFile => !IsDirectory && !IsParentEntry;

    /// <summary>供未来本地面板视图使用的图标键。</summary>
    public string Icon => IsDirectory ? "folder" : "file";

    /// <summary>可读大小;目录与合成父目录没有文件大小。</summary>
    public string FormattedSize => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    /// <summary>可读的修改时间;合成父目录没有元数据。</summary>
    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(LastModified);

    /// <summary>创建合成父目录行。</summary>
    public static LocalFileEntry CreateParent(string parentPath) =>
        new("..", parentPath, true, 0, DateTime.MinValue, false, true);

    /// <summary>将字节数格式化为可读字符串(如“1.5 MB”)。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
        return $"{bytes / Math.Pow(1024, unit):F1} {units[unit]}";
    }

    private static string FormatModifiedTime(DateTime dateTime)
    {
        DateTime local = dateTime.Kind == DateTimeKind.Unspecified ? dateTime : dateTime.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}

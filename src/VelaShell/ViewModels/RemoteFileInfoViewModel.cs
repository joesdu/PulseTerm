using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>将 <see cref="RemoteFileInfo"/> 呈现为文件浏览器的一行，并附加显示格式化与父目录项处理。</summary>
/// <param name="model">支撑此行的基础远程文件条目。</param>
public class RemoteFileInfoViewModel(RemoteFileInfo model)
{
    /// <summary>
    /// 扩展名的最长字符数。超出即判定那个点不是扩展名分隔符,"备份.2024年1月报表"
    /// 这类名字才不会在“类型”列里显示成一长串。
    /// </summary>
    private const int MaxExtensionLength = 8;

    /// <summary>底层条目(静默刷新的差异比对用,见 FileBrowserViewModel.RefreshSilentlyAsync)。</summary>
    internal RemoteFileInfo Model { get; } = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>仅对合成的 ".." 行返回 true。</summary>
    public bool IsParentEntry { get; private init; }

    /// <summary>
    /// 真正的目录行(排除合成的 ".." 行)——驱动设计 dyuii 中的琥珀色
    /// 文件夹图标与蓝色名称样式。
    /// </summary>
    public bool IsRegularDirectory => IsDirectory && !IsParentEntry;

    /// <summary>A real file row — gates the file-only context actions (打开/编辑器打开等)。</summary>
    public bool IsRegularFile => !IsDirectory && !IsParentEntry;

    /// <summary>来自远程列表的原始条目名称。</summary>
    public string Name => Model.Name;

    /// <summary>
    /// 列表显示名称:即原始条目名称。琥珀色文件夹图标已标记目录,
    /// 因此不追加尾部斜杠。
    /// </summary>
    public string DisplayName => IsParentEntry ? ".." : Name;

    /// <summary>条目在远程主机上的绝对路径。</summary>
    public string FullPath => Model.FullPath;

    /// <summary>该条目是否为目录。</summary>
    public bool IsDirectory => Model.IsDirectory;

    /// <summary>远程主机报告的权限字符串(例如 rwxr-xr-x)。</summary>
    public string Permissions => Model.Permissions;

    /// <summary>条目的所属用户。</summary>
    public string Owner => Model.Owner;

    /// <summary>条目的所属组。</summary>
    public string Group => Model.Group;

    /// <summary>用于渲染该行的图标键("folder" 或 "file")。</summary>
    public string Icon => Model.IsDirectory ? "folder" : "file";

    /// <summary>
    /// “类型”列文案:目录为“文件夹”,带扩展名的文件为“PHP 文件”,其余为“文件”;
    /// 合成的 ".." 行为空。
    /// </summary>
    public string FileTypeDisplay => IsParentEntry
                                         ? string.Empty
                                         : IsDirectory
                                             ? Strings.Folder
                                             : DescribeFileType(Name);

    // 目录也显示其报告的尺寸(设计 dyuii 中文件夹列出 "4.0 KB")。
    /// <summary>用于显示的可读大小;合成的 ".." 行为空。</summary>
    public string FormattedSize => IsParentEntry ? string.Empty : FormatSize(Model.Size);

    /// <summary>用于显示的可读最后修改时间戳;合成的 ".." 行为空。</summary>
    public string FormattedModifiedTime => IsParentEntry ? string.Empty : FormatModifiedTime(Model.LastModified);

    /// <summary>条目大小(字节)。</summary>
    public long SizeBytes => Model.Size;

    /// <summary>条目的最后修改时间。</summary>
    public DateTime LastModified => Model.LastModified;

    /// <summary>
    /// 合成的 ".." 首行(§6):激活时跳转到父目录,
    /// 不提供任何文件操作。
    /// </summary>
    public static RemoteFileInfoViewModel CreateParentEntry(string parentPath) =>
        new(new()
        {
            Name = "..",
            FullPath = parentPath,
            Size = 0,
            Permissions = string.Empty,
            IsDirectory = true,
            LastModified = DateTime.MinValue,
            Owner = string.Empty,
            Group = string.Empty
        })
        { IsParentEntry = true };

    /// <summary>将字节数格式化为可读的大小字符串(例如 "4.0 KB")。</summary>
    /// <param name="bytes">以字节为单位的大小。</param>
    /// <returns>带有合适单位后缀的格式化大小字符串。</returns>
    public static string FormatSize(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    /// <summary>
    /// 由扩展名生成类型文案。点开头的文件(.bashrc)按 Unix 惯例是“隐藏文件”而非
    /// “bashrc 类型”,与无扩展名的一样归为“文件”;扩展名还要求短且全为字母数字,
    /// 否则 "backup.tar 副本" 里的点会被当成分隔符。
    /// </summary>
    private static string DescribeFileType(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return Strings.File;
        }
        string extension = name[(dot + 1)..];
        return extension.Length > MaxExtensionLength || !extension.All(char.IsLetterOrDigit)
                   ? Strings.File
                   : Strings.Format("Sftp_FileTypeExt", extension.ToUpperInvariant());
    }

    /// <summary>
    /// 统一的时间戳格式 "yyyy-MM-dd HH:mm:ss"(与本地面板 <see cref="LocalFileEntry"/> 一致)。
    /// Unspecified 视为已是本地时间,其余转本地时区显示。
    /// </summary>
    private static string FormatModifiedTime(DateTime dateTime)
    {
        DateTime local = dateTime.Kind == DateTimeKind.Unspecified
                             ? dateTime
                             : dateTime.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}

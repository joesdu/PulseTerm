using System.Runtime.InteropServices;

namespace VelaShell.Core.Models;

/// <summary>
/// 系统标准目录的平台实现(目前只有"下载")。
/// </summary>
/// <remarks>
/// .NET 的 <see cref="Environment.SpecialFolder" /> 没有"下载"这一项,历史上只能拼
/// <c>~/Downloads</c> —— 而这个目录在 Windows 上是可以被用户改到任意位置的
/// (资源管理器 → 下载 → 属性 → 位置),改过之后 <c>%USERPROFILE%\Downloads</c> 要么不存在,
/// 要么是个没人往里放东西的空壳(#257)。Linux 同理:XDG 用户目录允许把下载目录
/// 指到 <c>~/下载</c> 之类的本地化名字上。因此这里按平台问系统要真实位置:
/// <list type="bullet">
///   <item>Windows:<c>SHGetKnownFolderPath(FOLDERID_Downloads)</c>,重定向后返回的就是新位置;</item>
///   <item>Linux:<c>XDG_DOWNLOAD_DIR</c> 环境变量,没有则读 <c>user-dirs.dirs</c>;</item>
///   <item>macOS:系统不支持重定向,由调用方回落到 <c>~/Downloads</c>。</item>
/// </list>
/// 取不到一律返回 null,由 <see cref="UserPathResolver.Downloads" /> 回落到 <c>~/Downloads</c>。
/// </remarks>
internal static partial class SystemFolders
{
    /// <summary>FOLDERID_Downloads。</summary>
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>KF_FLAG_DONT_VERIFY:目录暂时不可达(如重定向到未连接的移动盘)时也返回路径,存在性由调用方判。</summary>
    private const uint KfFlagDontVerify = 0x00004000;

    /// <summary>系统"下载"目录;取不到返回 null。</summary>
    /// <remarks>不做缓存:用户在运行期改了下载目录位置,下一次用到时就该生效。</remarks>
    internal static string? Downloads()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsDownloads();
            }
            return OperatingSystem.IsLinux() ? LinuxDownloads() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // 问不到就当没有:下载目录不值得让调用方炸掉。
            return null;
        }
    }

    private static string? WindowsDownloads()
    {
        nint buffer = 0;
        try
        {
            return SHGetKnownFolderPath(in FolderIdDownloads, KfFlagDontVerify, 0, out buffer) == 0 && buffer != 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            if (buffer != 0)
            {
                // 无论成败都要还:SHGetKnownFolderPath 用 CoTaskMemAlloc 分配返回串。
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    /// <summary>
    /// XDG 用户目录:先看环境变量(桌面会话通常不导出,但用户可以自己导),
    /// 再读 <c>$XDG_CONFIG_HOME/user-dirs.dirs</c>(默认 <c>~/.config/user-dirs.dirs</c>)。
    /// </summary>
    private static string? LinuxDownloads()
    {
        if (Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR") is { Length: > 0 } fromEnv)
        {
            return ExpandXdgValue(fromEnv);
        }

        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdgConfig
            ? xdgConfig
            : Path.Combine(UserPathResolver.Home, ".config");
        string file = Path.Combine(configHome, "user-dirs.dirs");
        if (!File.Exists(file))
        {
            return null;
        }

        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith('#') || !line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
            {
                continue;
            }
            string value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
            return value.Length == 0 ? null : ExpandXdgValue(value);
        }
        return null;
    }

    /// <summary>展开 user-dirs.dirs 里的 <c>$HOME</c> / <c>${HOME}</c> 前缀(该文件只用这一个变量)。</summary>
    private static string ExpandXdgValue(string value)
    {
        ReadOnlySpan<string> prefixes = ["$HOME/", "${HOME}/"];
        foreach (string prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Path.Combine(UserPathResolver.Home, value[prefix.Length..]);
            }
        }
        return value is "$HOME" or "${HOME}" ? UserPathResolver.Home : value;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}

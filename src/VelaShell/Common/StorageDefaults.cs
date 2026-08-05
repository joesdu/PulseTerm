using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Common;

/// <summary>
/// 文件/文件夹对话框的默认起始目录。
/// </summary>
/// <remarks>
/// 不指定 <c>SuggestedStartLocation</c> 时,Windows 通用对话框的回落顺序是
/// per-app MRU → 进程工作目录 → 系统默认库。进程工作目录由外部环境决定:开机自启
/// (<c>HKCU\...\Run</c>)时 Explorer 会把它设成 <c>C:\Windows\System32</c>,归一之后是应用
/// 安装目录(商店版的 WindowsApps 还是只读的)—— 两者都不是用户想要的落点(#120)。
/// 因此每个对话框都显式给出起点,统一由本类提供。
/// 取不到目录时一律返回 null(等同于不指定,交回系统默认),绝不因此让对话框打不开。
/// </remarks>
internal static class StorageDefaults
{
    /// <summary>把一个绝对路径转成对话框起始位置;路径为空/不存在/不可访问时返回 null。</summary>
    public static async Task<IStorageFolder?> FolderAsync(TopLevel? top, string? path)
    {
        if (top is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Directory.Exists(path)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用户主目录。</summary>
    public static Task<IStorageFolder?> HomeAsync(TopLevel? top) =>
        FolderAsync(top, UserPathResolver.Home);

    /// <summary>设置 → 文件传输里配置的下载目录;未配置或不存在时退回用户主目录。</summary>
    public static async Task<IStorageFolder?> DownloadsAsync(TopLevel? top) =>
        await FolderAsync(top, UserPathResolver.ResolveOrHome(await ConfiguredDownloadDirectoryAsync()))
        ?? await HomeAsync(top);

    /// <summary>密钥目录 <c>~/.ssh</c>;不存在时退回用户主目录。</summary>
    public static async Task<IStorageFolder?> SshAsync(TopLevel? top) =>
        await FolderAsync(top, Path.Combine(UserPathResolver.Home, ".ssh")) ?? await HomeAsync(top);

    /// <summary>系统图片库;不存在时退回用户主目录。</summary>
    public static async Task<IStorageFolder?> PicturesAsync(TopLevel? top) =>
        await FolderAsync(top, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
        ?? await HomeAsync(top);

    /// <summary>从设置服务读下载目录的原始配置值;读不出来返回 null(调用方回退到主目录)。</summary>
    private static async Task<string?> ConfiguredDownloadDirectoryAsync()
    {
        try
        {
            if (Application.Current is App { Services: { } services }
                && services.GetService<ISettingsService>() is { } settingsService)
            {
                AppSettings settings = await settingsService.GetSettingsAsync();
                return settings.Transfer.LocalDownloadDirectory;
            }
        }
        catch
        {
            // 设置读不出来就退回主目录:起始目录不值得让对话框弹不出来。
        }
        return null;
    }
}

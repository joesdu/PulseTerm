using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using VelaShell.Infrastructure.Persistence;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Hosting;

namespace VelaShell.Services;

/// <summary>
/// 宿主自我登记:每次启动把"我装在哪、什么版本、带的是哪一版 SDK 与 Avalonia"
/// 写进 <c>~/.velashell/host.json</c>,供 <c>vela-plugin</c> 生成 IDE 启动配置、
/// 核对版本兼容性(见 <see cref="HostRegistry" />)。
/// <para>
/// 换句话说:插件工具链不需要去猜安装路径。三个平台三套安装位置、便携版、自更新换过位置 ——
/// 探测逻辑既长又常年失准,而宿主自己报一次只要一次文件写入,报的还一定是真的。
/// </para>
/// </summary>
internal static class HostRegistrationService
{
    /// <summary>
    /// 登记本次运行的安装信息。失败只记一行日志:这个文件是工具链的捷径,不是功能依赖。
    /// </summary>
    /// <param name="paths">本次运行使用的存储路径。</param>
    public static void Register(VelaShellStoragePaths paths)
    {
        // 用了 --data-root 的实例不登记:那是插件开发者的调试实例,数据根是临时的,
        // 让它把自己写进注册表会把工具链指到一个随时会被删掉的配置上去。
        if (VelaShellStoragePaths.RootDirectoryOverride is not null)
        {
            return;
        }
        try
        {
            if (BuildEntry(paths) is { } entry)
            {
                HostRegistry.Upsert(entry, paths.HostRegistryFile);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Host registration failed: {ex.Message}");
        }
    }

    /// <summary>组装本次运行的登记条目;拿不到自身可执行文件路径时返回 <see langword="null" />。</summary>
    internal static HostRegistryEntry? BuildEntry(VelaShellStoragePaths paths)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return null; // 极少见的宿主形态:没什么可登记的。
        }
        string pluginHost = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "VelaShell.PluginHost.exe" : "VelaShell.PluginHost");
        return new()
        {
            ExePath = exePath,
            PluginHostPath = File.Exists(pluginHost) ? pluginHost : null,
            Version = InformationalVersion(typeof(HostRegistrationService).Assembly) ?? "0.0.0",
            ApiLevel = VelaPluginApi.Level,
            SdkVersion = VelaPluginApi.SdkVersion,
            AvaloniaVersion = InformationalVersion(typeof(Avalonia.Application).Assembly),
            DataRoot = paths.RootDirectory,
            UserPluginRoot = paths.UserPluginDirectory,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            LastSeen = DateTimeOffset.UtcNow
        };
    }

    /// <summary>取程序集的信息版本,去掉 <c>+sha</c> 之类的构建元数据后缀。</summary>
    private static string? InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? assembly.GetName().Version?.ToString();
}

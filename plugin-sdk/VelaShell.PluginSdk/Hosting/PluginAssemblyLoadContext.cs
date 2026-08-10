using System.Reflection;
using System.Runtime.Loader;

namespace VelaShell.PluginSdk.Hosting;

/// <summary>
/// 每插件一个的可收集 AssemblyLoadContext:插件自带依赖按其 <c>deps.json</c> 在
/// 插件目录内解析,与装载方依赖版本彻底解耦;SDK 契约程序集与共享框架程序集
/// 回落到默认 ALC,保证跨边界类型同一性。进程内宿主与 VelaShell.PluginHost 共用。
/// </summary>
public sealed class PluginAssemblyLoadContext(string pluginId, string entryAssemblyPath)
    : AssemblyLoadContext($"plugin:{pluginId}", isCollectible: true)
{
    /// <summary>必须与装载方共享的契约程序集:若插件目录误带了一份,也绝不加载副本。</summary>
    private static readonly string[] SharedAssemblies = ["VelaShell.PluginSdk"];

    /// <summary>
    /// 按前缀共享的框架程序集:进程内插件 UI 与宿主同用一套 Avalonia
    /// (跨 ALC 的 Control 类型必须同一,否则原生面板内容无法挂进宿主可视树)。
    /// PluginHost 进程无 Avalonia,该前缀在其中自然无命中。
    /// </summary>
    private static readonly string[] SharedPrefixes = ["Avalonia"];

    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name
            && (SharedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase)
                || SharedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            return null; // 回落默认 ALC:契约/框架类型必须同一,否则跨边界的转换全部失败。
        }
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

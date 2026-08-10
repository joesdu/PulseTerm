using System.Reflection;

namespace VelaShell.PluginSdk.Hosting;

/// <summary>插件入口类型定位(进程内宿主与 PluginHost 共用同一套规则)。</summary>
public static class PluginEntryLocator
{
    /// <summary>
    /// 在入口程序集中定位插件入口:优先取恰好一个带 <see cref="VelaPluginAttribute" />
    /// 的实现;没有带特性的则允许恰好一个裸实现;零个或多个都拒绝。
    /// </summary>
    /// <exception cref="InvalidOperationException">找不到或找到多个入口类型。</exception>
    public static Type FindEntryType(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }
        List<Type> implementors = [.. types.Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IVelaPlugin).IsAssignableFrom(t))];
        List<Type> attributed = [.. implementors.Where(t => t.GetCustomAttribute<VelaPluginAttribute>() is not null)];
        List<Type> candidates = attributed.Count > 0 ? attributed : implementors;
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("No public [VelaPlugin] class implementing IVelaPlugin found in entry assembly."),
            _ => throw new InvalidOperationException($"Multiple plugin entry types found: {string.Join(", ", candidates.Select(t => t.FullName))}.")
        };
    }
}

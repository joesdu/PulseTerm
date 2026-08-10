using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 采集宿主的主题令牌快照(<c>Vela*</c> 资源键,按当前明暗变体解析)供隔离插件进程使用:
/// PluginHost 把这些值注入其 Application 资源,插件的 <c>{DynamicResource VelaXxx}</c>
/// 在隔离模式下与进程内一样生效。进程内插件不需要本机制 —— 控件在宿主可视树里,
/// 令牌天然可查。
/// </summary>
internal static class PluginThemeTokens
{
    /// <summary>在 UI 线程枚举并解析全部 Vela* 令牌(画刷/颜色/数值/字体)。</summary>
    public static Task<IReadOnlyList<ThemeTokenDto>> CollectAsync() =>
        Dispatcher.UIThread.InvokeAsync<IReadOnlyList<ThemeTokenDto>>(CollectOnUiThread).GetTask();

    private static IReadOnlyList<ThemeTokenDto> CollectOnUiThread()
    {
        if (Application.Current is not { } app)
        {
            return [];
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        CollectKeys(app.Resources, keys);
        var tokens = new List<ThemeTokenDto>(keys.Count);
        foreach (string key in keys.Order(StringComparer.Ordinal))
        {
            if (app.TryGetResource(key, app.ActualThemeVariant, out object? value)
                && Map(key, value) is { } token)
            {
                tokens.Add(token);
            }
        }
        return tokens;
    }

    /// <summary>递归收集资源树(含 MergedDictionaries / ThemeDictionaries / ResourceInclude)里的 Vela* 键。</summary>
    private static void CollectKeys(IResourceProvider provider, HashSet<string> keys)
    {
        switch (provider)
        {
            case ResourceDictionary dictionary:
                foreach (object key in dictionary.Keys)
                {
                    if (key is string name && name.StartsWith("Vela", StringComparison.Ordinal))
                    {
                        keys.Add(name);
                    }
                }
                foreach (IResourceProvider merged in dictionary.MergedDictionaries)
                {
                    CollectKeys(merged, keys);
                }
                foreach (KeyValuePair<Avalonia.Styling.ThemeVariant, Avalonia.Controls.IThemeVariantProvider> theme in dictionary.ThemeDictionaries)
                {
                    CollectKeys(theme.Value, keys);
                }
                break;
            case ResourceInclude include when include.Loaded is { } loaded:
                CollectKeys(loaded, keys);
                break;
        }
    }

    private static ThemeTokenDto? Map(string key, object? value) => value switch
    {
        ISolidColorBrush brush => new(key, "brush", brush.Color.ToString()),
        Color color => new(key, "color", color.ToString()),
        double number => new(key, "double", number.ToString(CultureInfo.InvariantCulture)),
        FontFamily font when PortableFontFallback(font) is { Length: > 0 } fallback => new(key, "font", fallback),
        _ => null // 渐变画刷/几何/控件模板等不外发:插件按语义用纯色令牌足够
    };

    /// <summary>
    /// 去掉字体族里 <c>fonts:...#</c> 的内嵌集合段(插件进程未注册宿主的内嵌字体,
    /// 解析不了),只保留可移植的系统回退链。
    /// </summary>
    private static string PortableFontFallback(FontFamily font) =>
        string.Join(", ",
            font.FamilyNames.Select(name => name.Trim())
                .Where(name => name.Length > 0 && !name.Contains('#') && !name.Contains(":")));
}

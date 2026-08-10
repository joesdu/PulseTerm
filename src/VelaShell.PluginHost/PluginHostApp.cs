using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace VelaShell.PluginHost;

/// <summary>
/// 插件进程的 Avalonia 应用:提供 Fluent 基础主题让标准控件有模板可用,
/// 主题明暗跟随宿主(握手快照 + themeChanged 事件)。插件在此之上完全自由 ——
/// 自带样式、资源、国际化与第三方组件包都行。
/// </summary>
internal sealed class PluginHostApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    /// <summary>把宿主主题名映射到明暗变体并应用(任意线程可调)。</summary>
    public static void ApplyHostTheme(string theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Current is null)
            {
                return;
            }
            Current.RequestedThemeVariant = theme.Contains("light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : theme.Contains("dark", StringComparison.OrdinalIgnoreCase)
                    ? ThemeVariant.Dark
                    : ThemeVariant.Default;
        });
    }
}

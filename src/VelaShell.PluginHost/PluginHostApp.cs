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

    /// <summary>
    /// 把宿主的明暗基底应用到本进程(任意线程可调)。
    /// <para>
    /// 参数是**已解析**的布尔而不是主题名。老实现按主题名里有没有 "light"/"dark" 猜,
    /// 那在只有明暗两套时碰巧成立;宿主长出具名主题之后就错了 —— "tokyo-night"、"nord"、
    /// "sakura" 里一个关键字都没有,会被判成"跟随系统"从而去跟**插件进程自己**的系统设置,
    /// 而 "one-light" 又会碰巧蒙对。解析归宿主,这里只负责贴。
    /// </para>
    /// </summary>
    public static void ApplyHostTheme(bool isDark)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Current?.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        });
    }
}

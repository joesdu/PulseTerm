using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using VelaShell.Plugin.Redis.Tests;

[assembly: AvaloniaTestApplication(typeof(RedisPanelHeadlessApp))]

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 面板 UI 测试共用的 headless 宿主。**刻意只装 Fluent**:面板在真实宿主里靠
/// <c>{DynamicResource Vela*}</c> 取色,这里一个 Vela 令牌都不给 ——
/// 于是这套测试同时守住"宿主令牌缺席时面板照样能装载"(与 AI 插件同一条口径:
/// 未命中的令牌让属性保持默认值,不该抛)。
/// </summary>
public class RedisPanelHeadlessApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    /// <summary>headless 宿主的构建入口(由 Avalonia 的测试基建反射调用)。</summary>
    /// <returns>应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<RedisPanelHeadlessApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

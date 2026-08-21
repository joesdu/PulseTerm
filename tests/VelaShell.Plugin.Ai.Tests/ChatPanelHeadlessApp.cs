using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using VelaShell.Plugin.Ai.Tests;

[assembly: AvaloniaTestApplication(typeof(ChatPanelHeadlessApp))]

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 插件 UI 测试共用的 headless 宿主。**刻意只装 Fluent**:插件在真实宿主里靠
/// <c>{DynamicResource Vela*}</c> 取色,这里一个 Vela 令牌都不给 ——
/// 于是这套测试同时守住"宿主令牌缺席时面板照样能装载"(隔离进程首帧、
/// 主题令牌还没下发到位时就是这个状态)。
/// </summary>
public class ChatPanelHeadlessApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ChatPanelHeadlessApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

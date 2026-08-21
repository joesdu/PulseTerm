using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using VelaShell.Plugin.Sql.Tests;

[assembly: AvaloniaTestApplication(typeof(SqlPanelHeadlessApp))]

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 面板 UI 测试共用的 headless 宿主。
/// <para>
/// <b>刻意只装 Fluent + 两个控件库的主题,一个 <c>Vela*</c> 令牌都不给</b> ——
/// 于是这套测试同时守住"宿主令牌缺席时面板照样能装载"(与 Redis / AI 插件同一条口径:
/// 未命中的令牌让属性保持默认值,不该抛)。
/// </para>
/// <para>
/// AvaloniaEdit 与 DataGrid 的主题必须显式装:它们的控件模板随各自的主题提供。
/// 这两行同时也是**真宿主要抄的那两行**(见 <c>src/VelaShell/App.axaml</c>) ——
/// 少了它们控件连可视树都没有,而那种"什么都没画"很容易被误读成"虚拟化得真好"。
/// </para>
/// </summary>
public class SqlPanelHeadlessApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Sql.Tests/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Sql.Tests/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    /// <summary>headless 宿主的构建入口(由 Avalonia 的测试基建反射调用)。</summary>
    /// <returns>应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SqlPanelHeadlessApp>()
                  // 真渲染器:假绘制下 CaptureRenderedFrame 返回 null,截图验界面就无从谈起。
                  .UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

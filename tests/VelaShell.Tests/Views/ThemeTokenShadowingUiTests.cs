using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Views;

/// <summary>
/// 具名主题赖以成立的那条机制:写进 <c>Application.Resources</c> 顶层的令牌,
/// 必须盖得住 <c>VelaTokens.axaml</c> / <c>VelaShellTokens.axaml</c> 的 ThemeDictionaries
/// 与 App.axaml 挂上去的 DarkTheme/LightTheme。
/// <para>
/// 盖不住的话,九套主题里有六套(暗色那几套共用 Dark 变体)会全部退化成 VelaDark ——
/// 而且不会有任何报错,只是颜色不对。这条用例就是那道保险。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public sealed class ThemeTokenShadowingUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ThemeTokenShadowingUiTests).Assembly);

    [TestMethod]
    public async Task AppliedTokens_ShadowTheCompiledThemeDictionaries() =>
        await _session.Dispatch(() =>
        {
            Application app = Application.Current!;
            // 贴之前:解析到的是 axaml 里 Dark 变体的值(headless 宿主与 App.axaml 同一套资源栈)。
            Assert.AreEqual(
                Color.Parse(UiThemeCatalog.Get("dark").Palette.BgPage),
                Resolve(app, "VelaBgPage"),
                "起点不是 VelaDark 的话,后面的断言就说明不了问题。");

            try
            {
                foreach (string themeId in new[] { "tokyo-night", "everforest", "github-light" })
                {
                    UiTheme theme = UiThemeCatalog.Get(themeId);
                    ThemeTokenApplier.Apply(app.Resources, theme);

                    // 三类令牌各取一个:平面底色、文字、强调色派生的半透明底。
                    Assert.AreEqual(Color.Parse(theme.Palette.BgPage), Resolve(app, "VelaBgPage"),
                        $"{theme.Name}:平面底色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.TextPrimary), Resolve(app, "VelaTextPrimary"),
                        $"{theme.Name}:正文色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.Accent), Resolve(app, "VelaAccent"),
                        $"{theme.Name}:强调色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.Error), Resolve(app, "VelaError"),
                        $"{theme.Name}:语义色(App.axaml 的 ThemeDictionaries 那一层)没盖住。");
                }
            }
            finally
            {
                // 会话是全程序集共用的:改完必须还原,否则后面的 UI 用例对着别人的颜色跑。
                foreach (string key in ThemeTokenApplier.TokenKeys)
                {
                    app.Resources.Remove(key);
                }
                app.Resources.Remove("VelaShadowWindow");
            }

            Assert.AreEqual(
                Color.Parse(UiThemeCatalog.Get("dark").Palette.BgPage),
                Resolve(app, "VelaBgPage"),
                "移除顶层令牌后必须落回 axaml 的缺省 —— 说明刚才确实是遮蔽,不是把缺省改掉了。");
            return Task.FromResult(true);
        }, CancellationToken.None);

    private static Color Resolve(Application app, string key)
    {
        Assert.IsTrue(app.TryGetResource(key, app.ActualThemeVariant, out object? value), $"令牌 {key} 解析不到。");
        Assert.IsInstanceOfType<ISolidColorBrush>(value, $"令牌 {key} 不是画刷。");
        return ((ISolidColorBrush)value!).Color;
    }
}

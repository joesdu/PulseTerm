using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Views;

/// <summary>
/// 具名主题赖以成立的那条机制:整格换进 <c>Application.Resources.ThemeDictionaries</c> 的令牌,
/// 必须盖得住 <c>VelaTokens.axaml</c> / <c>VelaShellTokens.axaml</c>(它们是合并字典),
/// 以及 App.axaml 原本挂在同一格上的 DarkTheme / LightTheme。
/// <para>
/// 盖不住的话,共用 Dark 变体的那七套暗色会全部退化成 VelaDark —— 而且不会有任何报错,
/// 只是颜色不对。这条用例就是那道保险。
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

            // 会话是全程序集共用的:先记下原样,末尾还原,否则后面的 UI 用例对着别人的颜色跑。
            ThemeVariant requested = app.RequestedThemeVariant ?? ThemeVariant.Default;
            app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out IThemeVariantProvider? savedDark);
            app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out IThemeVariantProvider? savedLight);
            try
            {
                foreach (string themeId in new[] { "tokyo-night", "everforest", "github-light" })
                {
                    UiTheme theme = UiThemeCatalog.Get(themeId);
                    app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
                    ThemeTokenApplier.Apply(app, theme);

                    // 四类令牌各取一个,分别来自四份不同的 axaml —— 都得被这一格盖住。
                    Assert.AreEqual(Color.Parse(theme.Palette.BgPage), Resolve(app, "VelaBgPage"),
                        $"{theme.Name}:平面底色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.TextPrimary), Resolve(app, "VelaTextPrimary"),
                        $"{theme.Name}:正文色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.Accent), Resolve(app, "VelaAccent"),
                        $"{theme.Name}:强调色没盖住 axaml 的缺省。");
                    Assert.AreEqual(Color.Parse(theme.Palette.Error), Resolve(app, "VelaError"),
                        $"{theme.Name}:语义色(App.axaml 的 ThemeDictionaries 那一层)没盖住。");
                    // 合并字典里与主题无关的资源不能被这一格挤掉。
                    Assert.IsTrue(app.TryGetResource("VelaUiFont", app.ActualThemeVariant, out _),
                        $"{theme.Name}:换掉主题字典后,合并字典里的资源解析不到了。");
                }
            }
            finally
            {
                Restore(app, ThemeVariant.Dark, savedDark);
                Restore(app, ThemeVariant.Light, savedLight);
                app.RequestedThemeVariant = requested;
            }

            Assert.AreEqual(
                Color.Parse(UiThemeCatalog.Get("dark").Palette.BgPage),
                Resolve(app, "VelaBgPage"),
                "还原那一格后必须落回 axaml 的缺省 —— 说明刚才确实是遮蔽,不是把缺省改掉了。");
            return Task.FromResult(true);
        }, CancellationToken.None);

    /// <summary>
    /// 贴一套主题惊动可视树的次数必须与令牌数无关。
    /// <para>
    /// 资源字典每被写一次就沿树发一遍变更通知,树上每一处 <c>DynamicResource</c> 都要重新解析;
    /// 六十多个令牌逐个写下去,一次切主题在 400 个绑定的合成树上实测 40~57 ms(真实窗口只多不少),
    /// 手上就是"切一下顿一下"。整格替换实测 1.65 ms。
    /// </para>
    /// <para>这条用例钉的是**次数**而不是耗时 —— 耗时断言在 CI 上必然抖。</para>
    /// </summary>
    [TestMethod]
    public async Task ApplyingATheme_NotifiesTheTreeAConstantNumberOfTimes() =>
        await _session.Dispatch(() =>
        {
            Application app = Application.Current!;
            app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out IThemeVariantProvider? savedDark);
            int notifications = 0;
            void Count(object? sender, ResourcesChangedEventArgs e) => notifications++;

            ((IResourceHost)app).ResourcesChanged += Count;
            try
            {
                ThemeTokenApplier.Apply(app, UiThemeCatalog.Get("tokyo-night"));
            }
            finally
            {
                ((IResourceHost)app).ResourcesChanged -= Count;
                Restore(app, ThemeVariant.Dark, savedDark);
            }

            // 换一格实测发 3 次(摘旧的、挂新的、认领 Owner 各一次)。钉的是"与令牌数无关"这件事:
            // 逐个写会随令牌数线性上去,眼下是六十多次。
            Assert.IsTrue(notifications <= 4,
                $"贴一套主题发了 {notifications} 次资源变更通知 —— 多半是又改回了逐个令牌写 "
                + "Application.Resources。每一次通知都要把整棵树上的 DynamicResource 重解析一遍。");
            return Task.FromResult(true);
        }, CancellationToken.None);

    private static void Restore(Application app, ThemeVariant variant, IThemeVariantProvider? saved)
    {
        if (saved is null)
        {
            app.Resources.ThemeDictionaries.Remove(variant);
            return;
        }
        app.Resources.ThemeDictionaries[variant] = saved;
    }

    private static Color Resolve(Application app, string key)
    {
        Assert.IsTrue(app.TryGetResource(key, app.ActualThemeVariant, out object? value), $"令牌 {key} 解析不到。");
        Assert.IsInstanceOfType<ISolidColorBrush>(value, $"令牌 {key} 不是画刷。");
        return ((ISolidColorBrush)value!).Color;
    }
}

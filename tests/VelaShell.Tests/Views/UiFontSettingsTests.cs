using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置 → 外观 → 界面字体/字号是否真的落到界面上。
/// </summary>
/// <remarks>
/// 这两个选项曾经"设了没反应":令牌接线本身是通的,但界面 axaml 把字号写死了 ~350 处、
/// 把字体钉死在等宽令牌上 ~150 处,用户改设置只影响到极少数文本,观感上等同没生效。
/// 所以这里【不】只断言令牌值 —— 只断言令牌等于自证,写死字号的老毛病照样能全绿溜回来。
/// 关键的几条都是拿真实视图里的真实 TextBlock 去量的。
/// </remarks>
[TestClass]
[TestCategory("UiFontSettings")]
public class UiFontSettingsTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UiFontSettingsTests).Assembly);

    /// <summary>设计基准字号:令牌名里的数字就是这个基准下的磅值。</summary>
    private const double BaseUiFontSize = 13;

    [TestCleanup]
    public void RestoreDefaults() => OnUi(() => ApplyAppearance(new AppearanceOptions()));

    [TestMethod]
    public void DefaultUiFontSize_LeavesLadderAtDesignValues()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions());

            // 默认基准 13 时整套阶梯必须逐值等于设计值,否则这次改动就是一次全局改版。
            foreach (double design in (double[])[8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20])
            {
                Assert.AreEqual(design, Token($"VelaFontSize{design:0}"), $"VelaFontSize{design:0}");
            }
            Assert.AreEqual(13d, Token("VelaUiFontSize"));
        });
    }

    [TestMethod]
    public void LargerUiFontSize_ScalesWholeLadderProportionally()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions { UiFontSize = 20 });

            Assert.AreEqual(20d, Token("VelaUiFontSize"));
            // 20/13 ≈ 1.54:每一级都跟着放大,且保持严格递增(层级不走形)。
            Assert.AreEqual(Math.Round(10 * 20 / BaseUiFontSize), Token("VelaFontSize10"));
            Assert.AreEqual(Math.Round(11 * 20 / BaseUiFontSize), Token("VelaFontSize11"));
            Assert.IsGreaterThan(Token("VelaFontSize10"), Token("VelaFontSize11"));
            Assert.IsGreaterThan(Token("VelaFontSize12"), Token("VelaFontSize13"));
            // Fluent 内置控件(按钮/输入框/下拉)也得一起缩放,否则控件里的字与周围字对不上。
            Assert.AreEqual(20d, Token("ControlContentThemeFontSize"));
        });
    }

    [TestMethod]
    public void SmallestUiFontSize_KeepsEverySizeLegible()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions { UiFontSize = 9 });

            foreach (double design in (double[])[8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20])
            {
                Assert.IsGreaterThanOrEqualTo(6d, Token($"VelaFontSize{design:0}"), $"VelaFontSize{design:0}");
            }
        });
    }

    /// <summary>说明文字相对基准字号的比例(与 MainWindow.DescFontSizeRatio 同源)。</summary>
    private const double DescRatio = 0.85;

    [TestMethod]
    public void DescriptionSize_StaysAtFixedShareOfBase()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions());
            Assert.AreEqual(Math.Round(DescRatio * 13), Token("VelaFontSizeDesc"));

            ApplyAppearance(new AppearanceOptions { UiFontSize = 20 });
            Assert.AreEqual(Math.Round(DescRatio * 20), Token("VelaFontSizeDesc"));
        });
    }

    [TestMethod]
    public void UiFont_OverridesBothProportionalAndMonospaceInterfaceText()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions { UiFont = "Comic Sans MS" });

            // 界面上的文字要么走比例令牌要么走等宽令牌 —— 只换其中一个,用户看到的就是
            // "改了字体但大部分界面没变",正是这次要修的症状。
            foreach (string key in (string[])["VelaUiFont", "VelaUiMonoFont"])
            {
                Assert.IsTrue(
                    Application.Current!.TryGetResource(key, null, out object? value),
                    $"{key} 未定义"
                );
                Assert.Contains("Comic Sans MS", value!.ToString(), $"{key} 没跟随界面字体");
            }
        });
    }

    [TestMethod]
    public void DefaultUiFont_RestoresProportionalUiAndMonospaceChrome()
    {
        OnUi(() =>
        {
            ApplyAppearance(new AppearanceOptions { UiFont = "Comic Sans MS" });
            ApplyAppearance(new AppearanceOptions { UiFont = "  " });

            // 还原后:普通界面文字回到 Inter,按列对齐的界面文字回到等宽 —— 两者默认值不同,
            // 一起覆盖时也必须一起还原,不能把等宽区域留在 Inter 上。
            Assert.Contains("Inter", Font("VelaUiFont"));
            Assert.Contains("Cascadia Mono", Font("VelaUiMonoFont"));
        });
    }

    /// <summary>
    /// 真实设置窗口里的真实文本:标签跟着界面字号缩放,说明文字跟着走固定比例。
    /// 这条是本组测试的重心 —— 令牌对了但 axaml 写死字号的话,只有它会红。
    /// </summary>
    [TestMethod]
    public void SettingsWindowText_FollowsUiFontSize()
    {
        OnUi(() =>
        {
            var window = new SettingsView { DataContext = NewSettingsViewModel() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ApplyAppearance(new AppearanceOptions());
                Dispatcher.UIThread.RunJobs();
                double labelAtDefault = FirstTextSize(window, "row-label");
                double descAtDefault = FirstTextSize(window, "row-desc");

                ApplyAppearance(new AppearanceOptions { UiFontSize = 20 });
                Dispatcher.UIThread.RunJobs();

                Assert.IsGreaterThan(labelAtDefault, FirstTextSize(window, "row-label"), "设置项标签没跟随界面字号");
                Assert.IsGreaterThan(descAtDefault, FirstTextSize(window, "row-desc"), "设置项说明没跟随界面字号");
                Assert.AreEqual(Math.Round(DescRatio * 20), FirstTextSize(window, "row-desc"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 设置项说明必须换行:窗口 CanResize=False 且没有横向滚动条,不换行的长说明会直接
    /// 顶到可视区外面去 —— 字号越大越明显,所以这里拿最大字号来量。
    /// </summary>
    [TestMethod]
    public void LongSettingDescriptions_WrapInsteadOfOverflowing()
    {
        OnUi(() =>
        {
            var window = new SettingsView { DataContext = NewSettingsViewModel() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ApplyAppearance(new AppearanceOptions { UiFontSize = 24 });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                TextBlock[] descriptions =
                [
                    .. window.GetVisualDescendants()
                             .OfType<TextBlock>()
                             .Where(t => t.Classes.Contains("row-desc") && t.IsVisible && t.Bounds.Width > 0),
                ];
                Assert.IsNotEmpty(descriptions);

                // 定宽的行首标注(ANSI 色板的"普通/明亮")是有意关掉换行的,余下的都得能折行。
                foreach (TextBlock desc in descriptions.Where(t => double.IsNaN(t.Width)))
                {
                    Assert.AreEqual(TextWrapping.Wrap, desc.TextWrapping, $"说明「{desc.Text}」不会换行");
                }
                // 并且要真的折起来了 —— 只断言属性值的话,换行在布局上被别的容器挡掉也发现不了。
                Assert.IsTrue(
                    descriptions.Any(d => d.Bounds.Height > d.FontSize * 1.6),
                    "最大字号下没有任何一条说明折行,这条断言就是空的"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>真实设置窗口里的真实文本跟着界面字体换族 —— 含钉在等宽上的说明文字。</summary>
    [TestMethod]
    public void SettingsWindowText_FollowsUiFont()
    {
        OnUi(() =>
        {
            var window = new SettingsView { DataContext = NewSettingsViewModel() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ApplyAppearance(new AppearanceOptions { UiFont = "Comic Sans MS" });
                Dispatcher.UIThread.RunJobs();

                Assert.Contains("Comic Sans MS", FirstText(window, "row-label").FontFamily.ToString());
                Assert.Contains("Comic Sans MS", FirstText(window, "row-desc").FontFamily.ToString());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static SettingsViewModel NewSettingsViewModel()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return new SettingsViewModel(settings, Substitute.For<IThemeService>());
    }

    private static double FirstTextSize(Window window, string styleClass) =>
        FirstText(window, styleClass).FontSize;

    private static TextBlock FirstText(Window window, string styleClass) =>
        window.GetVisualDescendants()
              .OfType<TextBlock>()
              .First(t => t.Classes.Contains(styleClass) && t.IsVisible);

    private static double Token(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is double d
            ? d
            : throw new AssertFailedException($"令牌 {key} 不存在或不是 double");

    private static string Font(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is FontFamily family
            ? family.ToString()
            : throw new AssertFailedException($"令牌 {key} 不存在或不是 FontFamily");

    /// <summary>走生产那条通路下发外观(MainWindow 保存/预览时调用的就是它)。</summary>
    private static void ApplyAppearance(AppearanceOptions appearance)
    {
        MainWindow.ApplyUiFontTokens(Application.Current!, appearance);
        Dispatcher.UIThread.RunJobs();
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None
        ).GetAwaiter().GetResult();
}

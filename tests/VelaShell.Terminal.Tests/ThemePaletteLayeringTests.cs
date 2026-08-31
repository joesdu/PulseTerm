using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 终端配色的三层叠加(具名主题上线后新增中间一层):
/// 控件自带的明暗缺省 → 宿主下发的主题配色(整套)→ 用户改过的单色(稀疏)。
/// <para>
/// 中间这层是必须的:具名主题里有六套暗色,VelaDark 换到 Tokyo Night 时
/// <c>ThemeVariant</c> 根本没变,控件光靠听变体事件无从得知该换哪套色。
/// </para>
/// </summary>
[TestClass]
[TestCategory("TerminalPalette")]
public class ThemePaletteLayeringTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void ThemePalette_ReplacesTheBuiltInDefaults()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl();
            Rgba builtIn = control.PaletteForTest.DefaultBackground;

            control.ThemePalette = Full(background: Rgba.FromRgb(0x2E, 0x34, 0x40));

            Assert.AreNotEqual(builtIn, control.PaletteForTest.DefaultBackground);
            Assert.AreEqual(Rgba.FromRgb(0x2E, 0x34, 0x40), control.PaletteForTest.DefaultBackground);
            Assert.AreEqual(Rgba.FromRgb(0x88, 0xC0, 0xD0), control.PaletteForTest[4], "主题配色要连 ANSI 一起换。");
        });
    }

    [TestMethod]
    public void UserOverrides_WinOverTheThemePalette()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl
            {
                ThemePalette = Full(background: Rgba.FromRgb(0x2E, 0x34, 0x40)),
            };

            // 用户只改了背景一色:其余槽位仍然跟随主题。
            var user = new TerminalPaletteOverrides { Background = Rgba.FromRgb(0x10, 0x10, 0x10) };
            control.PaletteOverrides = user;

            Assert.AreEqual(Rgba.FromRgb(0x10, 0x10, 0x10), control.PaletteForTest.DefaultBackground);
            Assert.AreEqual(Rgba.FromRgb(0x88, 0xC0, 0xD0), control.PaletteForTest[4],
                "用户没改过的颜色必须继续跟随主题,而不是退回控件缺省。");
        });
    }

    /// <summary>换主题(重设整套)后,上一套主题的颜色一个都不能留下。</summary>
    [TestMethod]
    public void SwitchingThemePalette_LeavesNoStaleColors()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl
            {
                ThemePalette = Full(background: Rgba.FromRgb(0x2E, 0x34, 0x40)),
            };

            control.ThemePalette = Full(
                background: Rgba.FromRgb(0x1A, 0x1B, 0x26),
                blue: Rgba.FromRgb(0x7A, 0xA2, 0xF7));

            Assert.AreEqual(Rgba.FromRgb(0x1A, 0x1B, 0x26), control.PaletteForTest.DefaultBackground);
            Assert.AreEqual(Rgba.FromRgb(0x7A, 0xA2, 0xF7), control.PaletteForTest[4]);
        });
    }

    /// <summary>清掉主题配色即回到控件自带的明暗缺省(宿主不在场时的兜底)。</summary>
    [TestMethod]
    public void ClearingThemePalette_FallsBackToBuiltInDefaults()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl();
            Rgba builtIn = control.PaletteForTest.DefaultBackground;

            control.ThemePalette = Full(background: Rgba.FromRgb(0x2E, 0x34, 0x40));
            control.ThemePalette = null;

            Assert.AreEqual(builtIn, control.PaletteForTest.DefaultBackground);
        });
    }

    /// <summary>一套"整套都有值"的主题配色(宿主下发的形态)。</summary>
    private static TerminalPaletteOverrides Full(Rgba background, Rgba? blue = null)
    {
        var palette = new TerminalPaletteOverrides
        {
            Foreground = Rgba.FromRgb(0xD8, 0xDE, 0xE9),
            Background = background,
            Cursor = Rgba.FromRgb(0xD8, 0xDE, 0xE9),
            Selection = Rgba.FromRgb(0x43, 0x4C, 0x5E),
        };
        for (int i = 0; i < TerminalPaletteOverrides.AnsiCount; i++)
        {
            palette.Ansi[i] = Rgba.FromRgb(0x20, 0x20, 0x20);
        }
        palette.Ansi[4] = blue ?? Rgba.FromRgb(0x88, 0xC0, 0xD0);
        return palette;
    }
}

using Avalonia.Headless;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 具名主题 → 终端配色的下发链路:宿主按当前主题把**配套**的整套终端配色推给终端控件。
/// <para>
/// 这条链路是新加的:此前终端只认明暗两套(Dracula / Solarized Light),
/// 而具名主题里有六套暗色 —— VelaDark 换到 Tokyo Night 时明暗变体没变,
/// 没有这次下发,终端画面会原地不动,和换过颜色的界面拼在一起。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public sealed class TerminalThemePairingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TerminalThemePairingTests).Assembly);

    [TestMethod]
    public async Task ActiveTheme_PushesItsPairedTerminalScheme() =>
        await _session.Dispatch(() =>
        {
            foreach (UiTheme theme in UiThemeCatalog.All)
            {
                var control = new VelaTerminalControl();
                var vm = new MainWindowViewModel(themeService: new ThemeService(theme.Id));

                vm.ApplyTerminalAppearanceToPluginView(control);

                TerminalPaletteOverrides? pushed = control.ThemePalette;
                Assert.IsNotNull(pushed, $"{theme.Name}:没有给终端下发主题配色。");
                Assert.AreEqual(
                    ParseHex(theme.Terminal.Background),
                    pushed.Background,
                    $"{theme.Name}:终端背景与配套方案「{theme.TerminalSchemeName}」不符。");
                Assert.AreEqual(
                    ParseHex(theme.Terminal.AnsiNormal[4]),
                    pushed.Ansi[4],
                    $"{theme.Name}:ANSI 蓝与配套方案不符 —— 下发的多半不是整套。");
                Assert.AreEqual(
                    ParseHex(theme.Terminal.AnsiBright[7]),
                    pushed.Ansi[15],
                    $"{theme.Name}:高亮八色没有下发到 8–15 槽位。");
            }
            return Task.FromResult(true);
        }, CancellationToken.None);

    /// <summary>
    /// 没改过任何颜色时用户覆盖必须为空 —— 覆盖非空会把终端钉死在出厂色上,
    /// 换主题再也不跟随(这正是"跟随主题"与"选了具体方案"的分界)。
    /// </summary>
    [TestMethod]
    public async Task UntouchedAppearance_ProducesNoUserOverrides() =>
        await _session.Dispatch(() =>
        {
            var control = new VelaTerminalControl();
            var vm = new MainWindowViewModel(themeService: new ThemeService("nord"));

            vm.ApplyTerminalAppearanceToPluginView(control);

            Assert.IsNull(control.PaletteOverrides, "出厂配色不该产生任何用户覆盖。");
            // 覆盖为空 ⇒ 终端实际用的就是这里下发的主题配色(叠加顺序见
            // VelaShell.Terminal.Tests 的 ThemePaletteLayeringTests)。
            Assert.AreEqual(
                ParseHex(UiThemeCatalog.Get("nord").Terminal.Background),
                control.ThemePalette?.Background,
                "跟随态下终端背景就是主题配套方案的背景。");
            return Task.FromResult(true);
        }, CancellationToken.None);

    private static Rgba ParseHex(string hex)
    {
        uint rgb = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Rgba.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

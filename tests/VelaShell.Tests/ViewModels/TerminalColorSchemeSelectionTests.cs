using Avalonia.Headless;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Services;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 终端配色方案下拉的选中语义。
/// <para>
/// 用户报的缺陷:在 Nord / VelaLight 这类**配套方案不是 Dracula** 的主题下,
/// 下拉里选「Dracula」毫无反应,终端仍然是主题自带的那套,选中项还会自己跳回去。
/// 根因是老实现把「跟随主题」隐式编码成「颜色与出厂默认(= Dracula)一致」——
/// 于是「明确选了 Dracula」与「跟随主题」在设置里写出来的东西一模一样,分不开。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public sealed class TerminalColorSchemeSelectionTests
{
    private static HeadlessUnitTestSession _session = null!;

    /// <summary>下拉首项是「跟随主题」,其后才是内置方案表。</summary>
    private const int FollowItem = 0;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TerminalColorSchemeSelectionTests).Assembly);

    [TestMethod]
    public async Task PickingDracula_UnderANonDraculaTheme_ActuallyAppliesDracula() =>
        await _session.Dispatch(async () =>
        {
            SettingsViewModel vm = await CreateViewModelAsync("nord");
            Assert.AreEqual(FollowItem, vm.ColorSchemeIndex, "起点应是跟随态。");

            vm.ColorSchemeIndex = IndexOf("Dracula");

            Assert.AreEqual(IndexOf("Dracula"), vm.ColorSchemeIndex,
                "选中 Dracula 后不能自己跳回「跟随主题」。");
            Assert.IsFalse(TerminalColorScheme.FollowsTheme(vm.Appearance),
                "明确选了方案就不再是跟随态。");
            Assert.AreEqual("#282A36", vm.Appearance.TerminalBackground);

            TerminalPaletteOverrides? overrides =
                TerminalAppearanceMapper.BuildPaletteOverrides(vm.Appearance);
            Assert.IsNotNull(overrides, "选定的方案必须产生覆盖,否则终端仍是主题自带的那套。");
            Assert.AreEqual(Rgba.FromRgb(0x28, 0x2A, 0x36), overrides.Background);
            Assert.AreEqual(Rgba.FromRgb(0xBD, 0x93, 0xF9), overrides.Ansi[4], "整套下发,不只是背景。");
            return true;
        }, CancellationToken.None);

    /// <summary>配套方案本身也要能被「明确选中」—— 选它和跟随是两回事,只是眼下颜色相同。</summary>
    [TestMethod]
    public async Task PickingThePairedScheme_PinsItInsteadOfFollowing() =>
        await _session.Dispatch(async () =>
        {
            SettingsViewModel vm = await CreateViewModelAsync("nord");

            vm.ColorSchemeIndex = IndexOf("Nord");

            Assert.IsFalse(TerminalColorScheme.FollowsTheme(vm.Appearance));
            Assert.IsNotNull(TerminalAppearanceMapper.BuildPaletteOverrides(vm.Appearance),
                "钉住方案后换主题终端不该再跟着变,因此必须有覆盖。");
            return true;
        }, CancellationToken.None);

    [TestMethod]
    public async Task PickingFollowTheme_ClearsOverridesAndShowsThePairedColors() =>
        await _session.Dispatch(async () =>
        {
            SettingsViewModel vm = await CreateViewModelAsync("nord");
            vm.ColorSchemeIndex = IndexOf("Monokai");
            Assert.IsNotNull(TerminalAppearanceMapper.BuildPaletteOverrides(vm.Appearance));

            vm.ColorSchemeIndex = FollowItem;

            Assert.AreEqual(FollowItem, vm.ColorSchemeIndex);
            Assert.IsNull(TerminalAppearanceMapper.BuildPaletteOverrides(vm.Appearance),
                "跟随态一个槽位都不覆盖。");
            Assert.AreEqual("#2E3440", vm.Appearance.TerminalBackground,
                "回到跟随后,色块显示的应是配套方案(Nord)的颜色,而不是上一次选的 Monokai。");
            return true;
        }, CancellationToken.None);

    /// <summary>跟随态下手改单色 = 用户要自己定配色:必须就此脱离跟随,否则改了等于没改。</summary>
    [TestMethod]
    public async Task EditingASingleColorWhileFollowing_LeavesTheFollowingState() =>
        await _session.Dispatch(async () =>
        {
            SettingsViewModel vm = await CreateViewModelAsync("nord");
            Assert.IsTrue(TerminalColorScheme.FollowsTheme(vm.Appearance));

            vm.Appearance.TerminalBackground = "#101010";

            Assert.IsFalse(TerminalColorScheme.FollowsTheme(vm.Appearance));
            Assert.AreEqual(-1, vm.ColorSchemeIndex, "与任何方案都不一致 → 未选择。");
            TerminalPaletteOverrides? overrides =
                TerminalAppearanceMapper.BuildPaletteOverrides(vm.Appearance);
            Assert.IsNotNull(overrides);
            Assert.AreEqual(Rgba.FromRgb(0x10, 0x10, 0x10), overrides.Background);
            return true;
        }, CancellationToken.None);

    /// <summary>老配置(没有跟随标志、颜色恰为出厂 Dracula)必须仍被判为跟随态。</summary>
    [TestMethod]
    public void LegacySettingsWithoutTheFlag_AreStillTreatedAsFollowing()
    {
        var untouched = new AppearanceOptions();
        Assert.IsNull(untouched.TerminalColorsFollowTheme);
        Assert.IsTrue(TerminalColorScheme.FollowsTheme(untouched));
        Assert.IsNull(TerminalAppearanceMapper.BuildPaletteOverrides(untouched));

        // 老配置里改过配色的:同样没有标志,但颜色与出厂不一致 → 不跟随,覆盖照旧生效。
        var customized = new AppearanceOptions { TerminalBackground = "#123456" };
        Assert.IsFalse(TerminalColorScheme.FollowsTheme(customized));
        Assert.AreEqual(
            Rgba.FromRgb(0x12, 0x34, 0x56),
            TerminalAppearanceMapper.BuildPaletteOverrides(customized)?.Background);
    }

    private static int IndexOf(string schemeName) =>
        Array.FindIndex(TerminalColorScheme.BuiltIn, s => s.Name == schemeName) + 1;

    private static async Task<SettingsViewModel> CreateViewModelAsync(string themeId)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings { Theme = themeId }));
        var vm = new SettingsViewModel(
            settingsService: settings,
            themeService: new ThemeService(themeId));
        await vm.LoadCommand.Execute().FirstAsync();
        Assert.AreEqual(themeId, vm.Theme, "载入后主题应为用例指定的那套。");
        return vm;
    }
}

using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// <see cref="SettingsSectionKey" /> 与设置页左侧分区必须逐项对齐。
/// <para>
/// 消息中心的「有可用更新」靠 <c>SelectSection(SettingsSectionKey.About)</c> 落到关于页。
/// 往分区列表中间插一页而忘了同步枚举,跳转就会静默跑到隔壁页 —— 不会报错、不会崩,
/// 只是用户点了"去更新"却看到别的东西。这个测试就是为了让那种改动**当场失败**。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Notifications")]
public class SettingsSectionKeyTests
{
    /// <summary>枚举成员数与分区数一致,且逐项落在同名分区上。</summary>
    [TestMethod]
    public void SectionKeys_MatchBuiltSections()
    {
        SettingsViewModel vm = CreateViewModel();
        string[] expected =
        [
            Strings.Get("SetVm_SectionGeneral"),
            Strings.Get("SetVm_SectionAppearance"),
            Strings.Get("SetVm_SectionTerminal"),
            Strings.Get("SetVm_SectionKeys"),
            Strings.Get("SetVm_SectionShortcuts"),
            Strings.Get("SetVm_SectionTransfer"),
            Strings.Get("SetVm_SectionSecurity"),
            Strings.Get("SetVm_SectionProxy"),
            Strings.Get("SetVm_SectionSnippets"),
            Strings.Get("SetVm_SectionSync"),
            Strings.Get("SetVm_SectionAbout"),
            Strings.Get("SetVm_SectionSupport")
        ];

        Assert.HasCount(expected.Length, vm.Sections);
        Assert.HasCount(expected.Length, Enum.GetValues<SettingsSectionKey>());
        foreach (SettingsSectionKey key in Enum.GetValues<SettingsSectionKey>())
        {
            Assert.AreEqual(expected[(int)key], vm.Sections[(int)key].Name, $"{key} 没落在它该在的分区上。");
        }
    }

    /// <summary>跳到关于页会把选中项落在关于分区 —— 更新提醒点进来看到的就是这一页。</summary>
    [TestMethod]
    public void SelectSection_LandsOnAbout()
    {
        SettingsViewModel vm = CreateViewModel();

        vm.SelectSection(SettingsSectionKey.About);

        Assert.AreEqual(Strings.Get("SetVm_SectionAbout"), vm.Sections[vm.SelectedSectionIndex].Name);
    }

    /// <summary>只验证分区表本身,依赖全部替身即可。</summary>
    private static SettingsViewModel CreateViewModel() =>
        new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>());

    /// <summary>越界的枚举值不该把选中项设到列表外去。</summary>
    [TestMethod]
    public void SelectSection_IgnoresOutOfRangeKey()
    {
        SettingsViewModel vm = CreateViewModel();
        int before = vm.SelectedSectionIndex;

        vm.SelectSection((SettingsSectionKey)999);

        Assert.AreEqual(before, vm.SelectedSectionIndex);
    }
}

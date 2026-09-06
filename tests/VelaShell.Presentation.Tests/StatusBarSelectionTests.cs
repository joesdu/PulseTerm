using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests;

/// <summary>
/// 状态栏的选区计数与编码热切入口。
/// </summary>
[TestClass]
[TestCategory("StatusBar")]
public sealed class StatusBarSelectionTests
{
    [TestMethod]
    public void NoSelection_HidesTheSegment()
    {
        using var vm = new StatusBarViewModel();

        Assert.AreEqual(0, vm.SelectionLength);
        Assert.IsFalse(vm.HasSelection, "没有选区时那一段不该占位。");
    }

    [TestMethod]
    public void SettingALength_ShowsTheSegment_AndRaisesTheDerivedProperties()
    {
        using var vm = new StatusBarViewModel();
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectionLength = 42;

        Assert.IsTrue(vm.HasSelection);
        Assert.Contains(nameof(StatusBarViewModel.HasSelection), changed);
        Assert.Contains(nameof(StatusBarViewModel.SelectionLabel), changed);
        Assert.Contains("42", vm.SelectionLabel, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ClearingTheSelection_HidesTheSegmentAgain()
    {
        using var vm = new StatusBarViewModel { SelectionLength = 7 };

        vm.SelectionLength = 0;

        Assert.IsFalse(vm.HasSelection);
    }

    [TestMethod]
    public void AvailableEncodings_DefaultsToEmpty_UntilTheHostInjectsThem()
    {
        // 宿主没注入时菜单是空的,而不是崩掉或显示一份写死的副本。
        using var vm = new StatusBarViewModel();

        Assert.IsEmpty(vm.AvailableEncodings);
        Assert.IsNull(vm.ChangeEncodingCommand);
    }
}

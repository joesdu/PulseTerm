using VelaShell.Core.Services;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

/// <summary>
/// 状态栏后台活动指示器的聚合规则。核心那条是"不确定会传染" ——
/// 把说不出进度的活动按 0 混进平均值,算出来的百分比是假的。
/// </summary>
[TestClass]
[TestCategory("BackgroundActivity")]
public sealed class StatusBarBackgroundActivityTests
{
    [TestMethod]
    public void NoActivities_HidesTheIndicator()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyBackgroundActivities([Snapshot(1, "甲")]);
        vm.ApplyBackgroundActivities([]);

        Assert.IsFalse(vm.HasBackgroundActivity);
        Assert.IsEmpty(vm.BackgroundActivities);
        Assert.AreEqual(string.Empty, vm.BackgroundSummary);
    }

    [TestMethod]
    public void SingleActivity_ShowsItsTitleAsTheSummary()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyBackgroundActivities([Snapshot(1, "正在加载插件", "Redis Client")]);

        Assert.IsTrue(vm.HasBackgroundActivity);
        Assert.AreEqual("正在加载插件", vm.BackgroundSummary);
        Assert.IsTrue(vm.IsBackgroundIndeterminate, "没有进度的活动应让圆环走不确定动画。");
        Assert.Contains("Redis Client", vm.BackgroundTooltip);
    }

    [TestMethod]
    public void AllDeterminate_AveragesIntoASolidArc()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyBackgroundActivities([Snapshot(1, "甲", progress: 0.25), Snapshot(2, "乙", progress: 0.75)]);

        Assert.IsFalse(vm.IsBackgroundIndeterminate);
        Assert.AreEqual(0.5, vm.BackgroundProgress);
    }

    [TestMethod]
    public void OneUnknownProgress_MakesTheWholeRingIndeterminate()
    {
        var vm = new StatusBarViewModel();

        // 混合场景:把"不知道"当 0 算进平均值,圆环就会显示一个骗人的百分比。
        vm.ApplyBackgroundActivities([Snapshot(1, "甲", progress: 0.9), Snapshot(2, "乙")]);

        Assert.IsTrue(vm.IsBackgroundIndeterminate);
    }

    [TestMethod]
    public void MultipleActivities_SummarizeByCount_AndListEveryOne()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyBackgroundActivities(
        [
            Snapshot(1, "正在校验插件", "Redis Client", 0.5),
            Snapshot(2, "正在预热插件", "AI Assistant", 0.25)
        ]);

        Assert.Contains("2", vm.BackgroundSummary);
        Assert.HasCount(2, vm.BackgroundActivities);
        Assert.AreEqual("50%", vm.BackgroundActivities[0].Progress);
        Assert.IsTrue(vm.BackgroundActivities[0].HasProgress);
        Assert.IsTrue(vm.BackgroundActivities[0].HasDetail);
        Assert.Contains("Redis Client", vm.BackgroundTooltip);
        Assert.Contains("AI Assistant", vm.BackgroundTooltip);
    }

    [TestMethod]
    public void ActivityWithoutDetail_LeavesTheDetailRowHidden()
    {
        var vm = new StatusBarViewModel();

        vm.ApplyBackgroundActivities([Snapshot(1, "正在启动插件")]);

        Assert.IsFalse(vm.BackgroundActivities[0].HasDetail);
        Assert.IsFalse(vm.BackgroundActivities[0].HasProgress);
        Assert.AreEqual(string.Empty, vm.BackgroundActivities[0].Detail);
    }

    private static BackgroundActivitySnapshot Snapshot(long id, string title, string? detail = null,
        double? progress = null) => new(id, title, detail, progress);
}

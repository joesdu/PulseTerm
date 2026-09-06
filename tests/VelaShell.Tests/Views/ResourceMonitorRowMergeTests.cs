using System.Collections.ObjectModel;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源监视窗口行合并算法(<c>ResourceMonitorWindowViewModel.Merge</c>)的语义回归。
/// </summary>
/// <remarks>
/// 之前的 <c>Fill</c> 按索引整项替换,每换一个元素 <c>ItemsControl</c> 就销毁并重建一次
/// 容器与模板 —— 1 秒档下进程页每秒上百次模板实例化,而且选中行每刷新一次就丢。
/// 这些用例钉住"同一 key 拿到同一个行对象"以及"顺序变化走 Move 而不是重建"两条不变量。
/// 纯集合语义,不需要 UI。
/// </remarks>
[TestClass]
[TestCategory("MonitorUI")]
public sealed class ResourceMonitorRowMergeTests
{
    [TestMethod]
    public void SameKey_ReusesTheRowObject_AndUpdatesItInPlace()
    {
        ObservableCollection<CoreRow> target = [new("CPU0", 10, "10%")];
        CoreRow original = target[0];

        Merge(target, [new CoreRow("CPU0", 55, "55%")]);

        Assert.HasCount(1, target);
        Assert.IsTrue(ReferenceEquals(original, target[0]), "同一 key 应当复用行对象,而不是换一个新的。");
        Assert.AreEqual(55, target[0].Percent, "复用行应当就地更新到新值。");
        Assert.AreEqual("55%", target[0].PercentText);
    }

    [TestMethod]
    public void KeysThatDisappear_AreRemoved()
    {
        // 行复用不能把已经退出的进程留在表里:key 不再出现就该被移除。
        ObservableCollection<CoreRow> target =
        [
            new("CPU0", 10, "10%"),
            new("CPU1", 20, "20%"),
            new("CPU2", 30, "30%")
        ];
        CoreRow kept = target[0];

        Merge(target, [new CoreRow("CPU0", 55, "55%")]);

        Assert.HasCount(1, target);
        Assert.IsTrue(ReferenceEquals(kept, target[0]));
    }

    [TestMethod]
    public void ReorderedRows_AreMovedRatherThanRebuilt()
    {
        // 排序变化(按占用降序的进程/连接表每轮都在变)时用 Move 而不是删了再插 ——
        // 后者会把选中项冲掉,这正是 ProcessManagerViewModel 注释里写明的教训。
        ObservableCollection<CoreRow> target =
        [
            new("A", 10, "10%"),
            new("B", 20, "20%"),
            new("C", 30, "30%")
        ];
        CoreRow a = target[0], b = target[1], c = target[2];

        Merge(target, [new CoreRow("C", 31, "31%"), new CoreRow("A", 11, "11%"), new CoreRow("B", 21, "21%")]);

        Assert.HasCount(3, target);
        Assert.IsTrue(ReferenceEquals(c, target[0]));
        Assert.IsTrue(ReferenceEquals(a, target[1]));
        Assert.IsTrue(ReferenceEquals(b, target[2]));
        Assert.AreEqual(31, c.Percent);
    }

    [TestMethod]
    public void NewKeys_AreInsertedAtTheRightPosition()
    {
        ObservableCollection<CoreRow> target = [new("A", 10, "10%"), new("C", 30, "30%")];
        CoreRow a = target[0], c = target[1];

        Merge(target, [new CoreRow("A", 10, "10%"), new CoreRow("B", 20, "20%"), new CoreRow("C", 30, "30%")]);

        Assert.HasCount(3, target);
        Assert.IsTrue(ReferenceEquals(a, target[0]));
        Assert.AreEqual("B", target[1].Label);
        Assert.IsTrue(ReferenceEquals(c, target[2]));
    }

    [TestMethod]
    public void EmptySample_ClearsTheCollection()
    {
        ObservableCollection<CoreRow> target = [new("A", 10, "10%"), new("B", 20, "20%")];

        Merge(target, []);

        Assert.IsEmpty(target);
    }

    [TestMethod]
    public void MergeIntoAnEmptyCollection_AddsEverything()
    {
        ObservableCollection<CoreRow> target = [];

        Merge(target, [new CoreRow("A", 1, "1%"), new CoreRow("B", 2, "2%")]);

        Assert.HasCount(2, target);
        Assert.AreEqual("A", target[0].Label);
        Assert.AreEqual("B", target[1].Label);
    }

    private static void Merge(ObservableCollection<CoreRow> target, IEnumerable<CoreRow> source) =>
        ResourceMonitorWindowViewModel.MergeForTest(
            target, source, static row => row.Label, static (row, incoming) => row.Update(incoming));
}

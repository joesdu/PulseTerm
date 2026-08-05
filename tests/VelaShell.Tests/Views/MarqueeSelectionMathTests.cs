using VelaShell.Features.Sftp;

namespace VelaShell.Tests.Views;

/// <summary>
/// 框选的命中判定(<see cref="MarqueeSelectionMath.RowsInBand" />)。
/// 判定走内容坐标 + 行高整除,不遍历已实现的容器 —— 列表是虚拟化的,
/// 边拖边自动滚动时视口外的行没有容器,靠容器判定必然漏选。
/// </summary>
[TestClass]
[TestCategory("Marquee")]
public class MarqueeSelectionMathTests
{
    private const double RowHeight = 28;

    [TestMethod]
    public void CtrlMarquee_MergesTheMouseDownSnapshotWithSweptRows()
    {
        IReadOnlyList<string> result = MarqueeSelectionMath.MergeSelection(
            ["already-selected.txt"],
            ["already-selected.txt", "swept.txt"]);

        Assert.AreSequenceEqual(
            ["already-selected.txt", "swept.txt"], [.. result], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void DragPayload_WhenSourceIsSelected_ContainsAllSelectedNonParentItems()
    {
        string parent = "..";
        string source = "b.txt";
        string[] selected = [parent, source, "a.txt"];

        IReadOnlyList<string> result = DragSelectionResolver.Resolve(
            selected,
            source,
            item => item == parent);

        Assert.AreSequenceEqual(["a.txt", "b.txt"], [.. result], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void DragPayload_WhenSourceIsNotSelected_ContainsOnlyTheSource()
    {
        string parent = "..";
        string source = "c.txt";
        string[] selected = [parent, "a.txt", "b.txt"];

        IReadOnlyList<string> result = DragSelectionResolver.Resolve(
            selected,
            source,
            item => item == parent);

        Assert.AreSequenceEqual([source], [.. result]);
    }

    [TestMethod]
    public void DragPayload_WhenSourceIsParent_IsEmpty()
    {
        IReadOnlyList<string> result = DragSelectionResolver.Resolve(
            ["a.txt"],
            "..",
            item => item == "..");

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void DragPayload_UsesPressSnapshotWhenSourceWasSelectedBeforeListBoxMutation()
    {
        IReadOnlyList<string> result = DragSelectionResolver.ResolveAtDragStart(
            ["a.txt", "b.txt"],
            ["b.txt"],
            "b.txt",
            item => item == "..");

        Assert.AreSequenceEqual(["a.txt", "b.txt"], [.. result], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void DragPayload_UsesCurrentSelectionWhenPressSnapshotDidNotContainSource()
    {
        IReadOnlyList<string> result = DragSelectionResolver.ResolveAtDragStart(
            ["a.txt", "b.txt"],
            ["c.txt"],
            "c.txt",
            item => item == "..");

        Assert.AreSequenceEqual(["c.txt"], [.. result]);
    }

    [TestMethod]
    public void DragSelectionSynchronization_RestoresEveryPayloadItemToTheVisibleSelection()
    {
        List<string> visibleSelection = ["b.txt"];
        string[] dragItems = ["a.txt", "b.txt", "folder"];

        DragSelectionResolver.SynchronizeSelection(visibleSelection, dragItems);

        Assert.AreSequenceEqual(
            dragItems,
            [.. visibleSelection],
            Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ModifierDrag_DoesNotRestoreAStalePressSnapshot()
    {
        IReadOnlyList<string> result = DragSelectionResolver.ResolveAtDragStart(
            ["a.txt", "b.txt"],
            ["a.txt"],
            "b.txt",
            item => item == "..",
            usePressSnapshot: false);

        Assert.AreSequenceEqual(["b.txt"], [.. result]);
    }

    [TestMethod]
    public void ModifierDrag_UsesTheCurrentRangeSelection()
    {
        IReadOnlyList<string> result = DragSelectionResolver.ResolveAtDragStart(
            ["a.txt", "b.txt"],
            ["b.txt", "c.txt"],
            "b.txt",
            item => item == "..",
            usePressSnapshot: false);

        Assert.AreSequenceEqual(
            ["b.txt", "c.txt"],
            [.. result],
            Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void BandCoveringTwoRows_SelectsBothInclusive()
    {
        // 第 1 行中间拖到第 3 行中间:1、2、3 三行都该选中。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(42, 98, RowHeight, 10);

        Assert.AreEqual(1, first);
        Assert.AreEqual(3, last);
    }

    [TestMethod]
    public void DraggingUpwards_YieldsTheSameRangeAsDraggingDown()
    {
        // 向上拖时起点在下、终点在上,不能因为参数顺序就选不中。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(98, 42, RowHeight, 10);

        Assert.AreEqual(1, first);
        Assert.AreEqual(3, last);
    }

    [TestMethod]
    public void ZeroHeightBandInsideARow_StillSelectsThatRow()
    {
        // 按下不动(或只横向拖)时,矩形高度为 0,仍应选中光标所在那一行。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(70, 70, RowHeight, 10);

        Assert.AreEqual(2, first);
        Assert.AreEqual(2, last);
    }

    [TestMethod]
    public void BandBelowTheLastRow_SelectsNothing()
    {
        // 末行下面的空白处随手一拖不该莫名选中末行 —— 夹紧必须发生在"整条带子在内容之外"判掉之后。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(400, 500, RowHeight, 10);

        Assert.AreEqual(-1, first);
        Assert.AreEqual(-1, last);
    }

    [TestMethod]
    public void BandAboveTheFirstRow_SelectsNothing()
    {
        (int first, int last) = MarqueeSelectionMath.RowsInBand(-90, -10, RowHeight, 10);

        Assert.AreEqual(-1, first);
        Assert.AreEqual(-1, last);
    }

    [TestMethod]
    public void BandOverrunningBothEnds_ClampsToTheRealRows()
    {
        // 拖过头(自动滚动到底之后很常见)只能选到真实存在的行,不能给出越界下标。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(-500, 5000, RowHeight, 10);

        Assert.AreEqual(0, first);
        Assert.AreEqual(9, last);
    }

    [TestMethod]
    public void BandEndingExactlyOnARowBoundary_DoesNotSpillIntoTheNextRow()
    {
        // 84 = 第 3 行的上边界。拖到刚好贴边时不该把下一行也算进来。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(0, 84, RowHeight, 10);

        Assert.AreEqual(0, first);
        Assert.AreEqual(3, last);

        (int _, int justAbove) = MarqueeSelectionMath.RowsInBand(0, 83.9, RowHeight, 10);
        Assert.AreEqual(2, justAbove);
    }

    [TestMethod]
    public void EmptyListOrBadRowHeight_SelectsNothing()
    {
        Assert.AreEqual((-1, -1), MarqueeSelectionMath.RowsInBand(0, 100, RowHeight, 0));
        Assert.AreEqual((-1, -1), MarqueeSelectionMath.RowsInBand(0, 100, 0, 10));
        Assert.AreEqual((-1, -1), MarqueeSelectionMath.RowsInBand(0, 100, -5, 10));
    }
}

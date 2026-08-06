using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// Alt+左键矩形块选(#128)。几何规则由 <see cref="TerminalSelectionMath" /> 纯计算锁定,
/// 「Alt 决定块选、复制得到矩形文本」这条链路则用 headless 真事件端到端验证。
/// </summary>
[TestClass]
[TestCategory("BlockSelection")]
public sealed class BlockSelectionTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public void Normalize_Block_TakesRectangleCorners_RegardlessOfDragDirection()
    {
        // 从右下往左上拖:两端点围成的矩形与从左上往右下拖完全相同。
        ((int Row, int Col) start, (int Row, int Col) end) = TerminalSelectionMath.Normalize(
            (5, 9),
            (2, 3),
            block: true
        );
        Assert.AreEqual((2, 3), start);
        Assert.AreEqual((5, 9), end);
    }

    [TestMethod]
    public void Normalize_Linear_KeepsRowOrder_AndDoesNotSwapColumns()
    {
        // 线性选区跨行时列不参与归一化:首行 col 9 起、末行到 col 3 止(整行贯通)。
        ((int Row, int Col) start, (int Row, int Col) end) = TerminalSelectionMath.Normalize(
            (5, 3),
            (2, 9),
            block: false
        );
        Assert.AreEqual((2, 9), start);
        Assert.AreEqual((5, 3), end);
    }

    [TestMethod]
    public void Contains_Block_LimitsEveryRowToTheSameColumnRange()
    {
        ((int Row, int Col) Start, (int Row, int Col) End) sel = ((1, 2), (3, 5));

        Assert.IsTrue(TerminalSelectionMath.Contains(sel, block: true, 2, 2), "左边界含在内。");
        Assert.IsTrue(TerminalSelectionMath.Contains(sel, block: true, 2, 4));
        Assert.IsFalse(TerminalSelectionMath.Contains(sel, block: true, 2, 5), "右边界排它。");
        Assert.IsFalse(TerminalSelectionMath.Contains(sel, block: true, 2, 1));
        Assert.IsFalse(TerminalSelectionMath.Contains(sel, block: true, 0, 3), "行在选区之上。");
        Assert.IsFalse(TerminalSelectionMath.Contains(sel, block: true, 4, 3), "行在选区之下。");

        // 同一选区在线性模式下:中间行整行选中,与块选形成对照。
        Assert.IsTrue(TerminalSelectionMath.Contains(sel, block: false, 2, 0));
        Assert.IsTrue(TerminalSelectionMath.Contains(sel, block: false, 2, 40));
    }

    [TestMethod]
    public void RowSpan_ClampsToLineWidth_AndCollapsesEmptyRanges()
    {
        ((int Row, int Col) Start, (int Row, int Col) End) sel = ((1, 2), (3, 80));

        // 块选:每行同一段,超出行宽的部分被夹取。
        Assert.AreEqual((2, 10), TerminalSelectionMath.RowSpan(sel, true, 2, 10));
        // 起点列已超出短行宽度 → 空区间(不产生倒序循环)。
        Assert.AreEqual((1, 1), TerminalSelectionMath.RowSpan(sel, true, 2, 1));
        // 线性:中间行取整行。
        Assert.AreEqual((0, 10), TerminalSelectionMath.RowSpan(sel, false, 2, 10));
        // 不在选区内的行返回空区间。
        Assert.AreEqual((0, 0), TerminalSelectionMath.RowSpan(sel, false, 9, 10));
    }

    [TestMethod]
    public void AltDrag_SelectsRectangle_WhilePlainDragSelectsWholeLines()
    {
        Assert.AreEqual("cde\nklm\nstu", SelectedTextAfterDrag(withAlt: true));
        Assert.AreEqual("cdefgh\nijklmnop\nqrstu", SelectedTextAfterDrag(withAlt: false));
    }

    /// <summary>
    /// 在 3 行样本上从 (行0,列2) 拖到 (行2,列5),返回选中的文本。
    /// <paramref name="withAlt" /> 决定按下鼠标时是否按住 Alt(即是否块选)。
    /// </summary>
    private static string SelectedTextAfterDrag(bool withAlt)
    {
        string text = string.Empty;
        _session
            .Dispatch(
                () =>
                {
                    var control = new VelaTerminalControl
                    {
                        CopyOnSelect = false, // headless 下不去碰剪贴板
                    };
                    control.Feed(
                        Encoding.ASCII.GetBytes("abcdefgh\r\nijklmnop\r\nqrstuvwx")
                    );

                    var window = new Window
                    {
                        Width = 480,
                        Height = 320,
                        Content = control,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame(); // 填充屏幕行映射与单元格度量

                    RawInputModifiers modifiers = withAlt
                        ? RawInputModifiers.Alt
                        : RawInputModifiers.None;
                    window.MouseDown(CellPoint(control, 0, 2), MouseButton.Left, modifiers);
                    window.MouseMove(CellPoint(control, 2, 5), modifiers);
                    window.MouseUp(CellPoint(control, 2, 5), MouseButton.Left, modifiers);
                    Dispatcher.UIThread.RunJobs();

                    Assert.AreEqual(
                        withAlt,
                        control.IsBlockSelection,
                        "块选模式应由按下鼠标时的 Alt 决定。"
                    );
                    text = control.GetSelectedText();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        return text;
    }

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1
        );
}

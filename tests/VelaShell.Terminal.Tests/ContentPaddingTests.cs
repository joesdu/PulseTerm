using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 可调内边距(#227)。留白必须只吃掉格子数,不能让坐标算错 —— 那会表现为
/// 「选中的字比点的位置偏几格」「侧栏点不动」这类难查的错位,故直接用 headless
/// 真控件验证:网格随留白收缩、光标矩形整体平移、侧栏命中跟着平移。
/// </summary>
[TestClass]
[TestCategory("TerminalPadding")]
public class ContentPaddingTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void Padding_ShrinksGrid_AndShiftsCursorRect()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl();
            var window = new Window { Width = 640, Height = 400, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            int baseCols = control.Columns;
            int baseRows = control.Rows;
            Rect baseCursor = control.GetCursorRect();
            double cellWidth = control.CellWidthForTest;
            double cellHeight = control.CellHeightForTest;
            Assert.IsGreaterThan(0, cellWidth);
            Assert.IsGreaterThan(0, cellHeight);

            const double pad = 16;
            control.ContentPadding = pad;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            // 左右各扣一份留白 → 少掉的列数就是 2*pad 折算的格子数(边界受取整影响 ±1)。
            int expectedColLoss = (int)(2 * pad / cellWidth);
            Assert.AreEqual(
                (double)(baseCols - expectedColLoss),
                control.Columns,
                1,
                "列数应随左右留白按格宽收缩。"
            );
            Assert.AreEqual(
                (double)(baseRows - (int)(2 * pad / cellHeight)),
                control.Rows,
                1,
                "行数应随上下留白按行高收缩。"
            );

            // 光标矩形是弹层/IME 的锚点:留白是整体平移,它必须原样跟着挪。
            Rect padded = control.GetCursorRect();
            Assert.AreEqual(baseCursor.X + pad, padded.X, 0.01);
            Assert.AreEqual(baseCursor.Y + pad, padded.Y, 0.01);

            control.ContentPadding = 0;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Assert.AreEqual(baseCols, control.Columns, "归零后应回到原网格。");
            Assert.AreEqual(baseRows, control.Rows, "归零后应回到原网格。");
        });
    }

    [TestMethod]
    public void Padding_IsClampedToSaneRange()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ContentPadding = -5 };
            Assert.AreEqual(0d, control.ContentPadding, "负留白无意义,钳到 0。");

            control.ContentPadding = 1000;
            Assert.AreEqual(
                VelaTerminalControl.MaxContentPadding,
                control.ContentPadding,
                "过大的留白会把正文挤没,钳到上限。"
            );

            // 取整数像素:侧栏的 1px 笔画按 floor(x)+0.5 对齐,带小数的平移会把它糊成 2px。
            control.ContentPadding = 7.6;
            Assert.AreEqual(8d, control.ContentPadding);
        });
    }

    [TestMethod]
    public void Padding_ShiftsGutterHitTesting()
    {
        OnUi(() =>
        {
            const double pad = 12;
            var control = new VelaTerminalControl { ShowFoldMarker = true, ContentPadding = pad };
            control.Feed(Encoding.UTF8.GetBytes("L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5"));

            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Assert.AreEqual(0, control.FoldCountForTest);

            // 侧栏几何以正文坐标表达,真实点击落在「留白 + 折叠列」处才算命中。
            GutterLayout gutter = control.GutterForTest;
            var hit = new Point(pad + gutter.FoldLeft + 2, pad + 3 * control.CellHeightForTest + 2);
            window.MouseDown(hit, MouseButton.Left);
            window.MouseUp(hit, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, control.FoldCountForTest, "扣掉留白后应命中折叠列。");

            // 同一个 x 若不加留白,就落在留白带里 —— 那里不是折叠列,不该再折叠一次。
            window.CaptureRenderedFrame();
            var miss = new Point(gutter.FoldLeft + 2, pad + 5 * control.CellHeightForTest + 2);
            window.MouseDown(miss, MouseButton.Left);
            window.MouseUp(miss, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, control.FoldCountForTest, "留白带内的点击不应被当成折叠列命中。");
        });
    }
}

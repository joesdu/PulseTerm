using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// issue #245「终端能看出方块」的端到端回归:用 headless 真控件跑一遍真实渲染,
/// 再从真实绘制路径(<c>CellRect</c>)取出单元格背景矩形,验证 125% 缩放下相邻格子
/// 严丝合缝、每条边都落在整数设备像素上。
/// <para>
/// headless 窗口的 <c>RenderScaling</c> 恒为 1.0,而这个 bug 只在分数缩放下出现,
/// 故用 <c>RenderScalingOverrideForTest</c> 注入 1.25(截图实测值:行距 21.25 设备像素 = 17 DIP × 1.25)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("DevicePixelSnap")]
public class TerminalSeamSnapUiTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private const double FractionalScale = 1.25;
    private const double Padding = 5;

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>控件 + 窗口 + 一次真实渲染;返回已就绪的控件。</summary>
    private static VelaTerminalControl RenderedControl(double scale)
    {
        var control = new VelaTerminalControl { ContentPadding = Padding, RenderScalingOverrideForTest = scale };
        // 带背景色的输出(反显)才会走到逐单元 FillRectangle —— 也就是出缝的那条路径。
        control.Feed(
            Encoding.UTF8.GetBytes(
                "\u001b[7mPID USER  PRI NI VIRT\u001b[0m\r\nrow2\r\nrow3\r\nrow4"
            )
        );
        var window = new Window { Width = 640, Height = 400, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // 真跑一遍 Render(),顺带确认吸附路径不抛异常
        return control;
    }

    private static void AssertOnDevicePixel(double dip, double origin, double scale, string what)
    {
        double device = (origin + dip) * scale;
        Assert.AreEqual(Math.Round(device), device, 1e-6, $"{what} 落在设备像素 {device},不是整数边界。");
    }

    [TestMethod]
    public void FractionalScaling_AdjacentCellBackgrounds_LeaveNoSeam()
    {
        OnUi(() =>
        {
            VelaTerminalControl control = RenderedControl(FractionalScale);
            double originX = Padding + control.GutterForTest.TotalWidth;
            double originY = Padding;
            Assert.IsGreaterThan(0, control.CellWidthForTest);
            Assert.IsGreaterThan(0, control.CellHeightForTest);

            // 横向:同一行内逐列推进,公共边必须重合。
            double previousRight = double.NaN;
            for (int col = 0; col < Math.Min(40, control.Columns); col++)
            {
                Rect cell = control.CellRectForTest(col, 0);
                if (!double.IsNaN(previousRight))
                {
                    Assert.AreEqual(previousRight, cell.X, 1e-9, $"第 {col} 列与前一列之间出现缝隙/重叠。");
                }
                AssertOnDevicePixel(cell.X, originX, FractionalScale, $"第 {col} 列左边");
                AssertOnDevicePixel(cell.Right, originX, FractionalScale, $"第 {col} 列右边");
                Assert.IsGreaterThan(0, cell.Width, $"第 {col} 列被吸附成零宽。");
                previousRight = cell.Right;
            }

            // 纵向:逐行推进,公共边同样必须重合(截图里的横向条纹就出在这儿)。
            double previousBottom = double.NaN;
            for (int row = 0; row < Math.Min(20, control.Rows); row++)
            {
                Rect cell = control.CellRectForTest(0, row);
                if (!double.IsNaN(previousBottom))
                {
                    Assert.AreEqual(previousBottom, cell.Y, 1e-9, $"第 {row} 行与前一行之间出现缝隙/重叠。");
                }
                AssertOnDevicePixel(cell.Y, originY, FractionalScale, $"第 {row} 行上边");
                AssertOnDevicePixel(cell.Bottom, originY, FractionalScale, $"第 {row} 行下边");
                Assert.IsGreaterThan(0, cell.Height, $"第 {row} 行被吸附成零高。");
                previousBottom = cell.Bottom;
            }
        });
    }

    /// <summary>宽字符(CJK)占两格,吸附后仍必须正好接上后一格,不能因取整少半个像素。</summary>
    [TestMethod]
    public void FractionalScaling_WideCell_StillTilesWithItsNeighbour()
    {
        OnUi(() =>
        {
            VelaTerminalControl control = RenderedControl(FractionalScale);
            Rect wide = control.CellRectForTest(0, 0, 2);
            Rect next = control.CellRectForTest(2, 0);
            Assert.AreEqual(wide.Right, next.X, 1e-9, "双宽格子的右边必须与第 3 列的左边重合。");
            Assert.AreEqual(
                control.CellRectForTest(0, 0).X,
                wide.X,
                1e-9,
                "双宽格子的左边应与单宽时一致。"
            );
        });
    }

    /// <summary>吸附确实起了作用:1.25 缩放下至少有一条边被挪动过(否则上面的断言可能是空转)。</summary>
    [TestMethod]
    public void FractionalScaling_ActuallyMovesSomeEdges()
    {
        OnUi(() =>
        {
            VelaTerminalControl control = RenderedControl(FractionalScale);
            bool moved = false;
            for (int col = 0; col < Math.Min(16, control.Columns) && !moved; col++)
            {
                Rect snapped = control.CellRectForTest(col, 0);
                moved = Math.Abs(snapped.X - (col * control.CellWidthForTest)) > 1e-9;
            }
            Assert.IsTrue(moved, "125% 缩放下应当有格子边界被吸附挪动,否则这组回归测试是空转的。");
        });
    }

    /// <summary>100% 缩放(维护者本地的 Ubuntu/Debian 环境)必须零改动 —— 吸附不能动到既有观感。</summary>
    [TestMethod]
    public void IntegerScaling_LeavesCellRectsUntouched()
    {
        OnUi(() =>
        {
            VelaTerminalControl control = RenderedControl(1.0);
            for (int col = 0; col < Math.Min(16, control.Columns); col++)
            {
                Rect cell = control.CellRectForTest(col, 2);
                Assert.AreEqual(col * control.CellWidthForTest, cell.X, 1e-9, "100% 下横坐标不应被挪动。");
                Assert.AreEqual(2 * control.CellHeightForTest, cell.Y, 1e-9, "100% 下纵坐标不应被挪动。");
                Assert.AreEqual(control.CellWidthForTest, cell.Width, 1e-9, "100% 下格宽不应变化。");
                Assert.AreEqual(control.CellHeightForTest, cell.Height, 1e-9, "100% 下格高不应变化。");
            }
        });
    }
}

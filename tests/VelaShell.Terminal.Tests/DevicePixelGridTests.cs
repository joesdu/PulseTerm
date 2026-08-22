using Avalonia;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 设备像素吸附(issue #245「终端能看出方块」)。
/// <para>
/// 成因:格子尺寸取整到整数 <b>DIP</b>,在 125% / 150% 这类分数缩放下并不落在整数设备像素上。
/// 逐单元 <c>FillRectangle</c> 的相邻矩形共享的那条边落在像素中间,两个矩形各自抗锯齿,
/// 叠加后的覆盖率是 1 − f(1−f)(最坏 0.75),于是每条格线上留下一道浅缝,整屏看起来是网格方块。
/// 报告者的截图实测行距 21.25 设备像素 = 17 DIP × 1.25,列距 10 = 8 DIP × 1.25。
/// </para>
/// <para>本类锁住吸附本身的纯几何契约:<b>相邻矩形零缝隙零重叠,且每条边都落在整数设备像素上</b>。</para>
/// </summary>
[TestClass]
[TestCategory("DevicePixelSnap")]
public class DevicePixelGridTests
{
    // 截图反推出来的真实参数:8×17 DIP 的格子,5 DIP 内边距,125% 缩放。
    private const double CellWidth = 8;
    private const double CellHeight = 17;
    private const double Padding = 5;
    private const double FractionalScale = 1.25;

    private static void AssertOnDevicePixel(double dip, double origin, double scale, string what)
    {
        double device = (origin + dip) * scale;
        Assert.AreEqual(
            Math.Round(device),
            device,
            1e-6,
            $"{what} 落在设备像素 {device} 上,不是整数边界 —— 抗锯齿会在这里留缝。"
        );
    }

    /// <summary>同一行内相邻格子:公共边必须重合到同一个设备像素上。</summary>
    [TestMethod]
    public void Snap_AdjacentColumns_ShareExactlyOneDevicePixelEdge()
    {
        var grid = new DevicePixelGrid(Padding, Padding, FractionalScale);
        double previousRight = double.NaN;
        for (int col = 0; col < 64; col++)
        {
            Rect snapped = grid.Snap(new(col * CellWidth, 0, CellWidth, CellHeight));
            if (!double.IsNaN(previousRight))
            {
                Assert.AreEqual(
                    previousRight,
                    snapped.X,
                    1e-9,
                    $"第 {col} 列的左边与前一列的右边必须严丝合缝(有缝隙或重叠都会显出竖线)。"
                );
            }
            AssertOnDevicePixel(snapped.X, Padding, FractionalScale, $"第 {col} 列左边");
            AssertOnDevicePixel(snapped.Right, Padding, FractionalScale, $"第 {col} 列右边");
            Assert.IsGreaterThan(0, snapped.Width, $"第 {col} 列被吸附成了零宽,背景会整格消失。");
            previousRight = snapped.Right;
        }
    }

    /// <summary>
    /// 相邻行:issue 截图里的横向条纹就出在这儿 —— 21.25 的行距每 4 行才对齐一次,
    /// 其余 3 条边界都在像素中间。吸附后每行的带宽只在 21/22 设备像素之间浮动,但绝不留缝。
    /// </summary>
    [TestMethod]
    public void Snap_AdjacentRows_TileWithoutSeam_AndKeepHeightWithinOneDevicePixel()
    {
        var grid = new DevicePixelGrid(Padding, Padding, FractionalScale);
        double previousBottom = double.NaN;
        var deviceHeights = new HashSet<long>();
        for (int row = 0; row < 40; row++)
        {
            Rect snapped = grid.Snap(new(0, row * CellHeight, CellWidth, CellHeight));
            if (!double.IsNaN(previousBottom))
            {
                Assert.AreEqual(
                    previousBottom,
                    snapped.Y,
                    1e-9,
                    $"第 {row} 行的上边与前一行的下边必须严丝合缝(有缝隙就是横向条纹)。"
                );
            }
            AssertOnDevicePixel(snapped.Y, Padding, FractionalScale, $"第 {row} 行上边");
            AssertOnDevicePixel(snapped.Bottom, Padding, FractionalScale, $"第 {row} 行下边");
            deviceHeights.Add((long)Math.Round(snapped.Height * FractionalScale));
            previousBottom = snapped.Bottom;
        }

        // 21.25 的行距只能落在 21 或 22 设备像素上;出现第三种值说明吸附把行距算飘了。
        CollectionAssert.AreEquivalent(
            new long[] { 21, 22 },
            deviceHeights.OrderBy(h => h).ToArray(),
            "行高应只在 21/22 设备像素之间浮动。"
        );
    }

    /// <summary>
    /// 反证:不吸附时同样的矩形边确实落在像素中间 —— 说明上面两条测试锁的是真问题,
    /// 而不是一个本来就成立的恒等式。
    /// </summary>
    [TestMethod]
    public void WithoutSnapping_RowEdges_FallBetweenDevicePixels()
    {
        int offGrid = 0;
        for (int row = 0; row < 8; row++)
        {
            double device = (Padding + (row * CellHeight)) * FractionalScale;
            if (Math.Abs(device - Math.Round(device)) > 1e-6)
            {
                offGrid++;
            }
        }
        Assert.IsGreaterThan(0, offGrid, "125% 缩放下未吸附的行边界本应大多不在整数设备像素上。");
    }

    /// <summary>整数缩放(100% / 200%)且原点也在整数上时,吸附必须是恒等变换 —— 现有观感零改动。</summary>
    [TestMethod]
    [DataRow(1.0)]
    [DataRow(2.0)]
    public void Snap_IntegerScale_IsIdentity(double scale)
    {
        var grid = new DevicePixelGrid(Padding, Padding, scale);
        Assert.IsTrue(grid.IsAligned, $"{scale}× 且整数原点下应判定为天然对齐。");
        for (int col = 0; col < 16; col++)
        {
            var raw = new Rect(col * CellWidth, 3 * CellHeight, CellWidth, CellHeight);
            Rect snapped = grid.Snap(raw);
            Assert.AreEqual(raw.X, snapped.X, 1e-9);
            Assert.AreEqual(raw.Y, snapped.Y, 1e-9);
            Assert.AreEqual(raw.Width, snapped.Width, 1e-9);
            Assert.AreEqual(raw.Height, snapped.Height, 1e-9);
        }
    }

    /// <summary>分数缩放、或原点本身停在半个像素上,都不算对齐。</summary>
    [TestMethod]
    public void IsAligned_DetectsFractionalScaleAndFractionalOrigin()
    {
        Assert.IsFalse(new DevicePixelGrid(Padding, Padding, 1.25).IsAligned, "1.25× 不对齐。");
        Assert.IsFalse(new DevicePixelGrid(0.5, 0, 1.0).IsAligned, "100% 下 0.5 DIP 的原点不对齐。");
        Assert.IsTrue(new DevicePixelGrid(0.5, 0, 2.0).IsAligned, "200% 下 0.5 DIP 正好是一个设备像素。");
    }

    /// <summary>非法缩放(0/负数/NaN)回退为 1,不能让绘制坐标变成 NaN。</summary>
    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-2.0)]
    [DataRow(double.NaN)]
    public void Snap_InvalidScale_FallsBackToOne(double scale)
    {
        var grid = new DevicePixelGrid(Padding, Padding, scale);
        Assert.AreEqual(1.0, grid.Scale, 1e-9);
        Rect snapped = grid.Snap(new(CellWidth, CellHeight, CellWidth, CellHeight));
        Assert.AreEqual(CellWidth, snapped.Width, 1e-9);
        Assert.AreEqual(CellHeight, snapped.Height, 1e-9);
    }
}

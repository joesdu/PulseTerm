using Avalonia.Media;
using VelaShell.Controls.Controls;

namespace VelaShell.Controls.Tests;

/// <summary>
/// <see cref="PenCache" /> 的回归。这类缓存最典型的坑是"复用出了旧颜色":
/// 若按画刷实例引用做键,而调用方就地改了 <c>SolidColorBrush.Color</c>(主题令牌改色、
/// 颜色动画),控件就会一直画着上一版颜色。这里把颜色入键这条不变量钉死。
/// </summary>
[TestClass]
[TestCategory("PenCache")]
public class PenCacheTests
{
    [TestMethod]
    public void SameParameters_ReturnSameInstance()
    {
        var cache = new PenCache();
        IPen first = cache.Get(Brushes.Red, 2, PenLineCap.Round, PenLineJoin.Round);
        IPen second = cache.Get(Brushes.Red, 2, PenLineCap.Round, PenLineJoin.Round);
        Assert.AreSame(first, second, "参数完全相同时应复用同一支画笔 —— 否则缓存等于没做。");
    }

    [TestMethod]
    public void DifferingParameters_ReturnDistinctPens()
    {
        var cache = new PenCache();
        IPen thin = cache.Get(Brushes.Red, 1);
        IPen thick = cache.Get(Brushes.Red, 2);
        IPen rounded = cache.Get(Brushes.Red, 1, PenLineCap.Round);
        IPen joined = cache.Get(Brushes.Red, 1, PenLineCap.Flat, PenLineJoin.Round);

        Assert.AreNotSame(thin, thick);
        Assert.AreNotSame(thin, rounded);
        Assert.AreNotSame(thin, joined);
        Assert.AreEqual(1, thin.Thickness);
        Assert.AreEqual(2, thick.Thickness);
        Assert.AreEqual(PenLineCap.Round, rounded.LineCap);
        Assert.AreEqual(PenLineJoin.Round, joined.LineJoin);
    }

    [TestMethod]
    public void MutatedBrushColor_YieldsFreshPen_NotTheStaleOne()
    {
        // 关键不变量:同一个画刷实例被就地改色后,必须拿到新颜色的画笔。
        var brush = new SolidColorBrush(Colors.Red);
        var cache = new PenCache();

        IPen before = cache.Get(brush, 2);
        Assert.AreEqual(Colors.Red, ((ISolidColorBrush)before.Brush!).Color);

        brush.Color = Colors.Lime;
        IPen after = cache.Get(brush, 2);

        Assert.AreNotSame(before, after, "画刷就地改色后仍复用旧画笔 —— 控件会一直画着旧颜色。");
        Assert.AreEqual(Colors.Lime, ((ISolidColorBrush)after.Brush!).Color);
    }

    [TestMethod]
    public void NonSolidBrush_IsNotCached_ButStillProducesUsablePen()
    {
        // 渐变画刷判等不便宜,直接现建;这些场景本就不在热路径上,但不能因此画不出来。
        var gradient = new LinearGradientBrush
        {
            GradientStops =
            [
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Blue, 1)
            ]
        };
        var cache = new PenCache();

        IPen first = cache.Get(gradient, 3);
        IPen second = cache.Get(gradient, 3);

        Assert.AreNotSame(first, second, "非纯色画刷不入缓存(实现约定)。");
        Assert.IsNotNull(first.Brush);
        Assert.AreEqual(3, first.Thickness);
    }

    [TestMethod]
    public void ManyDistinctColors_DoNotGrowUnbounded()
    {
        // 上限保险丝:插件下发的自定义配色理论上可以无界,缓存必须自己收口。
        var cache = new PenCache();
        for (int i = 0; i < 200; i++)
        {
            IPen pen = cache.Get(new SolidColorBrush(Color.FromRgb((byte)i, 0, 0)), 1);
            Assert.IsNotNull(pen);
        }

        // 清空过之后仍然照常工作(不是清完就废)。
        IPen a = cache.Get(Brushes.Red, 1);
        IPen b = cache.Get(Brushes.Red, 1);
        Assert.AreSame(a, b);
    }
}

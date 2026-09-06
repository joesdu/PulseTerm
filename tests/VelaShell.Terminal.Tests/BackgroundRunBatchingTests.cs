using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 背景矩形合批(<c>AppendBackground</c> / <c>FlushBackgroundRun</c>)的守门回归。
/// </summary>
/// <remarks>
/// <para>
/// 原先每个背景 ≠ 默认色的格子都发一次 <c>FillRectangle</c>:全屏 TUI 与大段选区
/// 每帧就是 O(行 × 列) 个矩形指令,200×50 的窗口最坏一万次。现在相邻同色合成一个。
/// </para>
/// <para>
/// <b>天花板是次序决定的,不是实现偷懒。</b>字形攒批延迟画,下划线/删除线/字体回退即时画,
/// 所以背景 run 必须在这些绘制之前冲刷,也就必然在每处字形 run 断裂点(样式或前景色变化)
/// 断开。因此:纯色空白区域合成 1 个矩形;同底色但前景色频繁变化的行仍接近逐格。
/// 下面的用例把这两种形态都钉住,免得后来者把"没合并到 1"当 bug 修。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("BackgroundBatching")]
public class BackgroundRunBatchingTests
{
    private static HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>建好一个已显示、已渲染过一帧的终端控件,并喂入给定输出。</summary>
    private static (VelaTerminalControl Control, Window Window) ShowTerminal(string text)
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = false,
            CursorBlink = false
        };
        control.Feed(Encoding.UTF8.GetBytes(text));
        var window = new Window { Width = 640, Height = 360, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        return (control, window);
    }

    /// <summary>再逼一帧渲染,并返回该帧发出的背景矩形数。</summary>
    private static int RenderAndCountRects(VelaTerminalControl control, Window window)
    {
        control.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        return control.BackgroundRectCountForTest;
    }

    [TestMethod]
    public void DefaultBackgroundOnly_EmitsNoRectangles()
    {
        OnUi(() =>
        {
            // 默认底色不画(那是画布本身的颜色),所以整屏白文本一个矩形都不该发。
            (VelaTerminalControl control, Window window) = ShowTerminal("hello world\r\nsecond line\r\n");

            Assert.AreEqual(0, RenderAndCountRects(control, window),
                "默认背景不应产生任何 FillRectangle。");

            window.Close();
        });
    }

    [TestMethod]
    public void ARunOfBlanksInOneColour_CollapsesToASingleRectangle()
    {
        OnUi(() =>
        {
            // 红底 40 个空格:没有字形、没有样式变化 → 合成 1 个矩形(原先是 40 个)。
            (VelaTerminalControl control, Window window) = ShowTerminal("\e[41m" + new string(' ', 40) + "\e[0m");

            Assert.AreEqual(1, RenderAndCountRects(control, window),
                "一整段同色空白背景应当只发一个矩形。");

            window.Close();
        });
    }

    [TestMethod]
    public void ColourChanges_BreakTheRun()
    {
        OnUi(() =>
        {
            // 红 / 绿 / 红 三段空白 → 三个矩形(颜色变化必须断开 run,否则会画错色)。
            (VelaTerminalControl control, Window window) = ShowTerminal(
                "\e[41m          \e[42m          \e[41m          \e[0m");

            Assert.AreEqual(3, RenderAndCountRects(control, window),
                "颜色变化处必须断开背景 run。");

            window.Close();
        });
    }

    [TestMethod]
    public void DefaultBackgroundGaps_BreakTheRun()
    {
        OnUi(() =>
        {
            // 红底 — 默认底 — 红底:中间那段回到默认色,两侧不能被连成一整条。
            (VelaTerminalControl control, Window window) = ShowTerminal(
                "\e[41m     \e[49m     \e[41m     \e[0m");

            Assert.AreEqual(2, RenderAndCountRects(control, window),
                "回到默认背景处必须断开 run,否则中间那段会被误涂成红色。");

            window.Close();
        });
    }

    [TestMethod]
    public void RunsDoNotCrossLineBoundaries()
    {
        OnUi(() =>
        {
            // 两行各一段红底 → 两个矩形。run 若跨行,矩形的 y 就是错的。
            (VelaTerminalControl control, Window window) = ShowTerminal(
                "\e[41m          \e[0m\r\n\e[41m          \e[0m\r\n");

            Assert.AreEqual(2, RenderAndCountRects(control, window),
                "背景 run 不得跨越行边界。");

            window.Close();
        });
    }

    [TestMethod]
    public void ForegroundChangesOnASharedBackground_SplitTheRun_ByDesign()
    {
        OnUi(() =>
        {
            // 同一红底上前景色换三次(vim 状态行的形态)。字形是延迟画的,背景必须在字形
            // 之前落地,所以每处字形 run 断裂都会连带断开背景 run —— 这是次序正确性的代价。
            // 断言"大于 1"而不是某个精确值:精确值随字形合批策略而变,而这里要钉的是
            // "会断开"这个事实本身,免得有人把它当 bug 修成一整条盖住字形。
            (VelaTerminalControl control, Window window) = ShowTerminal(
                "\e[41m\e[31mAAA\e[32mBBB\e[34mCCC\e[0m");

            int rects = RenderAndCountRects(control, window);

            Assert.IsGreaterThan(1, rects,
                "同底色但前景色变化的行会按字形 run 断开背景 run —— 这是已知且必要的代价。");

            window.Close();
        });
    }

    [TestMethod]
    public void UnderlinedTextOnAColouredBackground_StillRasterisesTheUnderline()
    {
        OnUi(() =>
        {
            // 下划线是即时绘制:背景 run 若不在它之前冲刷,后发的矩形会把线整条盖掉。
            // 这里只断言渲染不抛且确实发过背景矩形;像素级由 RenderTests 的整帧回归兜底。
            (VelaTerminalControl control, Window window) = ShowTerminal("\e[41m\e[4munderlined\e[0m");

            Assert.IsGreaterThan(0, RenderAndCountRects(control, window));

            window.Close();
        });
    }

    [TestMethod]
    public void CountIsResetEveryFrame()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal("\e[41m          \e[0m");

            int first = RenderAndCountRects(control, window);
            int second = RenderAndCountRects(control, window);

            Assert.AreEqual(first, second, "计数器应当每帧归零,而不是累加。");

            window.Close();
        });
    }
}

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 光标/幽灵叠加层的 headless 端到端回归。
/// <para>
/// 光标与幽灵文本被拆到一个独立的可视子元素上,好让 530ms 一次的闪烁只失效那一层,
/// 而不是为了一个格子重新记录整屏(1 万格的解析色 + 逐行语义扫描 + 全部 GlyphRun)。
/// 这里同时锁住拆层的两条契约 —— 收益侧「闪烁不碰正文」与正确性侧「正文一变光标必须跟着重画」,
/// 后者漏掉就是输入时光标停在旧位置不跟手。
/// </para>
/// </summary>
[TestClass]
[TestCategory("CursorOverlay")]
public class CursorOverlayUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>建好一个已显示、已渲染过一帧的终端控件。</summary>
    private static (VelaTerminalControl Control, Window Window) ShowTerminal()
    {
        var control = new VelaTerminalControl();
        control.Feed(Encoding.UTF8.GetBytes("abc"));
        var window = new Window { Width = 480, Height = 320, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // 逼出一帧真实渲染(headless 平台下返回 null,取的是副作用)
        return (control, window);
    }

    [TestMethod]
    public void CursorOverlay_IsArrangedAsVisualChild_CoveringTheControl()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal();

            Visual[] children = [.. control.GetVisualChildren()];
            Assert.HasCount(1, children, "终端控件应恰好挂着一个叠加层可视子元素。");

            Visual overlay = children[0];

            // 手工挂在 VisualChildren 上的子元素不参与默认的测量/排布:漏掉任一步它的 Bounds
            // 就是空的,渲染器直接整层跳过 —— 表现为光标彻底消失,而单元测试完全看不出来。
            Assert.IsGreaterThan(0, overlay.Bounds.Width, "叠加层没被排布(Bounds 为空)→ 渲染器会整层跳过,光标将不可见。");
            Assert.AreEqual(control.Bounds.Width, overlay.Bounds.Width, 0.01, "叠加层应铺满控件宽度。");
            Assert.AreEqual(control.Bounds.Height, overlay.Bounds.Height, 0.01, "叠加层应铺满控件高度。");

            window.Close();
        });
    }

    [TestMethod]
    public void CursorOverlay_RendersAtLeastOnce_WhenShown()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal();

            Assert.IsGreaterThan(0, control.BodyRenderCountForTest, "正文应至少渲染过一次。");
            Assert.IsGreaterThan(
                0,
                control.OverlayRenderCountForTest,
                "叠加层一次都没渲染 —— 说明它没被渲染器访问到,光标不会出现在屏幕上。");

            window.Close();
        });
    }

    [TestMethod]
    public void BlinkTick_RepaintsOverlayOnly_LeavingBodyUntouched()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal();
            int bodyBefore = control.BodyRenderCountForTest;
            int overlayBefore = control.OverlayRenderCountForTest;

            control.BlinkTick();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.IsGreaterThan(
                overlayBefore,
                control.OverlayRenderCountForTest,
                "闪烁应当重绘叠加层。");
            Assert.AreEqual(
                bodyBefore,
                control.BodyRenderCountForTest,
                "闪烁重绘了正文 —— 拆层的收益(不为一个格子重记录整屏)已经丢失。");

            window.Close();
        });
    }

    [TestMethod]
    public void InvalidateTerminal_RepaintsBodyAndOverlayTogether()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal();
            int bodyBefore = control.BodyRenderCountForTest;
            int overlayBefore = control.OverlayRenderCountForTest;

            control.InvalidateTerminal();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.IsGreaterThan(bodyBefore, control.BodyRenderCountForTest, "InvalidateTerminal 应重绘正文。");
            Assert.IsGreaterThan(
                overlayBefore,
                control.OverlayRenderCountForTest,
                "InvalidateTerminal 没有连带重绘叠加层 —— 光标会停在旧位置(输入时不跟手)。");

            window.Close();
        });
    }

    [TestMethod]
    public void Output_RepaintsOverlay_SoCursorFollowsText()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal();
            int overlayBefore = control.OverlayRenderCountForTest;

            // 走真实输出路径(而不是直接调 InvalidateTerminal):这条才是日常输入时走的链路,
            // 它必须把叠加层一起带上,否则光标会落后于刚打出来的字符。
            control.Feed(Encoding.UTF8.GetBytes("defgh"));
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.IsGreaterThan(
                overlayBefore,
                control.OverlayRenderCountForTest,
                "有新输出后叠加层没有重绘 —— 光标会停在输出前的位置。");

            window.Close();
        });
    }
}

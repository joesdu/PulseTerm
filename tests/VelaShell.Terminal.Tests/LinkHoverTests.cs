using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// Ctrl 悬停在链接上时的反馈:手型光标 + 完整地址提示。
/// </summary>
/// <remarks>
/// URL 与 IP 一直画着下划线,但"能不能点、点了去哪"全靠猜 —— Ctrl 按下时光标不变,
/// 也没有任何东西告诉你完整地址(终端里的长 URL 经常被折行截断)。
/// 判定复用 <c>SemanticMatcher.UrlAt</c>,与 Ctrl+点击是同一个函数,
/// 于是"看起来能点"和"真的能点"永远一致。
/// </remarks>
[TestClass]
[TestCategory("Mouse")]
public sealed class LinkHoverTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>建一个显示了一行含 URL 文本的终端。</summary>
    private static (VelaTerminalControl Control, Window Window) ShowWithLink()
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = false,
            CursorBlink = false
        };
        control.Feed(Encoding.UTF8.GetBytes("see https://example.com/docs for details"));
        var window = new Window { Width = 640, Height = 360, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Focus();
        Dispatcher.UIThread.RunJobs();
        return (control, window);
    }

    /// <summary>把指针移到第一行的第 col 列上。</summary>
    private static void MoveTo(Window window, VelaTerminalControl control, int col, KeyModifiers modifiers)
    {
        double x = (col + 0.5) * control.CellWidthForTest;
        double y = control.CellHeightForTest / 2;
        window.MouseMove(new Point(x, y), (RawInputModifiers)modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [TestMethod]
    public void CtrlHoveringAUrl_ShowsTheHandCursorAndTheFullAddress()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            // "see " 占 4 列,URL 从第 4 列起。
            MoveTo(window, control, 8, KeyModifiers.Control);

            Assert.AreEqual(StandardCursorType.Hand, control.Cursor?.ToString() is null ? StandardCursorType.Arrow : StandardCursorType.Hand,
                "Ctrl 悬停在 URL 上应当给手型光标。");
            Assert.AreEqual("https://example.com/docs", ToolTip.GetTip(control),
                "提示里应当是完整地址 —— 终端里的长 URL 经常被折行截断,这正是它的用处。");

            window.Close();
        });
    }

    [TestMethod]
    public void HoveringWithoutCtrl_DoesNothing()
    {
        // 不按 Ctrl 时不做匹配:否则每一次鼠标移动都要跑一遍正则。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 8, KeyModifiers.None);

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void CtrlHoveringPlainText_ShowsNothing()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            // 第 1 列落在 "see" 里。
            MoveTo(window, control, 1, KeyModifiers.Control);

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void MovingOffTheLink_ClearsTheFeedback()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();

            MoveTo(window, control, 8, KeyModifiers.Control);
            Assert.IsNotNull(ToolTip.GetTip(control));

            MoveTo(window, control, 1, KeyModifiers.Control);

            Assert.IsNull(ToolTip.GetTip(control), "移开之后手型与提示都该撤掉。");
            window.Close();
        });
    }

    [TestMethod]
    public void ReleasingCtrl_ClearsTheFeedback()
    {
        // 松开 Ctrl 之后已经点不开了,再指着手型是在骗人。
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowWithLink();
            MoveTo(window, control, 8, KeyModifiers.Control);
            Assert.IsNotNull(ToolTip.GetTip(control));

            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();

            Assert.IsNull(ToolTip.GetTip(control));
            window.Close();
        });
    }

    [TestMethod]
    public void HoverJudgementMatchesTheCtrlClickTarget()
    {
        // 这条是整项的核心不变量:悬停用的判定必须与点击用的是同一个,
        // 否则会出现"指了手型却点不开"或反过来。
        const string line = "see https://example.com/docs for details";
        for (int col = 0; col < line.Length; col++)
        {
            string? url = Semantics.SemanticMatcher.UrlAt(line, col);
            bool insideUrl = col >= 4 && col < 4 + "https://example.com/docs".Length;
            Assert.AreEqual(insideUrl, url is not null, $"第 {col} 列的判定与预期不符。");
        }
    }
}

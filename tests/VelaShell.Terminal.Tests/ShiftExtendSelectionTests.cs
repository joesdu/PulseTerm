using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// Shift+左键扩展选区(#266)。锚点必须在扩展时原地不动 —— 这正是该 issue 报的症状
/// (Shift+点击把锚点冲成点击处,旧选区凭空消失),因此全部用 headless 真事件端到端验证。
/// </summary>
[TestClass]
[TestCategory("ShiftExtendSelection")]
public sealed class ShiftExtendSelectionTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private const string Sample = "abcdefgh\r\nijklmnop\r\nqrstuvwx";

    [TestMethod]
    public void ShiftClick_ExtendsFromExistingAnchor_InsteadOfDroppingTheSelection()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 先常规拖出一小段选区,再在别处 Shift+点击。
                Drag(window, control, (0, 2), (0, 5));
                Assert.AreEqual("cde", control.GetSelectedText(), "前置拖拽选区应成立。");

                ShiftClick(window, control, (2, 5));

                Assert.AreEqual(
                    "cdefgh\nijklmnop\nqrstu",
                    control.GetSelectedText(),
                    "Shift+点击应保留原锚点(行0列2)并把游标挪到点击处。"
                );
            }
        );
    }

    [TestMethod]
    public void ShiftClick_ExtendsBackwards_WhenClickedBeforeTheAnchor()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (1, 2), (1, 5));
                ShiftClick(window, control, (0, 1));

                // 锚点(行1列2)在点击处之后:归一化后选区变成 (0,1) → (1,2)。
                Assert.AreEqual("bcdefgh\nij", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void ShiftDrag_AfterExtending_KeepsAdjustingTheSameAnchor()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 2), (0, 5));

                // Shift 按下后不松手继续拖:锚点仍不动,游标跟着走。
                window.MouseDown(CellPoint(control, 2, 5), MouseButton.Left, RawInputModifiers.Shift);
                window.MouseMove(CellPoint(control, 1, 4), RawInputModifiers.Shift);
                window.MouseUp(CellPoint(control, 1, 4), MouseButton.Left, RawInputModifiers.Shift);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual("cdefgh\nijkl", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void ShiftClick_KeepsBlockMode_TakenAtTheOriginalPress()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 2), (2, 5), RawInputModifiers.Alt);
                Assert.AreEqual("cde\nklm\nstu", control.GetSelectedText());

                // 扩展时不按 Alt 也不该退回线性选区:模式由第一次按下决定。
                ShiftClick(window, control, (2, 7));

                Assert.IsTrue(control.IsBlockSelection, "块选模式应沿用,Shift 不改写它。");
                Assert.AreEqual("cdefg\nklmno\nstuvw", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void ShiftClick_WithoutSelection_StartsANewSelection()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 没有锚点时 Shift+点击仍是"新建选区"——Shift 绕过应用鼠标上报的既有语义不能被夺走。
                Drag(window, control, (1, 1), (1, 4), RawInputModifiers.Shift);

                Assert.AreEqual("jkl", control.GetSelectedText());
            }
        );
    }

    /// <summary>在带 3 行样本文本的 headless 终端上跑一段交互。</summary>
    private static void RunOnTerminal(Action<Window, VelaTerminalControl> body) =>
        _session
            .Dispatch(
                () =>
                {
                    var control = new VelaTerminalControl
                    {
                        CopyOnSelect = false, // headless 下不去碰剪贴板
                    };
                    control.Feed(Encoding.ASCII.GetBytes(Sample));

                    var window = new Window
                    {
                        Width = 480,
                        Height = 320,
                        Content = control,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame(); // 填充屏幕行映射与单元格度量

                    body(window, control);
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

    private static void Drag(
        Window window,
        VelaTerminalControl control,
        (int Row, int Col) from,
        (int Row, int Col) to,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        window.MouseDown(CellPoint(control, from.Row, from.Col), MouseButton.Left, modifiers);
        window.MouseMove(CellPoint(control, to.Row, to.Col), modifiers);
        window.MouseUp(CellPoint(control, to.Row, to.Col), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ShiftClick(Window window, VelaTerminalControl control, (int Row, int Col) cell)
    {
        Point point = CellPoint(control, cell.Row, cell.Col);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1
        );
}

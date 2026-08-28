using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 不连续多段选区:Ctrl+Shift+左键拖拽再添一段,复制时各段按文档顺序拼接、段间断行
/// (「选第 1 行 + 第 3 行,一次复制得到这两行」)。段的合并、排序与"何时该从头选起"
/// 都在控件里,故全部走 headless 真事件。
/// </summary>
[TestClass]
[TestCategory("MultiRegionSelection")]
public sealed class MultiRegionSelectionTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession:每类各起一个时,拆除会互相踩)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    private const string Sample = "abcdefgh\r\nijklmnop\r\nqrstuvwx";

    [TestMethod]
    public void CtrlShiftDrag_AddsASecondRegion_AndCopiesBoth()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 0), (0, 8)); // 第 1 行整行
                Drag(window, control, (2, 0), (2, 8), Append); // 第 3 行整行

                Assert.AreEqual(
                    "abcdefgh\nqrstuvwx",
                    control.GetSelectedText(),
                    "两段都该在剪贴板里,中间那行不该被带上。"
                );
            }
        );
    }

    [TestMethod]
    public void Regions_AreCopiedTopDown_RegardlessOfThePickingOrder()
    {
        RunOnTerminal(
            (window, control) =>
            {
                // 先挑第 3 行、再回头挑第 1 行:复制出来仍要自上而下读。
                Drag(window, control, (2, 0), (2, 8));
                Drag(window, control, (0, 0), (0, 8), Append);

                Assert.AreEqual("abcdefgh\nqrstuvwx", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void PlainDrag_StartsOver_DiscardingEarlierRegions()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 0), (0, 8));
                Drag(window, control, (2, 0), (2, 8), Append);

                // 不带 Ctrl+Shift 的拖拽 = 从头选起。
                Drag(window, control, (1, 0), (1, 8));

                Assert.AreEqual("ijklmnop", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void ShiftClick_ExtendsOnlyTheLatestRegion_LeavingEarlierOnesIntact()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 0), (0, 3)); // "abc"
                Drag(window, control, (2, 0), (2, 3), Append); // "qrs"

                // #266 的扩展只作用在进行中那段上。
                window.MouseDown(CellPoint(control, 2, 8), MouseButton.Left, RawInputModifiers.Shift);
                window.MouseUp(CellPoint(control, 2, 8), MouseButton.Left, RawInputModifiers.Shift);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual("abc\nqrstuvwx", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void CtrlShiftAltDrag_AppendsARectangularRegion()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 0), (0, 3)); // 线性 "abc"
                Drag(window, control, (1, 2), (2, 5), Append | RawInputModifiers.Alt); // 块选

                // 每段各记各的模式:线性一段 + 矩形一段可以并存。
                Assert.AreEqual("abc\nklm\nstu", control.GetSelectedText());
            }
        );
    }

    [TestMethod]
    public void CtrlShiftDoubleClick_AddsAnotherWord()
    {
        RunOnTerminal(
            (window, control) =>
            {
                Drag(window, control, (0, 0), (0, 3)); // "abc"

                // 双击选词在 Ctrl+Shift 下是"再添一个词",不是重来。
                Point point = CellPoint(control, 2, 3);
                window.MouseDown(point, MouseButton.Left, Append);
                window.MouseUp(point, MouseButton.Left, Append);
                window.MouseDown(point, MouseButton.Left, Append);
                window.MouseUp(point, MouseButton.Left, Append);
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual("abc\nqrstuvwx", control.GetSelectedText());
            }
        );
    }

    private const RawInputModifiers Append =
        RawInputModifiers.Control | RawInputModifiers.Shift;

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

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1
        );
}

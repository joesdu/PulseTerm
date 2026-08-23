using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 宿主网格不得被主机流悄悄改小(issue #253:执行 <c>screen -R test</c> 后可选中的区域缩成 80 列,
/// 切一次标签才恢复)。这里用 headless 真控件把两层保护端到端锁住:
/// <list type="number">
///   <item>xterm 系 terminfo 的初始化串(<c>screen</c>/<c>tmux</c>/<c>tput init</c> 都会发)喂进去后,
///   列数、行宽与拖拽可选中的范围一格不变;</item>
///   <item>万一网格仍被绕过布局改掉,下一次输出更新的自愈闸会把它拉回当前布局,并把新尺寸补发给 PTY。</item>
/// </list>
/// 断言落在「拖出来的文本」上而不是内部字段:用户抱怨的正是选不到,行宽夹取(TerminalSelectionMath.RowSpan)
/// 才是症状的直接成因。
/// </summary>
[TestClass]
[TestCategory("TerminalGrid")]
public sealed class HostGridReconcileUiTests
{
    /// <summary>xterm-256color 的 <c>is2</c>:里面的 <c>ESC[?3l</c> 就是 #253 的扳机。</summary>
    private const string XtermInitString = "\x1b[!p\x1b[?3;4l\x1b[4l\x1b>";

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public void XtermInitString_DoesNotShrinkTheSelectableWidth()
    {
        OnUi(() =>
        {
            // 控件够宽,布局给出的列数必然远超 DECCOLM 的 80 —— 否则这条测试证明不了什么。
            (VelaTerminalControl control, Window window) = NewTerminal(1400, 400);
            int cols = control.Columns;
            Assert.IsGreaterThan(80, cols, "测试前提:布局列数必须多于 80,才谈得上被压缩。");

            string row = new('x', cols);
            control.Feed(Encoding.ASCII.GetBytes(row));
            control.Feed(Encoding.ASCII.GetBytes(XtermInitString));
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.AreEqual(cols, control.Columns, "初始化串不得改变网格列数。");
            Assert.AreEqual(row, DragRow0(control, window), "整行必须仍然可选中,而不是只剩前 80 列。");
        });
    }

    [TestMethod]
    public void GridChangedBehindTheHost_SnapsBackOnNextOutput()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = NewTerminal(1400, 400);
            int cols = control.Columns;
            int rows = control.Rows;

            (int Cols, int Rows)? pty = null;
            control.PtySizeChanged += (c, r) => pty = (c, r);

            // 模拟"有东西改了几何却没通知任何人"——#253 里的 DECCOLM 当初就是这样。
            control.DesyncGridForTest(80, rows);
            Assert.AreEqual(80, control.Columns);

            // 下一批输出到来时,自愈闸按当前布局把网格拉回去,并补发 PTY 尺寸。
            control.Feed(Encoding.ASCII.GetBytes("hello"));
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.AreEqual(cols, control.Columns, "网格应自愈回布局尺寸。");
            Assert.AreEqual(rows, control.Rows);
            Assert.AreEqual((cols, rows), pty, "远端 PTY 必须收到纠正后的尺寸,否则它的换行数学继续按错宽度算。");
        });
    }

    private static (VelaTerminalControl Control, Window Window) NewTerminal(int width, int height)
    {
        var control = new VelaTerminalControl
        {
            CopyOnSelect = false, // headless 下不去碰剪贴板
        };
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = control,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // 填充屏幕行映射与单元格度量
        return (control, window);
    }

    /// <summary>拖过第 0 行的整行宽度,返回选中的文本。</summary>
    private static string DragRow0(VelaTerminalControl control, Window window)
    {
        window.MouseDown(CellPoint(control, 0, 0), MouseButton.Left);
        window.MouseMove(CellPoint(control, 0, control.Columns));
        window.MouseUp(CellPoint(control, 0, control.Columns), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        return control.GetSelectedText();
    }

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1
        );

    private static void OnUi(Action body) =>
        _session
            .Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
}

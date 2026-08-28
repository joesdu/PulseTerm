using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 回归 #265:Ctrl+Shift+V 的多行粘贴确认框弹两次、粘贴两遍。
/// <para>
/// 根因是路由事件与 <c>async void</c> 的经典冲突:<see cref="VelaTerminalControl.OnKeyDown" />
/// 原本 <c>await PasteAsync()</c> 之后才置 <c>e.Handled</c>,而处理器一遇 await 就地返回 ——
/// 事件带着 <c>Handled == false</c> 继续冒泡到 <c>TerminalTabView.OnKeyDown</c>,那里又粘贴一次。
/// </para>
/// <para>
/// 因此断言落在「<b>RaiseEvent 同步返回时</b> Handled 是否已置位」以及「冒泡到父级时事件是否已被
/// 标记消费」——这正是双弹窗的充要条件,比数弹窗次数更直接也更稳。
/// </para>
/// </summary>
[TestClass]
[TestCategory("PasteShortcutUi")]
public class PasteShortcutUiTests
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

    private static KeyEventArgs KeyDown(Key key, KeyModifiers modifiers) =>
        new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        };

    /// <summary>
    /// 多行粘贴走确认框(会 await,必然让出),此时按键仍必须已被同步标记为已处理,
    /// 且冒泡到父级时父级看到的是「已消费」—— 否则父级会再粘贴一次(#265)。
    /// </summary>
    [TestMethod]
    public void CtrlShiftV_WithMultilineClipboard_IsHandledSynchronouslyAndDoesNotBubbleUnconsumed()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ConfirmMultilinePaste = true };
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            control.Focus();

            // 剪贴板必须真有多行文本,否则 PasteAsync 会提前 return、根本不 await,
            // 测试就会因为"压根没走到异步"而假绿。
            IClipboard clipboard = TopLevel.GetTopLevel(control)!.Clipboard!;
            Task set = clipboard.SetTextAsync("hostname\r\nwhoami\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(set.IsCompleted, "headless 剪贴板写入应同步完成。");

            // 确认框永不完成:保证 PasteAsync 一定在此让出控制权。
            var neverCompletes = new TaskCompletionSource<bool>();
            int confirmCalls = 0;
            control.MultilinePasteConfirmation = _ =>
            {
                confirmCalls++;
                return neverCompletes.Task;
            };

            // 父级探针:模拟 TerminalTabView 那层的回退处理(它只在事件未被消费时才粘贴)。
            int bubbledUnconsumed = 0;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, args) =>
                {
                    if (!args.Handled)
                    {
                        bubbledUnconsumed++;
                    }
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            KeyEventArgs key = KeyDown(Key.V, KeyModifiers.Control | KeyModifiers.Shift);
            control.RaiseEvent(key);

            Assert.IsTrue(key.Handled, "Ctrl+Shift+V 必须在处理器同步返回前就标记为已处理。");
            Assert.AreEqual(0, bubbledUnconsumed, "事件不得以未消费状态冒泡到父级,否则父级会再粘贴一次。");
            Assert.AreEqual(1, confirmCalls, "多行确认框只应弹一次。");

            neverCompletes.TrySetResult(false);
            Dispatcher.UIThread.RunJobs();
            window.Close();
        });
    }

    /// <summary>Shift+Insert 是同一条粘贴路径(经典 X11 惯例),同样不得漏标已处理。</summary>
    [TestMethod]
    public void ShiftInsert_TakesSamePastePath_AndIsHandledSynchronously()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl { ConfirmMultilinePaste = true };
            var window = new Window { Width = 480, Height = 320, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            control.Focus();

            Task set = TopLevel.GetTopLevel(control)!.Clipboard!.SetTextAsync("a\r\nb\r\n");
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(set.IsCompleted);

            var neverCompletes = new TaskCompletionSource<bool>();
            control.MultilinePasteConfirmation = _ => neverCompletes.Task;

            KeyEventArgs key = KeyDown(Key.Insert, KeyModifiers.Shift);
            control.RaiseEvent(key);

            Assert.IsTrue(key.Handled);

            neverCompletes.TrySetResult(false);
            Dispatcher.UIThread.RunJobs();
            window.Close();
        });
    }

    /// <summary>
    /// 结构守卫:<c>OnKeyDown</c> 不得是 <c>async</c>。
    /// <para>
    /// 上面两条行为测试只覆盖粘贴一条路径,复制路径(<c>await CopyAsync()</c>)在 headless 下
    /// 剪贴板写入是同步完成的、根本不让出,行为测试对它<b>测不出来</b>(真机上才异步)。与其留一条
    /// 永远绿的假测试,不如直接断言这个类的根本不变量:处理器一旦是 async,任何一条分支
    /// 只要 await,<c>e.Handled</c> 就必然置晚 —— #265 的整类 bug 由此杜绝。
    /// </para>
    /// </summary>
    [TestMethod]
    public void OnKeyDown_IsNotAsync_SoHandledCanNeverBeSetTooLate()
    {
        MethodInfo? onKeyDown = typeof(VelaTerminalControl).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(KeyEventArgs)]);

        Assert.IsNotNull(onKeyDown);
        Assert.IsNull(
            onKeyDown.GetCustomAttribute<AsyncStateMachineAttribute>(),
            "VelaTerminalControl.OnKeyDown 必须是同步方法:async 处理器一遇 await 就地返回,"
            + "事件会带着 Handled == false 继续冒泡,父级于是重复执行同一个快捷键(#265)。");
    }
}

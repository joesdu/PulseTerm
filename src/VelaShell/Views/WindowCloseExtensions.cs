using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Views;

/// <summary>
/// 在输入事件处理链(Click / OnKeyDown / Tapped、以及由按钮点击触发的命令订阅回调)里
/// 安全关闭窗口的助手。
/// </summary>
/// <remarks>
/// 同步 <see cref="Window.Close()" /> 会立刻销毁窗口的 PlatformImpl,而本轮手势/按键尚未
/// 走完的后续路由(PointerReleased、KeyUp 等)仍会派发到这个已销毁的窗口,Avalonia 便刷一条
/// <c>[Control] PlatformImpl is null, couldn't handle input</c>(输入被丢弃,无害但刷屏)。
/// 把 Close 推迟到当前输入事件出栈之后再执行,
/// 后续路由就落在仍存活的窗口上,警告消失。<see cref="Window.ShowDialog{TResult}" /> 的返回值
/// 照常透传(推迟只影响关闭的时刻,不影响结果)。
/// </remarks>
internal static class WindowCloseExtensions
{
    /// <summary>推迟到当前输入事件出栈后关闭窗口(无返回值)。</summary>
    public static void PostClose(this Window window) =>
        Dispatcher.UIThread.Post(window.Close);

    /// <summary>推迟到当前输入事件出栈后关闭窗口,并透传 <see cref="Window.ShowDialog{TResult}" /> 结果。</summary>
    public static void PostClose(this Window window, object? result) =>
        Dispatcher.UIThread.Post(() => window.Close(result));
}

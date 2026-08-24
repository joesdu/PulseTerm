using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Views;

/// <summary>
/// 自绘标题栏"按住拖动窗口"的统一入口:Windows 上自己走一遍移动模态循环,以免踩到
/// Avalonia 的 <see cref="Window.BeginMoveDrag" /> 留下的 (0,0) 幽灵指针(#264)。
/// </summary>
/// <remarks>
/// 【问题】Avalonia 12 的 Win32 <c>BeginMoveDrag</c> 是:<c>SendMessage(WM_SYSCOMMAND, SC_MOUSEMOVE)</c>
/// 进系统移动模态循环(该调用阻塞到松键为止),循环结束后再 <c>SendMessage(WM_LBUTTONUP, 0, 0)</c>
/// 给自己补一条弹起 —— 真正那次弹起被模态循环吃掉了,不补 Avalonia 的指针状态会停在"按下"。
/// 但补发的 <c>lParam = 0</c> 被照常解码成客户区 (0,0),于是 pointer-over 落到窗口左上角:
/// 自绘窗体那里压着 NorthWest 缩放抓取区(Cursor=TopLeftCorner),光标便闪一下对角双箭头,
/// 直到下一次真实鼠标移动才恢复(#264 截图);没有抓取区的对话框则在左上角留一块悬停高亮。
///
/// 【修法】照抄 Avalonia 那两步,只把补发弹起的坐标换成光标真实所在的客户区位置。
/// 语义不变(Avalonia 该收的弹起照收),只是位置从"窗口左上角"变成"鼠标当前位置"。
///
/// 【为什么不挂 WndProc 钩子改那条消息】试过,会打死窗口:<c>Win32Properties.AddWndProcHookCallback</c>
/// 是往同一个委托上 <c>+=</c>,而 <c>WindowImpl.WndProcMessageHandler</c> 取的是【最后一个回调】的
/// 返回值。再挂一个钩子就会把 <see cref="Win32WindowChrome" /> 给 WM_NCHITTEST 返回的
/// HTCLIENT/HTMAXBUTTON 覆盖成 0(HTNOWHERE),整窗对鼠标失聪 —— 拖不动、按钮也点不了。
/// 一个窗口只能有一个真正决定返回值的钩子,已经被窗口装饰占了。
/// </remarks>
internal static partial class WindowMoveDrag
{
    private const uint WM_SYSCOMMAND = 0x0112,
        WM_LBUTTONUP = 0x0202;

    /// <summary>SC_MOVE(0xF010) + 2:低位是命中码 HTCAPTION,即"鼠标发起的标题栏移动"。</summary>
    private const int SC_MOUSEMOVE = 0xF012;

    /// <summary>
    /// 开始拖动窗口。自绘标题栏一律走这里,不要直接调 <see cref="Window.BeginMoveDrag" />
    /// (原因见类型注释;<c>WindowMoveDragUsageTests</c> 会把这条约定钉死)。
    /// </summary>
    /// <param name="window">被拖动的窗口。</param>
    /// <param name="e">触发拖动的指针按下事件。</param>
    public static void BeginWindowMoveDrag(this Window window, PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(e);
        if (!OperatingSystem.IsWindows()
            || !e.Pointer.IsPrimary
            || window.TryGetPlatformHandle() is not { } handle)
        {
            window.BeginMoveDrag(e); // 非 Windows(或拿不到窗口句柄)照走平台实现
            return;
        }
        IntPtr hWnd = handle.Handle;
        e.Pointer.Capture(null);
        // 与 Avalonia 同样后置到派发队列:在输入处理栈里直接进模态循环会把这次派发一起卡住。
        Dispatcher.UIThread.Post(
            () =>
            {
                _ = SendMessage(hWnd, WM_SYSCOMMAND, SC_MOUSEMOVE, IntPtr.Zero); // 阻塞至松键
                if (IsWindow(hWnd))
                {
                    _ = SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, CursorLParam(hWnd));
                }
            },
            DispatcherPriority.Send
        );
    }

    /// <summary>把光标当前位置打包成鼠标消息的 lParam(客户区物理坐标,低 16 位 x / 高 16 位 y)。</summary>
    /// <remarks>取不到就退回 0 —— 与 Avalonia 原本的行为一致,不会更差。</remarks>
    private static IntPtr CursorLParam(IntPtr hWnd)
    {
        if (!GetCursorPos(out POINT cursor) || !ScreenToClient(hWnd, ref cursor))
        {
            return IntPtr.Zero;
        }
        return unchecked((IntPtr)(((cursor.Y & 0xFFFF) << 16) | (cursor.X & 0xFFFF)));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }
}

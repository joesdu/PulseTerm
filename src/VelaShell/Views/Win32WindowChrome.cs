using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 给 <c>WindowDecorations="None"</c> 的自绘窗体补回 DWM 框架语义(WindowChrome 手法)。
/// </summary>
/// <remarks>
/// 自绘窗体默认会丢掉 DWM 框架,表现为:没有阴影、Win11 下没有圆角、最小化/最大化没有动画、
/// 也没有悬停最大化按钮弹出的贴靠布局面板 —— 看上去就是一个"原生的方框窗口"。
///
/// 做法是补回 WS_CAPTION|WS_THICKFRAME|WS_MIN/MAXIMIZEBOX(经样式回调,防 Avalonia 每次
/// 重算窗口样式时清掉),再用 WM_NCCALCSIZE 让客户区占满整个窗口,于是框架语义还在、
/// 可视的系统标题栏和边框没了。WM_NCHITTEST 其余区域一律报 HTCLIENT,否则系统会按
/// WS_CAPTION 在顶部划出一条非客户带,吞掉自绘标题栏的输入(Avalonia 12 的 extend 模式踩过)。
///
/// 光有样式位还不够:WM_NCCALCSIZE 把非客户区裁到零之后,DWM 认为这个窗口没有可投影的框架,
/// 投影随之消失(#171)。必须再用非零 MARGINS 调 DwmExtendFrameIntoClientArea 把框架"借"回
/// 客户区,投影才回来 —— 客户区被 Avalonia 全幅不透明绘制盖住,借回来的那 1px 玻璃看不见。
/// Win11 的圆角再用 DWMWA_WINDOW_CORNER_PREFERENCE 显式钉一次,不依赖系统的默认推断。
///
/// 只适用于【不透明】窗体:透明窗体(TransparencyLevelHint=Transparent)会把借回的框架透出来,
/// 那类窗体照旧用自绘卡片的 BoxShadow(VelaShadowWindow 令牌)拿投影。
/// 新的自绘不透明窗体只要调用 <see cref="Attach" /> 即可获得同样的外观。
/// </remarks>
internal static partial class Win32WindowChrome
{
    private const int HTMAXBUTTON = 9;

    private const long StyleWsCaption = 0x00C00000,
        StyleWsThickFrame = 0x00040000,
        StyleWsMinimizeBox = 0x00020000,
        StyleWsMaximizeBox = 0x00010000;

    private static readonly HashSet<Window> Attached = [];

    /// <summary>
    /// 为窗口装上原生框架语义。仅在 Windows 上有效,其他平台是空操作;同一窗口重复调用只装一次
    /// (Opened 每次 Show 都会重发,不去重会让钩子越积越多)。
    /// </summary>
    /// <param name="window">目标窗口,须为 <c>WindowDecorations="None"</c>。</param>
    /// <param name="maximizeButton">
    /// 自绘的最大化按钮。传入后,鼠标悬停其上时会向系统报 HTMAXBUTTON,从而弹出 Win11 的
    /// 贴靠布局面板;为 null 则不提供该交互。
    /// </param>
    /// <param name="setMaximizeHover">按钮进入/离开非客户区悬停时的视觉反馈回调。</param>
    /// <param name="toggleMaximize">在按钮上完成点击时执行的最大化/还原动作。</param>
    public static void Attach(
        Window window,
        Func<Control?>? maximizeButton = null,
        Action<bool>? setMaximizeHover = null,
        Action? toggleMaximize = null
    )
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows() || !Attached.Add(window))
        {
            return;
        }
        window.Closed += (_, _) => Attached.Remove(window);

        Win32Properties.AddWindowStylesCallback(
            window,
            (style, exStyle) =>
                (
                    (uint)(
                        style
                        | StyleWsCaption
                        | StyleWsThickFrame
                        | StyleWsMinimizeBox
                        | StyleWsMaximizeBox
                    ),
                    exStyle
                )
        );
        Win32Properties.AddWndProcHookCallback(
            window,
            (hWnd, msg, wParam, lParam, ref handled) =>
                WndProc(
                    window,
                    hWnd,
                    msg,
                    wParam,
                    lParam,
                    ref handled,
                    maximizeButton,
                    setMaximizeHover,
                    toggleMaximize
                )
        );

        // 立即应用一次并触发 FRAMECHANGED,当前会话即刻生效。
        if (window.TryGetPlatformHandle() is { } handle)
        {
            const int GWL_STYLE = -16;
            const uint SWP_FRAMECHANGED = 0x0020,
                SWP_NOMOVE = 0x0002,
                SWP_NOSIZE = 0x0001,
                SWP_NOZORDER = 0x0004;
            long style = GetWindowLongPtrW(handle.Handle, GWL_STYLE);
            SetWindowLongPtrW(
                handle.Handle,
                GWL_STYLE,
                style | StyleWsCaption | StyleWsThickFrame | StyleWsMinimizeBox | StyleWsMaximizeBox
            );
            SetWindowPos(
                handle.Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER
            );
            ApplyDwmFrame(handle.Handle);
        }
    }

    /// <summary>
    /// 把 DWM 框架借 1px 回客户区(拿回投影),并在 Win11 上显式声明圆角。
    /// </summary>
    /// <remarks>
    /// 仅有 WS_THICKFRAME 而在 WM_NCCALCSIZE 里把非客户区裁到零,DWM 会判定此窗口无框架可投影,
    /// 于是不再合成投影 —— 表现为窗口与桌面/背后窗口糊在一起、分不清层级(#171)。
    /// DwmExtendFrameIntoClientArea 传非零 MARGINS 即可让 DWM 继续按有框架的窗口处理;
    /// 借回的 1px 落在客户区最外圈,被 Avalonia 的不透明绘制完全盖住,不产生可见的玻璃边。
    /// 两个调用都失败即忽略:拿不到投影只是观感回退,不该让窗口打不开。
    /// </remarks>
    private static void ApplyDwmFrame(IntPtr hWnd)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33,
            DWMWCP_ROUND = 2;
        var margins = new MARGINS
        {
            Left = 1,
            Right = 1,
            Top = 1,
            Bottom = 1,
        };
        _ = DwmExtendFrameIntoClientArea(hWnd, in margins);
        // 圆角属性 Win11(22000+)才认;更低版本会返回 E_INVALIDARG,直接不发。
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(
                hWnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                in corner,
                sizeof(int)
            );
        }
    }

    private static IntPtr WndProc(
        Window window,
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled,
        Func<Control?>? maximizeButton,
        Action<bool>? setMaximizeHover,
        Action? toggleMaximize
    )
    {
        const uint WM_NCCALCSIZE = 0x0083,
            WM_NCHITTEST = 0x0084,
            WM_NCMOUSELEAVE = 0x02A2,
            WM_NCLBUTTONDOWN = 0x00A1,
            WM_NCLBUTTONUP = 0x00A2;
        const int HTCLIENT = 1;
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                // 客户区占满窗口(去掉可视的系统标题/边框,保留 DWM 框架语义)。
                // 最大化时窗口矩形按惯例大出边框宽度,须裁回工作区,否则四周越屏。
                if (IsZoomed(hWnd))
                {
                    IntPtr monitor = MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                    var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                    if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
                    {
                        Marshal.StructureToPtr(info.Work, lParam, false);
                    }
                }
                handled = true;
                return IntPtr.Zero;
            case WM_NCHITTEST:
                if (IsPointOverMaximizeButton(window, maximizeButton?.Invoke(), lParam))
                {
                    setMaximizeHover?.Invoke(true);
                    handled = true;
                    return HTMAXBUTTON;
                }
                setMaximizeHover?.Invoke(false);
                // 其余全部按客户区处理:拖动/双击由自绘标题栏负责,缩放由自绘抓取区负责;
                // 不拦截会让 DefWindowProc 按 WS_CAPTION 在顶部划非客户带吞掉输入。
                handled = true;
                return HTCLIENT;
            case WM_NCMOUSELEAVE:
                setMaximizeHover?.Invoke(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                handled = true; // 吞掉按下,防 DefWindowProc 的历史行为
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                handled = true;
                setMaximizeHover?.Invoke(false);
                toggleMaximize?.Invoke();
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private static bool IsPointOverMaximizeButton(Window window, Control? button, IntPtr lParam)
    {
        if (button is not { IsVisible: true } || !button.IsAttachedToVisualTree())
        {
            return false;
        }
        // lParam:屏幕物理坐标(低 16 位 x / 高 16 位 y,有符号)。
        long packed = lParam.ToInt64();
        int screenX = unchecked((short)(packed & 0xFFFF));
        int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        PixelPoint topLeft = button.PointToScreen(new(0, 0));
        var rect = new PixelRect(
            topLeft,
            new PixelSize(
                (int)(button.Bounds.Width * window.RenderScaling),
                (int)(button.Bounds.Height * window.RenderScaling)
            )
        );
        return rect.Contains(new PixelPoint(screenX, screenY));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtrW(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial long SetWindowLongPtrW(IntPtr hWnd, int index, long value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, in MARGINS margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hWnd, int attribute, in int value, int size);

    /// <summary>DWM 的框架外扩量,字段顺序即 cxLeftWidth/cxRightWidth/cyTopHeight/cyBottomHeight。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left,
            Right,
            Top,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINRECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public WINRECT Monitor;
        public WINRECT Work;
        public uint Flags;
    }
}

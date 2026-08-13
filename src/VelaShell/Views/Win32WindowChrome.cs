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
/// 圆角要显式声明:WM_NCCALCSIZE 把非客户区裁到零之后,DWM 的默认策略(DWMWCP_DEFAULT =
/// 系统自行判断)对这种"没有可见框架"的窗口不保证圆角,还原、改尺寸时尤其容易掉回直角。
/// 用 DWMWA_WINDOW_CORNER_PREFERENCE 显式声明即可,但必须【跟着窗口状态走】:普通态圆角、
/// 最大化/全屏直角,见 <see cref="ApplyCornerPreference" />。
///
/// 【但别再加 DwmExtendFrameIntoClientArea】(#171 试过又撤掉):它能把 DWM 投影一并拿回来,
/// 代价却是主窗口启动时挂着一层要手动缩放一次才消散的显示效果,不划算,已按决定回退。
/// 两者别混为一谈 —— 圆角属性只影响窗口轮廓怎么裁,不参与合成,不产生那层残影。
/// 主窗口因此有圆角、没投影;次级窗体本来就不走这条路,它们是透明窗 + 自绘卡片的
/// BoxShadow(VelaShadowWindow 令牌),不受影响。
///
/// 主窗口与任务管理器共用这一套,新的自绘窗体只要调用 <see cref="Attach" /> 即可获得同样的外观。
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
            ApplyCornerPreference(window, handle.Handle);
        }
        // 兜底:WM_SIZE 里读到的 Avalonia 窗口状态可能还没跟上这一拍(还原时尤其明显),
        // 状态属性真正落定后再钉一次,两条路合起来才不会停在错的那一档。
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty
                && window.TryGetPlatformHandle() is { } current)
            {
                ApplyCornerPreference(window, current.Handle);
            }
        };
    }

    /// <summary>
    /// 按窗口当前状态钉 Win11 的圆角策略:普通态圆角,最大化/全屏直角。
    /// </summary>
    /// <remarks>
    /// 不设这个属性时 DWM 走 DWMWCP_DEFAULT —— 由系统判断该不该圆,而本窗口的非客户区已在
    /// WM_NCCALCSIZE 里裁到零,判断结果并不稳定:常见表现是改完尺寸之后变回直角。
    ///
    /// 但【不能无条件钉 ROUND】:最大化的窗口铺满工作区,圆角会在屏幕边缘啃出四个缺口。
    /// 启动即最大化时尤其明显 —— 窗口状态在 Show 之前就设好了(App 恢复上次的最大化),
    /// Attach 这一刻窗口已是最大化,无条件 ROUND 就把圆角钉在了铺满的窗口上,
    /// 而手动还原再最大化时系统真正走了一遍 zoom,反倒显得"第二次就正常了"。
    ///
    /// 判定同时看 Avalonia 的 WindowState 与 Win32 的 IsZoomed:两者在状态切换的瞬间
    /// 谁先更新并不确定,取并集才不会在中间态判错。
    /// 22000 以下不认这个属性,会返回 E_INVALIDARG,直接不发;失败也忽略 —— 圆角只是观感。
    /// </remarks>
    private static void ApplyCornerPreference(Window window, IntPtr hWnd)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33,
            DWMWCP_DONOTROUND = 1,
            DWMWCP_ROUND = 2;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }
        bool flat = window.WindowState is WindowState.Maximized or WindowState.FullScreen || IsZoomed(hWnd);
        int corner = flat ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, in corner, sizeof(int));
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
        const uint WM_SIZE = 0x0005,
            WM_NCCALCSIZE = 0x0083,
            WM_NCHITTEST = 0x0084,
            WM_NCMOUSELEAVE = 0x02A2,
            WM_NCLBUTTONDOWN = 0x00A1,
            WM_NCLBUTTONUP = 0x00A2;
        const int HTCLIENT = 1;
        switch (msg)
        {
            case WM_SIZE:
                // 每次尺寸变化都按当前状态重钉一次:退出最大化时 DWM 不一定把圆角还回来
                // (反馈:窗口一缩小就变直角),进入最大化又必须收掉圆角,免得铺满的窗口
                // 在屏幕四角啃出缺口。幂等且极廉价,不设 handled,尺寸消息照常交给 Avalonia。
                ApplyCornerPreference(window, hWnd);
                break;
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
    private static partial int DwmSetWindowAttribute(IntPtr hWnd, int attribute, in int value, int size);

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

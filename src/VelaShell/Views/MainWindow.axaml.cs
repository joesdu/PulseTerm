using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.Services.ZModem;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>应用主窗口:自绘无边框标题栏、侧边栏与终端主区的宿主,统筹连接、设置、会话恢复与关闭链路。</summary>
public partial class MainWindow : Window
{
    private IDisposable? _fileBrowserVisibilitySub;
    private bool _forceClose;
    private bool _confirmationInProgress;
    private bool _standaloneSftpShutdownInProgress;
    private bool _standaloneSftpShutdownComplete;
    private bool _openedInitialized;
    private bool _snapLayoutsInitialized;

    /// <summary>自绘缩放抓取区:普通状态按下即进入原生缩放;最大化时整层隐藏(见 OnPropertyChanged)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && sender is Border { Tag: string tag } && Enum.TryParse(tag, out WindowEdge edge)
        )
        {
            BeginResizeDrag(edge, e);
        }
    }

    /// <summary>响应窗口属性变化:窗口状态切换时,按是否普通态显隐自绘缩放抓取区。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 最大化/全屏时缩放抓取区必须让位(否则挡住屏幕边缘 5px 的标题栏与状态栏点击)。
        if (change.Property == WindowStateProperty && this.FindControl<Panel>("ResizeGrips") is { } grips)
        {
            grips.IsVisible = WindowState == WindowState.Normal;
        }
        // 最小化(含隐入托盘)时暂停状态栏的每秒 SSH 探测与周期 ICMP,恢复时重启。
        if (change.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            vm.SetStatusPollingSuspended(WindowState == WindowState.Minimized);
        }
    }

    /// <summary>
    /// 面板重新打开时恢复的文件区高度(§6 拖拽放大)。默认 360
    /// = 侧边栏最近连接块(320) + 底栏(40),使文件区顶部分隔条
    /// 与侧边栏的树/最近连接分隔条处于同一水平线上。
    /// </summary>
    private double _lastFileRowHeight = 360;

    private AppSettings? _settings;

    private ISettingsService? _settingsService;
    private bool _sidebarOnRight;

    // 注意:窗口的 DataContext 必须在构造之后(在 App 的对象初始化器中)再赋值:
    // 若过早设置,子视图的编译期绑定(x:DataType = 各自 VM)会在 InitializeComponent 期间
    // 短暂看到继承来的 MainWindowViewModel,从而在各自本地 DataContext 绑定接管前
    // 喷出一连串 InvalidCastException 绑定错误。
    // 代价是 Layout 仍为空时 DockControl 主题绑定($self.Layout.* 带 FallbackValue)会
    // 输出少量良性的“Value is null”消息——这点噪声尚可接受。
    /// <summary>创建主窗口,挂接侧边栏事件、文件浏览可见性联动与窗口打开回调(Windows 下额外注册贴靠布局钩子)。</summary>
    public MainWindow()
    {
        InitializeComponent();
        if (this.FindControl<SidebarView>("SidebarHost") is { } sidebar)
        {
            sidebar.OpenConnectionProfileRequested += OnOpenConnectionProfileRequested;
            sidebar.RecentConnectRequested += OnSidebarRecentConnectRequested;
            sidebar.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
        }
        DataContextChanged += (_, _) => HookFileBrowserVisibility();
        // 主题(暗/亮)切换时,按新主题色重建背景令牌覆盖画刷。否则之前设的覆盖仍持旧主题色、
        // 一直 shadow 掉换主题后的令牌值,导致终端/SFTP/侧栏等背景停在旧色,须再动一次滑杆才同步。
        ActualThemeVariantChanged += (_, _) =>
            ApplyBackgroundOpacities(_lastImageOpacity, _lastContentOpacity);
        Opened += OnWindowOpened;
        if (OperatingSystem.IsWindows())
        {
            Opened += (_, _) => SetupSnapLayouts();
        }
    }

    // ---- 原生窗口效果(WindowChrome 手法) --------------------------------------
    // WindowDecorations="None" 的自绘窗体默认失去 DWM 框架语义。这里补回
    // WS_CAPTION|WS_THICKFRAME|WS_MIN/MAXIMIZEBOX(样式回调,防 Avalonia 重算时
    // 清掉),再用 WM_NCCALCSIZE 让客户区占满窗口(无可视系统标题/边框)——
    // 由此找回:DWM 阴影、Win11 圆角、最小化/最大化动画、悬停最大化按钮的
    // 贴靠布局面板(还需 WM_NCHITTEST 对按钮报 HTMAXBUTTON)。
    // WM_NCHITTEST 其余区域强制 HTCLIENT,防止系统按 WS_CAPTION 在顶部划出
    // 非客户带吞掉自绘标题栏的输入(Avalonia 12 extend 模式踩过的坑)。

    private const int HTMAXBUTTON = 9;

    private TitleBarView? TitleBar => this.FindControl<TitleBarView>("TitleBarHost");

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

    private const long StyleWsCaption = 0x00C00000,
        StyleWsThickFrame = 0x00040000,
        StyleWsMinimizeBox = 0x00020000,
        StyleWsMaximizeBox = 0x00010000;

    private void SetupSnapLayouts()
    {
        // 只装一次:Opened 每次 Show() 都会重发(从托盘重开亦然),否则每次重开都会再追加一份
        // 样式回调与 WndProc 钩子,越积越多、白白重复处理每条窗口消息。
        if (_snapLayoutsInitialized)
        {
            return;
        }
        _snapLayoutsInitialized = true;
        // 样式回调:Avalonia 每次重算窗口样式后追加 DWM 框架位。
        Win32Properties.AddWindowStylesCallback(
            this,
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
        Win32Properties.AddWndProcHookCallback(this, SnapLayoutsWndProc);

        // 立即应用一次并触发 FRAMECHANGED,当前会话即刻生效。
        if (TryGetPlatformHandle() is { } handle)
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
        }
    }

    private IntPtr SnapLayoutsWndProc(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled
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
                    IntPtr monitor = MonitorFromWindow(
                        hWnd,
                        2 /* MONITOR_DEFAULTTONEAREST */
                    );
                    var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                    if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
                    {
                        Marshal.StructureToPtr(info.Work, lParam, false);
                    }
                }
                handled = true;
                return IntPtr.Zero;
            case WM_NCHITTEST:
                if (IsPointOverMaximizeButton(lParam))
                {
                    TitleBar?.SetMaximizeNcHover(true);
                    handled = true;
                    return HTMAXBUTTON;
                }
                TitleBar?.SetMaximizeNcHover(false);
                // 其余全部按客户区处理:拖动/双击由自绘标题栏负责,缩放由自绘抓取区负责;
                // 不拦截会让 DefWindowProc 按 WS_CAPTION 在顶部划非客户带吞掉输入。
                handled = true;
                return HTCLIENT;
            case WM_NCMOUSELEAVE:
                TitleBar?.SetMaximizeNcHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                handled = true; // 吞掉按下,防 DefWindowProc 的历史行为
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                handled = true;
                TitleBar?.SetMaximizeNcHover(false);
                TitleBar?.ToggleMaximize();
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private bool IsPointOverMaximizeButton(IntPtr lParam)
    {
        if (TitleBar?.MaximizeButtonControl is not { IsVisible: true } button || !button.IsAttachedToVisualTree())
        {
            return false;
        }
        // lParam:屏幕物理坐标(低 16 位 x / 高 16 位 y,有符号)。
        long packed = lParam.ToInt64();
        int screenX = unchecked((short)(packed & 0xFFFF));
        int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        PixelPoint topLeft = button.PointToScreen(new Point(0, 0));
        var rect = new PixelRect(
            topLeft,
            new PixelSize(
                (int)(button.Bounds.Width * RenderScaling),
                (int)(button.Bounds.Height * RenderScaling)
            )
        );
        return rect.Contains(new PixelPoint(screenX, screenY));
    }

    /// <summary>
    /// 随 FileBrowser.IsVisible 切换折叠/展开文件区行。WhenAnyValue
    /// 直接跟踪 FileBrowser 属性本身,因此每个标签重绑定时会自动重新订阅。
    /// </summary>
    private void HookFileBrowserVisibility()
    {
        _fileBrowserVisibilitySub?.Dispose();
        _fileBrowserVisibilitySub = null;
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        _fileBrowserVisibilitySub = vm.WhenAnyValue(x => x.FileBrowser.IsVisible)
            .Subscribe(visible => Dispatcher.UIThread.Post(() => SetFileRowsVisible(visible)));
    }

    private void SetFileRowsVisible(bool visible)
    {
        if (this.FindControl<Grid>("MainAreaGrid") is not { RowDefinitions.Count: >= 3 } grid)
        {
            return;
        }
        RowDefinition splitterRow = grid.RowDefinitions[1];
        RowDefinition fileRow = grid.RowDefinitions[2];
        if (visible)
        {
            splitterRow.Height = new(5);
            fileRow.MinHeight = 120;
            fileRow.Height = new(Math.Max(_lastFileRowHeight, 120));
        }
        else
        {
            // 记住用户拖出的高度,以便重新打开时恢复。
            if (fileRow.Height is { IsAbsolute: true, Value: > 0 })
            {
                _lastFileRowHeight = fileRow.Height.Value;
            }
            fileRow.MinHeight = 0;
            fileRow.Height = new(0);
            splitterRow.Height = new(0);
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        // Avalonia 的 Window 每次 Show() 都会重发 Opened(Hide() 清 _shown,再 Show 即再触发)。
        // 开启“关闭时最小化到托盘”后,从托盘重新显示走的正是 Show(),若在此重复接线,
        // tree.ConnectRequested 等订阅会逐次累积 —— 双击连接便按重开次数成倍连接(重开 N 次连 N 个)。
        // 事件接线与会话初始化只应在窗口生命周期内做一次,故此处早退幂等。
        if (_openedInitialized)
        {
            return;
        }
        _openedInitialized = true;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TerminalSearchRequested += OnTerminalSearchRequested;
            vm.TerminalFocusRequested += (_, _) => FocusActiveTerminal(vm);
            vm.NewConnectionRequested += (_, _) => _ = OpenProfileDialogAsync(null);
            vm.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
            vm.InteractiveAuthenticator = PromptCredentialsAsync;
            vm.MultilinePasteConfirmer = ConfirmMultilinePasteAsync;
            vm.ZModemDownloadFolderPicker = PromptForZModemDownloadFolderAsync;
            vm.ZModemUploadFilePicker = PromptForZModemUploadFilesAsync;
            vm.ExportBufferRequested += (_, _) => _ = ExportTerminalBufferAsync(vm);
            // 工具菜单“连接诊断”:对当前标签的配置打开诊断中心(设计 RGXg1)。
            vm.DiagnosticsRequested += profile =>
                Dispatcher.UIThread.Post(() => _ = OpenDiagnosticsDialogAsync(profile));

            // 资源管理器树:右键连接/双击连接 + 右键编辑。
            if (vm.Sidebar.SessionTree is { } tree)
            {
                tree.ConnectRequested += profile =>
                    Dispatcher.UIThread.Post(() => SafeFireAndForget(() => vm.TryConnectProfileAsync(profile)));
                tree.EditRequested += profile =>
                    Dispatcher.UIThread.Post(() => _ = OpenProfileDialogAsync(profile));

                // 打开 SFTP:先连接(已连接则新开标签),随后展开文件浏览面板。
                tree.OpenSftpRequested += profile =>
                    Dispatcher.UIThread.Post(() => SafeFireAndForget(() => vm.OpenSftpForProfileAsync(profile)));

                // 端口转发:打开隧道管理面板并预选该服务器(全局非模态,见 fuXS7);
                // 无需先建立终端会话,面板会在创建隧道时后台自动连接。
                tree.PortForwardRequested += profile =>
                    Dispatcher.UIThread.Post(() => SafeFireAndForget(() => { vm.OpenTunnelPanel(profile); return Task.CompletedTask; }));

                // 连接诊断:对选中的配置打开诊断中心(设计 RGXg1)。
                tree.DiagnoseRequested += profile =>
                    Dispatcher.UIThread.Post(() => SafeFireAndForget(() => OpenDiagnosticsDialogAsync(profile)));

                // 断开连接:断开该配置所有已连接的终端标签(保留缓冲以便重连)。
                // 必须按 Profile.Id 匹配——tab.SessionId 是 SSH 连接会话 ID,与配置 ID
                // 不是一回事,之前用它比较永远匹配不上,菜单点了没反应(#2)。
                tree.DisconnectRequested += profile =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (
                            TerminalTabViewModel tab in vm
                                .TabBar.Tabs.OfType<TerminalTabViewModel>()
                                .Where(t =>
                                    t.Profile?.Id == profile.Id
                                    && t.ConnectionStatus == SessionStatus.Connected
                                )
                                .ToList()
                        )
                        {
                            tab.DisconnectCommand.Execute().Subscribe();
                        }
                    });
            }
            await vm.InitializeAsync();
        }

        // 外观/行为设置:启动时应用一次,设置保存后热更新。
        if (Application.Current is App { Services: { } services } && services.GetService<ISettingsService>() is { } settingsService)
        {
            _settingsService = settingsService;
            settingsService.SettingsSaved += OnSettingsSavedForWindow;
            Closed += (_, _) => settingsService.SettingsSaved -= OnSettingsSavedForWindow;

            // 外观即时预览(未持久化):同样应用窗口外观,但不覆盖 _settings(已保存状态)。
            if (services.GetService<ISettingsPreviewService>() is { } previewService)
            {
                previewService.PreviewRequested += OnSettingsPreviewedForWindow;
                Closed += (_, _) => previewService.PreviewRequested -= OnSettingsPreviewedForWindow;
                previewService.WindowOpacityPreviewRequested += OnSettingsOpacityPreviewedForWindow;
                Closed += (_, _) =>
                    previewService.WindowOpacityPreviewRequested -= OnSettingsOpacityPreviewedForWindow;
                previewService.BackgroundOpacityPreviewRequested += OnBackgroundOpacityPreviewedForWindow;
                Closed += (_, _) =>
                    previewService.BackgroundOpacityPreviewRequested -= OnBackgroundOpacityPreviewedForWindow;
            }
            try
            {
                _settings = await settingsService.GetSettingsAsync();
                ApplyWindowAppearance(_settings);
            }
            catch
            {
                // 设置读取失败不影响窗口本身。
            }
            await RestoreSessionsAsync(_settings);
        }
    }

    /// <summary>
    /// 恢复会话(设置 → 常规 → 启动):重连上次退出时在线的连接。缺凭据的
    /// 配置会走既有的登录验证弹窗;单个失败不影响其余会话。
    /// </summary>
    private async Task RestoreSessionsAsync(AppSettings? settings)
    {
        if (
            settings?.General.RestoreSessionsOnStartup != true
            || settings.General.LastOpenProfileIds.Count == 0
            || DataContext is not MainWindowViewModel vm
            || Application.Current is not App { Services: { } services }
            || services.GetService<ISessionRepository>() is not { } repository
        )
        {
            return;
        }
        foreach (Guid profileId in settings.General.LastOpenProfileIds.Distinct().ToList())
        {
            try
            {
                SessionProfile? profile = await repository.GetSessionAsync(profileId);
                if (profile is not null)
                {
                    await vm.TryConnectProfileAsync(profile);
                }
            }
            catch
            {
                // 配置已删除或连接失败:跳过,继续恢复其余会话。
            }
        }
    }

    private void OnSettingsSavedForWindow(AppSettings settings) =>
        Dispatcher.UIThread.Post(() =>
        {
            _settings = settings;
            ApplyWindowAppearance(settings);
        });

    private void OnSettingsPreviewedForWindow(AppSettings settings) =>
        RunOnUiThread(() => ApplyWindowAppearance(settings));

    private void OnSettingsOpacityPreviewedForWindow(int percent) =>
        RunOnUiThread(() => ApplyWindowOpacity(percent));

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        Dispatcher.UIThread.Post(action);
    }

    /// <summary>应用设置 → 外观:窗口透明度、侧边栏位置、界面字体/字号。
    /// (菜单栏显隐设置已随文字菜单一并移除:自绘标题栏承载窗口控制按钮,必须常显。)</summary>
    private void ApplyWindowAppearance(AppSettings settings)
    {
        AppearanceOptions a = settings.Appearance;
        ApplyWindowOpacity(a.WindowOpacityPercent);
        ApplySidebarPosition(a.SidebarPosition == "right");
        ApplyBackgroundImage(a);
        if (Application.Current is not { } app)
        {
            return;
        }
        // 界面字体:覆盖 VelaUiFont 令牌(空或默认 Inter 时还原主题字体);
        // App 级 :is(Window) 样式让所有窗口继承,未显式指定 FontFamily 的文本统一换字体。
        string uiFont = a.UiFont.Trim();
        if (string.IsNullOrEmpty(uiFont) || string.Equals(uiFont, "Inter", StringComparison.OrdinalIgnoreCase))
        {
            app.Resources.Remove("VelaUiFont");
        }
        else
        {
            app.Resources["VelaUiFont"] = new FontFamily($"{uiFont}, Segoe UI, Microsoft YaHei, sans-serif");
        }

        // 界面字号:覆盖 VelaUiFontSize 令牌(同上,全窗口继承);同时覆盖 Fluent 的
        // ControlContentThemeFontSize,让内置控件(按钮/输入框/下拉等)一起缩放。
        double uiFontSize = Math.Clamp(a.UiFontSize, 9, 24);
        app.Resources["VelaUiFontSize"] = uiFontSize;
        app.Resources["ControlContentThemeFontSize"] = uiFontSize;
    }

    private Bitmap? _backgroundBitmap;
    private string? _loadedBackgroundPath;
    // 最近一次应用的不透明度:主题切换时需按新主题色重建 scrim,故缓存以便重放。
    private int _lastImageOpacity = 100, _lastContentOpacity = 85;

    // 单一整窗 scrim 方案:背景图铺最底层,其上叠一层【半透明遮罩 scrim】(主题底色,整窗一层),
    // 再上面所有内容背景令牌全部置【透明】,于是终端/SFTP/侧栏/面板一律透出 scrim+图片。
    // 因全透明,任意嵌套容器都不会叠加(透明×N 仍透明),彻底消除"多层叠加致某区域偏暗"的问题。
    // scrim 底色 = 主题 page 色(与 VelaShellTokens 两套变体一致,改那边记得同步)。
    private static readonly Color ScrimDark = Color.Parse("#191A21");
    private static readonly Color ScrimLight = Color.Parse("#F2EDDA");

    // 有背景图时置全透明、让 scrim+图片透出的内容背景令牌(仅本窗口)。
    // VelaBgSurface(弹层/对话框/卡片)、VelaBgInput(输入框)刻意不在其列,保持不透明以保证可读性。
    private static readonly string[] ContentTokenKeys =
    [
        "VelaBgPage", "VelaBgSidebar", "VelaBgTerminal", "VelaBgSftpPanel", "VelaBgDockDocument",
    ];

    // 内容区的小块「强调底」(选项卡标题、选中项、悬停、输入框/下拉、表头):不能全透明(会丢失可辨识度),
    // 而是有背景图时压成【半透明】,既保留强调作用又与背景图融合,不再突兀。颜色须与 VelaShellTokens 两套变体一致;
    // Fraction = 保留的不透明比例(越低越透)。VelaBgSurface 不在其列(弹层/对话框/下拉浮层需不透明保证可读),
    // 内容区表头改用专门的 VelaBgContentHeader。
    private static readonly (string Key, Color Dark, Color Light, double Fraction)[] AccentTranslucentTokens =
    [
        ("VelaTabActiveBg", Color.Parse("#282A36"), Color.Parse("#FFFBEB"), 0.75),
        ("VelaTabInactiveBg", Color.Parse("#191A21"), Color.Parse("#EBE5CC"), 0.45),
        ("VelaBgActive", Color.Parse("#44475A"), Color.Parse("#644AC922"), 0.65),
        ("VelaBgHover", Color.Parse("#363948"), Color.Parse("#EDE7D0"), 0.5),
        ("VelaBgInput", Color.Parse("#282A36"), Color.Parse("#F7F2DF"), 0.7),
        ("VelaBgContentHeader", Color.Parse("#343746"), Color.Parse("#FFFBEB"), 0.6),
    ];

    /// <summary>
    /// 应用背景图片(设置 → 外观 → 背景图片):装配窗口最底层的 <c>BackgroundImageLayer</c>。
    /// 仅在【路径变化】时(重新)解码图片,避免拖动不透明度滑杆时反复读盘;不透明度由
    /// <see cref="ApplyBackgroundOpacities" /> 单独装配(可被即时预览直接调用)。
    /// </summary>
    private void ApplyBackgroundImage(AppearanceOptions a)
    {
        string path = a.BackgroundImagePath?.Trim() ?? "";
        if (!string.Equals(path, _loadedBackgroundPath, StringComparison.Ordinal))
        {
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    _backgroundBitmap = new Bitmap(path);
                }
                catch
                {
                    _backgroundBitmap = null; // 解码失败:当作未设置,不打断启动/预览。
                }
            }
            _loadedBackgroundPath = path;
            if (this.FindControl<Image>("BackgroundImageLayer") is { } layer)
            {
                layer.Source = _backgroundBitmap;
                layer.IsVisible = _backgroundBitmap is not null;
            }
        }
        ApplyBackgroundOpacities(a.BackgroundImageOpacity, a.ContentBackgroundOpacity);
    }

    /// <summary>
    /// 装配背景图相关的不透明度(即时预览与保存共用,不涉及图片解码,故可高频调用):图片图层不透明度、
    /// scrim 遮罩不透明度、以及把内容背景令牌整体置透明(仅本窗口,弹层/对话框/输入框不受影响)。
    /// 无背景图时隐藏 scrim、移除令牌覆盖、终端填充还原为不透明,完全恢复旧行为。
    /// </summary>
    private void ApplyBackgroundOpacities(int imageOpacity, int contentOpacity)
    {
        (_lastImageOpacity, _lastContentOpacity) = (imageOpacity, contentOpacity);
        bool active = _backgroundBitmap is not null;
        if (this.FindControl<Image>("BackgroundImageLayer") is { } layer)
        {
            layer.Opacity = Math.Clamp(imageOpacity, 0, 100) / 100.0;
        }

        if (this.FindControl<Border>("BackgroundScrim") is { } scrim)
        {
            Color baseColor = ActualThemeVariant == ThemeVariant.Light ? ScrimLight : ScrimDark;
            scrim.Background = new SolidColorBrush(baseColor);
            scrim.Opacity = Math.Clamp(contentOpacity, 0, 100) / 100.0; // 遮罩越不透明,越盖住背景图
            scrim.IsVisible = active;
        }

        foreach (string key in ContentTokenKeys)
        {
            if (active)
            {
                Resources[key] = Brushes.Transparent;
            }
            else
            {
                Resources.Remove(key);
            }
        }

        // 强调底压成半透明(而非全透明):选项卡标题、选中项、悬停、输入框、内容表头等,融入背景图不再突兀。
        bool light = ActualThemeVariant == ThemeVariant.Light;
        foreach ((string key, Color dark, Color lightColor, double fraction) in AccentTranslucentTokens)
        {
            if (active)
            {
                Color c = light ? lightColor : dark;
                Resources[key] = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * fraction), c.R, c.G, c.B));
            }
            else
            {
                Resources.Remove(key);
            }
        }

        // 终端控件自绘填充:有图时置全透明(不画背景),透出 scrim+背景图;无图时恒不透明(=1),行为不变。
        // 终端文字与彩色单元格底(选区/彩色输出)不受影响,照常绘制,不伤可读性。
        (DataContext as MainWindowViewModel)?.ApplyTerminalBackgroundOpacityToAllTabs(active ? 0.0 : 1.0);
    }

    /// <summary>背景图/内容背景不透明度的即时预览(拖动滑杆):只调不透明度,不重新解码图片。</summary>
    private void OnBackgroundOpacityPreviewedForWindow((int Image, int Content) v) =>
        RunOnUiThread(() => ApplyBackgroundOpacities(v.Image, v.Content));

    private void ApplyWindowOpacity(int percent) => Opacity = Math.Clamp(percent, 10, 100) / 100.0;

    /// <summary>侧边栏位置(设置 → 外观):交换侧边栏与主区所在列,分隔条留在中间。</summary>
    private void ApplySidebarPosition(bool right)
    {
        if (right == _sidebarOnRight)
        {
            return;
        }
        if (
            this.FindControl<SidebarView>("SidebarHost") is not { } sidebar
            || this.FindControl<Grid>("MainAreaGrid") is not { } main
            || sidebar.Parent is not Grid contentGrid
            || contentGrid.ColumnDefinitions.Count < 3
        )
        {
            return;
        }
        _sidebarOnRight = right;
        ColumnDefinitions cols = contentGrid.ColumnDefinitions;
        int sidebarCol = right ? 2 : 0;
        int mainCol = right ? 0 : 2;

        // 保留用户拖出来的侧边栏宽度。
        GridLength sidebarWidth = cols[right ? 0 : 2].Width;
        if (!sidebarWidth.IsAbsolute)
        {
            sidebarWidth = new(260);
        }
        cols[sidebarCol].Width = sidebarWidth;
        cols[sidebarCol].MinWidth = 180;
        cols[sidebarCol].MaxWidth = 520;
        cols[mainCol].Width = new(1, GridUnitType.Star);
        cols[mainCol].MinWidth = 400;
        cols[mainCol].MaxWidth = double.PositiveInfinity;
        Grid.SetColumn(sidebar, sidebarCol);
        Grid.SetColumn(main, mainCol);
    }

    /// <summary>托盘“退出”/关闭确认后的真正退出:跳过托盘拦截与确认弹窗。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        // 推迟到当前事件出栈后再关:本方法常在对话框的关闭延续(ShowDialog 的 await 续体)、
        // 托盘菜单回调或独立 SFTP 收尾的 finally 里被同步调用。若此刻直接 Close(),会在别的
        // 窗口的 windowWillClose: 通知栈内嵌套关闭本窗口 —— macOS 上 AppKit 隐藏窗口时会回调
        // firstRectForCharacterRange: 到已拆卸的终端 IME 视图,触发 EXC_BAD_ACCESS(崩溃
        // EF96F409,0x18 空指针)。用 Post 断开嵌套,让每个窗口在各自干净的栈上关闭。
        this.PostClose();
    }

    /// <summary>
    /// 关闭链路(设置 → 常规):最小化到托盘 → 关闭前确认 → 记住窗口状态。
    /// 系统关机/应用程序退出(CloseReason ≠ 用户点关闭)不拦截。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        AppSettings? settings = _settings;
        bool userInitiated = e.CloseReason == WindowCloseReason.WindowClosing;
        if (!_forceClose && userInitiated && settings is not null)
        {
            if (settings.General.MinimizeToTray && Application.Current is App app && HasActiveTrayIcon(app))
            {
                e.Cancel = true;
                Hide();
                base.OnClosing(e);
                return;
            }
            if (settings.General.ConfirmBeforeClose && HasConnectedSessions())
            {
                e.Cancel = true;
                if (!_confirmationInProgress)
                {
                    _confirmationInProgress = true;
                    _ = ConfirmCloseAsync();
                }
                base.OnClosing(e);
                return;
            }
        }
        PersistWindowBounds(settings);
        if (
            !_standaloneSftpShutdownComplete
            && DataContext is MainWindowViewModel vm
            && vm.HasPendingStandaloneSftpDocuments()
        )
        {
            e.Cancel = true;
            if (!_standaloneSftpShutdownInProgress)
            {
                _standaloneSftpShutdownInProgress = true;
                _ = CloseStandaloneSftpDocumentsAndRetryAsync(vm);
            }
            base.OnClosing(e);
            return;
        }
        EndTextInputSessionBeforeClose();
        base.OnClosing(e);
    }

    /// <summary>
    /// 提交关闭前(macOS)主动清除键盘焦点,结束终端的原生输入法会话。
    /// 终端是一个 IME 文本输入客户端(<c>TextInputMethodClientRequestedEvent</c>);窗口一旦关闭并被
    /// AppKit 隐藏,系统的光标跟踪器(<c>TUINSCursorUIController</c>)会经 KVO 回调
    /// <c>-[AvnView firstRectForCharacterRange:]</c> 查询已拆卸的视图 —— libAvaloniaNative 未做空判,
    /// 触发 EXC_BAD_ACCESS(崩溃 EF96F409)。<c>Focus(null)</c> 把焦点移出终端,促使输入法管理器
    /// SetClient(null)、在原生隐藏前重置 macOS 输入上下文,系统便无客户端可查询。
    /// 仅 macOS 需要;其他平台无此原生路径,跳过以免改动焦点行为。
    /// </summary>
    private void EndTextInputSessionBeforeClose()
    {
        if (OperatingSystem.IsMacOS())
        {
            FocusManager?.Focus(null);
        }
    }

    private async Task CloseStandaloneSftpDocumentsAndRetryAsync(MainWindowViewModel vm)
    {
        try
        {
            await vm.CloseStandaloneSftpDocumentsAsync();
        }
        catch
        {
            // 逐文档清理通过 VM 报告预期失败;如有未处理的聚合异常逃逸,保持窗口关闭路径安全。
        }
        finally
        {
            _standaloneSftpShutdownComplete = true;
            _standaloneSftpShutdownInProgress = false;
            ForceClose();
        }
    }

    private static bool HasActiveTrayIcon(App app) => app.TrayIconActive;

    private bool HasConnectedSessions() =>
        DataContext is MainWindowViewModel vm
        && (
            vm.TabBar.Tabs.OfType<TerminalTabViewModel>().Any(t => t.IsConnected)
            || vm.Layout.AllDocuments().OfType<SftpDocument>().Any()
        );

    private async Task ConfirmCloseAsync()
    {
        try
        {
            bool confirmed = await MessageDialog.ConfirmAsync(
                this,
                Strings.Get("Main_CloseConfirmTitle"),
                Strings.Get("Main_CloseConfirmBody")
            );
            if (confirmed)
            {
                ForceClose();
            }
        }
        finally
        {
            _confirmationInProgress = false;
        }
    }

    /// <summary>
    /// 退出时的状态记忆:窗口尺寸/最大化(启动时窗口状态 = 记住上次)与
    /// 已连接会话的配置 id(恢复会话)。同步等待,本地写入很快。
    /// </summary>
    private void PersistWindowBounds(AppSettings? settings)
    {
        if (DataContext is MainWindowViewModel sidebarStateViewModel)
        {
            try
            {
                sidebarStateViewModel.PersistSidebarStateAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 侧栏布局保存失败不阻塞窗口关闭。
            }
        }
        if (_settingsService is null || settings is null)
        {
            return;
        }
        bool rememberWindow = settings.Appearance.StartupWindowState == "remember";
        bool rememberSessions = settings.General.RestoreSessionsOnStartup;
        if (!rememberWindow && !rememberSessions)
        {
            return;
        }
        try
        {
            if (rememberWindow)
            {
                settings.Appearance.LastWindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    settings.Appearance.LastWindowWidth = Width;
                    settings.Appearance.LastWindowHeight = Height;
                }
            }
            if (rememberSessions && DataContext is MainWindowViewModel vm)
            {
                settings.General.LastOpenProfileIds =
                [
                    .. vm.TabBar.Tabs
                        .OfType<TerminalTabViewModel>()
                        .Where(t => t is { IsConnected: true, Profile: { } p } && p.Id != Guid.Empty)
                        .Select(t => t.Profile!.Id)
                        .Concat(
                            vm.Layout
                                .AllDocuments()
                                .OfType<SftpDocument>()
                                .Select(document => document.ViewModel.Profile.Id)
                                .Where(id => id != Guid.Empty)
                        )
                        .Distinct(),
                ];
            }
            _settingsService.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            // 记忆退出状态失败不阻塞退出。
        }
    }

    /// <summary>菜单/命令面板内的终端查找 → 打开当前可见终端视图的搜索栏。</summary>
    private void OnTerminalSearchRequested(object? sender, EventArgs e)
    {
        foreach (TerminalTabView view in this.GetVisualDescendants().OfType<TerminalTabView>())
        {
            if (view.IsEffectivelyVisible)
            {
                view.OpenSearch();
                return;
            }
        }
    }

    private void FocusActiveTerminal(MainWindowViewModel viewModel)
    {
        foreach (TerminalTabView view in this.GetVisualDescendants().OfType<TerminalTabView>())
        {
            if (view.IsEffectivelyVisible && ReferenceEquals(view.DataContext, viewModel.ActiveTerminalTab))
            {
                view.FocusTerminal();
                return;
            }
        }
    }

    // 按设计规范 §2,窗口使用操作系统原生标题栏——不做自绘标题栏。

    private void OnSidebarRecentConnectRequested(object? sender, RecentConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // 连接失败已在标签页内以覆盖层提示(设计 yxjmg),不再弹全局框。
        _ = vm.TryConnectRecentAsync(entry);
    }

    private void OnOpenConnectionProfileRequested(object? sender, EventArgs e) => _ = OpenProfileDialogAsync(null);

    /// <summary>打开“新建连接”弹窗;传入 existing 时为编辑既有配置。</summary>
    private async Task OpenProfileDialogAsync(SessionProfile? existing)
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        if (Application.Current is not App app || app.Services is null)
        {
            return;
        }

        // 新建连接的默认端口与默认密钥(设置 → 常规 / 密钥管理)。
        int defaultPort = _settings?.DefaultPort ?? 22;
        string? defaultKeyPath = null;
        if (_settings?.Keys.DefaultKeyName is { Length: > 0 } keyName)
        {
            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh",
                keyName
            );
            if (File.Exists(candidate))
            {
                defaultKeyPath = candidate;
            }
        }
        var connectionProfileViewModel = new ConnectionProfileViewModel(
            existing,
            app.Services.GetService<IConnectionWorkflowService>(),
            app.Services.GetService<ISessionRepository>(),
            defaultPort,
            defaultKeyPath
        );
        var dialog = new ConnectionProfileView { DataContext = connectionProfileViewModel };
        SessionProfile? profile = await dialog.ShowDialog<SessionProfile?>(this);
        if (profile is null)
        {
            return;
        }

        // 保存/连接均已持久化配置 —— 资源管理器树同步刷新。
        await mainWindowViewModel.RefreshSessionTreeAsync();

        // 仅“连接”按钮触发实际连接;“保存”只落库。
        if (!connectionProfileViewModel.ConnectAfterClose)
        {
            return;
        }

        // TryConnectProfileAsync 永不抛异常 —— 连接失败已在标签页内以覆盖层提示(设计 yxjmg),
        // 不再弹全局框。
        await mainWindowViewModel.TryConnectProfileAsync(profile);
    }

    /// <summary>打开连接诊断中心(设计 RGXg1):打开即自动执行一轮四步检测。</summary>
    private async Task OpenDiagnosticsDialogAsync(SessionProfile profile)
    {
        if (Application.Current is not App app || app.Services?.GetService<IConnectionDiagnosticsService>() is not { } diagnosticsService)
        {
            return;
        }
        var dialog = new ConnectionDiagnosticsView
        {
            DataContext = new ConnectionDiagnosticsViewModel(profile, diagnosticsService),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>打开设置窗口(设计 §14):DI 单例 VM,打开时重新加载持久化设置。</summary>
    private async Task OpenSettingsAsync()
    {
        if (Application.Current is not App app || app.Services?.GetService<SettingsViewModel>() is not { } settingsViewModel)
        {
            return;
        }
        await settingsViewModel.LoadCommand.Execute();
        var dialog = new SettingsView { DataContext = settingsViewModel };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// 登录验证流程(设计:身份验证 第1步/第2步):补全用户名与认证凭据。
    /// 已信任主机显示其指纹;首次连接提示握手时记录(TOFU)。取消返回 null。
    /// </summary>
    private async Task<SessionProfile?> PromptCredentialsAsync(SessionProfile profile)
    {
        string? knownFingerprint = null;
        if (Application.Current is App app && app.Services?.GetService<IHostKeyService>() is { } hostKeys)
        {
            try
            {
                List<KnownHost> hosts = await hostKeys.GetKnownHostsAsync();
                knownFingerprint = hosts
                    .FirstOrDefault(h => h.Host == profile.Host && h.Port == profile.Port)
                    ?.Fingerprint;
            }
            catch
            {
                // 指纹仅用于展示,读取失败不阻塞验证流程。
            }
        }
        var viewModel = new AuthenticationDialogViewModel(
            profile.Host,
            profile.Port,
            profile.Username,
            knownFingerprint,
            profile.AuthMethod
        );
        var dialog = new AuthenticationDialogView { DataContext = viewModel };
        AuthenticationResult? result = await dialog.ShowDialog<AuthenticationResult?>(this);
        if (result is null)
        {
            return null;
        }
        profile.Username = result.Username;
        profile.AuthMethod = result.AuthMethod;
        if (result.AuthMethod == AuthMethod.Password)
        {
            // 交接点:SecureString → 管线所需的明文,随即释放 SecureString。
            using (result.Password)
            {
                profile.Password = SecureStringConvert.ToPlaintext(result.Password);
            }
            profile.RememberPassword = result.RememberPassword;
        }
        else
        {
            profile.PrivateKeyPath = result.PrivateKeyPath;
            profile.PrivateKeyPassphrase = result.PrivateKeyPassphrase;
        }
        return profile;
    }

    /// <summary>导出终端输出(§12.4):有选区导出选区,否则导出整个缓冲区(scrollback+屏幕)。</summary>
    private async Task ExportTerminalBufferAsync(MainWindowViewModel vm)
    {
        (string Text, string SuggestedFileName)? export = vm.GetActiveTerminalExport();
        if (export is null)
        {
            return;
        }
        (string text, string suggestedName) = export.Value;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(
            new()
            {
                Title = Strings.Get("Main_ExportTerminalTitle"),
                SuggestedFileName = suggestedName,
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new(Strings.Get("Main_FileTypeText")) { Patterns = ["*.txt"] },
                    new(Strings.Get("Main_FileTypeLog")) { Patterns = ["*.log"] },
                ],
            }
        );
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, text);
            vm.StatusBar.Status = Strings.Format("Main_TerminalExported", path);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(
                this,
                Strings.Get("Main_ExportFailed"),
                ex.Message,
                MessageDialogKind.Error
            );
        }
    }

    /// <summary>
    /// ZMODEM 下载目录选择(视图层):后台接收线程调用时编组到 UI 线程,
    /// 弹出原生文件夹选择框。返回所选本地目录的绝对路径;用户取消则返回 null。
    /// </summary>
    private Task<string?> PromptForZModemDownloadFolderAsync(ZModemFolderPromptRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            TopLevel? top = GetTopLevel(this);
            if (top?.StorageProvider is not { } storage)
            {
                return null;
            }
            IStorageFolder? start = null;
            try
            {
                if (Directory.Exists(request.SuggestedDirectory))
                {
                    start = await storage.TryGetFolderFromPathAsync(request.SuggestedDirectory);
                }
            }
            catch
            {
                // 起始目录解析失败无关紧要。
            }
            string title;
            if (request.IsRetryAfterCancel)
            {
                // 二次弹窗:标题提示这是防误触的最后机会,再次取消即中止。
                title = Strings.Get("ZModem_ChooseDownloadFolderRetry");
            }
            else
            {
                title = string.IsNullOrEmpty(request.FirstFileName)
                    ? Strings.Get("ZModem_ChooseDownloadFolder")
                    : Strings.Format("ZModem_ChooseDownloadFolderFor", request.FirstFileName);
            }
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new()
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        });
    }

    /// <summary>
    /// ZMODEM 上传文件选择(视图层):远端跑 <c>rz</c> 时,后台发送线程调用本方法编组到 UI 线程,
    /// 弹出原生多选文件框。返回所选本地文件的绝对路径清单;用户取消则返回空清单。
    /// </summary>
    /// <param name="isRetryAfterCancel">是否为首次取消后的二次弹窗(标题提示再次取消即中止)。</param>
    /// <param name="cancellationToken"></param>
    private Task<IReadOnlyList<string>> PromptForZModemUploadFilesAsync(bool isRetryAfterCancel, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Dispatcher.UIThread.InvokeAsync<IReadOnlyList<string>>(async () =>
        {
            TopLevel? top = GetTopLevel(this);
            if (top?.StorageProvider is not { } storage)
            {
                return [];
            }
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new()
            {
                Title = isRetryAfterCancel
                    ? Strings.Get("ZModem_ChooseUploadFilesRetry")
                    : Strings.Get("ZModem_ChooseUploadFiles"),
                AllowMultiple = true
            });
            List<string> paths = [];
            foreach (IStorageFile file in files)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    paths.Add(path);
                }
            }
            return paths;
        });
    }

    /// <summary>
    /// 多行粘贴确认(设置 → 终端 → 粘贴时确认多行内容):预览前几行,防止把
    /// 整段脚本误粘进 shell 直接执行。
    /// </summary>
    private Task<bool> ConfirmMultilinePasteAsync(string text)
    {
        string[] lines = text.Split('\n');
        IEnumerable<string> previewLines = lines.Take(5).Select(l => l.TrimEnd('\r'));
        string preview = string.Join('\n', previewLines);
        if (lines.Length > 5)
        {
            preview += "\n…";
        }
        return MessageDialog.ConfirmAsync(
            this,
            Strings.Get("Main_PasteMultilineTitle"),
            Strings.Format("Main_PasteMultilineBody", lines.Length, preview)
        );
    }

    /// <summary>
    /// 安全的 fire-and-forget 包装:捕获取消与同步异常,防止未观察的任务异常或
    /// 同步参数校验失败导致应用崩溃。异步异常(网络失败等)由各方法的 try/catch 自行处理。
    /// </summary>
    private static async void SafeFireAndForget(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户取消 / 会话取消:正常事件,不记录。
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] Unhandled fire-and-forget error: {ex}");
        }
    }
}

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
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Processes;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Security;
using VelaShell.Services;
using VelaShell.Services.FileTransfer;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>应用主窗口:自绘无边框标题栏、侧边栏与终端主区的宿主,统筹连接、设置、会话恢复与关闭链路。</summary>
public partial class MainWindow : Window
{
    /// <summary>任务管理器窗口尺寸在文档存储里的键。</summary>
    private const string ProcessManagerLayoutKey = "processManager";

    /// <summary>链路追踪窗口尺寸在文档存储里的键。</summary>
    private const string TraceRouteLayoutKey = "traceRoute";

    /// <summary>资源监视窗口尺寸在文档存储里的键。</summary>
    private const string ResourceMonitorLayoutKey = "resourceMonitor";

    /// <summary>每个会话至多一扇任务管理器窗口,按会话标识去重。</summary>
    private readonly Dictionary<Guid, ProcessManagerView> _processManagers = [];

    /// <summary>每个会话至多一扇资源监视窗口,按会话标识去重。</summary>
    private readonly Dictionary<Guid, ResourceMonitorWindow> _resourceMonitors = [];

    /// <summary>每个追踪目标至多一扇窗口,按目标主机去重。</summary>
    private readonly Dictionary<string, TraceRouteWindow> _traceWindows = [with(StringComparer.OrdinalIgnoreCase)];

    private IDisposable? _fileBrowserVisibilitySub;
    private IDisposable? _sidebarCollapsedSub;
    private bool _forceClose;
    private bool _confirmationInProgress;
    private bool _standaloneSftpShutdownInProgress;
    private bool _standaloneSftpShutdownComplete;
    private bool _openedInitialized;

    /// <summary>
    /// 自绘缩放抓取区:普通状态按下即进入原生缩放;最大化时整层隐藏(见 OnPropertyChanged)。
    /// 只认左键:BeginResizeDrag 在 Win32 上是伪造一条 WM_NCLBUTTONDOWN 进系统 sizing 模态
    /// 循环,而该循环只在左键弹起时退出。用右/中键起手会让循环永远等不到那次弹起,
    /// 表现为松开按键后窗口仍一直跟着光标改尺寸(#116)。标题栏拖动同此守卫。
    /// </summary>
    private void ResizeEdge_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
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

    /// <summary>折叠态侧栏的图标细条宽度(与 SidebarView 的 CollapsedRail 一致)。</summary>
    private const double SidebarRailWidth = 40;

    /// <summary>展开态侧栏的最小宽度,与 MainWindow.axaml 的 ColumnDefinition 保持一致。</summary>
    private const double SidebarMinWidth = 180;

    /// <summary>
    /// 侧栏展开时的宽度。**必须存在字段里**:折叠后列宽是 40,此时若去设置里切换
    /// 侧栏左右位置,ApplySidebarPosition 从列上读到的就是 40,展开回来会缩成细条。
    /// </summary>
    private double _lastSidebarWidth = 260;

    private bool _sidebarCollapsed;

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
            sidebar.PluginsRequested += (_, _) => OpenPluginManager();
            sidebar.ImportSessionsRequested += (_, _) => _ = OpenSessionImportDialogAsync();
        }
        DataContextChanged += (_, _) =>
        {
            HookFileBrowserVisibility();
            HookSidebarCollapsed();
        };
        // 主题(暗/亮)切换时,按新主题色重建背景令牌覆盖画刷。否则之前设的覆盖仍持旧主题色、
        // 一直 shadow 掉换主题后的令牌值,导致终端/SFTP/侧栏等背景停在旧色,须再动一次滑杆才同步。
        ActualThemeVariantChanged += (_, _) =>
            ApplyBackgroundOpacities(_lastImageOpacity, _lastContentOpacity);
        Opened += OnWindowOpened;
        Opened += (_, _) =>
            Win32WindowChrome.Attach(
                this,
                () => TitleBar?.MaximizeButtonControl,
                hover => TitleBar?.SetMaximizeNcHover(hover),
                () => TitleBar?.ToggleMaximize()
            );
    }

    // ---- 原生窗口效果 ----------------------------------------------------------
    // 自绘窗体的 DWM 框架语义(阴影、Win11 圆角、最大化动画、贴靠布局面板)统一由
    // Win32WindowChrome 负责,任务管理器等其他自绘窗体共用同一套,外观才一致。

    private TitleBarView? TitleBar => this.FindControl<TitleBarView>("TitleBarHost");

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

    /// <summary>
    /// 随 Sidebar.IsCollapsed 收放侧栏列宽。与 <see cref="HookFileBrowserVisibility" /> 同一套写法:
    /// WhenAnyValue 跟踪 Sidebar 属性本身,侧栏视图模型整体替换时会自动重新订阅。
    /// </summary>
    private void HookSidebarCollapsed()
    {
        _sidebarCollapsedSub?.Dispose();
        _sidebarCollapsedSub = null;
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        _sidebarCollapsedSub = vm.WhenAnyValue(x => x.Sidebar.IsCollapsed)
            .Subscribe(collapsed => Dispatcher.UIThread.Post(() => SetSidebarCollapsed(collapsed)));
    }

    /// <summary>
    /// 把侧栏列在「展开宽度」与「40px 图标细条」之间切换,并连带收起拖拽条 ——
    /// 细条没有可调的宽度,留着分隔条只会让人拖出一个既不是细条也不是侧栏的中间态。
    /// 侧栏里显示哪一副面孔由 SidebarView 自己按同一个状态位决定。
    /// </summary>
    private void SetSidebarCollapsed(bool collapsed)
    {
        if (
            this.FindControl<SidebarView>("SidebarHost") is not { Parent: Grid contentGrid }
            || contentGrid.ColumnDefinitions.Count < 3
        )
        {
            return;
        }
        _sidebarCollapsed = collapsed;
        ColumnDefinitions cols = contentGrid.ColumnDefinitions;
        ColumnDefinition sidebarCol = cols[_sidebarOnRight ? 2 : 0];
        if (collapsed)
        {
            // 记住用户拖出来的宽度,以便展开时恢复(与文件区 _lastFileRowHeight 同一处置)。
            if (sidebarCol.Width is { IsAbsolute: true } width && width.Value >= SidebarMinWidth)
            {
                _lastSidebarWidth = width.Value;
            }
            // MinWidth 必须先归零:它是 180,不清掉的话 40 的宽度会被顶回 180。
            sidebarCol.MinWidth = 0;
            sidebarCol.Width = new(SidebarRailWidth);
            cols[1].Width = new(0);
        }
        else
        {
            sidebarCol.MinWidth = SidebarMinWidth;
            sidebarCol.Width = new(_lastSidebarWidth);
            cols[1].Width = new(5);
        }
        SidebarSplitterLine.IsVisible = !collapsed;
        SidebarSplitter.IsVisible = !collapsed;
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
            vm.SettingsSectionRequested += (_, section) => _ = OpenSettingsAsync(section);
            vm.ExternalUrlRequested += (_, url) => _ = OpenExternalUrlAsync(url);
            vm.InteractiveAuthenticator = PromptCredentialsAsync;
            // FTPS 与插件协议的 TLS 端点共用同一套「先拒绝 → 提示 → 记指纹后重连」的信任流程,
            // 因此也共用同一个对话框;两者的异常类型不同,这里各接一个薄适配。
            vm.FtpCertificateTrustPrompt = (profile, certificate) => PromptCertificateTrustAsync(
                profile.Host, certificate.Subject, certificate.Issuer,
                certificate.ExpiresOn, certificate.Thumbprint, certificate.PolicyErrors);
            vm.PluginCertificateTrustPrompt = (profile, certificate) => PromptCertificateTrustAsync(
                profile.Host, certificate.Subject, certificate.Issuer,
                certificate.ExpiresOn, certificate.Thumbprint, certificate.PolicyErrors);
            // 插件提议一条连接(如 Redis 插件从 SSH 会话里探到一个实例):
            // 打开宿主自己的「新建连接」对话框并预填。**插件不能自己写会话库** ——
            // 那是用户数据、凭据也在里面;它只能提议,由用户过一眼再按保存。
            if (Application.Current is App proposalApp
                && proposalApp.Services?.GetService<Infrastructure.Plugins.Protocols.PluginProtocolRegistry>()
                    is { } proposalRegistry)
            {
                proposalRegistry.ConnectionProposalHandler = ProposeConnectionAsync;
            }
            vm.MultilinePasteConfirmer = ConfirmMultilinePasteAsync;
            vm.TransferDownloadFolderPicker = PromptForTransferDownloadFolderAsync;
            vm.TransferUploadFilePicker = PromptForTransferUploadFilesAsync;
            vm.ExportBufferRequested += (_, _) => _ = ExportTerminalBufferAsync(vm);
            // 工具菜单“连接诊断”:对当前标签的配置打开诊断中心(设计 RGXg1)。
            vm.DiagnosticsRequested += profile =>
                Dispatcher.UIThread.Post(() => _ = OpenDiagnosticsDialogAsync(profile));
            // 标题栏“任务管理器”:每个会话最多一扇窗,非模态。
            vm.ProcessManagerRequested += (sessionId, label) =>
                Dispatcher.UIThread.Post(() => _ = OpenProcessManagerAsync(sessionId, label));

            vm.ResourceMonitorRequested += (sessionId, label) =>
                Dispatcher.UIThread.Post(() => _ = OpenResourceMonitorAsync(sessionId, label));
            // 标题栏"链路追踪":每个目标最多一扇窗,非模态。
            vm.TraceRouteRequested += (host, label) =>
                Dispatcher.UIThread.Post(() => _ = OpenTraceRouteAsync(vm, host, label));

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

                // 删除分组会连带删掉组内全部连接,必须先确认(红色危险按钮)。
                tree.ConfirmDeleteGroup = ConfirmDeleteGroupAsync;
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

        // 先按记录顺序把配置取齐(本地文档库,命中缓存,很快)。顺序在这里定死,
        // 下面并发发起时标签仍按这个次序建出来:TryConnectProfileAsync 在建标签之前
        // 只 await 一次设置快照,而设置此刻已在缓存里、同步返回,不会打乱先后。
        var profiles = new List<SessionProfile>(settings.General.LastOpenProfileIds.Count);
        foreach (Guid profileId in settings.General.LastOpenProfileIds.Distinct())
        {
            try
            {
                if (await repository.GetSessionAsync(profileId) is { } profile)
                {
                    profiles.Add(profile);
                }
            }
            catch
            {
                // 配置已删除或读不出来:跳过,继续恢复其余会话。
            }
        }

        // 握手一次性全部发起,不再一个一个等(#118)。整条链路(TCP、认证、开 shell 通道)
        // 都是真异步 API,底层 SshConnectionService 本就支持并发,串行 await 只是把总耗时
        // 白白叠成各会话之和 —— N 个会话就要等 N 倍。并发后总耗时≈最慢的那一个。
        // 缺凭据的配置仍会弹登录框,但弹窗由 PromptCredentialsAsync 的闸门串行化,
        // 不会同时叠出多扇模态框。
        await Task.WhenAll(
            profiles
                .Select(async profile =>
                {
                    try
                    {
                        await vm.TryConnectProfileAsync(profile);
                    }
                    catch
                    {
                        // 连接失败已在标签页内以覆盖层提示(设计 yxjmg);
                        // 这里只保证一个会话的失败不会掀掉其余会话的恢复。
                    }
                })
                .ToList()
        );
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
        if (Application.Current is { } app)
        {
            ApplyUiFontTokens(app, a);
        }
    }

    /// <summary>
    /// 把设置 → 外观 → 界面字体/字号下发成应用级令牌。界面上的字体与字号【只】经由令牌
    /// 取值(axaml 里不写字面量),所以这一处覆盖就是"界面字体/字号"这两个选项的全部实现。
    /// </summary>
    internal static void ApplyUiFontTokens(Application app, AppearanceOptions a)
    {
        // 界面字体:覆盖 VelaUiFont(比例)与 VelaUiMonoFont(界面里按列对齐的等宽部分)
        // 两个令牌 —— 界面上的文字要么用前者要么用后者,一起换才谈得上"界面字体"生效。
        // 空或默认 Inter 时移除覆盖,还原令牌默认值(比例 Inter + 等宽 Cascadia Mono)。
        // App 级 :is(Window) 样式让所有窗口继承,终端画面自身不受影响(它吃终端设置)。
        string uiFont = a.UiFont.Trim();
        if (string.IsNullOrEmpty(uiFont) || string.Equals(uiFont, "Inter", StringComparison.OrdinalIgnoreCase))
        {
            app.Resources.Remove("VelaUiFont");
            app.Resources.Remove("VelaUiMonoFont");
        }
        else
        {
            var family = new FontFamily($"{uiFont}, Segoe UI, Microsoft YaHei, sans-serif");
            app.Resources["VelaUiFont"] = family;
            app.Resources["VelaUiMonoFont"] = family;
        }

        // 界面字号:覆盖 VelaUiFontSize 令牌(同上,全窗口继承);同时覆盖 Fluent 的
        // ControlContentThemeFontSize,让内置控件(按钮/输入框/下拉等)一起缩放。
        double uiFontSize = Math.Clamp(a.UiFontSize, MinUiFontSize, MaxUiFontSize);
        app.Resources["VelaUiFontSize"] = uiFontSize;
        app.Resources["ControlContentThemeFontSize"] = uiFontSize;

        // 整套字号阶梯按 基准/13 等比缩放:界面各处不再写死字号,层级关系也不会因缩放走形。
        foreach (double step in FontSizeSteps)
        {
            app.Resources[$"VelaFontSize{step:0}"] = ScaleFontSize(step, uiFontSize);
        }
        // 说明文字固定为基准字号的 85%(不参与等比阶梯,见 SettingsView 的 row-desc)。
        app.Resources["VelaFontSizeDesc"] = ScaleFontSize(BaseUiFontSize * DescFontSizeRatio, uiFontSize);
    }

    /// <summary>界面字号的取值范围(与设置页 NumericUpDown 的 Minimum/Maximum 一致)。</summary>
    private const double MinUiFontSize = 9, MaxUiFontSize = 24;

    /// <summary>设计基准字号:字号阶梯令牌名里的数字就是这个基准下的磅值。</summary>
    private const double BaseUiFontSize = 13;

    /// <summary>设置页说明文字相对基准字号的比例。</summary>
    private const double DescFontSizeRatio = 0.85;

    /// <summary>缩放后的字号下限:再小就糊了,基准取最小值时给小号字兜个底。</summary>
    private const double MinScaledFontSize = 6;

    /// <summary>VelaTokens.axaml 里定义的字号阶梯(= 基准 13 下的磅值)。</summary>
    private static readonly double[] FontSizeSteps = [8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20];

    /// <summary>把基准 13 下的设计字号换算到用户设定的基准上,取整以免落在半像素上。</summary>
    private static double ScaleFontSize(double designSize, double uiFontSize) =>
        Math.Max(MinScaledFontSize, Math.Round(designSize * uiFontSize / BaseUiFontSize));

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
        // 亮色是 #AARRGGBB:强调色 13% 淡底。写成 #644AC922 会解析成一片绿(见 VelaShellTokens)。
        ("VelaBgActive", Color.Parse("#44475A"), Color.Parse("#22644AC9"), 0.65),
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

        // 保留用户拖出来的侧边栏宽度。**只认展开态的列宽**:折叠时列宽是 40(细条),
        // 拿它当"用户宽度"记下来,展开回去侧栏就缩成一条 40px 的残条了。
        GridLength sidebarWidth = cols[right ? 0 : 2].Width;
        if (!_sidebarCollapsed && sidebarWidth is { IsAbsolute: true } dragged && dragged.Value >= SidebarMinWidth)
        {
            _lastSidebarWidth = dragged.Value;
        }
        cols[sidebarCol].Width = new(_sidebarCollapsed ? SidebarRailWidth : _lastSidebarWidth);
        cols[sidebarCol].MinWidth = _sidebarCollapsed ? 0 : SidebarMinWidth;
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

    /// <summary>
    /// 打开「导入会话」弹窗:对话框自身会自动扫描全部已注册来源(Xshell / WinSCP …)并智能勾选;
    /// 导入成功后刷新资源管理器树。
    /// </summary>
    private async Task OpenSessionImportDialogAsync()
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        if (Application.Current is not App app || app.Services is not { } services)
        {
            return;
        }
        List<ISessionImportService> importServices = [.. services.GetServices<ISessionImportService>()];
        if (importServices.Count == 0)
        {
            return;
        }
        var dialog = new SessionImportView { DataContext = new SessionImportViewModel(importServices) };
        SessionImportOutcome? outcome = await dialog.ShowDialog<SessionImportOutcome?>(this);
        if (outcome is { Imported: > 0 })
        {
            await mainWindowViewModel.RefreshSessionTreeAsync();
        }
    }

    /// <summary>
    /// 插件提议的连接:把提议变成一份**尚未落盘**的配置,交给「新建连接」对话框预填。
    /// <para>
    /// 走同一扇对话框而不是另造一个"确认导入"框,是因为用户接下来大概率还要改点什么
    /// (分组、名字、环境标记),而那些控件本来就都在这扇窗里。
    /// </para>
    /// </summary>
    /// <param name="proposal">插件的提议。</param>
    /// <returns>用户是否保存了这条连接。</returns>
    private async Task<bool> ProposeConnectionAsync(
        PluginSdk.Workspaces.WorkspaceConnectionProposal proposal)
    {
        var profile = new SessionProfile
        {
            // 新 Guid = 一条新记录。对话框在"编辑"模式下预填,保存即入库。
            Id = Guid.NewGuid(),
            Name = proposal.Name,
            Host = proposal.Host,
            Port = proposal.Port,
            Username = proposal.Username,
            Password = proposal.Password,
            AuthMethod = AuthMethod.Password,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = proposal.WorkspaceId,
            PluginSettings = new Dictionary<string, string>(proposal.Settings, StringComparer.Ordinal),
            // 分组:提议只带名字,而分组在宿主里是有 id 的实体 —— 让用户在对话框里选,
            // 比替他新建一个可能与既有分组重名的分组更稳。
            // 探到的口令要能留住:没有这一条,用户保存后下次连接又得重填一遍。
            RememberPassword = proposal.Password.Length > 0
        };
        await OpenProfileDialogAsync(profile).ConfigureAwait(true);
        // 对话框自己会落盘并刷新树;这里用"库里有没有这条 id"作为"用户保存了没有"的判据 ——
        // 比让对话框多回一个布尔值更难出错(用户可能保存后又点了连接)。
        if (Application.Current is not App app
            || app.Services?.GetService<ISessionRepository>() is not { } repository)
        {
            return false;
        }
        IReadOnlyList<SessionProfile> saved = await repository.GetAllSessionsAsync().ConfigureAwait(true);
        return saved.Any(candidate => candidate.Id == profile.Id);
    }

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
            defaultKeyPath,
            // 协议注册表:连接页据此画出插件协议页签(不装载任何插件程序集),
            // 用户点到某个页签才触发它的惰性激活。
            app.Services.GetService<Infrastructure.Plugins.Protocols.PluginProtocolRegistry>()
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

    /// <summary>
    /// 打开(或前置)某个会话的任务管理器窗口。非模态 —— 用户要一边看进程一边在终端里
    /// 敲命令;同一会话重复点击只把已开的窗口激活,不再叠一扇。
    /// </summary>
    private async Task OpenProcessManagerAsync(Guid sessionId, string label)
    {
        if (_processManagers.TryGetValue(sessionId, out ProcessManagerView? existing))
        {
            existing.Activate();
            return;
        }
        if (Application.Current is not App app
            || app.Services?.GetService<IRemoteProcessService>() is not { } processService)
        {
            return;
        }
        var window = new ProcessManagerView
        {
            DataContext = new ProcessManagerViewModel(processService, sessionId, label),
        };
        _processManagers[sessionId] = window;
        window.Closed += (_, _) => _processManagers.Remove(sessionId);

        // 尺寸记忆:所有会话共用一条记录 —— 用户调的是"这个面板多大",不是"这台服务器的面板多大"。
        if (app.Services?.GetService<WindowLayoutStore>() is { } layoutStore)
        {
            WindowLayoutStore.Apply(window, await layoutStore.LoadAsync(ProcessManagerLayoutKey));
            window.Closing += (_, _) => _ = layoutStore.SaveAsync(ProcessManagerLayoutKey, window);
        }
        window.Show(this);
    }

    /// <summary>
    /// 打开(或前置)某个会话的资源监视窗口。与任务管理器同样按会话去重、非模态、记忆尺寸 ——
    /// 用户要一边盯曲线一边在终端里敲命令。
    /// </summary>
    private async Task OpenResourceMonitorAsync(Guid sessionId, string label)
    {
        if (_resourceMonitors.TryGetValue(sessionId, out ResourceMonitorWindow? existing))
        {
            existing.Activate();
            return;
        }
        if (Application.Current is not App app
            || app.Services?.GetService<ISessionMetricsService>() is not { } metricsService)
        {
            return;
        }
        var window = new ResourceMonitorWindow
        {
            DataContext = new ResourceMonitorWindowViewModel(metricsService, sessionId, label),
        };
        _resourceMonitors[sessionId] = window;
        window.Closed += (_, _) => _resourceMonitors.Remove(sessionId);

        // 尺寸记忆:所有会话共用一条记录(同任务管理器)。
        if (app.Services?.GetService<WindowLayoutStore>() is { } layoutStore)
        {
            WindowLayoutStore.Apply(window, await layoutStore.LoadAsync(ResourceMonitorLayoutKey));
            window.Closing += (_, _) => _ = layoutStore.SaveAsync(ResourceMonitorLayoutKey, window);
        }
        window.Show(this);
    }

    /// <summary>打开(或前置)某个目标的链路追踪窗口。非模态,尺寸与任务管理器一样记忆。</summary>
    private async Task OpenTraceRouteAsync(MainWindowViewModel vm, string host, string label)
    {
        if (_traceWindows.TryGetValue(host, out TraceRouteWindow? existing))
        {
            existing.Activate();
            return;
        }
        IServiceProvider? services = (Application.Current as App)?.Services;
        IIpGeolocationService? geo = services?.GetService<IIpGeolocationService>();
        ISettingsService? settings = services?.GetService<ISettingsService>();
        var viewModel = new TraceRouteViewModel(
            vm.TraceRouteService,
            geo,
            // 用户选过一次就记住,下次开窗直接加载 —— 这一项不进设置页,入口只在追踪窗口里。
            path => _ = RememberGeoDatabaseAsync(settings, path)
        );
        viewModel.PointAt(host, label);
        var window = new TraceRouteWindow { DataContext = viewModel };
        _traceWindows[host] = window;
        window.Closed += (_, _) => _traceWindows.Remove(host);
        if (Application.Current is App app && app.Services?.GetService<WindowLayoutStore>() is { } layoutStore)
        {
            WindowLayoutStore.Apply(window, await layoutStore.LoadAsync(TraceRouteLayoutKey));
            window.Closing += (_, _) => _ = layoutStore.SaveAsync(TraceRouteLayoutKey, window);
        }
        window.Show(this);
    }

    /// <summary>记住用户选定的归属地数据库路径;写失败只影响下次是否要重选,不打扰用户。</summary>
    private static async Task RememberGeoDatabaseAsync(ISettingsService? settings, string path)
    {
        if (settings is null)
        {
            return;
        }
        try
        {
            AppSettings current = await settings.GetSettingsAsync();
            current.General.GeoIpDatabasePath = path;
            await settings.SaveSettingsAsync(current);
        }
        catch
        {
            // 尽力而为。
        }
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
    /// <summary>
    /// 在系统浏览器里打开一个网址(消息中心的外链条目)。
    /// **只放行 https** —— 网址来自远端资讯源,这是它落地前的最后一道闸。
    /// </summary>
    private async Task OpenExternalUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }
        try
        {
            if (GetTopLevel(this) is { Launcher: { } launcher })
            {
                await launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // 没有可用浏览器/被系统拒绝:面板本身仍然可用,不该因此崩掉窗口。
        }
    }

    /// <summary>打开设置窗口;<paramref name="section" /> 非空时直接落到那一分区。</summary>
    private async Task OpenSettingsAsync(SettingsSectionKey? section = null)
    {
        if (Application.Current is not App app || app.Services?.GetService<SettingsViewModel>() is not { } settingsViewModel)
        {
            return;
        }
        await settingsViewModel.LoadCommand.Execute().FirstAsync();
        if (section is { } target)
        {
            // 在 Load 之后选:Load 会重建 Sections 并顺带复位选中项。
            settingsViewModel.SelectSection(target);
        }
        var dialog = new SettingsView { DataContext = settingsViewModel };
        await dialog.ShowDialog(this);
    }

    private PluginManagerWindow? _pluginManagerWindow;

    /// <summary>打开(或聚焦已开的)插件管理窗口。</summary>
    private void OpenPluginManager()
    {
        if (_pluginManagerWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if (Application.Current is not App { Services: { } services }
            || services.GetService<Infrastructure.Plugins.PluginManager>() is not { } manager)
        {
            return;
        }
        var viewModel = new PluginManagerViewModel(manager,
            services.GetService<Infrastructure.Plugins.PluginPermissionGate>());
        _pluginManagerWindow = new PluginManagerWindow { DataContext = viewModel };
        _pluginManagerWindow.Closed += (_, _) => _pluginManagerWindow = null;
        _pluginManagerWindow.Show(this);
    }

    /// <summary>
    /// 登录验证弹窗的串行闸门:同一时刻只允许一扇凭据对话框。启动恢复会话是并发发起的
    /// (#118),资源管理器树的双击/右键连接也是即发即忘,两条路径都可能同时要凭据;
    /// 同一 owner 上叠两扇模态框会互相争抢 owner 的禁用/启用,表现为对话框点不动。
    /// </summary>
    private readonly SemaphoreSlim _credentialPromptGate = new(1, 1);

    /// <summary>
    /// 登录验证流程(设计:身份验证 第1步/第2步):补全用户名与认证凭据。
    /// 一次只弹一扇(见 <see cref="_credentialPromptGate" />),排队的连接依次拿到弹窗。
    /// </summary>
    /// <summary>
    /// 服务器证书未通过校验时的信任提示(FTPS 与插件协议的 TLS 端点共用)。
    /// 把指纹与主体摊开给用户看,确认后由 VM 记进配置并重连。
    /// <para>
    /// 这条链路刻意做成「连接失败 → 提示 → 重连」而不是在 TLS 回调里同步等用户点按钮:
    /// 后者要把异步对话框阻塞成同步,极易死锁(证书回调不保证在哪个线程上触发)。
    /// </para>
    /// </summary>
    private async Task<bool> PromptCertificateTrustAsync(
        string host,
        string subject,
        string issuer,
        DateTimeOffset expiresOn,
        string thumbprint,
        string policyErrors)
    {
        string message = string.Join(Environment.NewLine,
            Strings.Format("Cert_UntrustedFmt", host),
            string.Empty,
            $"{Strings.Get("Cert_Subject")}: {subject}",
            $"{Strings.Get("Cert_Issuer")}: {issuer}",
            $"{Strings.Get("Cert_Expires")}: {expiresOn:yyyy-MM-dd}",
            $"{Strings.Get("Cert_Fingerprint")}: {FormatThumbprint(thumbprint)}",
            $"{Strings.Get("Cert_Problem")}: {policyErrors}");
        return await MessageDialog.ConfirmAsync(this,
            Strings.Get("Cert_Title"),
            message,
            Strings.Get("Cert_Trust"),
            Strings.Cancel,
            MessageDialogKind.Warning,
            danger: true);
    }

    /// <summary>
    /// 删除分组前的确认(资源管理器树右键“删除分组”)。提示语由视图模型按分组名与
    /// 组内连接数拼好,这里只负责弹窗;确认按钮走 danger 渲染,默认动作是取消。
    /// </summary>
    private Task<bool> ConfirmDeleteGroupAsync(string message) =>
        MessageDialog.ConfirmAsync(this,
            Strings.Get("Tree_DeleteGroup"),
            message,
            Strings.Delete,
            Strings.Cancel,
            MessageDialogKind.Warning,
            danger: true);

    /// <summary>指纹按每两字节加冒号分组,便于与服务器端比对。</summary>
    private static string FormatThumbprint(string thumbprint) =>
        thumbprint.Length < 2
            ? thumbprint
            : string.Join(':', Enumerable.Range(0, thumbprint.Length / 2).Select(i => thumbprint.Substring(i * 2, 2)));

    private async Task<SessionProfile?> PromptCredentialsAsync(SessionProfile profile)
    {
        // 不带 ConfigureAwait(false):后续 ShowDialog 必须回到 UI 线程。
        await _credentialPromptGate.WaitAsync();
        try
        {
            return await PromptCredentialsCoreAsync(profile);
        }
        finally
        {
            _credentialPromptGate.Release();
        }
    }

    /// <summary>
    /// 登录验证弹窗本体:已信任主机显示其指纹;首次连接提示握手时记录(TOFU)。取消返回 null。
    /// </summary>
    private async Task<SessionProfile?> PromptCredentialsCoreAsync(SessionProfile profile)
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
                SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
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
    private Task<string?> PromptForTransferDownloadFolderAsync(TransferFolderPromptRequest request, CancellationToken cancellationToken)
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
    private Task<IReadOnlyList<string>> PromptForTransferUploadFilesAsync(bool isRetryAfterCancel, CancellationToken cancellationToken)
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
                AllowMultiple = true,
                SuggestedStartLocation = await StorageDefaults.DownloadsAsync(top)
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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using VelaShell.Core.Data;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Processes;
using VelaShell.Core.Protocols;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Infrastructure.Pty;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.FileTransfer;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 主窗口视图模型:应用外壳的中枢,统筹终端标签、SSH/本地会话生命周期、停靠工作区、
/// 侧边栏、状态栏、命令面板、SFTP 文件面板与隧道面板,并串联设置、连接工作流与各项服务。
/// </summary>
public class MainWindowViewModel : ReactiveObject, Services.Plugins.ITerminalResolver
{
    /// <summary>
    /// bash 提示符目录上报钩子(内置、静默注入):每次提示符出现时发送 OSC 7,
    /// 供 SFTP 文件浏览器的「跟随终端目录」功能读取当前工作目录。
    /// 由「设置 → 终端 → 会话 → 上报终端工作目录」开关控制,关掉即一字节不注入(#286)。
    /// </summary>
    /// <remarks>
    /// bash 代码放在单引号包裹的 eval 参数里,避免 fish 等 shell 预解析函数体时报错;
    /// 外层守卫让非 bash shell 不执行。只追加 PROMPT_COMMAND,不覆盖用户已有的
    /// starship/direnv/atuin 等钩子,并在重连时按函数名去重。
    /// <para>
    /// 这道守卫只在 POSIX 世界内部有效:它挡得住 fish/csh,挡不住 cmd.exe ——
    /// Windows OpenSSH 的默认 shell 把整行当命令执行,屏幕上就是
    /// <c>'test' 不是内部或外部命令</c>(#305)。所以注入前必须先用
    /// <see cref="RemoteShellProbe" /> 确认对端认 sh 语法,守卫是第二道闸不是第一道。
    /// </para>
    /// </remarks>
    private const string WorkingDirectoryReportHook =
        """
        test -n "$BASH_VERSION" && eval 'vela_shell_osc7() { printf "\033]7;file://%s%s\033\\\\" "$HOSTNAME" "$PWD"; }; case ";$PROMPT_COMMAND;" in *";vela_shell_osc7;"*) ;; *) PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND;}vela_shell_osc7";; esac'; printf "\r\033[2K"
        """;

    /// <summary>RIS(ESC c)完全重置序列:重开会话前清掉旧进程的残留缓冲。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c']; // ESC c

    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly ISessionMetricsService? _metricsService;
    private readonly IRemoteProcessService? _remoteProcessService;

    // ---- 会话日志(设置 → 常规 → 数据与存储) ----

    private readonly Dictionary<TerminalTabViewModel, SessionLogWriter> _sessionLogs = [];

    // ---- 会话录制(设置 → 安全审计 → 会话录制) ----

    private readonly Dictionary<TerminalTabViewModel, SessionRecorder> _sessionRecorders = [];
    private readonly ISessionRecordingStore? _recordingStore;

    private readonly IAppDataStore? _appDataStore;
    private readonly ISessionRepository? _sessionRepository;
    private readonly ISettingsService? _settingsService;
    private readonly ISftpService? _sftpService;
    private readonly IFtpSessionService? _ftpSessionService;
    private readonly IPluginProtocolSessionService? _pluginProtocols;

    /// <summary>协议注册表:开插件协议文档时要按协议 id 取回它的动作与能力位。</summary>
    private readonly PluginProtocolRegistry? _protocolRegistry;

    /// <summary>工作台连接类型(Redis 等)的会话启动器;无插件宿主的单测里为 null。</summary>
    private readonly PluginWorkspaceLauncher? _workspaceLauncher;

    /// <summary>FTP 会话标识 → 会话配置标识:状态事件只带会话标识,树上按配置标识定位节点。</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _ftpSessionProfiles = new();

    /// <summary>插件协议会话标识 → 会话配置标识;用途同 <see cref="_ftpSessionProfiles" />。</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _pluginSessionProfiles = new();

    /// <summary>工作台会话 id → 连接配置 id(树上的状态圆点按它定位)。</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _workspaceProfiles = new();

    /// <summary>工作台会话 id → 已打开的停靠文档(插件被停用时要按 id 找到它并关掉)。</summary>
    private readonly ConcurrentDictionary<Guid, PluginWorkspaceDocument> _workspaceDocuments = new();

    /// <summary>工作台会话 id → 为它建的隧道 id(文档关闭时要拆掉,否则本地端口一直占着)。</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _workspaceTunnels = new();
    private readonly ISshConnectionService? _sshConnectionService;

    /// <summary>当前界面主题(具名主题目录),用于给终端下发配套的终端配色。</summary>
    private readonly IThemeService? _themeService;
    private readonly Func<ITerminalEmulator> _terminalEmulatorFactory;
    private readonly ITunnelService? _tunnelService;
    private readonly ITunnelWorkflowService? _tunnelWorkflowService;
    private readonly QuickCommandsViewModel? _quickCommands;
    private readonly QuickCommandRunnerViewModel? _quickCommandRunner;
    private readonly TerminalTargetSelectorViewModel _terminalTargetSelector;
    private readonly Dictionary<TerminalTabViewModel, IDisposable> _quickCommandTargetSubscriptions = [];

    /// <summary>
    /// 每个终端标签的连接状态订阅,用于重算它那条配置在会话树上的状态标签
    /// (见 <see cref="RefreshSessionStatus" />)。与快捷命令目标订阅同生命周期:
    /// 在 <see cref="OnTabsCollectionChanged" /> 里随标签进出标签栏挂上与退订。
    /// </summary>
    private readonly Dictionary<TerminalTabViewModel, IDisposable> _sessionStatusSubscriptions = [];

    /// <summary>同步输入频道的对等转发中枢(标签右键菜单 → 同步输入)。</summary>
    private readonly SyncInputCoordinator _syncInput = new();

    /// <summary>全局命令历史(命令补全数据源;终端标签提交命令后写入)。</summary>
    public CommandHistoryService CommandHistory { get; }

    /// <summary>补全建议提供器(历史 ∪ 快捷命令),注入到每个终端标签。</summary>
    private readonly CommandSuggestionProvider _suggestionProvider;

    // SFTP/文件管理视图(源自设计稿)
    private FileBrowserViewModel _fileBrowser;

    /// <summary>
    /// 按会话缓存的 SFTP 面板实例:切换标签复用(保留路径/列表/排序/列宽,免重复列目录),
    /// 标签关闭或连接断开时经 <see cref="EvictFileBrowser" /> 驱逐。
    /// </summary>
    private readonly Dictionary<Guid, FileBrowserViewModel> _fileBrowserCache = [];
    private readonly object _fileBrowserPreferenceSaveSync = new();
    private Task _fileBrowserPreferenceSaveTail = Task.CompletedTask;
    private readonly Lock _sftpCloseTasksSync = new();
    private readonly Dictionary<SftpDocument, Task> _sftpCloseTasks = [];
    private FileTransferViewModel _fileTransfer;

    private bool _latencyPolling;
    private int _latencyTick;
    private AppSettings? _latestSettings;
    private AppState _appState = new();
    private bool _isApplyingSidebarState;
    private CancellationTokenSource? _sidebarStateSaveDebounce;

    private Dictionary<Guid, string> _paletteGroupNames = [];

    // ---- 命令面板的全量会话(§12.3:面板作为中枢,收录全部已保存配置) ----

    private IReadOnlyList<SessionProfile> _paletteProfiles = [];
    private SidebarViewModel _sidebar;
    private StatusBarViewModel _statusBar;
    private bool _statusMetricsPolling;

    // ---- Status-bar live metrics (spec §7: cpu / memory / net for the active session) ----

    private DispatcherTimer? _statusMetricsTimer;
    private DispatcherTimer? _fontSizePersistDebounce;
    private int _pendingFontSize;
    private TabBarViewModel _tabBar;

    /// <summary>
    /// 用可选注入的各项服务构造主窗口视图模型:装配命令补全、停靠工作区、侧边栏/标签栏/状态栏、
    /// SFTP 面板与命令注册,并订阅设置保存、外观预览、安全告警等事件、启动状态栏指标轮询。
    /// 无 UI 的单元测试可全部传 null 构造。
    /// </summary>
    public MainWindowViewModel(
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISshConnectionService? sshConnectionService = null,
        Func<ITerminalEmulator>? terminalEmulatorFactory = null,
        ISettingsService? settingsService = null,
        ISessionRepository? sessionRepository = null,
        ISftpService? sftpService = null,
        ITransferManager? transferManager = null,
        ITunnelService? tunnelService = null,
        ITunnelWorkflowService? tunnelWorkflowService = null,
        ISessionMetricsService? metricsService = null,
        IRecentConnectionService? recentConnectionService = null,
        ISecurityAlertService? securityAlertService = null,
        ISettingsPreviewService? settingsPreviewService = null,
        IAppDataStore? appDataStore = null,
        ISessionRecordingStore? recordingStore = null,
        QuickCommandsViewModel? quickCommands = null,
        IQuickCommandRepository? quickCommandRepository = null,
        IRemoteProcessService? remoteProcessService = null,
        ITraceRouteService? traceRouteService = null,
        ICommandRegistry? commandRegistry = null,
        IFtpSessionService? ftpSessionService = null,
        IPluginProtocolSessionService? pluginProtocolService = null,
        PluginProtocolRegistry? protocolRegistry = null,
        PluginWorkspaceLauncher? workspaceLauncher = null,
        IGistSyncService? gistSyncService = null,
        IBackgroundActivityService? backgroundActivity = null,
        INotificationCenter? notificationCenter = null,
        IAnnouncementFeed? announcementFeed = null,
        IUpdateService? updateService = null,
        IThemeService? themeService = null
    )
    {
        // 注册表可注入(DI 里与插件命令桥共享同一单例);无 UI 单测传 null 时自建。
        Commands = commandRegistry ?? new CommandRegistry();
        _remoteProcessService = remoteProcessService;
        TraceRouteService = traceRouteService;
        _appDataStore = appDataStore;
        _recordingStore = recordingStore;
        _quickCommands = quickCommands;
        _terminalTargetSelector = new();
        _quickCommandRunner = quickCommands is null
            ? null
            : new(quickCommands, _terminalTargetSelector);
        _quickCommandRunner?.ExecutionRequested += OnQuickCommandExecutionRequested;

        // 命令补全(plan.md #16):全局命令历史 + 建议提供器(历史 ∪ 快捷命令),
        // 逐标签在 CreateConnectingTab 注入。
        CommandHistory = new(appDataStore);
        _suggestionProvider = new(CommandHistory, quickCommandRepository);
        _connectionWorkflowService = connectionWorkflowService;
        _sshConnectionService = sshConnectionService;
        _themeService = themeService;
        // 切主题 → 终端画面跟着换整套配色。不能只靠控件自己听 ThemeVariant:
        // 具名主题里有多套暗色,VelaDark 换到 Tokyo Night 时变体压根没变(#主题目录)。
        if (themeService is not null)
        {
            themeService.ThemeChanged += _ => RefreshTerminalThemePalette();
            // 「跟随系统」下系统明暗翻转:主题服务不动,只有实际变体变了,同样要重下发。
            // 只在有主题服务时才挂:没有它的那些单测会造出成百个视图模型,
            // 每个都往共用的 Application 上挂一个再也不会摘掉的处理器。
            if (Avalonia.Application.Current is { } themeHost)
            {
                themeHost.ActualThemeVariantChanged += (_, _) => RefreshTerminalThemePalette();
            }
        }
        _settingsService = settingsService;
        _sessionRepository = sessionRepository;
        _sftpService = sftpService;
        _ftpSessionService = ftpSessionService;
        _pluginProtocols = pluginProtocolService;
        _protocolRegistry = protocolRegistry;
        _workspaceLauncher = workspaceLauncher;
        gistSyncService?.ProfilesApplied += OnSyncProfilesApplied;
        // 插件被停用/卸载 → 它名下的工作台文档已无人应答,走正常关闭路径撤掉标签页。
        workspaceLauncher?.SessionAbandoned += OnWorkspaceSessionAbandoned;
        // 新建连接对话框里的「测试」对插件连接类型没法拿 SSH 去试(那只会撞出一个
        // 与真实原因无关的超时)。探针挂在这里而不是注进工作流服务:真开一次插件会话
        // 要用到隧道链路与凭据解密,那些都只在界面层有。
        _connectionWorkflowService?.PluginProbe = ProbePluginConnectionAsync;
        // FTP 与插件协议都没有 SSH 那种可订阅的长驻会话对象:断线只在下一次操作时暴露。
        // 由服务主动上报,树上的状态圆点才能自动变灰,而不是一直停在绿点上。
        ftpSessionService?.SessionStateChanged += OnFtpSessionStateChanged;
        pluginProtocolService?.SessionStateChanged += OnPluginSessionStateChanged;
        _tunnelService = tunnelService;
        _tunnelWorkflowService = tunnelWorkflowService;
        _metricsService = metricsService;
        _terminalEmulatorFactory = terminalEmulatorFactory ?? (() => new VelaTerminalControl());
        Layout = new DockWorkspace();
        Layout.DocumentClosed += document =>
        {
            if (document is TerminalDocument terminalDocument)
            {
                OnDocumentClosed(terminalDocument);
            }
            else if (document is SftpDocument sftpDocument)
            {
                _ = GetOrCreateSftpCloseTask(sftpDocument);
            }
            else if (document is PluginWorkspaceDocument workspaceDocument)
            {
                _ = CloseWorkspaceDocumentAsync(workspaceDocument);
            }
        };
        Layout.ActiveDocumentChanged += SetActiveFromDocument;
        _sidebar = new(recentConnectionService, _quickCommandRunner);
        _sidebar.PropertyChanged += OnSidebarStateChanged;
        if (sessionRepository is not null)
        {
            _sidebar.SessionTree = new(sessionRepository);
        }
        _tabBar = new();
        _tabBar.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _statusBar = new();
        WireBackgroundActivity(backgroundActivity);
        _fileBrowser = new(null, Guid.Empty);
        _fileTransfer = new(transferManager, appDataStore);
        _tabBar
            .WhenAnyValue(tabBar => tabBar.ActiveTab)
            .Subscribe(activeTab =>
            {
                ActiveTerminalTab = activeTab as TerminalTabViewModel;
                activeTab?.HasBellAlert = false; // 切换到该标签即清除 Bell 提醒
                RebindFileBrowser();
                SyncWorkspaceToActiveTab(activeTab as TerminalTabViewModel);
                RefreshQuickCommandTargets();
                RevealActiveSessionInSidebar(activeTab as TerminalTabViewModel);
            });

        // SFTP 面板“打开/关闭”是每个标签自己的状态:跟踪当前面板实例上的 IsVisible
        // 变化(标题栏切换、面板关闭按钮),回写到拥有该会话的标签
        // (TerminalTabViewModel.FileBrowserOpen),切回该标签时按此恢复。对象整体替换
        // (切标签重绑、驱逐后的占位)属于程序行为,Skip(1) 跳过替换瞬间的初值,
        // 不污染标签状态。
        this.WhenAnyValue(x => x.FileBrowser)
            .Select(browser => browser
                .WhenAnyValue(b => b.IsVisible)
                .Skip(1)
                .Select(visible => (browser.SessionId, Visible: visible)))
            .Switch()
            .Subscribe(change => RememberFileBrowserStateForTab(change.SessionId, change.Visible));

        // 状态栏随活动标签同步:活动标签变化时以及该标签自身的连接状态/延迟变化时刷新。
        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Signal.Emit(RxVoid.Default)
                    : tab.WhenAnyValue(t => t.ConnectionStatus, t => t.Latency)
                        .Select(_ => RxVoid.Default)
            )
            .Switch()
            .Subscribe(_ => UpdateStatusBarForActiveTab());

        this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Signal.Emit(RxVoid.Default)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => RxVoid.Default)
            )
            .Switch()
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanToggleFileBrowser));
                this.RaisePropertyChanged(nameof(CanOpenProcessManager));
            });

        // 已保存的设置即时应用到所有已打开的终端(#3/#15/#21) —— 回滚、字体、
        // 字号与编码实时生效;TERM 按会话保持(在连接时协商)。
        _settingsService?.SettingsSaved += OnSettingsSaved;

        // 外观即时预览(设置窗口广播,未持久化):只重刷已打开标签的终端外观,
        // 不动 _latestSettings(新建标签仍用已保存的设置)。
        settingsPreviewService?.PreviewRequested += settings =>
            RxSchedulers.MainThreadScheduler.Schedule(() => ApplyLiveSettingsToOpenTabs(settings));

        // 安全告警(设置 → 安全审计 → 告警通道):应用内 → 状态栏;提示音 → 系统提示音。
        securityAlertService?.Alerted += notice =>
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (notice.InApp)
                {
                    StatusBar.Status = notice.Message;
                }
                if (notice.Sound)
                {
                    SystemSound.Alert();
                }
            });
        StartStatusMetricsPolling();
        SetUpNotificationCenter(notificationCenter, announcementFeed, updateService);
        OpenSettingsCommand = ReactiveCommand.Create(() =>
            SettingsRequested?.Invoke(this, EventArgs.Empty)
        );
        CommandPalette = new(BuildPaletteItems);
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => CommandPalette.Open());
        IObservable<bool> canToggleFileBrowser = this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Signal.Emit(false)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => CanToggleFileBrowser)
            )
            .Switch();
        ToggleFileBrowserCommand = ReactiveCommand.Create(ToggleFileBrowser, canToggleFileBrowser);
        IObservable<bool> canOpenProcessManager = this.WhenAnyValue(x => x.ActiveTerminalTab)
            .Select(tab =>
                tab is null
                    ? Signal.Emit(false)
                    : tab.WhenAnyValue(t => t.IsConnected).Select(_ => CanOpenProcessManager)
            )
            .Switch();
        OpenProcessManagerCommand = ReactiveCommand.Create(OpenProcessManager, canOpenProcessManager);
        OpenResourceMonitorCommand = ReactiveCommand.Create(OpenResourceMonitor, canOpenProcessManager);
        // 命令注入状态栏,而不是让状态栏去 $parent[Window].DataContext 找:
        // 视图加载早于窗口 DataContext 赋值,跨树查找会在启动时刷一条绑定错误。
        StatusBar.OpenResourceMonitorCommand = OpenResourceMonitorCommand;
        OpenTraceRouteCommand = ReactiveCommand.Create(OpenTraceRoute, canToggleFileBrowser);
        CloseActiveTabCommand = ReactiveCommand.Create(CloseActiveTab);
        RegisterCommands();
        RunCommand = ReactiveCommand.Create<string>(id => Commands.Execute(id));
    }

    /// <summary>
    /// 菜单栏、命令面板与快捷键共用的单条命令来源(设计稿 §4A.1)——每个入口展示的名称、提示与行为都一致。
    /// </summary>
    public ICommandRegistry Commands { get; }

    /// <summary>通过 id 执行一条注册命令(菜单项通过 CommandParameter 使用)。</summary>
    public ReactiveCommand<string, RxVoid>? RunCommand { get; private set; }

    /// <summary>活动会话的隧道管理面板(设计 fuXS7,规范 §10)。</summary>
    public TunnelPanelViewModel? TunnelPanel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>隧道面板当前是否展开显示。</summary>
    public bool IsTunnelPanelOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>消息中心面板(侧边栏铃铛)。</summary>
    public NotificationPanelViewModel? NotificationPanel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>消息中心面板当前是否展开显示。</summary>
    public bool IsNotificationPanelOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>活动标签的自绘终端控件(当活动标签存在时)。</summary>
    private VelaTerminalControl? ActiveTerminalControl =>
        ActiveTerminalTab?.TerminalEmulator.Control as VelaTerminalControl;

    /// <summary>Ctrl+P / Ctrl+K 命令面板浮层。</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>打开命令面板(Ctrl+P / Ctrl+K)的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenCommandPaletteCommand { get; }

    /// <summary>显示或隐藏当前 SSH 会话的远程文件面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleFileBrowserCommand { get; }

    /// <summary>当前活动标签是否支持打开远程文件面板。</summary>
    public bool CanToggleFileBrowser =>
        _sftpService is not null
        && ActiveTerminalTab is { IsConnected: true, Profile: not null } tab
        && tab.SessionId != Guid.Empty;

    /// <summary>
    /// 任务管理器是否可用:必须是一个已连接的 SSH 终端标签。本地终端(LocalShell 非空)
    /// 没有远端可管;聚焦 SFTP 标签时 ActiveTerminalTab 已被置空(见 SetActiveFromDocument),
    /// 两种情况按钮都自动变灰。
    /// </summary>
    public bool CanOpenProcessManager =>
        _remoteProcessService is not null
        && ActiveTerminalTab is { IsConnected: true, Profile: not null, LocalShell: null } tab
        && tab.SessionId != Guid.Empty;

    /// <summary>
    /// 由窗口注入的交互式身份验证(两步弹窗):补全用户名/密码/密钥后返回更新的配置,
    /// 取消时返回 null。
    /// </summary>
    public Func<SessionProfile, Task<SessionProfile?>>? InteractiveAuthenticator { get; set; }

    /// <summary>
    /// FTPS 服务器证书未通过校验时的信任提示;返回 true 表示用户同意信任该指纹。
    /// 由 View 层挂上(与 <see cref="InteractiveAuthenticator" /> 同样的手法);未挂时按拒绝处理。
    /// </summary>
    public Func<SessionProfile, VelaFtpCertificateException, Task<bool>>? FtpCertificateTrustPrompt { get; set; }

    /// <summary>
    /// 插件协议端点的证书未通过校验时的信任提示;返回 true 表示用户同意信任该指纹。
    /// 自建 MinIO / Ceph 的自签证书是常态,没有这条路径这类端点根本连不上。
    /// </summary>
    public Func<SessionProfile, PluginProtocolCertificateException, Task<bool>>? PluginCertificateTrustPrompt { get; set; }

    /// <summary>最近一次连接的错误消息,若上次尝试成功则为 null。</summary>
    public string? LastConnectionError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>窗口注入的多行粘贴确认弹窗(设置 → 终端 → 粘贴时确认多行内容)。</summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmer { get; set; }

    /// <summary>
    /// 窗口注入的 ZMODEM 下载目录选择委托(视图层实现,独占 StorageProvider)。
    /// 分发给每个新建的终端标签,供其 ZMODEM 接收时弹出保存目录选择框。
    /// </summary>
    public Func<TransferFolderPromptRequest, CancellationToken, Task<string?>>? TransferDownloadFolderPicker { get; set; }

    /// <summary>
    /// 窗口注入的 ZMODEM 上传文件选择委托(视图层实现,独占 StorageProvider)。
    /// 分发给每个新建的终端标签,供远端 <c>rz</c> 时弹出多选文件框。
    /// </summary>
    public Func<bool, CancellationToken, Task<IReadOnlyList<string>>>? TransferUploadFilePicker { get; set; }

    /// <summary>
    /// 为新建的终端标签注入 ZMODEM 传输所需的依赖:下载目录选择委托、上传文件选择委托、
    /// 共享传输面板与设置读取委托。前者 + 面板 + 设置就绪时 AttachTransport 才会启用 ZMODEM 路由器。
    /// </summary>
    private void WireZModemDownload(TerminalTabViewModel terminalTab)
    {
        terminalTab.TransferDownloadFolderPicker = TransferDownloadFolderPicker;
        terminalTab.TransferUploadFilePicker = TransferUploadFilePicker;
        terminalTab.FileTransfer = _fileTransfer;
        if (_settingsService is { } settings)
        {
            terminalTab.GetSettingsAsync = settings.GetSettingsAsync;
        }
    }

    /// <summary>左侧边栏视图模型:资源管理器会话树与最近连接。</summary>
    public SidebarViewModel Sidebar
    {
        get => _sidebar;
        set => this.RaiseAndSetIfChanged(ref _sidebar, value);
    }

    /// <summary>标签栏视图模型:管理终端标签的集合与激活项。</summary>
    public TabBarViewModel TabBar
    {
        get => _tabBar;
        set => this.RaiseAndSetIfChanged(ref _tabBar, value);
    }

    /// <summary>
    /// 插件终端能力经此按会话 id 解析到仿真器与人类可读标签(<see cref="Services.Plugins.ITerminalResolver" />)。
    /// </summary>
    (ITerminalEmulator Emulator, string Label)? Services.Plugins.ITerminalResolver.Resolve(Guid sessionId)
    {
        foreach (TerminalTabViewModel tab in _tabBar.Tabs.OfType<TerminalTabViewModel>())
        {
            if (tab.SessionId == sessionId)
            {
                string label = tab.Profile is { } p
                    ? (string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name)
                    : sessionId.ToString("N")[..8];
                return (tab.TerminalEmulator, label);
            }
        }
        return null;
    }

    /// <summary>底部状态栏视图模型:连接状态、延迟、窗口尺寸与会话资源指标。</summary>
    public StatusBarViewModel StatusBar
    {
        get => _statusBar;
        set => this.RaiseAndSetIfChanged(ref _statusBar, value);
    }

    /// <summary>当前激活的终端标签;无活动标签时为 null。</summary>
    public TerminalTabViewModel? ActiveTerminalTab
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前是否存在活动的终端标签。</summary>
    public bool HasActiveTerminalTab => ActiveTerminalTab is not null;

    /// <summary>自研 VelaDock 工作区:承载终端文档(标签可拖拽重排、拆分分屏)。</summary>
    public DockWorkspace Layout { get; }

    /// <summary>当前会话的 SFTP 文件浏览面板(按会话缓存,随活动标签切换重绑)。</summary>
    public FileBrowserViewModel FileBrowser
    {
        get => _fileBrowser;
        set => this.RaiseAndSetIfChanged(ref _fileBrowser, value);
    }

    /// <summary>链路追踪服务;窗口打开时用它构造面板视图模型。</summary>
    public ITraceRouteService? TraceRouteService { get; }

    /// <summary>文件传输面板视图模型:承载上传/下载任务队列与进度。</summary>
    public FileTransferViewModel FileTransfer
    {
        get => _fileTransfer;
        set => this.RaiseAndSetIfChanged(ref _fileTransfer, value);
    }

    /// <summary>打开设置窗口的命令(Ctrl+, / 菜单 / 侧边栏齿轮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; }

    /// <summary>关闭当前活动标签(Ctrl+W);走停靠层的关闭语义,保证传输层同时拆除。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseActiveTabCommand { get; }

    /// <summary>打开当前 SSH 会话的任务管理器;本地终端与 SFTP 标签下不可用。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenProcessManagerCommand { get; }

    /// <summary>打开链路追踪窗口;启用条件与 SFTP 资源管理器一致。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenTraceRouteCommand { get; }

    /// <summary>
    /// 请求为某个会话打开任务管理器窗口。参数依次为会话标识与窗口标题用的会话名称;
    /// 由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<Guid, string>? ProcessManagerRequested;

    /// <summary>把打开任务管理器的请求转交视图层;条件不满足时是空操作。</summary>
    private void OpenProcessManager()
    {
        if (!CanOpenProcessManager || ActiveTerminalTab is not { Profile: { } profile } tab)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? $"{profile.Host}:{profile.Port}"
                           : profile.Name;
        ProcessManagerRequested?.Invoke(tab.SessionId, label);
    }

    /// <summary>打开当前 SSH 会话的资源监视窗口(状态栏右下角的指示器按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenResourceMonitorCommand { get; }

    /// <summary>
    /// 请求为某个会话打开资源监视窗口。参数依次为会话标识与窗口标题用的会话名称;
    /// 由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<Guid, string>? ResourceMonitorRequested;

    /// <summary>把打开资源监视窗口的请求转交视图层;条件不满足时是空操作。</summary>
    private void OpenResourceMonitor()
    {
        if (!CanOpenProcessManager || ActiveTerminalTab is not { Profile: { } profile } tab)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? $"{profile.Host}:{profile.Port}"
                           : profile.Name;
        ResourceMonitorRequested?.Invoke(tab.SessionId, label);
    }

    private void RegisterCommands()
    {
        Commands.Register(
            new(
                "session.new",
                Strings.Get("Cmd_NewSshConnection"),
                Strings.Get("CmdCat_Session"),
                () => NewConnectionRequested?.Invoke(this, EventArgs.Empty),
                Shortcut: "Ctrl+N",
                Icon: "Icon.plus"
            )
        );
        Commands.Register(
            new(
                "session.close",
                Strings.Get("Cmd_CloseCurrentSession"),
                Strings.Get("CmdCat_Session"),
                () => CloseActiveTabCommand.Execute().Subscribe(),
                () => TabBar.ActiveTab is not null || Layout.ActiveDocument is not null,
                "Ctrl+W"
            )
        );
        Commands.Register(
            new(
                "session.reconnect",
                Strings.Get("Cmd_Reconnect"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (ActiveTerminalTab is { } tab)
                    {
                        _ = ReconnectTabAsync(tab);
                    }
                },
                () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Disconnected,
                "Ctrl+R"
            )
        );
        Commands.Register(
            new(
                "session.clone",
                Strings.Get("Cmd_CloneSession"),
                Strings.Get("CmdCat_Session"),
                () =>
                {
                    if (ActiveTerminalTab?.Profile is { } profile)
                    {
                        _ = TryConnectProfileAsync(profile);
                    }
                },
                () => ActiveTerminalTab?.Profile is not null,
                "Ctrl+Shift+N",
                "Icon.copy"
            )
        );
        Commands.Register(
            new(
                "edit.copy",
                Strings.Get("Copy"),
                Strings.Get("CmdCat_Edit"),
                () =>
                {
                    if (ActiveTerminalControl is { } c)
                    {
                        _ = c.CopyAsync();
                    }
                },
                () => ActiveTerminalControl is not null,
                "Ctrl+Shift+C",
                "Icon.copy"
            )
        );
        Commands.Register(
            new(
                "edit.paste",
                Strings.Get("Cmd_Paste"),
                Strings.Get("CmdCat_Edit"),
                () =>
                {
                    if (ActiveTerminalControl is { } c)
                    {
                        _ = c.PasteAsync();
                    }
                },
                () => ActiveTerminalControl is not null,
                "Ctrl+Shift+V"
            )
        );
        Commands.Register(
            new(
                "terminal.export",
                Strings.Get("Cmd_ExportTerminalOutput"),
                Strings.Get("CmdCat_Session"),
                () => ExportBufferRequested?.Invoke(this, EventArgs.Empty),
                () => ActiveTerminalControl is not null,
                Icon: "Icon.save"
            )
        );
        Commands.Register(
            new(
                "search.terminal",
                Strings.Get("Cmd_FindInTerminal"),
                Strings.Get("CmdCat_Search"),
                () => TerminalSearchRequested?.Invoke(this, EventArgs.Empty),
                () => ActiveTerminalTab is not null,
                "Ctrl+F",
                "Icon.search"
            )
        );
        // 隧道独立于终端会话(后台自动连接),无活动标签也可用。
        Commands.Register(
            new(
                "tools.tunnel",
                Strings.Get("Cmd_TunnelManager"),
                Strings.Get("CmdCat_Tools"),
                ToggleTunnelPanel,
                Shortcut: "Ctrl+Shift+T",
                Icon: "Icon.route"
            )
        );
        Commands.Register(
            new(
                "tools.files",
                Strings.Get("Cmd_SftpFileManager"),
                Strings.Get("CmdCat_Tools"),
                () => ToggleFileBrowserCommand.Execute().Subscribe(),
                () => CanToggleFileBrowser,
                "Ctrl+Shift+F",
                "Icon.folder"
            )
        );
        Commands.Register(
            new(
                "tools.processes",
                Strings.Get("Cmd_ProcessManager"),
                Strings.Get("CmdCat_Tools"),
                () => OpenProcessManagerCommand.Execute().Subscribe(),
                () => CanOpenProcessManager,
                Icon: "Icon.activity"
            )
        );
        Commands.Register(
            new(
                "tools.diagnostics",
                Strings.Get("Cmd_ConnectionDiagnostics"),
                Strings.Get("CmdCat_Tools"),
                () =>
                {
                    if (ActiveTerminalTab?.Profile is { } profile)
                    {
                        DiagnosticsRequested?.Invoke(profile);
                    }
                },
                () => ActiveTerminalTab?.Profile is not null,
                Icon: "Icon.stethoscope"
            )
        );
        Commands.Register(
            new(
                "edit.clear",
                Strings.Get("Cmd_ClearScreen"),
                Strings.Get("CmdCat_Edit"),
                () => ActiveTerminalTab?.TerminalEmulator.WriteInput([0x0C]),
                () => ActiveTerminalTab?.ConnectionStatus == SessionStatus.Connected
            )
        );
        Commands.Register(
            new(
                "terminal.linegutter",
                Strings.Get("Cmd_ToggleLineGutter"),
                Strings.Get("CmdCat_Edit"),
                ToggleLineGutter,
                Shortcut: "Ctrl+Shift+L"
            )
        );
        Commands.Register(
            new(
                "app.settings",
                Strings.Get("Cmd_OpenSettings"),
                Strings.Get("CmdCat_Edit"),
                () => OpenSettingsCommand.Execute().Subscribe(),
                Shortcut: "Ctrl+,",
                Icon: "Icon.settings"
            )
        );
        Commands.Register(
            new(
                "app.settings.about",
                Strings.Get("Cmd_OpenAbout"),
                Strings.Get("CmdCat_Edit"),
                () => SettingsSectionRequested?.Invoke(this, SettingsSectionKey.About),
                Icon: "Icon.info"
            )
        );
        Commands.Register(
            new(
                "app.palette",
                Strings.Get("Cmd_CommandPalette"),
                Strings.Get("CmdCat_Search"),
                () => CommandPalette.Open(),
                Shortcut: "Ctrl+P",
                Icon: "Icon.zap"
            )
        );

        // 分屏(标题栏分屏按钮与命令面板共用;右键标签菜单另有直达入口)。
        Commands.Register(
            new(
                "split.horizontal",
                Strings.Get("Dock_SplitHorizontal"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (Layout.ActiveDocument is { } document)
                    {
                        Layout.SplitDocument(document, DockOrientation.Horizontal);
                    }
                },
                () => Layout.ActiveDocument is not null,
                Icon: "Icon.columns-2"
            )
        );
        Commands.Register(
            new(
                "split.vertical",
                Strings.Get("Dock_SplitVertical"),
                Strings.Get("CmdCat_Actions"),
                () =>
                {
                    if (Layout.ActiveDocument is { } document)
                    {
                        Layout.SplitDocument(document, DockOrientation.Vertical);
                    }
                },
                () => Layout.ActiveDocument is not null,
                Icon: "Icon.rows-2"
            )
        );
        // XMODEM / YMODEM 手动入口。ZMODEM 会自动接管(远端 sz/rz 的引导序列可识别),
        // 而这一族协议在链路上没有可识别的引导 —— sb/sx 静默等接收方发 'C',rb/rx 只吐裸 'C',
        // 在终端输出里与普通字符无异,自动检测必然误触发。所以只能由用户在远端敲好命令后手动发起。
        RegisterManualTransferCommand(
            "transfer.ymodem.receive", "Cmd_YModemReceive",
            TerminalTransferProtocol.YModem, FileTransferDirection.Receive, "Icon.download");
        RegisterManualTransferCommand(
            "transfer.ymodem.send", "Cmd_YModemSend",
            TerminalTransferProtocol.YModem, FileTransferDirection.Send, "Icon.upload");
        RegisterManualTransferCommand(
            "transfer.ymodemg.receive", "Cmd_YModemGReceive",
            TerminalTransferProtocol.YModemG, FileTransferDirection.Receive, "Icon.download");
        RegisterManualTransferCommand(
            "transfer.xmodem.receive", "Cmd_XModemReceive",
            TerminalTransferProtocol.XModem, FileTransferDirection.Receive, "Icon.download");
        RegisterManualTransferCommand(
            "transfer.xmodem.send", "Cmd_XModemSend",
            TerminalTransferProtocol.XModem, FileTransferDirection.Send, "Icon.upload");
        RegisterManualTransferCommand(
            "transfer.xmodem1k.send", "Cmd_XModem1KSend",
            TerminalTransferProtocol.XModem1K, FileTransferDirection.Send, "Icon.upload");

        // 本地终端(§12 P1-1):按本机安装情况动态注册 PowerShell/CMD/WSL/Git Bash 入口。
        foreach (LocalShellInfo shell in LocalShellCatalog.DetectShells())
        {
            LocalShellInfo captured = shell;
            Commands.Register(
                new(
                    $"local.{captured.Id}",
                    Strings.Format("Cmd_OpenLocalTerminal", captured.Name),
                    Strings.Get("CmdCat_Session"),
                    () => _ = OpenLocalTerminalAsync(captured),
                    Icon: "Icon.terminal"
                )
            );
        }
    }

    /// <summary>
    /// 注册一条「手动发起 XMODEM / YMODEM 传输」的命令。可用性由当前活动标签决定:
    /// 要有活着的传输路由器、没有正在跑的会话,上传方向还要求已接线文件选择能力 ——
    /// 条件不满足时命令在面板里就是灰的,不需要再弹一层失败提示。
    /// </summary>
    private void RegisterManualTransferCommand(
        string id,
        string titleKey,
        TerminalTransferProtocol protocol,
        FileTransferDirection direction,
        string icon)
    {
        Commands.Register(
            new(
                id,
                Strings.Get(titleKey),
                Strings.Get("CmdCat_Transfer"),
                () => ActiveTerminalTab?.StartManualTransfer(protocol, direction),
                () => ActiveTerminalTab?.CanStartManualTransfer(direction) == true,
                Icon: icon
            )
        );
    }

    /// <summary>
    /// 打开一个本地终端标签:走与 SSH 相同的 桥 → VT 引擎 → 自绘控件 管线,
    /// 传输层换成 ConPTY(输出恒为 UTF-8,不套用设置里的远端编码)。
    /// </summary>
    public async Task OpenLocalTerminalAsync(LocalShellInfo shell)
    {
        AppSettings settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new();
        _latestSettings = settings;
        ITerminalEmulator terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, TerminalType.XtermColor256, true);
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = shell.Name,
            ConnectionStatus = SessionStatus.Connecting,
            ConnectionSummary = Strings.Format("Msg_LocalPrefix", shell.Name),
            TerminalTypeName = TerminalType.XtermColor256.ToTermName(),
            EncodingName = "UTF-8",
            LocalShell = shell,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);
        // shell 退出(exit)后覆盖层上的“关闭标签”按钮靠这条订阅生效;缺了它本地终端
        // 标签退出后点关闭没有任何反应。
        terminalTab.CloseRequested += (_, _) => CloseTerminalTab(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        WireZModemDownload(terminalTab);
        terminalTab.CommandLineSubmitted += CommandHistory.Record;
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (
                    _latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab)
                )
                {
                    terminalTab.HasBellAlert = true;
                }
            };
        }
        var document = new TerminalDocument(terminalTab);
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        Layout.AddDocument(document);
        UpdateStatusBarForActiveTab();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AttachLocalShell(terminalTab, shell, settings);
            }
        }
        catch (Exception ex)
        {
            RemoveTerminalTab(terminalTab, document);
            LastConnectionError = Strings.Format(
                "Msg_LocalShellStartFailed",
                shell.Name,
                ex.Message
            );
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>重开本地终端标签:RIS 清屏后重新拉起 shell(与 SSH 重连同语义)。</summary>
    private void ReopenLocalShell(TerminalTabViewModel tab, LocalShellInfo shell)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        try
        {
            tab.TerminalEmulator.Feed(RisResetSequence);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AttachLocalShell(tab, shell, _latestSettings ?? new AppSettings());
            }
            LastConnectionError = null;
        }
        catch (Exception ex)
        {
            tab.MarkDisconnected();
            LastConnectionError = Strings.Format(
                "Msg_LocalShellReopenFailed",
                shell.Name,
                ex.Message
            );
            StatusBar.Status = LastConnectionError;
        }
    }

    /// <summary>
    /// 拉起本地 shell 进程并挂上标签(打开与重开共用)。
    /// </summary>
    [SupportedOSPlatform(nameof(OSPlatform.Windows))]
    private void AttachLocalShell(
        TerminalTabViewModel tab,
        LocalShellInfo shell,
        AppSettings settings
    )
    {
        var stream = ConPtyShellStream.Start(
            shell.CommandLine,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            tab.TerminalEmulator.Columns,
            tab.TerminalEmulator.Rows
        );
        tab.AttachTransport(stream);
        tab.Start();
        tab.ConnectionStatus = SessionStatus.Connected;
        tab.ResetReconnectAttempts();
        StartSessionLogging(tab, settings);
        UpdateStatusBarForActiveTab();
    }

    /// <summary>创建一个空白占位 FileBrowserViewModel 用于隐藏底部面板。</summary>
    private FileBrowserViewModel CreatePlaceholderFileBrowser() =>
        new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };

    /// <summary>
    /// 将 SFTP 文件浏览器指向活动标签的会话(#22)。每个已连接标签有一个根植于自己会话的浏览器;
    /// 未连接会话时面板显示为空。
    /// 面板实例按会话缓存:切回已看过的标签直接复用(旧列表秒显 + 后台静默刷新),
    /// 保留浏览路径/排序/列宽,不再每次切换都重建对象、重新列目录。
    /// 缓存的驱逐点:标签关闭与连接断开(<see cref="EvictFileBrowser" />)。
    /// </summary>
    private void RebindFileBrowser()
    {
        if (_sftpService is null)
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab is null)
        {
            return;
        }

        // 本地终端(ConPTY)与插件终端协议(Telnet…)都没有 SFTP 会话:不得继续展示
        // 上一个 SSH 会话的文件面板,否则切到 PowerShell / Telnet 标签后下方仍显示上一个
        // SSH 会话的文件面板。换成隐藏的空占位;上一个面板不 Detach(仍按其会话缓存),
        // 其开关状态留在缓存实例与所属标签上,切回远程标签时恢复展示。
        // 注意不能靠下面那句 SessionId == Guid.Empty 兜底 —— 它是 return 而不是换占位。
        if (tab.LocalShell is not null || tab.Profile?.ConnectionType == ConnectionType.Plugin)
        {
            if (FileBrowser.SessionId != Guid.Empty || FileBrowser.IsVisible)
            {
                FileBrowser = CreatePlaceholderFileBrowser();
            }
            return;
        }
        if (tab.SessionId == Guid.Empty)
        {
            return;
        }
        if (FileBrowser.SessionId == tab.SessionId)
        {
            return;
        }

        // 切回看过的标签:面板照它自己的状态恢复(开着的自动展示并静默刷新,
        // 关着的保持隐藏、不加载数据),与其他标签的开关互不影响。
        if (_fileBrowserCache.TryGetValue(tab.SessionId, out FileBrowserViewModel? cached))
        {
            FileBrowser = cached;
            if (cached.IsVisible)
            {
                _ = cached.RefreshSilentlyAsync();
            }
            return;
        }

        // 本标签首次建面板:初始开关取标签生命周期内记忆的状态(断线重连沿用),
        // 没有记忆时按设置「连接后自动打开文件浏览器」的当前值决定。
        bool wasVisible = tab.FileBrowserOpen
            ?? _latestSettings?.TerminalBehavior.AutoOpenFileBrowser
            ?? new TerminalBehaviorOptions().AutoOpenFileBrowser;
        tab.FileBrowserOpen = wasVisible;

        string serverName = tab.Profile is { } profile
            ? string.IsNullOrWhiteSpace(profile.Name)
                ? profile.Host
                : profile.Name
            : tab.Title;
        var browser = new FileBrowserViewModel(_sftpService, tab.SessionId)
        {
            TransferSink = FileTransfer,
            IsVisible = wasVisible,
            GetDefaultEditorPath = QueryDefaultEditorPathAsync,
            TransferOptions = _latestSettings?.Transfer ?? new TransferOptions(),
            ShowHiddenFiles = _latestSettings?.Transfer.ShowHiddenFiles ?? false,
            ShowHiddenFilesToggled = PersistShowHiddenFiles,

            // 列显示先按设置铺好,回调后挂:对象初始化器按书写顺序赋值,
            // 反过来会让这几行“初始化”被当成用户切换而回写一遍设置。
            ShowSizeColumn = _latestSettings?.Transfer.ShowSizeColumn ?? true,
            ShowPermissionsColumn = _latestSettings?.Transfer.ShowPermissionsColumn ?? true,
            ShowOwnerColumn = _latestSettings?.Transfer.ShowOwnerColumn ?? true,
            ShowGroupColumn = _latestSettings?.Transfer.ShowGroupColumn ?? true,
            ShowTypeColumn = _latestSettings?.Transfer.ShowTypeColumn ?? true,
            ShowModifiedColumn = _latestSettings?.Transfer.ShowModifiedColumn ?? true,
            ColumnVisibilityToggled = PersistColumnVisibility,
            ServerDisplayName = serverName,
            AccentBrush = tab.Profile is { } p ? ConnectionAccent.BrushFor(p.Id) : null,
        };
        // 「文件浏览器跟随终端目录」(map-pin):该会话终端 shell 的 cwd(OSC 7)变化 → 面板同步(仅在开启跟随时)。
        // 先播种当前已知 cwd(供开启开关时立即同步),再订阅后续变化。二者同会话、同生共死,eviction 时解绑。
        if (tab.TerminalWorkingDirectory is { } cwd)
        {
            browser.OnTerminalWorkingDirectoryChanged(cwd);
        }
        tab.WorkingDirectoryChanged += browser.OnTerminalWorkingDirectoryChanged;

        _fileBrowserCache[tab.SessionId] = browser;
        FileBrowser = browser;
        if (wasVisible)
        {
            // 全新面板首次展示:走初始加载(定位到登录家目录),而不是刷新根目录。
            FileBrowser.LoadInitialCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>
    /// 驱逐一个会话的缓存面板(标签关闭/连接断开):取消其在飞操作并移出缓存;
    /// 若当前面板正指向该会话,换成隐藏的空占位。
    /// </summary>
    private void EvictFileBrowser(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }
        if (_fileBrowserCache.Remove(sessionId, out FileBrowserViewModel? cached))
        {
            // 解绑「跟随终端目录」订阅(该会话的终端标签 → 面板),再拆除面板。
            if (TabBar.Tabs.OfType<TerminalTabViewModel>().FirstOrDefault(t => t.SessionId == sessionId) is { } tab)
            {
                tab.WorkingDirectoryChanged -= cached.OnTerminalWorkingDirectoryChanged;
            }
            cached.Detach();
        }
        if (FileBrowser.SessionId != sessionId)
        {
            return;
        }
        FileBrowser.Detach();

        // 会话已死,面板收起(空面板没有可看内容);该标签的开关状态留在
        // TerminalTabViewModel.FileBrowserOpen(对象替换不触发状态跟踪),
        // 重连后由 RebindFileBrowser 按标签记忆恢复,不会被这里的隐藏传染。
        FileBrowser = new(_sftpService, Guid.Empty) { TransferSink = FileTransfer };
    }

    /// <summary>
    /// 把面板实例上的显示/隐藏变化回写到拥有该会话的标签
    /// (<see cref="TerminalTabViewModel.FileBrowserOpen" />),作为该标签的生命周期记忆。
    /// </summary>
    private void RememberFileBrowserStateForTab(Guid sessionId, bool visible)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }
        TerminalTabViewModel? owner = _tabBar.Tabs
            .OfType<TerminalTabViewModel>()
            .FirstOrDefault(t => t.SessionId == sessionId);
        owner?.FileBrowserOpen = visible;
        PersistAutoOpenFileBrowser(visible);
    }

    /// <summary>
    /// 用户手动开/关底部文件浏览器后,把最后选择写回设置,供下次启动与之后的新连接作为默认值。
    /// </summary>
    private void PersistAutoOpenFileBrowser(bool visible)
    {
        if (_settingsService is null)
        {
            return;
        }
        lock (_fileBrowserPreferenceSaveSync)
        {
            _fileBrowserPreferenceSaveTail = _fileBrowserPreferenceSaveTail
                .ContinueWith(
                    _ => PersistAutoOpenFileBrowserAsync(visible),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task PersistAutoOpenFileBrowserAsync(bool visible)
    {
        try
        {
            AppSettings settings = await _settingsService!
                .GetSettingsAsync()
                .ConfigureAwait(false);
            if (settings.TerminalBehavior.AutoOpenFileBrowser == visible)
            {
                return;
            }
            settings.TerminalBehavior.AutoOpenFileBrowser = visible;
            await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
        }
        catch
        {
            // 写回失败只影响下次启动的默认开关,不打断当前标签的面板状态。
        }
    }

    /// <summary>SFTP「使用默认编辑器打开」读取的编辑器命令(设置 → 文件传输 → 默认编辑器)。</summary>
    private async Task<string?> QueryDefaultEditorPathAsync()
    {
        if (_settingsService is null)
        {
            return null;
        }
        AppSettings settings = await _settingsService.GetSettingsAsync();
        return settings.Transfer.DefaultEditorPath;
    }

    /// <summary>
    /// 切换活动会话的 SFTP 面板(#22,规范 §9)。打开时(若尚未绑定)将浏览器绑定到当前会话并加载初始列表。
    /// </summary>
    public void ToggleFileBrowser()
    {
        if (!CanToggleFileBrowser)
        {
            return;
        }
        // 在展示前确保浏览器指向活动标签(此时已连接)的会话。
        // 活动标签订阅靠自己做不到:会话 Id 在标签激活后才分配,因此我们也在此按需重新绑定。
        RebindFileBrowser();
        FileBrowser.IsVisible = !FileBrowser.IsVisible;
        if (FileBrowser.IsVisible && FileBrowser.SessionId != Guid.Empty)
        {
            RefreshOrLoadFileBrowser();
        }
    }

    /// <summary>
    /// 请求为当前会话打开链路追踪窗口。参数依次为目标主机与窗口标题用的会话名称;
    /// 与任务管理器一样由 MainWindow 承接(视图层才建得了窗口)。
    /// </summary>
    public event Action<string, string>? TraceRouteRequested;

    /// <summary>打开链路追踪窗口,目标默认取当前会话的主机。</summary>
    private void OpenTraceRoute()
    {
        if (!CanToggleFileBrowser || ActiveTerminalTab?.Profile is not { } profile)
        {
            return;
        }
        string label = string.IsNullOrWhiteSpace(profile.Name)
                           ? $"{profile.Host}:{profile.Port}"
                           : profile.Name;
        // 目标不用用户再抄一遍 IP —— 这正是内建追踪相对 mtr 的意义。
        TraceRouteRequested?.Invoke(profile.Host, label);
    }

    /// <summary>已加载过的面板静默刷新(保留旧列表秒显),从未加载过的走完整初始加载。</summary>
    private void RefreshOrLoadFileBrowser()
    {
        if (FileBrowser.HasLoaded)
        {
            _ = FileBrowser.RefreshSilentlyAsync();
        }
        else
        {
            FileBrowser.LoadInitialCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>
    /// 连接完成后调用:将文件浏览器绑定到该会话。 面板是否展示
    /// 由 <see cref="RebindFileBrowser" /> 按标签自己的状态决定(首次连接取设置
    /// 「连接后自动打开文件浏览器」的当前值,断线重连沿用标签生命周期内的记忆)。
    /// </summary>
    private void ShowFileBrowserForActiveSession()
    {
        RebindFileBrowser();
    }

    /// <summary>
    /// 用户通过菜单/面板请求终端内搜索时触发;窗口将其转发到活动终端视图的搜索栏(§5.3)。
    /// </summary>
    public event EventHandler? TerminalSearchRequested;

    /// <summary>请求视图聚焦当前活动终端。</summary>
    public event EventHandler? TerminalFocusRequested;

    /// <summary>导出终端输出(命令面板“导出终端输出到文件”)—— 窗口弹保存对话框并落盘。</summary>
    public event EventHandler? ExportBufferRequested;

    /// <summary>
    /// 取当前标签的导出内容:有选区导出选区,否则导出整个缓冲区;附建议文件名。
    /// 无活动终端时返回 null。
    /// </summary>
    public (string Text, string SuggestedFileName)? GetActiveTerminalExport()
    {
        if (ActiveTerminalControl is not { } control || ActiveTerminalTab is not { } tab)
        {
            return null;
        }
        string selection = control.GetSelectedText();
        string text = string.IsNullOrEmpty(selection) ? control.GetBufferText() : selection;
        string safeTitle = string.Concat(
            tab.Title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
        );
        if (safeTitle.Length > 40)
        {
            safeTitle = safeTitle[..40];
        }
        return (text, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }

    /// <summary>Ctrl+N / 菜单 / 命令面板“新建 SSH 连接” —— 由窗口打开新建连接弹窗。</summary>
    public event EventHandler? NewConnectionRequested;

    /// <summary>Ctrl+, / 菜单 / 侧边栏齿轮“打开设置” —— 由窗口打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// 打开设置并直接落到某一分区 —— 由窗口打开设置窗口后调 <c>SelectSection</c>。
    /// 消息中心的「有可用更新」就走这条路进「关于」页,用户点完通知即可就地更新,
    /// 而不是被丢在设置首页自己去找。
    /// </summary>
    public event EventHandler<SettingsSectionKey>? SettingsSectionRequested;

    /// <summary>工具菜单“连接诊断”(针对当前标签的配置)—— 由窗口打开诊断中心弹窗。</summary>
    public event Action<SessionProfile>? DiagnosticsRequested;

    /// <summary>单例切换(规范 §17.2):再次打开时聚焦现有面板。</summary>
    public void ToggleTunnelPanel()
    {
        if (IsTunnelPanelOpen)
        {
            IsTunnelPanelOpen = false;
            return;
        }
        OpenTunnelPanel();
    }

    /// <summary>
    /// 打开隧道面板(可选预选某台服务器)。面板以服务器为中心、生命周期与终端
    /// 会话无关:无需先打开终端标签,创建隧道时由面板后台自建 SSH 连接。
    /// </summary>
    public void OpenTunnelPanel(SessionProfile? preselect = null)
    {
        // 隧道要跑在 SSH 连接上:纯文件协议(FTP / 插件协议)没有可承载端口转发的通道,
        // 拿它们预选只会开出一个永远连不上的面板。SFTP 虽走 SSH,但按既有约定同样不从这里进。
        if (preselect?.ConnectionType is ConnectionType.SFTP or ConnectionType.FTP or ConnectionType.Plugin)
        {
            return;
        }
        if (_tunnelWorkflowService is null)
        {
            return;
        }
        if (TunnelPanel is null)
        {
            Func<Task<IReadOnlyList<SessionProfile>>>? servers = _sessionRepository is null
                ? null
                : async () => await _sessionRepository.GetAllSessionsAsync();
            var panel = new TunnelPanelViewModel(
                _tunnelWorkflowService,
                servers,
                ConnectTunnelHostAsync,
                id => _sshConnectionService?.GetClient(id)?.IsConnected == true,
                id => _connectionWorkflowService?.DisconnectAsync(id) ?? Task.CompletedTask,
                _appDataStore
            );
            panel.CloseRequested += (_, _) => IsTunnelPanelOpen = false;
            TunnelPanel = panel;
        }
        _ = TunnelPanel.OpenAsync(preselect?.Id ?? ActiveTerminalTab?.Profile?.Id);
        IsTunnelPanelOpen = true;
    }

    // ---- 消息中心(侧边栏铃铛) ----

    private INotificationCenter? _notificationCenter;

    /// <summary>
    /// 装配消息中心:面板、铃铛角标,以及两个内容来源(本地的更新检查 + 订阅的资讯源)。
    /// 无消息中心(单元测试)时整块跳过。
    /// </summary>
    private void SetUpNotificationCenter(
        INotificationCenter? center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        if (center is null)
        {
            return;
        }
        _notificationCenter = center;
        var panel = new NotificationPanelViewModel(center, Commands.Execute, OpenExternalUrlAsync, _appDataStore);
        panel.CloseRequested += (_, _) => IsNotificationPanelOpen = false;
        NotificationPanel = panel;

        // 铃铛角标:未读数由消息中心推给侧边栏,侧边栏不关心它从哪来。
        center.Changed += () => RxSchedulers.MainThreadScheduler.Schedule(() =>
            Sidebar.NotificationUnreadCount = center.UnreadCount);
        Sidebar.NotificationsRequested += (_, _) => IsNotificationPanelOpen = !IsNotificationPanelOpen;

        _ = InitializeNotificationsAsync(center, feed, updateService);

        // 周期拉取。计时器按固定的半小时跳,真正拉不拉由 FeedIntervalHours 决定 ——
        // 这样用户在设置里改完间隔,下一跳就生效,不用重启也不用去重设计时器。
        // 无 Avalonia 应用(单元测试)时不起表。
        if (Application.Current is null || feed is null)
        {
            return;
        }
        _feedTimer = new()
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _feedTimer.Tick += (_, _) =>
        {
            AppSettings settings = _latestSettings ?? new();
            var due = TimeSpan.FromHours(Math.Max(1, settings.Notifications.FeedIntervalHours));
            if (DateTime.UtcNow - _lastFeedFetch < due)
            {
                return;
            }
            _lastFeedFetch = DateTime.UtcNow;
            _ = RefreshNotificationSourcesAsync(center, feed, updateService: null);
        };
        _feedTimer.Start();
    }

    private DispatcherTimer? _feedTimer;

    /// <summary>上次拉取资讯源的时刻,用于按 <c>FeedIntervalHours</c> 判断是否到点。</summary>
    private DateTime _lastFeedFetch = DateTime.UtcNow;

    /// <summary>
    /// 载入历史消息,再拉一次内容源。整段吞异常:消息中心是锦上添花的东西,
    /// 它出问题不该拦住应用启动。
    /// </summary>
    private async Task InitializeNotificationsAsync(
        INotificationCenter center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        try
        {
            await center.LoadAsync().ConfigureAwait(true);
            Sidebar.NotificationUnreadCount = center.UnreadCount;
            await RefreshNotificationSourcesAsync(center, feed, updateService).ConfigureAwait(true);
        }
        catch
        {
            // 载入/拉取失败时铃铛照常可用,只是没有新内容。
        }
    }

    /// <summary>把「有可用更新」与订阅资讯源的内容投进消息中心。</summary>
    private async Task RefreshNotificationSourcesAsync(
        INotificationCenter center, IAnnouncementFeed? feed, IUpdateService? updateService)
    {
        AppSettings settings = _latestSettings ?? new();
        List<NotificationItem> incoming = [];
        if (updateService is not null && settings.Notifications.NotifyUpdates && settings.General.CheckUpdatesOnStartup)
        {
            if (await BuildUpdateNotificationAsync(updateService).ConfigureAwait(true) is { } update)
            {
                incoming.Add(update);
            }
        }
        if (feed is not null)
        {
            IReadOnlyList<NotificationItem> fetched = await feed.FetchAsync().ConfigureAwait(true);
            incoming.AddRange(settings.Notifications.AllowPromotions
                                  ? fetched
                                  : fetched.Where(item => item.Kind != NotificationKind.Promotion));
        }
        if (incoming.Count > 0)
        {
            await center.PublishAsync(incoming).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 检查更新,有新版本就攒一条消息。
    /// <para>
    /// 商店版直接跳过:安装目录只读、更新由 Microsoft Store 接管,推一条"去关于页更新"
    /// 只会把用户送到一个什么也做不了的页面。
    /// </para>
    /// </summary>
    private static async Task<NotificationItem?> BuildUpdateNotificationAsync(IUpdateService updateService)
    {
        if (updateService.IsStoreManaged)
        {
            return null;
        }
        bool hasUpdate;
        try
        {
            hasUpdate = await updateService.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch
        {
            // 离线或更新源不可达是常态。
            return null;
        }
        if (!hasUpdate || updateService.AvailableVersion is not { Length: > 0 } available)
        {
            return null;
        }
        return new()
        {
            // id 里带上版本号:同一个版本每次启动都会重投,靠它去重并保住已读状态;
            // 真出了新版本则是一条新 id,会重新亮起未读。
            Id = $"update:{available}",
            Kind = NotificationKind.Update,
            Title = Strings.Format("Notify_UpdateTitle", available),
            Body = Strings.Format("Notify_UpdateBody", updateService.CurrentVersion ?? "?"),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_UpdateAction"),
                CommandId = "app.settings.about"
            }
        };
    }

    /// <summary>在系统浏览器里打开外链(由消息中心的外链条目调用)。</summary>
    private Task OpenExternalUrlAsync(string url)
    {
        ExternalUrlRequested?.Invoke(this, url);
        return Task.CompletedTask;
    }

    /// <summary>请求在系统浏览器中打开一个网址 —— 由窗口层执行(ViewModel 不碰 TopLevel)。</summary>
    public event EventHandler<string>? ExternalUrlRequested;

    /// <summary>为隧道面板后台建立 SSH 连接:不开终端标签,凭据缺失时走登录验证弹窗。</summary>
    private async Task<Guid> ConnectTunnelHostAsync(
        SessionProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (_connectionWorkflowService is null)
        {
            throw new InvalidOperationException(Strings.Get("Msg_SshServiceNotConfigured"));
        }
        SessionProfile current = profile;
        if (RequiresCredentials(current))
        {
            SessionProfile? updated = InteractiveAuthenticator is { } prompt
                ? await prompt(current)
                : null;
            current =
                updated
                ?? throw new InvalidOperationException(Strings.Get("Msg_AuthPromptCancelled"));
        }
        SshSession session = await _connectionWorkflowService.ConnectProfileAsync(
            current,
            cancellationToken
        );
        return session.SessionId;
    }

    /// <summary>
    /// 每秒轮询一次活动会话的指标到状态栏中。探测运行在专用 SSH exec 通道上,
    /// 因此绝不触碰终端流;连续采样让采集器获得真实的瞬时 CPU% 和网络速率。
    /// </summary>
    private void StartStatusMetricsPolling()
    {
        // 无头单元测试在没有 Avalonia 应用的情况下构造此 VM;应跳过。
        // 延迟测量(ICMP)不依赖 metrics 服务,所以只要有 UI 就启动计时器。
        if (Application.Current is null)
        {
            return;
        }
        _statusMetricsTimer = new(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _ = PollStatusMetricsAsync();
                _ = PollLatencyAsync();
            }
        );
        _statusMetricsTimer.Start();
    }

    /// <summary>
    /// 窗口最小化/隐入托盘时暂停状态栏指标与延迟轮询(由视图按 WindowState 驱动):
    /// 用户看不见状态栏时,每秒一次的 SSH exec 探测 + 周期 ICMP 纯属浪费 CPU/网络,
    /// 还会阻止系统进入低功耗。恢复可见即重启,下一秒就有新数据。
    /// </summary>
    public void SetStatusPollingSuspended(bool suspended)
    {
        if (_statusMetricsTimer is null)
        {
            return;
        }
        if (suspended)
        {
            _statusMetricsTimer.Stop();
        }
        else if (!_statusMetricsTimer.IsEnabled)
        {
            _statusMetricsTimer.Start();
        }
    }

    /// <summary>
    /// 状态栏延迟指示(设计 gzmsb sbLatency,之前缺失):每 3 秒对活动标签的主机
    /// 发一次 ICMP ping,RTT 写入 tab.Latency(经既有 WhenAnyValue 管道刷新状态栏)。
    /// 目标禁 ICMP 或解析失败时清空显示,不打扰;不用 TCP 探测以免刷爆 sshd 日志。
    /// </summary>
    private async Task PollLatencyAsync()
    {
        if (_latencyPolling || _latencyTick++ % 3 != 0)
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab?.Profile is null || tab.ConnectionStatus != SessionStatus.Connected)
        {
            tab?.Latency = null;
            return;
        }
        _latencyPolling = true;
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(tab.Profile.Host, TimeSpan.FromSeconds(2));

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                return;
            }
            tab.Latency =
                reply.Status == IPStatus.Success
                    ? TimeSpan.FromMilliseconds(reply.RoundtripTime)
                    : null;
        }
        catch
        {
            tab.Latency = null;
        }
        finally
        {
            _latencyPolling = false;
        }
    }

    private async Task PollStatusMetricsAsync()
    {
        if (_statusMetricsPolling || _metricsService is null)
        {
            return;
        }
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (
            tab is null
            || tab.SessionId == Guid.Empty
            || tab.ConnectionStatus != SessionStatus.Connected
        )
        {
            StatusBar.ClearSessionMetrics();
            return;
        }
        _statusMetricsPolling = true;
        try
        {
            SessionMetrics? metrics = await _metricsService.GetMetricsAsync(tab.SessionId);

            // 探测期间用户可能切换了标签;不要把结果写到别的会话上。
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                return;
            }
            if (metrics is null)
            {
                StatusBar.ClearSessionMetrics();
                return;
            }
            StatusBar.CpuUsage = $"{metrics.CpuPercent:F2}%";
            StatusBar.MemUsage = $"{metrics.MemPercent:F1}%";
            StatusBar.SwapUsage = metrics.SwapTotalBytes > 0 ? $"{metrics.SwapPercent:F1}%" : "--";
            StatusBar.DiskUsage = metrics.DiskTotalBytes > 0 ? $"{metrics.DiskPercent:F1}%" : "--";
            StatusBar.UpdateNetwork(
                metrics.NetRxBytesPerSec,
                metrics.NetTxBytesPerSec,
                metrics.HasNetRates
            );

            // CPU 逐核心、磁盘逐挂载点、网速逐网卡的悬停提示详情。
            StatusBar.CpuTooltip = BuildCpuTooltip(metrics);
            StatusBar.MemTooltip = BuildMemTooltip(metrics);
            StatusBar.DiskTooltip = BuildDiskTooltip(metrics);
            StatusBar.NetTooltip = BuildNetTooltip(metrics);
        }
        catch
        {
            // 绝不让失败的探测浮现到 UI 循环里;下次 tick 再重试。
        }
        finally
        {
            _statusMetricsPolling = false;
        }
    }

    private static string BuildCpuTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(Strings.Format("Msg_CpuTooltipTotal", m.CpuPercent, m.CpuCores));
        if (m.CorePercents is { Count: > 0 } percents)
        {
            string corePrefix = Strings.Get("Msg_CpuCorePrefix");
            for (int i = 0; i < percents.Count; i++)
            {
                string name =
                    i < m.CoreCounters.Count
                        ? m.CoreCounters[i].Name.Replace("cpu", corePrefix)
                        : $"{corePrefix}{i}";
                sb.Append('\n').Append($"{name}: {percents[i]:F0}%");
            }
        }
        else if (m.CoreCounters.Count > 0)
        {
            sb.Append('\n').Append(Strings.Get("Msg_PerCoreCollecting"));
        }
        return sb.ToString();
    }

    private static string BuildMemTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            Strings.Format(
                "Msg_MemTooltip",
                FormatGb(m.MemUsedBytes),
                FormatGb(m.MemTotalBytes),
                m.MemPercent
            )
        );
        if (m.SwapTotalBytes > 0)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_SwapTooltip",
                        FormatGb(m.SwapUsedBytes),
                        FormatGb(m.SwapTotalBytes),
                        m.SwapPercent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildDiskTooltip(SessionMetrics m)
    {
        if (m.Disks.Count == 0)
        {
            return m.DiskTotalBytes > 0
                ? Strings.Format(
                    "Msg_DiskRootTooltip",
                    FormatGb(m.DiskUsedBytes),
                    FormatGb(m.DiskTotalBytes),
                    m.DiskPercent
                )
                : Strings.Get("Msg_Disk");
        }
        var sb = new StringBuilder(Strings.Get("Msg_DiskUsage"));
        foreach (DiskUsage d in m.Disks)
        {
            sb.Append('\n')
                .Append(
                    Strings.Format(
                        "Msg_DiskMountLine",
                        d.MountPoint,
                        FormatGb(d.UsedBytes),
                        FormatGb(d.TotalBytes),
                        d.Percent
                    )
                );
        }
        return sb.ToString();
    }

    private static string BuildNetTooltip(SessionMetrics m)
    {
        var sb = new StringBuilder();
        sb.Append(
            m.HasNetRates
                ? Strings.Format(
                    "Msg_NetTooltipTotal",
                    StatusBarViewModel.FormatRate(m.NetRxBytesPerSec),
                    StatusBarViewModel.FormatRate(m.NetTxBytesPerSec)
                )
                : Strings.Get("Msg_NetCollecting")
        );
        if (m.NicRates is not { Count: > 0 } rates)
        {
            return sb.ToString();
        }
        foreach (NetInterfaceRate r in rates)
        {
            sb.Append('\n')
                .Append(
                    $"{r.Name}: ↓ {StatusBarViewModel.FormatRate(r.RxBytesPerSec)}  ↑ {StatusBarViewModel.FormatRate(r.TxBytesPerSec)}"
                );
        }
        return sb.ToString();
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");

    /// <summary>
    /// 加载已持久化的最近连接历史(SonnetDB)到侧边栏,使重启后仍保留。
    /// </summary>
    public async Task InitializeAsync()
    {
        await CommandHistory.LoadAsync();
        if (_quickCommands is not null)
        {
            await _quickCommands.LoadAsync();
        }
        if (_settingsService is not null)
        {
            _appState = await _settingsService.GetStateAsync();
            ApplySidebarState(_appState);
            ApplyShellPreferences(await LoadSettingsSnapshotAsync());
        }
        await Sidebar.RecentConnections.RefreshAsync();
        await RefreshSessionTreeAsync();
        RevealActiveSessionInSidebar();
    }

    private void ApplyShellPreferences(AppSettings settings)
    {
        Sidebar.IsQuickCommandsVisible =
            _quickCommandRunner is not null && settings.Appearance.ShowQuickCommandsPanel;
    }

    private void ApplySidebarState(AppState state)
    {
        _isApplyingSidebarState = true;
        try
        {
            Sidebar.QuickCommandsExpanded = state.SidebarQuickCommandsExpanded;
            Sidebar.QuickCommandsHeight = NormalizeSidebarHeight(
                state.SidebarQuickCommandsHeight,
                160
            );
            Sidebar.RecentConnectionsExpanded = state.SidebarRecentConnectionsExpanded;
            Sidebar.RecentConnectionsHeight = NormalizeSidebarHeight(
                state.SidebarRecentConnectionsHeight,
                180
            );
        }
        finally
        {
            _isApplyingSidebarState = false;
        }
        CaptureSidebarState();
    }

    private static double NormalizeSidebarHeight(double height, double fallback) =>
        double.IsFinite(height) ? Math.Clamp(height, 100, 1200) : fallback;

    private void OnSidebarStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            _isApplyingSidebarState
            || _settingsService is null
            || e.PropertyName
                is not (
                    nameof(SidebarViewModel.QuickCommandsExpanded)
                    or nameof(SidebarViewModel.QuickCommandsHeight)
                    or nameof(SidebarViewModel.RecentConnectionsExpanded)
                    or nameof(SidebarViewModel.RecentConnectionsHeight)
                )
        )
        {
            return;
        }
        CaptureSidebarState();
        CancellationTokenSource next = new();
        _sidebarStateSaveDebounce?.Cancel();
        _sidebarStateSaveDebounce = next;
        _ = SaveSidebarStateAfterDelayAsync(next.Token);
    }

    private void CaptureSidebarState()
    {
        _appState.SidebarQuickCommandsExpanded = Sidebar.QuickCommandsExpanded;
        _appState.SidebarQuickCommandsHeight = NormalizeSidebarHeight(
            Sidebar.QuickCommandsHeight,
            160
        );
        _appState.SidebarRecentConnectionsExpanded = Sidebar.RecentConnectionsExpanded;
        _appState.SidebarRecentConnectionsHeight = NormalizeSidebarHeight(
            Sidebar.RecentConnectionsHeight,
            180
        );
    }

    private async Task SaveSidebarStateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (_settingsService is not null)
            {
                await _settingsService.SaveStateAsync(_appState).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 被更晚的折叠或拖动结果替代。
        }
        catch
        {
            // 布局状态保存失败不影响当前交互;关闭窗口时还会再尝试一次。
        }
    }

    internal async Task PersistSidebarStateAsync()
    {
        _sidebarStateSaveDebounce?.Cancel();
        CaptureSidebarState();
        if (_settingsService is not null)
        {
            await _settingsService.SaveStateAsync(_appState).ConfigureAwait(false);
        }
    }

    private void RevealActiveSessionInSidebar(TerminalTabViewModel? tab = null)
    {
        if ((_latestSettings?.General.FollowActiveTerminalInExplorer ?? true) != true)
        {
            return;
        }
        TerminalTabViewModel? target = tab ?? ActiveTerminalTab;
        if (target?.Profile is { Id: var profileId } && profileId != Guid.Empty)
        {
            Sidebar.SessionTree?.SelectSession(profileId);
        }
    }

    private void OnTabsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    )
    {
        var currentTabs = TabBar.Tabs.OfType<TerminalTabViewModel>().ToHashSet();
        foreach (
            TerminalTabViewModel removed in _quickCommandTargetSubscriptions
                .Keys.Where(tab => !currentTabs.Contains(tab))
                .ToArray()
        )
        {
            _quickCommandTargetSubscriptions.Remove(removed, out IDisposable? subscription);
            subscription?.Dispose();
            _sessionStatusSubscriptions.Remove(removed, out IDisposable? statusSubscription);
            statusSubscription?.Dispose();
            _syncInput.Detach(removed);
            // 标签离开标签栏(关闭、连接失败或取消后静默移除)→ 重算它那条配置的树上状态,
            // 否则节点会停在这个已经不存在的标签留下的状态上(#321)。
            if (removed.Profile is { Id: var removedProfileId } && removedProfileId != Guid.Empty)
            {
                RefreshSessionStatus(removedProfileId);
            }
        }
        foreach (TerminalTabViewModel added in currentTabs)
        {
            _syncInput.Attach(added);
            // 资源管理器树的状态圆点与「活跃/连接中/离线」标签(设计 FrJPu)跟随该配置
            // 名下**所有**标签的合并状态;订阅随标签在标签栏里的存续期存在。
            if (
                !_sessionStatusSubscriptions.ContainsKey(added)
                && added.Profile is { Id: var addedProfileId }
                && addedProfileId != Guid.Empty
            )
            {
                _sessionStatusSubscriptions[added] = added
                    .WhenAnyValue(tab => tab.ConnectionStatus)
                    .Subscribe(_ => RefreshSessionStatus(addedProfileId));
            }
            if (_quickCommandTargetSubscriptions.ContainsKey(added))
            {
                continue;
            }
            _quickCommandTargetSubscriptions[added] = added
                // ConnectionStatus 在 TerminalTabViewModel 更新 IsConnected 之前触发。
                // 观察 IsConnected 本身,使刷新看到最终可用的状态。
                .WhenAnyValue(tab => tab.IsConnected, tab => tab.Title)
                .Subscribe(_ => RefreshQuickCommandTargets());
        }
        RefreshQuickCommandTargets();
    }

    /// <summary>
    /// 重算某配置在会话树里的同步输入频道字母:该配置可能开着多个标签(复制会话),
    /// 取其中第一个已加入频道的;全部不在频道时上报空串清除标识。
    /// </summary>
    private void RefreshSessionSyncChannel(Guid profileId)
    {
        SyncInputChannel? channel = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Where(tab => tab.Profile?.Id == profileId)
            .Select(tab => tab.SyncChannel)
            .FirstOrDefault(c => c is not null);
        Sidebar.SessionTree?.SetSessionSyncChannel(
            profileId,
            channel?.ToString() ?? string.Empty
        );
    }

    /// <summary>
    /// 重算某配置在会话树里的状态标签:一条配置可以同时开着多个标签(复制会话、
    /// 对同一台机器再开一个),而树上只有一个节点 —— 取这些标签里"最活跃"的那个状态,
    /// 而不是最后一次变更的那个标签的状态。
    /// <para>
    /// 合并优先级 Connected &gt; Connecting &gt; Error &gt; Disconnected:一条已经连上的会话
    /// 不该因为旁边多了个正在握手或握手失败的标签而被写成「连接中」/「离线」。
    /// 按"最后一次变更"来更新会留下一个走不出去的状态(#321:在已连上的会话上再开一个
    /// 标签、趁它还在连接时立刻关掉,节点会永远停在「连接中」)。
    /// </para>
    /// <para>没有任何标签属于该配置时归零为 Disconnected —— 最后一个标签关掉即回到未连接。</para>
    /// </summary>
    private void RefreshSessionStatus(Guid profileId)
    {
        SessionStatus status = TabBar
            .Tabs.OfType<TerminalTabViewModel>()
            .Where(tab => tab.Profile?.Id == profileId)
            .Aggregate(
                SessionStatus.Disconnected,
                (best, tab) =>
                    SessionStatusRank(tab.ConnectionStatus) > SessionStatusRank(best)
                        ? tab.ConnectionStatus
                        : best
            );
        Sidebar.SessionTree?.SetSessionStatus(profileId, status);
    }

    /// <summary>多标签合并时的状态优先级,数值越大越"活跃"(见 <see cref="RefreshSessionStatus" />)。</summary>
    private static int SessionStatusRank(SessionStatus status) =>
        status switch
        {
            SessionStatus.Connected => 3,
            SessionStatus.Connecting => 2,
            SessionStatus.Error => 1,
            _ => 0,
        };

    private void RefreshQuickCommandTargets()
    {
        (Guid Id, string DisplayName)[] targets = [.. TabBar.Tabs.OfType<TerminalTabViewModel>().Where(tab => tab.IsConnected).Select(tab => (tab.Id, tab.Title))];
        _terminalTargetSelector.UpdateTargets(targets);
        _terminalTargetSelector.SetCurrentTarget(ActiveTerminalTab is { IsConnected: true } current ? current.Id : null);
    }

    private void OnQuickCommandExecutionRequested(
        object? sender,
        QuickCommandExecutionRequest request
    )
    {
        var targetIds = request.TargetIds.ToHashSet();
        TerminalTabViewModel[] targets = [.. TabBar.Tabs.OfType<TerminalTabViewModel>().Where(tab => tab.IsConnected && targetIds.Contains(tab.Id))];
        bool sent = false;
        foreach (TerminalTabViewModel target in targets)
        {
            sent |= target.TrySendCommandText(request.CommandText);
        }
        if (sent)
        {
            TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 重新加载资源管理器会话树(新建/编辑/删除配置后调用),并同步刷新命令面板
    /// 的全量会话缓存。
    /// </summary>
    public async Task RefreshSessionTreeAsync()
    {
        if (Sidebar.SessionTree is { } tree)
        {
            try
            {
                await tree.LoadCommand.Execute().FirstAsync();
            }
            catch
            {
                // 树加载失败不影响其余启动流程。
            }
        }
        await RefreshPaletteSessionsAsync();
        RevealActiveSessionInSidebar();
    }

    /// <summary>
    /// 云同步在后台线程完成 Profile upsert 后刷新所有会话入口。除侧边栏树外还要刷新
    /// 命令面板的全量会话缓存,否则树已出现新连接而命令面板仍需重启才可搜索到。
    /// </summary>
    private void OnSyncProfilesApplied(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        RxSchedulers.MainThreadScheduler.Schedule(() => _ = RefreshSessionTreeAsync());
    }

    /// <summary>BuildPaletteItems 是同步回调,这里预取 session_profiles 全量与分组名。</summary>
    private async Task RefreshPaletteSessionsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<SessionProfile> profiles = await _sessionRepository.GetAllSessionsAsync();
            List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync();
            _paletteGroupNames = groups.ToDictionary(g => g.Id, g => g.Name);
            _paletteProfiles = [.. profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            // 面板会话缓存刷新失败不影响其余流程,下次刷新重试。
        }
    }

    private List<CommandPaletteItem> BuildPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // 最近连接优先 —— 快捷访问桶(Enter 连接)。
        var recentProfileIds = new HashSet<Guid>();
        foreach (RecentConnectionItemViewModel item in Sidebar.RecentConnections.Connections)
        {
            RecentConnectionEntry captured = item.Entry;
            if (captured.ProfileId is { } pid)
            {
                recentProfileIds.Add(pid);
            }
            string title = string.IsNullOrWhiteSpace(item.DisplayName)
                ? captured.Host
                : item.DisplayName;
            items.Add(
                new(
                    Strings.Get("RecentConnections"),
                    title,
                    () => _ = TryConnectRecentAsync(captured),
                    Strings.Get("Msg_EnterToConnect"),
                    isSession: true
                )
            );
        }

        // 全部已保存配置(§12.3),带分组徽章;已出现在最近连接里的不重复列出。
        foreach (SessionProfile profile in _paletteProfiles)
        {
            if (recentProfileIds.Contains(profile.Id))
            {
                continue;
            }
            SessionProfile captured = profile;
            string? groupName =
                captured.GroupId is { } groupId
                && _paletteGroupNames.TryGetValue(groupId, out string? name)
                    ? name
                    : null;
            items.Add(
                new(
                    Strings.Get("Sessions"),
                    string.IsNullOrWhiteSpace(captured.Name) ? captured.Host : captured.Name,
                    () => _ = TryConnectProfileAsync(captured),
                    Strings.Get("Msg_EnterToConnect"),
                    groupName,
                    true
                )
            );
        }

        // 全局操作来自共享命令注册表(菜单/面板/快捷键一致)。
        items.AddRange(
            Commands.All.Select(captured => new CommandPaletteItem(
                Strings.Get("Command"),
                captured.Title,
                () => Commands.Execute(captured.Id),
                captured.Shortcut
            ))
        );
        return items;
    }

    /// <summary>读取设置快照并缓存到 <see cref="_latestSettings" />(无设置服务时用默认值)。</summary>
    private async Task<AppSettings> LoadSettingsSnapshotAsync()
    {
        AppSettings settings = _settingsService is not null
            ? await _settingsService.GetSettingsAsync()
            : new();
        _latestSettings = settings;
        return settings;
    }

    /// <summary>
    /// 立即创建一个“连接中”的终端标签并加入标签栏/停靠区(#17:慢连接不再像卡死,
    /// 用户立刻拿到可见、可关闭的标签)。握手由 <see cref="RunHandshakeAsync" /> 完成。
    /// 认证重试会复用同一标签,不重复建标签。
    /// </summary>
    private (TerminalTabViewModel Tab, TerminalDocument Document) CreateConnectingTab(
        SessionProfile profile,
        AppSettings settings,
        string? protocolLabel = null
    )
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        ITerminalEmulator terminalEmulator = _terminalEmulatorFactory();
        ConfigureTerminal(terminalEmulator, settings, terminalType);

        // 状态栏连接指示按设计 gzmsb 显示"SSH • <显示名称>"——不暴露用户名与 IP(安全要求);
        // 未配置名称时才退回主机地址。
        string displayName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        // 协议名默认 SSH;插件终端协议(Telnet…)传自己的页签名进来 ——
        // 状态栏写死 "SSH • xxx" 会让一条 Telnet 会话看起来是加密的。
        string protocol = string.IsNullOrWhiteSpace(protocolLabel) ? "SSH" : protocolLabel;
        var terminalTab = new TerminalTabViewModel(terminalEmulator)
        {
            Title = displayName,
            ConnectionStatus = SessionStatus.Connecting,
            // 配了跳板的会话在状态栏点明经由跳板,确认链路生效。
            ConnectionSummary = profile.JumpHostProfileId is null
                ? $"{protocol} • {displayName}"
                : $"{protocol} • {displayName} • {Strings.Get("Msg_ViaJumpHost")}",
            TerminalTypeName = terminalType.ToTermName(),
            EncodingName = string.IsNullOrWhiteSpace(settings.TerminalEncoding)
                ? "UTF-8"
                : settings.TerminalEncoding,
            Profile = profile,
        };
        terminalTab.ReconnectRequested += (_, _) => _ = ReconnectTabAsync(terminalTab);
        terminalTab.Disconnected += (_, _) => OnTabDisconnected(terminalTab);

        // 命令补全:注入建议提供器;提交(已回显校验)的命令进全局历史。
        terminalTab.SuggestionProvider = _suggestionProvider;
        WireZModemDownload(terminalTab);
        terminalTab.CommandLineSubmitted += CommandHistory.Record;

        // 树上的状态圆点与「活跃/连接中/离线」标签不在这里订阅:一条配置可能同时开着
        // 多个标签,得取合并结果而不是某一个标签的状态。订阅随标签进出标签栏在
        // OnTabsCollectionChanged 里挂上与退订(#321)。

        // 资源管理器树节点名前的同步输入频道字母跟随该配置任一标签的频道归属;
        // 标签关闭时经 SyncInputCoordinator.Detach → LeaveSyncChannel 同样走到这里复位。
        terminalTab
            .WhenAnyValue(x => x.SyncChannel)
            .Subscribe(_ => RefreshSessionSyncChannel(profile.Id));

        // 后台标签收到 BEL → 点亮闪烁提醒(设置 → 终端 → 标签闪烁提醒);切回标签时清除。
        if (terminalEmulator is VelaTerminalControl bellSource)
        {
            bellSource.BellRang += () =>
            {
                if (
                    _latestSettings?.TerminalBehavior.TabFlashAlert != false
                    && !ReferenceEquals(ActiveTerminalTab, terminalTab)
                )
                {
                    terminalTab.HasBellAlert = true;
                }
            };
        }
        var document = new TerminalDocument(terminalTab);
        // 标签页内失败覆盖层(设计 yxjmg)的“关闭标签页”按钮:闭包捕获 document 以整体移除。
        terminalTab.CloseRequested += (_, _) => CloseTerminalTab(terminalTab);
        TabBar.AddTab(terminalTab);
        ActiveTerminalTab = terminalTab;
        Layout.AddDocument(document);
        UpdateStatusBarForActiveTab();
        return (terminalTab, document);
    }

    /// <summary>
    /// 在一个已存在的“连接中”标签上完成 SSH 握手并挂上传输;失败时向上抛,由调用方
    /// 决定撤标签(直接入口)还是保留标签显示覆盖层(交互入口)。
    /// </summary>
    private async Task RunHandshakeAsync(
        TerminalTabViewModel terminalTab,
        SessionProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken
    )
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        SshSession session = await _connectionWorkflowService!.ConnectProfileAsync(
            profile,
            cancellationToken
        );
        ISshClientWrapper client =
            _sshConnectionService!.GetClient(session.SessionId)
            ?? throw new InvalidOperationException("SSH client was not created for the session.");
        // 先问一句对端是不是 POSIX shell,再决定要不要注入目录上报钩子(#305)。
        // 独立 exec 通道,用完即关;每台主机只探一次,之后走缓存。
        bool isPosixShell = await ProbePosixShellAsync(client, profile, settings, cancellationToken);
        // 通道打开是网络往返(pty-req + shell,2~3 个 RTT);真异步 API,UI 线程零阻塞。
        IShellStreamWrapper shellStream = await client.CreateShellStreamAsync(
            terminalType.ToTermName(), 120, 32, 0, 0, 4096,
            cancellationToken: cancellationToken
        );
        terminalTab.SessionId = session.SessionId;
        terminalTab.AttachTransport(shellStream);
        terminalTab.Start();
        terminalTab.ConnectionStatus = SessionStatus.Connected;
        await FeedJumpChainNoticeAsync(terminalTab, profile);
        StartSessionLogging(terminalTab, settings);
        SendStartupCommand(terminalTab, settings, isPosixShell);

        // 会话 Id 从现在起才存在(握手完成后)——活动标签订阅在它被赋值前已触发,
        // 因此在这里绑定 SFTP 浏览器(并展示+加载),否则它将一直指向空占位,永远加载不到列表(#22)。
        ShowFileBrowserForActiveSession();
        if (_metricsService is not null)
        {
            terminalTab.ResourceMonitor = new(
                _metricsService,
                session.SessionId,
                terminalTab.Title
            );
        }

        // 连接历史已由工作流写入 SonnetDB,这里刷新侧边栏“最近连接”。
        await Sidebar.RecentConnections.RefreshAsync();
        StatusBar.ResetUptime();
        UpdateStatusBarForActiveTab();
        LastConnectionError = null;
    }

    /// <summary>
    /// 在同一位置重连一个已断开的会话:复用同一个标签、模拟器与回滚缓冲,
    /// 仅重建传输层。由已断开标签上的 Enter / Ctrl+R 触发(或在 exit/reboot 后),
    /// 省去用户打开新标签的操作(#19)。
    /// </summary>
    public async Task ReconnectTabAsync(
        TerminalTabViewModel tab,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);

        // 忽略正在连接或已连接时的重连请求。
        if (tab.ConnectionStatus is SessionStatus.Connecting or SessionStatus.Connected)
        {
            return;
        }

        // 本地终端标签:重开 = 重新拉起 shell 进程(复用同一标签与缓冲)。
        if (tab.LocalShell is { } localShell)
        {
            ReopenLocalShell(tab, localShell);
            return;
        }

        // 插件终端协议(Telnet…):它们没有 SSH 会话,落进下面的 SSH 重连路径会
        // 拿 Telnet 的主机端口去做 SSH 握手 —— 表现为"重连一次就报认证失败"。
        if (tab.Profile is { ConnectionType: ConnectionType.Plugin } pluginProfile)
        {
            await ReconnectPluginTerminalAsync(tab, pluginProfile, cancellationToken).ConfigureAwait(true);
            return;
        }
        if (
            tab.Profile is null
            || _connectionWorkflowService is null
            || _sshConnectionService is null
        )
        {
            return;
        }
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        UpdateStatusBarForActiveTab();
        try
        {
            AppSettings settings = _settingsService is not null
                ? await _settingsService.GetSettingsAsync()
                : new();
            _latestSettings = settings;
            TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
            SshSession session = await _connectionWorkflowService.ConnectProfileAsync(
                tab.Profile,
                cancellationToken
            );
            ISshClientWrapper client =
                _sshConnectionService.GetClient(session.SessionId)
                ?? throw new InvalidOperationException(
                    "SSH client was not created for the session."
                );
            // 同 RunHandshakeAsync:注入前先确认对端是 POSIX shell(#305)。首连已探过的主机命中缓存,不再发探针。
            bool isPosixShell = await ProbePosixShellAsync(client, tab.Profile, settings, cancellationToken);
            // 同 RunHandshakeAsync:通道打开走真异步 API,UI 线程零阻塞。
            IShellStreamWrapper shellStream = await client.CreateShellStreamAsync(
                terminalType.ToTermName(), 120, 32, 0, 0, 4096,
                cancellationToken: cancellationToken
            );

            // 在新会话输出到达前做一次完全复位(RIS),使新的标语不至于附加在旧缓冲内容之后。
            tab.TerminalEmulator.Feed("\ec"u8.ToArray());
            tab.SessionId = session.SessionId;
            tab.AttachTransport(shellStream);
            tab.Start();
            tab.ConnectionStatus = SessionStatus.Connected;
            await FeedJumpChainNoticeAsync(tab, tab.Profile);
            tab.ResetReconnectAttempts();
            StartSessionLogging(tab, settings);
            SendStartupCommand(tab, settings, isPosixShell);
            if (_metricsService is not null)
            {
                tab.ResourceMonitor = new(_metricsService, session.SessionId, tab.Title);
            }

            // 重连产生全新的会话 id;重新绑定 SFTP 浏览器并重新加载(#22)。
            ShowFileBrowserForActiveSession();
            StatusBar.ResetUptime();
            UpdateStatusBarForActiveTab();
            LastConnectionError = null;
        }
        catch (OperationCanceledException)
        {
            tab.MarkDisconnected();
        }
        catch (Exception ex)
        {
            // 重连失败:保留标签,标签页内覆盖层显示“连接失败 + 原因”(设计 yxjmg),不弹全局框。
            LastConnectionError = DescribeConnectionError(ex, tab.Profile);
            StatusBar.Status = LastConnectionError;
            tab.MarkDisconnected(LastConnectionError);
        }
    }

    /// <summary>
    /// 经由跳板建立的会话在终端顶部显示灰色提示,标注实际经过的跳板链路。
    /// 纯装饰,失败不影响连接。
    /// </summary>
    private async Task FeedJumpChainNoticeAsync(TerminalTabViewModel tab, SessionProfile profile)
    {
        if (_sessionRepository is null || profile.JumpHostProfileId is null)
        {
            return;
        }
        try
        {
            var names = new List<string>();
            var visited = new HashSet<Guid> { profile.Id };
            Guid? jumpId = profile.JumpHostProfileId;
            while (jumpId is { } id && visited.Add(id) && names.Count < 5)
            {
                SessionProfile? jump = await _sessionRepository.GetSessionAsync(id);
                if (jump is null)
                {
                    break;
                }
                names.Add(string.IsNullOrWhiteSpace(jump.Name) ? jump.Host : jump.Name);
                jumpId = jump.JumpHostProfileId;
            }
            if (names.Count == 0)
            {
                return;
            }

            // 配置里跳板由内向外嵌套;反转成"本机 → 最外层跳板 → … → 目标"的阅读顺序。
            names.Reverse();
            string target = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
            string notice =
                "\e[90m● "
                + Strings.Format("Msg_JumpChainNotice", string.Join(" → ", names), target)
                + "\e[0m\r\n";
            tab.TerminalEmulator.Feed(Encoding.UTF8.GetBytes(notice));
        }
        catch
        {
            // 提示为纯装饰,读取跳板名失败时静默跳过。
        }
    }

    /// <summary>
    /// 关闭标签背后的 SSH 会话:标签的 DisconnectCommand 只拆终端
    /// 传输层,底层 SshClient 仍保持 TCP 连接;这里显式断开并释放,避免"界面显示已断开、
    /// 连接实际还活着"。该会话上的隧道也一并停止。
    /// </summary>
    private void TeardownSshSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty || _connectionWorkflowService is null)
        {
            return;
        }
        ITunnelService? tunnelService = _tunnelService;
        _ = Task.Run(async () =>
        {
            if (tunnelService is not null)
            {
                try
                {
                    await tunnelService.StopAllForSessionAsync(sessionId);
                }
                catch
                {
                    // 隧道清理失败不阻塞断开。
                }
            }
            try
            {
                await _connectionWorkflowService.DisconnectAsync(sessionId);
            }
            catch
            {
                // 会话可能已被服务端拆除或从未完成握手。
            }
        });
    }

    /// <summary>开启后把该会话的原始输出写入日志文件;每次(重)连接换新文件。</summary>
    private void StartSessionLogging(TerminalTabViewModel tab, AppSettings settings)
    {
        StopSessionLogging(tab);
        if (settings.General.SessionLogging && tab.Bridge is not null)
        {
            SessionLogWriter? writer = SessionLogService.CreateWriter(tab.Title);
            if (writer is not null)
            {
                tab.Bridge.DataReceived += writer.Write;
                _sessionLogs[tab] = writer;
            }
        }

        // 会话录制(设置 → 安全审计):与会话日志同挂钩点(桥的原始输出),
        // 每次(重)连接产生一条新录制;开关只对之后建立的连接生效。
        if (
            settings.Security.RecordProductionSessions
            && _recordingStore is not null
            && tab.Bridge is not null
        )
        {
            var recorder = new SessionRecorder(_recordingStore, tab.Title);
            tab.Bridge.DataReceived += recorder.Write;
            _sessionRecorders[tab] = recorder;
        }
    }

    private void StopSessionLogging(TerminalTabViewModel tab)
    {
        if (_sessionLogs.Remove(tab, out SessionLogWriter? writer))
        {
            writer.Dispose(); // 旧桥可能还在收尾;Write 对已释放流是 no-op。
        }
        if (_sessionRecorders.Remove(tab, out SessionRecorder? recorder))
        {
            recorder.Dispose(); // 收尾写入元数据(时长/结束时间)。
        }
    }

    /// <summary>
    /// 连接断开(设置 → 常规 → 行为/通知):状态栏提醒 + 可选提示音 +
    /// 自动重连(用户主动断开除外,按重连间隔与最大重试执行)。
    /// </summary>
    private void OnTabDisconnected(TerminalTabViewModel tab)
    {
        StopSessionLogging(tab);

        // 会话断开后 SFTP 通道随之失效:驱逐缓存的文件面板并释放 SFTP 客户端。
        // 重连会拿到新的 SessionId,面板届时按新会话重建。
        CloseSftpForTab(tab);

        // 不论主动断开还是远端掉线,都把底层 SSH 客户端一并拆掉;
        // 重连会新建会话,不受影响。
        TeardownSshSession(tab.SessionId);
        AppSettings? settings = _latestSettings;
        if (settings is null)
        {
            return;
        }
        if (settings.General.NotifyOnDisconnect)
        {
            StatusBar.Status = Strings.Format("Msg_TabDisconnected", tab.Title);
            if (!ReferenceEquals(ActiveTerminalTab, tab))
            {
                tab.HasBellAlert = true;
            }
        }
        if (settings.General.SoundAlerts && OperatingSystem.IsWindows())
        {
            SystemSound.Alert();
        }

        // 无头单元测试在没有 Avalonia 应用的情况下构造此 VM;此处无计时器。
        // 本地终端不自动重开:shell 退出(exit)是用户意图,自动拉起会没完没了。
        if (
            !settings.General.AutoReconnect
            || tab.UserRequestedDisconnect
            || tab.LocalShell is not null
            || Application.Current is null
        )
        {
            return;
        }
        int maxRetries = Math.Max(1, settings.General.MaxRetries);
        tab.MaxReconnectAttempts = maxRetries; // 全部自动重连路径共用同一权威值(设置审计 C-02)
        if (tab.ReconnectAttempts >= maxRetries)
        {
            return;
        }
        tab.IncrementReconnectAttempt();
        int delaySeconds = Math.Clamp(settings.General.ReconnectIntervalSeconds, 1, 300);
        StatusBar.Status = Strings.Format(
            "Msg_AutoReconnectCountdown",
            tab.Title,
            delaySeconds,
            tab.ReconnectAttempts,
            maxRetries
        );
        DispatcherTimer.RunOnce(
            () =>
            {
                // 等待期间用户可能已手动重连、关掉标签或主动断开。
                if (
                    tab
                        is
                    {
                        ConnectionStatus: SessionStatus.Disconnected,
                        UserRequestedDisconnect: false
                    }
                    && TabBar.Tabs.Contains(tab)
                )
                {
                    _ = ReconnectTabAsync(tab);
                }
            },
            TimeSpan.FromSeconds(delaySeconds)
        );
    }

    /// <summary>
    /// 探一句对端是不是 POSIX shell(#305):只有它认 sh 语法,才敢注入目录上报钩子。
    /// 「上报终端工作目录」关着时直接返回 false —— 连探针那条 exec 通道都不开,守住
    /// "关掉就一个字节都不发"的承诺(#286)。结果按主机缓存,只有每台机器的首次连接付这次往返。
    /// </summary>
    /// <remarks>
    /// 刻意排在开 shell 通道**之前**、而不是与之并行:一是探针用完即关,不与 shell 通道并存,
    /// 对 <c>MaxSessions 1</c> 的严苛服务端也只占一个通道名额;二是结论在注入前就已就绪,
    /// 注入仍然赶在 shell 画出提示符之前,钩子末尾那记清行(见
    /// <see cref="WorkingDirectoryReportHook" />)才落在该落的地方。
    /// </remarks>
    private static Task<bool> ProbePosixShellAsync(
        ISshClientWrapper client,
        SessionProfile? profile,
        AppSettings settings,
        CancellationToken cancellationToken
    ) =>
        settings.TerminalBehavior.ReportWorkingDirectory
            ? RemoteShellProbe.IsPosixShellAsync(
                client,
                RemoteShellProbe.CacheKey(profile?.Host, profile?.Port ?? 22, profile?.Username),
                cancellationToken
            )
            : Task.FromResult(false);

    /// <summary>
    /// 连接成功后按设置注入目录上报钩子,并追加用户配置的"连接后执行命令"
    /// (设置 → 终端 → 会话)。PTY 输入由内核缓冲,shell 就绪后才会读取,
    /// 无需等待提示符。
    /// </summary>
    /// <param name="tab">已挂上传输的终端标签。</param>
    /// <param name="settings">当前设置。</param>
    /// <param name="isPosixShell">
    /// <see cref="ProbePosixShellAsync" /> 的结论;false 时不注入钩子。
    /// 用户自己配的"连接后执行命令"不受它影响 —— 那是用户明确要求执行的东西,
    /// 对端是什么 shell 由用户自己负责。
    /// </param>
    private static void SendStartupCommand(
        TerminalTabViewModel tab,
        AppSettings settings,
        bool isPosixShell
    ) =>
        tab.SendSilentCommand(
            BuildStartupCommand(settings.TerminalBehavior.StartupCommand, isPosixShell)
        );

    /// <summary>
    /// 组合内置目录上报钩子与用户启动命令;保持一次注入以共用同一个回显抑制窗口。
    /// </summary>
    /// <param name="userCommand">用户配置的"连接后执行命令";空则只剩钩子。</param>
    /// <param name="reportWorkingDirectory">
    /// 是否注入 OSC 7 目录上报钩子(设置 → 终端 → 会话,#286)。关掉时两者皆空 =
    /// 返回空串,<see cref="TerminalTabViewModel.SendSilentCommand" /> 对空串直接不发,
    /// 连回车都不会多出来。
    /// </param>
    internal static string BuildStartupCommand(string? userCommand, bool reportWorkingDirectory = true)
    {
        string user = userCommand?.Trim() ?? string.Empty;
        if (!reportWorkingDirectory)
        {
            return user;
        }
        return user.Length == 0 ? WorkingDirectoryReportHook : WorkingDirectoryReportHook + "; " + user;
    }

    /// <summary>
    /// 用户语义的关闭标签统一入口(覆盖层关闭按钮 / Esc / Ctrl+W / 命令面板)。
    /// 必须走 <see cref="DockWorkspace.CloseDocument" />:只有它会触发 DocumentClosed,
    /// 从而把逻辑标签、停靠文档、会话日志与底层 SSH/PTY 传输一并拆干净。
    /// 直接调 TabBar.CloseTab 只删逻辑标签,会留下可见的僵尸标签并泄漏传输层。
    /// </summary>
    public void CloseTerminalTab(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        TerminalDocument? document = Layout
            .AllDocuments()
            .OfType<TerminalDocument>()
            .FirstOrDefault(d => ReferenceEquals(d.Terminal, tab));
        if (document is not null)
        {
            Layout.CloseDocument(document);
            return;
        }

        // 文档已被静默移除(连接失败路径)时只剩逻辑标签需要收尾。
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
    }

    /// <summary>关闭当前活动标签,终端与 SFTP 文档同等对待(Ctrl+W / session.close)。</summary>
    private void CloseActiveTab()
    {
        if (Layout.ActiveDocument is { } document)
        {
            Layout.CloseDocument(document);
            return;
        }
        if (TabBar.ActiveTab is TerminalTabViewModel tab)
        {
            CloseTerminalTab(tab);
        }
    }

    private void RemoveTerminalTab(TerminalTabViewModel tab, TerminalDocument document)
    {
        StopSessionLogging(tab);
        // 防御性驱逐 SFTP 面板缓存:本路径(连接失败/取消)静默移除文档,不触发
        // DocumentClosed,若标签曾短暂连上过,缓存里的面板会悬挂。幂等,无缓存时空操作。
        CloseSftpForTab(tab);
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        Layout.RemoveDocument(document);
        if (ReferenceEquals(ActiveTerminalTab, tab))
        {
            ActiveTerminalTab = TabBar.ActiveTab as TerminalTabViewModel;
        }
        tab.Dispose();
    }

    /// <summary>缺少连接所需凭据(用户名/密码/私钥)时需要先走登录验证流程。</summary>
    private static bool RequiresCredentials(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Username)
        || (profile.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(profile.Password))
        || (
            profile.AuthMethod == AuthMethod.PrivateKey
            && string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
        );

    /// <summary>
    /// 执行连接且绝不让异常逃逸到调用方。认证失败、主机不可达等被捕获进
    /// <see cref="LastConnectionError" /> 并反映在状态栏中,而非让应用崩溃。
    /// 凭据缺失或认证失败时通过 <see cref="InteractiveAuthenticator" /> 走两步验证弹窗(最多重试 3 次)。
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default
    )
    {
        if (profile.ConnectionType == ConnectionType.SFTP)
        {
            await OpenSftpDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
            return null;
        }
        if (profile.ConnectionType == ConnectionType.FTP)
        {
            await OpenFtpDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
            return null;
        }
        if (profile.ConnectionType == ConnectionType.Plugin)
        {
            // 形态由插件的**声明**决定,查它是同步的、不会装载任何程序集,所以放在最前:
            // 工作台(Redis…)→ 向插件索取一个控件挂成停靠文档。
            if (_protocolRegistry?.KindOf(profile.PluginProtocolId) == PluginConnectionKind.Workspace)
            {
                await OpenWorkspaceDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
                return null;
            }
            // 其余插件协议有两种:注册了终端实现的(Telnet…)开终端标签,
            // 注册了文件系统的(S3…)开双栏文件面板。判据只看注册表里有什么,
            // 宿主依旧不认识任何一种具体协议。可能触发惰性激活。
            PluginProtocolRegistration? registration = _protocolRegistry is { } protocols
                ? await protocols.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true)
                : null;
            if (registration is { Terminal: not null })
            {
                return await OpenPluginTerminalForProfileAsync(profile, registration, cancellationToken)
                    .ConfigureAwait(true);
            }
            await OpenPluginDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
            return null;
        }
        if (_connectionWorkflowService is null || _sshConnectionService is null)
        {
            return null;
        }
        SessionProfile current = profile;
        AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);

        // 标签只创建一次:连接中→(失败则)标签页内覆盖层→(认证重试)复用同一标签,
        // 不再每次尝试都新建/销毁标签。慢连接不阻塞其它连接(SshConnectionService 已并发)。
        TerminalTabViewModel? tab = null;
        TerminalDocument? document = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool needsPrompt = attempt > 0 || RequiresCredentials(current);
            if (needsPrompt)
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    if (attempt > 0)
                    {
                        return tab; // 无法交互重试,保留失败标签(含覆盖层)。
                    }
                }
                else
                {
                    SessionProfile? updated = await prompt(current);
                    if (updated is null)
                    {
                        // 用户取消:不弹连接失败提示,撤掉尚未连上的标签。
                        LastConnectionError = null;
                        if (tab is not null && document is not null)
                        {
                            RemoveTerminalTab(tab, document);
                        }
                        return null;
                    }
                    current = updated;
                }
            }
            if (tab is null)
            {
                (tab, document) = CreateConnectingTab(current, settings);
            }
            else
            {
                // 认证重试:复用标签,回到“连接中”(隐去上次的失败覆盖层)。
                tab.Profile = current;
                tab.ConnectionStatus = SessionStatus.Connecting;
            }
            try
            {
                await RunHandshakeAsync(tab, current, settings, cancellationToken);
                return tab;
            }
            catch (OperationCanceledException)
            {
                // 用户取消(超时):撤掉这个正在连接的标签。
                if (document is not null)
                {
                    RemoveTerminalTab(tab, document);
                }
                return null;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                bool isAuth = ex is VelaSshAuthenticationException;

                // 认证失败但无法交互重试(headless):保持既有契约,撤标签、返回 null。
                if (isAuth && InteractiveAuthenticator is null)
                {
                    if (document is not null)
                    {
                        RemoveTerminalTab(tab, document);
                    }
                    return null;
                }

                // 认证失败且可交互:标记失败态并循环回去重新弹凭据重试。
                tab.MarkConnectionFailed(LastConnectionError);
                if (isAuth && InteractiveAuthenticator is not null)
                {
                    continue;
                }

                // 网络/超时等失败:保留标签,标签页内显示失败覆盖层(设计 yxjmg),不弹全局框。
                return tab;
            }
        }

        // 认证重试用尽:保留标签显示“认证失败”覆盖层,交给用户手动重连/关闭。
        return tab;
    }

    /// <summary>
    /// 为 SSH 或 SFTP 配置打开一个独立的 SFTP 文档。此路径绝不创建终端标签或 shell 流。
    /// </summary>
    public async Task<TerminalTabViewModel?> OpenSftpForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await OpenSftpDocumentForProfileAsync(profile, cancellationToken).ConfigureAwait(true);
        return null;
    }

    /// <summary>
    /// 通过常规工作流连接,并在认证成功后才创建一个文档范围的串行化 SFTP 通道。
    /// </summary>
    public async Task<SftpDocument?> OpenSftpDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_connectionWorkflowService is null)
        {
            return null;
        }

        SessionProfile current = profile;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresCredentials(current))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return null;
                }
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    return null;
                }
                current = prompted;
            }

            SshSession? session = null;
            try
            {
                session = await _connectionWorkflowService.ConnectProfileAsync(current, cancellationToken)
                    .ConfigureAwait(true);
                if (session is null)
                {
                    return null;
                }
                if (_sftpService is null)
                {
                    await _connectionWorkflowService.DisconnectAsync(session.SessionId, cancellationToken)
                        .ConfigureAwait(true);
                    return null;
                }
                AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
                var document = new SftpDocument(
                    new SftpDocumentViewModel(
                        current,
                        session,
                        _connectionWorkflowService,
                        _sftpService,
                        settings.Transfer,
                        FileTransfer,
                        QueryDefaultEditorPathAsync));
                Layout.AddDocument(document);
                // 与 FTP 同理:连接续体可能落在后台线程上,树节点是绑定属性,必须回主线程再改。
                SetTreeSessionStatus(current.Id, SessionStatus.Connected);
                return document;
            }
            catch (OperationCanceledException)
            {
                if (session is not null)
                {
                    await _connectionWorkflowService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(true);
                }
                return null;
            }
            catch (VelaSshAuthenticationException)
            {
                if (session is not null)
                {
                    await _connectionWorkflowService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(true);
                }
                continue;
            }
            catch (Exception ex)
            {
                if (session is not null)
                {
                    await _connectionWorkflowService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(true);
                }
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 打开一个 FTP / FTPS 文档标签:建立 FTP 会话并复用与 SFTP 完全相同的双栏文件面板。
    /// <para>
    /// 与 SFTP 的两点不同:一是不走 <see cref="IConnectionWorkflowService" />(那是 SSH 握手),
    /// 二是 FTPS 证书没过校验时会抛 <see cref="VelaFtpCertificateException" /> ——
    /// 此时弹一次信任提示,用户同意就把指纹记进配置再重连(刻意不在证书回调里同步等 UI,那样极易死锁)。
    /// </para>
    /// </summary>
    public async Task<SftpDocument?> OpenFtpDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_ftpSessionService is null || _sftpService is null)
        {
            return null;
        }

        SessionProfile current = profile;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresFtpCredentials(current))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return null;
                }
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    return null;
                }
                current = prompted;
            }

            try
            {
                Guid sessionId = await _ftpSessionService
                    .OpenSessionAsync(FtpConnectionInfo.FromProfile(current), cancellationToken)
                    .ConfigureAwait(true);
                AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
                var document = new SftpDocument(
                    new SftpDocumentViewModel(
                        current,
                        sessionId,
                        (id, token) => _ftpSessionService.CloseSessionAsync(id, token),
                        _sftpService,
                        settings.Transfer,
                        FileTransfer,
                        QueryDefaultEditorPathAsync));
                // 用**原始** profile 的标识登记:登录弹窗可能换过 current 的字段,
                // 但树上的节点始终是按最初那条配置的 Id 建的。
                _ftpSessionProfiles[sessionId] = profile.Id;
                Layout.AddDocument(document);
                SetTreeSessionStatus(profile.Id, SessionStatus.Connected);
                return document;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (VelaFtpCertificateException certificate)
            {
                // 用户同意信任 → 记下指纹后重来一次;拒绝(或没有提示钩子)→ 按普通连接失败上报。
                if (FtpCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedCertificate(current, certificate.Thumbprint);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    attempt--; // 信任后的这次重连不算认证重试
                    continue;
                }
                LastConnectionError = certificate.Message;
                StatusBar.Status = LastConnectionError;
                return null;
            }
            catch (VelaFtpAuthenticationException)
            {
                continue;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 打开一个插件协议文档标签(S3、WebDAV…):建立会话并复用与 SFTP 完全相同的双栏文件面板。
    /// <para>
    /// 结构与 <see cref="OpenFtpDocumentForProfileAsync" /> 一一对应(同样不走
    /// <see cref="IConnectionWorkflowService" /> —— 那是 SSH 握手,同样在证书不可信时
    /// 弹一次信任提示后重连)。差别只在缺少凭据的判定:声明了 AnonymousAccess 的协议
    /// (如 S3 的公开只读桶)不填凭据也是一条正当路径,弹框会把它堵死。
    /// </para>
    /// <para>
    /// 这个方法**对具体协议一无所知**:能力位、右键动作、证书字段全部从协议描述里读。
    /// 再接入一种协议时它一行都不用改。
    /// </para>
    /// </summary>
    /// <param name="profile">会话配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档;失败或取消时为 null。</returns>
    public async Task<SftpDocument?> OpenPluginDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_pluginProtocols is null || _sftpService is null)
        {
            return null;
        }

        ProtocolDescriptor? descriptor = null;
        if (profile.PluginProtocolId is { Length: > 0 } protocolId && _protocolRegistry is { } registry)
        {
            // 可能触发插件的惰性激活(用户刚从「最近连接」点开一条 S3 会话)。
            descriptor = (await registry.ResolveAsync(protocolId).ConfigureAwait(true))?.Descriptor;
        }
        bool allowsAnonymous = descriptor?.Features.HasFlag(ProtocolFeatures.AnonymousAccess) == true;

        SessionProfile current = profile;
        // 证书提示单独计数:attempt-- 会与 attempt++ 相抵,把 3 次上限彻底架空。
        // 多节点各自签一张证书的端点(DNS 轮询的 MinIO/Ceph)每轮都是"新证书",
        // 不设独立上限的话用户只能靠点"不信任"才退得出来。
        int certPrompts = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresPluginCredentials(current, allowsAnonymous))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return null;
                }
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    return null;
                }
                current = prompted;
            }

            try
            {
                Guid sessionId = await _pluginProtocols.OpenSessionAsync(current, cancellationToken).ConfigureAwait(true);
                AppSettings settings = _latestSettings ?? await LoadSettingsSnapshotAsync().ConfigureAwait(true);
                var viewModel = new SftpDocumentViewModel(
                    current,
                    sessionId,
                    (id, token) => _pluginProtocols.CloseSessionAsync(id, token),
                    _sftpService,
                    settings.Transfer,
                    FileTransfer,
                    QueryDefaultEditorPathAsync);
                // 协议专属的右键菜单项:声明式,按下右键那一帧就能画出来。
                if (descriptor is { Actions.Count: > 0 })
                {
                    // 传协议声明本身:菜单在每次右键时按命中行重建(见 FileBrowserViewModel.ContextTarget)。
                    viewModel.RemoteFiles.SetProtocolActions(descriptor.DisplayName, descriptor.Actions);
                    viewModel.RemoteFiles.InvokeProtocolAction = (actionId, path) =>
                        _pluginProtocols.InvokeActionAsync(sessionId, actionId, path, CancellationToken.None);
                }
                var document = new SftpDocument(viewModel);
                // 用**原始** profile 的标识登记:登录弹窗可能换过 current 的字段,
                // 但树上的节点始终是按最初那条配置的 Id 建的。
                _pluginSessionProfiles[sessionId] = profile.Id;
                Layout.AddDocument(document);
                SetTreeSessionStatus(profile.Id, SessionStatus.Connected);
                return document;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (PluginProtocolCertificateException certificate)
            {
                // 用户同意信任 → 记下指纹后重来一次;拒绝(或没有提示钩子)→ 按普通连接失败上报。
                if (PluginCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedPluginCertificate(current, certificate);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    if (certificate.SettingKey is { Length: > 0 } && ++certPrompts <= 2)
                    {
                        // 指纹确实记下了,下次重连不会再撞同一张证书 —— 这一次不算认证重试。
                        // 但最多宽容两次:协议没声明存放位置、或端点每次出示不同证书时,
                        // current 根本没变化,再减下去就是无限弹框。
                        attempt--;
                    }
                    continue;
                }
                LastConnectionError = certificate.Message;
                StatusBar.Status = LastConnectionError;
                return null;
            }
            catch (PluginProtocolAuthenticationException)
            {
                continue;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 打开一条**插件终端协议**会话(Telnet、串口…):走与 SSH / 本地终端完全相同的
    /// 桥 → VT 引擎 → 自绘控件 管线,只是传输层由插件提供。
    /// <para>
    /// 与 SSH 那条路径的三点差别,都是协议决定的而非偷懒:
    /// </para>
    /// <list type="bullet">
    ///   <item>不走 <see cref="IConnectionWorkflowService" /> —— 那是 SSH 握手;
    ///     因此没有 SessionId,SFTP 面板、任务管理器、资源监视器、隧道自动灰掉。</item>
    ///   <item>不发"连接后执行命令" —— Telnet 连上先看到的是对端的 <c>login:</c>,
    ///     此时注入命令等于把它打进登录提示符。</item>
    ///   <item>凭据不缺就不弹登录框:声明了 NoCredentials 的协议根本没有凭据可填。</item>
    /// </list>
    /// </summary>
    /// <param name="profile">会话配置。</param>
    /// <param name="registration">已解析的协议注册(带终端实现)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的标签;取消时为 null(连接失败也返回标签,失败信息画在标签页内)。</returns>
    public async Task<TerminalTabViewModel?> OpenPluginTerminalForProfileAsync(
        SessionProfile profile,
        PluginProtocolRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registration);
        AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
        (TerminalTabViewModel tab, TerminalDocument document) =
            CreateConnectingTab(profile, settings, registration.Descriptor.DisplayName);
        try
        {
            await AttachPluginTerminalAsync(tab, profile, registration, settings, cancellationToken)
                .ConfigureAwait(true);
            await Sidebar.RecentConnections.RefreshAsync().ConfigureAwait(true);
            return tab;
        }
        catch (OperationCanceledException)
        {
            RemoveTerminalTab(tab, document);
            return null;
        }
        catch (Exception ex)
        {
            // 与 SSH 同口径:标签留着,失败原因画在标签页内的覆盖层上(设计 yxjmg),
            // 用户按 Enter 即重连 —— 撤掉标签会连带把错误信息一起吞掉。
            LastConnectionError = DescribeConnectionError(ex, profile);
            StatusBar.Status = LastConnectionError;
            tab.MarkConnectionFailed(LastConnectionError);
            return tab;
        }
    }

    /// <summary>在一个"连接中"标签上建立插件终端会话并挂上传输(打开与重连共用)。</summary>
    private async Task AttachPluginTerminalAsync(
        TerminalTabViewModel tab,
        SessionProfile profile,
        PluginProtocolRegistration registration,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        TerminalType terminalType = TerminalTypeExtensions.FromTermName(settings.TerminalType);
        // 初始行列取模拟器当前值:标签刚建出来还没布局过时它是默认值,
        // 真实尺寸随后由控件的 Resize 通知补上(Telnet 会重发一次 NAWS)。
        var options = new ProtocolTerminalOptions(
            terminalType.ToTermName(),
            Math.Max(2, tab.TerminalEmulator.Columns),
            Math.Max(2, tab.TerminalEmulator.Rows));
        IShellStreamWrapper stream = await PluginProtocolTerminalConnector
            .OpenAsync(registration, profile, options, cancellationToken)
            .ConfigureAwait(true);
        tab.AttachTransport(stream);
        tab.Start();
        tab.ConnectionStatus = SessionStatus.Connected;
        tab.ResetReconnectAttempts();
        StartSessionLogging(tab, settings);
        StatusBar.ResetUptime();
        UpdateStatusBarForActiveTab();
        LastConnectionError = null;
    }

    /// <summary>重连一条插件终端会话:复用同一标签与回滚缓冲,RIS 复位后重建传输。</summary>
    private async Task ReconnectPluginTerminalAsync(
        TerminalTabViewModel tab,
        SessionProfile profile,
        CancellationToken cancellationToken)
    {
        tab.ConnectionStatus = SessionStatus.Connecting;
        tab.DetachTransport();
        UpdateStatusBarForActiveTab();
        try
        {
            AppSettings settings = await LoadSettingsSnapshotAsync().ConfigureAwait(true);
            PluginProtocolRegistration? registration = _protocolRegistry is { } protocols
                ? await protocols.ResolveAsync(profile.PluginProtocolId).ConfigureAwait(true)
                : null;
            if (registration is not { Terminal: not null })
            {
                // 插件在这中间被禁用/卸载了:如实说,别停在"连接中"。
                tab.MarkConnectionFailed(Strings.Get("Plugin_ProtocolUnavailable"));
                return;
            }
            // 新会话的输出到达前完全复位(RIS),免得新标语附在旧缓冲后面。
            tab.TerminalEmulator.Feed(RisResetSequence);
            await AttachPluginTerminalAsync(tab, profile, registration, settings, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            tab.MarkDisconnected();
        }
        catch (Exception ex)
        {
            LastConnectionError = DescribeConnectionError(ex, profile);
            StatusBar.Status = LastConnectionError;
            tab.MarkConnectionFailed(LastConnectionError);
        }
    }

    /// <summary>
    /// 打开一条**工作台**会话(Redis 等由插件全权渲染界面的连接类型)。
    /// <para>
    /// 与 <see cref="OpenPluginDocumentForProfileAsync" /> 共用同一套连接流程纪律:
    /// 缺凭据先弹登录框、认证失败原地重试(至多三次)、证书未信任走"提示 → 记指纹 → 重连"
    /// 且证书提示单独计数(否则 <c>attempt--</c> 会把三次上限彻底架空)。
    /// 区别只在最后一步:那边打开宿主的双栏浏览器,这边把插件的控件挂成停靠文档。
    /// </para>
    /// </summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档;失败或用户取消时为 null。</returns>
    public async Task<PluginWorkspaceDocument?> OpenWorkspaceDocumentForProfileAsync(
        SessionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_workspaceLauncher is not { } launcher)
        {
            return null;
        }

        bool allowsAnonymous = false;
        if (_protocolRegistry is { } registry)
        {
            // 可能触发插件的惰性激活(用户刚从「最近连接」点开一条 Redis 会话)。
            WorkspaceDescriptor? descriptor =
                (await registry.ResolveWorkspaceAsync(profile.PluginProtocolId).ConfigureAwait(true))?.Descriptor;
            allowsAnonymous = descriptor?.Features.HasFlag(WorkspaceFeatures.AnonymousAccess) == true;
        }

        SessionProfile current = profile;
        int certPrompts = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || RequiresPluginCredentials(current, allowsAnonymous))
            {
                if (InteractiveAuthenticator is not { } prompt)
                {
                    return null;
                }
                SessionProfile? prompted = await prompt(current).ConfigureAwait(true);
                if (prompted is null)
                {
                    return null;
                }
                current = prompted;
            }

            try
            {
                // 声明了 SshTunnel 且用户选了跳板机 → 宿主先把 SSH 会话与本地转发建好,
                // 插件只看到一个已经能连的本地端点(凭据永不出宿主)。
                (WorkspaceEndpoint? endpoint, Guid tunnelId) =
                    await EstablishWorkspaceTunnelAsync(current, cancellationToken).ConfigureAwait(true);
                PluginWorkspaceSession session;
                try
                {
                    session = await launcher.OpenAsync(current, endpoint, cancellationToken).ConfigureAwait(true);
                }
                catch
                {
                    // 连接失败就把刚建的隧道拆掉 —— 留着它等于占着一个本地端口和一条 SSH 通道。
                    await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
                    throw;
                }
                var document = new PluginWorkspaceDocument(current, session.SessionId, session.TypeName, session.Document);
                if (tunnelId != Guid.Empty)
                {
                    _workspaceTunnels[session.SessionId] = tunnelId;
                }
                // 用**原始** profile 的标识登记:登录弹窗可能换过 current 的字段,
                // 但树上的节点始终是按最初那条配置的 Id 建的。
                _workspaceProfiles[session.SessionId] = profile.Id;
                _workspaceDocuments[session.SessionId] = document;
                session.Document.StatusChanged += OnWorkspaceStatusChanged;
                Layout.AddDocument(document);
                SetTreeSessionStatus(profile.Id, SessionStatus.Connected);
                return document;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (PluginProtocolCertificateException certificate)
            {
                if (PluginCertificateTrustPrompt is { } trustPrompt &&
                    await trustPrompt(current, certificate).ConfigureAwait(true))
                {
                    current = WithTrustedPluginCertificate(current, certificate);
                    await PersistProfileIfSavedAsync(current).ConfigureAwait(true);
                    if (certificate.SettingKey is { Length: > 0 } && ++certPrompts <= 2)
                    {
                        attempt--;
                    }
                    continue;
                }
                LastConnectionError = certificate.Message;
                StatusBar.Status = LastConnectionError;
                return null;
            }
            catch (PluginProtocolAuthenticationException)
            {
                continue;
            }
            catch (Exception ex)
            {
                LastConnectionError = DescribeConnectionError(ex, current);
                StatusBar.Status = LastConnectionError;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 插件连接的「测试」:真开一次会话,随即原路关掉。
    /// <para>
    /// 为什么必须走这条路而不是探个 TCP 端口:能连上 6379 不等于这条配置能用 ——
    /// 口令错、ACL 用户没权限、库号越界、TLS 证书不被信任,全都是端口通着却连不上的。
    /// 「测试」要能替用户排除的正是这些,所以它跑的必须是与「连接」同一套握手。
    /// </para>
    /// <para>
    /// 隧道与文档都在 finally 里拆:测试不留任何东西 —— 既不占本地转发端口,
    /// 也不给插件留一条没人管的连接。
    /// </para>
    /// </summary>
    /// <param name="profile">要测的配置(凭据已由弹窗填好)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ProbePluginConnectionAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // 文件系统形态(S3 之类)与工作台形态(Redis 之类)是两套打开路径,按声明分流。
        // 查形态不会装载任何插件程序集。
        if (_protocolRegistry is { } registry
            && registry.KindOf(profile.PluginProtocolId) == PluginConnectionKind.FileSystem)
        {
            if (_pluginProtocols is not { } sessions)
            {
                throw new InvalidOperationException(Strings.Get("Plugin_TestUnavailable"));
            }
            Guid fileSessionId = await sessions.OpenSessionAsync(profile, cancellationToken).ConfigureAwait(true);
            await sessions.CloseSessionAsync(fileSessionId, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (_workspaceLauncher is not { } launcher)
        {
            throw new InvalidOperationException(Strings.Get("Plugin_TestUnavailable"));
        }
        (WorkspaceEndpoint? endpoint, Guid tunnelId) =
            await EstablishWorkspaceTunnelAsync(profile, cancellationToken).ConfigureAwait(true);
        PluginWorkspaceSession? session = null;
        try
        {
            session = await launcher.OpenAsync(profile, endpoint, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            if (session is not null)
            {
                launcher.Forget(session.SessionId);
                try
                {
                    await session.Document.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 测试的收尾失败不该改变测试结论 —— 结论已经由 OpenAsync 给出了。
                }
            }
            await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
        }
    }

    /// <summary>工作台文档关闭:摘登记、退订状态、拆隧道、释放插件那边的连接。幂等。</summary>
    private async Task CloseWorkspaceDocumentAsync(PluginWorkspaceDocument document)
    {
        document.Workspace.StatusChanged -= OnWorkspaceStatusChanged;
        _workspaceDocuments.TryRemove(document.SessionId, out _);
        _workspaceLauncher?.Forget(document.SessionId);
        if (_workspaceProfiles.TryRemove(document.SessionId, out Guid profileId))
        {
            SetTreeSessionStatus(profileId, SessionStatus.Disconnected);
        }
        // 先关插件那边的连接,再拆隧道:反过来会让插件的关闭流程对着一条已经断掉的通道超时。
        await document.CloseAsync().ConfigureAwait(true);
        if (_workspaceTunnels.TryRemove(document.SessionId, out Guid tunnelId))
        {
            await RemoveWorkspaceTunnelAsync(tunnelId).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 按连接配置里选的跳板会话建立 SSH 会话与本地端口转发。
    /// <para>
    /// 这一步刻意留在界面层:建 SSH 会话要走宿主既有的两步认证、指纹校验与 ProxyJump 链路,
    /// 而**凭据永不出宿主**是硬规则。插件只拿到"一个已经能连的本地端点"。
    /// </para>
    /// <para>
    /// 已连着的会话优先复用 —— 用户刚在终端里连上那台跳板机,再为同一台开第二条 SSH
    /// 只是白付一次握手与一份内存。
    /// </para>
    /// </summary>
    /// <returns>端点与隧道 id;没有配跳板机时两者都为空。</returns>
    private async Task<(WorkspaceEndpoint? Endpoint, Guid TunnelId)> EstablishWorkspaceTunnelAsync(
        SessionProfile profile,
        CancellationToken cancellationToken)
    {
        if (_protocolRegistry is not { } registry
            || _tunnelService is null
            || _sessionRepository is null
            || _connectionWorkflowService is null)
        {
            return (null, Guid.Empty);
        }
        WorkspaceDescriptor? descriptor =
            (await registry.ResolveWorkspaceAsync(profile.PluginProtocolId).ConfigureAwait(true))?.Descriptor;
        if (descriptor is null || !descriptor.Features.HasFlag(WorkspaceFeatures.SshTunnel))
        {
            return (null, Guid.Empty);
        }
        // 找到那个 SshSession 形态的字段,读出用户选的跳板配置 id。
        ProtocolSettingField? field = descriptor.Fields
            .FirstOrDefault(candidate => candidate.Kind == ProtocolSettingKind.SshSession);
        string? raw = field is null
            ? null
            : profile.PluginSettings?.GetValueOrDefault(field.Key);
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out Guid jumpProfileId))
        {
            return (null, Guid.Empty);
        }

        IReadOnlyList<SessionProfile> saved = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(true);
        // 跳板配置被删了。**不静默直连** —— 那会把一条本该走内网的连接直接打到公网上去。
        SessionProfile? jump = saved.FirstOrDefault(candidate => candidate.Id == jumpProfileId) ?? throw new PluginProtocolConnectionException(Strings.Get("Plugin_JumpSessionMissing"));

        // 按"目标主机 + 端口 + 用户"匹配已连着的会话。这不是权宜:隧道要穿的是**那台主机**,
        // 谁开的那条 SSH 无关紧要 —— 而 SshSession 上本就没有"来自哪条配置"这个信息。
        Guid sshSessionId = _sshConnectionService?.Sessions
            .FirstOrDefault(session => session.Status == SessionStatus.Connected
                                       && string.Equals(session.ConnectionInfo.Host, jump.Host, StringComparison.OrdinalIgnoreCase)
                                       && session.ConnectionInfo.Port == jump.Port
                                       && string.Equals(session.ConnectionInfo.Username, jump.Username, StringComparison.Ordinal))
            ?.SessionId ?? Guid.Empty;
        if (sshSessionId == Guid.Empty)
        {
            SshSession connected = await _connectionWorkflowService
                .ConnectProfileAsync(jump, cancellationToken).ConfigureAwait(true);
            sshSessionId = connected.SessionId;
        }

        // 本地端口自己挑:隧道服务按配置里的端口监听,没有"由内核分配后回报"这条路。
        // 先 bind 0 拿一个空闲端口再放掉,是这种情况下的标准做法(有极小的竞争窗口,
        // 撞上了表现为"端口已被占用"的明确失败,而不是静默连错地方)。
        int localPort = ReserveLocalPort();
        var config = new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = $"{profile.Name} ↝ {jump.Name}",
            LocalHost = "127.0.0.1",
            LocalPort = (uint)localPort,
            RemoteHost = profile.Host,
            RemotePort = (uint)profile.Port
        };
        TunnelInfo tunnel = await _tunnelService
            .CreateLocalForwardAsync(sshSessionId, config, cancellationToken).ConfigureAwait(true);
        return (new("127.0.0.1", localPort, profile.Host, profile.Port, jump.Name), tunnel.Id);
    }

    /// <summary>拆掉一条为工作台建的隧道(尽力而为:拆不掉不该把关闭流程也带崩)。</summary>
    private async Task RemoveWorkspaceTunnelAsync(Guid tunnelId)
    {
        if (tunnelId == Guid.Empty || _tunnelService is null)
        {
            return;
        }
        try
        {
            await _tunnelService.RemoveTunnelAsync(tunnelId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Workspace] Removing tunnel {tunnelId} failed: {ex.Message}");
        }
    }

    /// <summary>借一个空闲的本地端口号(bind 0 → 读端口 → 放掉)。</summary>
    private static int ReserveLocalPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 提供该连接类型的插件被停用/卸载:把它名下还开着的标签页撤掉。
    /// <para>
    /// 走 <see cref="DockWorkspace.CloseDocument" /> 而不是直接释放 —— 只有它会触发
    /// <c>DocumentClosed</c>,不然界面上会留下一个再也不会应答的面板。
    /// </para>
    /// </summary>
    private void OnWorkspaceSessionAbandoned(Guid sessionId) =>
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (_workspaceDocuments.TryGetValue(sessionId, out PluginWorkspaceDocument? document))
            {
                Layout.CloseDocument(document);
            }
        });

    /// <summary>工作台会话状态变化 → 资源管理器树的状态圆点(理由与线程约束同 FTP 侧)。</summary>
    private void OnWorkspaceStatusChanged(object? sender, WorkspaceStatus status)
    {
        if (sender is not IWorkspaceDocument document)
        {
            return;
        }
        Guid sessionId = _workspaceDocuments
            .FirstOrDefault(pair => ReferenceEquals(pair.Value.Workspace, document)).Key;
        if (sessionId == Guid.Empty || !_workspaceProfiles.TryGetValue(sessionId, out Guid profileId))
        {
            return;
        }
        SetTreeSessionStatus(profileId, status.State switch
        {
            ProtocolSessionState.Connected => SessionStatus.Connected,
            ProtocolSessionState.Faulted => SessionStatus.Error,
            _ => SessionStatus.Disconnected,
        });
    }

    /// <summary>插件协议会话状态变化 → 资源管理器树的状态圆点(理由与线程约束同 FTP 侧)。</summary>
    private void OnPluginSessionStateChanged(object? sender, PluginProtocolSessionStateChange change)
    {
        if (!_pluginSessionProfiles.TryGetValue(change.SessionId, out Guid profileId))
        {
            return;
        }
        if (change.State == PluginProtocolSessionState.Closed)
        {
            _pluginSessionProfiles.TryRemove(change.SessionId, out _);
        }
        SetTreeSessionStatus(profileId, change.State switch
        {
            PluginProtocolSessionState.Connected => SessionStatus.Connected,
            PluginProtocolSessionState.Faulted => SessionStatus.Error,
            _ => SessionStatus.Disconnected,
        });
    }

    /// <summary>
    /// 插件协议缺少登录凭据时才需要弹登录框。允许匿名的协议(S3 的公开只读桶)下
    /// **两者都空 = 匿名访问**,是一条正当路径,不能弹框;
    /// 只有「填了用户名却没有口令」才是真的缺东西。
    /// </summary>
    private static bool RequiresPluginCredentials(SessionProfile profile, bool allowsAnonymous) =>
        allowsAnonymous
            ? !string.IsNullOrWhiteSpace(profile.Username) && string.IsNullOrEmpty(profile.Password)
            : string.IsNullOrWhiteSpace(profile.Username) || string.IsNullOrEmpty(profile.Password);

    /// <summary>
    /// 返回一份把服务器证书指纹记为已信任的配置副本。指纹写进协议自己声明的那个隐藏字段
    /// —— 宿主不知道该协议管它叫什么,所以字段键由异常带过来。
    /// </summary>
    private static SessionProfile WithTrustedPluginCertificate(SessionProfile profile, PluginProtocolCertificateException certificate)
    {
        if (certificate.SettingKey is not { Length: > 0 } key)
        {
            // 协议没声明存放位置:只能本次连接内信任,不落盘。
            return profile;
        }
        Dictionary<string, string> settings = SessionProfile.CloneSettings(profile.PluginSettings) ?? [with(StringComparer.Ordinal)];
        settings[key] = certificate.Thumbprint;
        profile.PluginSettings = settings;
        return profile;
    }

    /// <summary>
    /// FTP 会话状态变化 → 资源管理器树的状态圆点。可能来自任意线程(操作失败的那条),
    /// 因此统一切回 UI 线程再改绑定属性。
    /// </summary>
    private void OnFtpSessionStateChanged(object? sender, FtpSessionStateChange change)
    {
        if (!_ftpSessionProfiles.TryGetValue(change.SessionId, out Guid profileId))
        {
            return;
        }
        if (change.State == FtpSessionState.Closed)
        {
            _ftpSessionProfiles.TryRemove(change.SessionId, out _);
        }
        SetTreeSessionStatus(profileId, change.State switch
        {
            FtpSessionState.Connected => SessionStatus.Connected,
            // 掉线显示为「离线」而不是「未连接」:文档还开着,用户需要看出是断了而不是没连过。
            FtpSessionState.Faulted => SessionStatus.Error,
            _ => SessionStatus.Disconnected,
        });
    }

    /// <summary>
    /// 在主线程上更新树节点状态(绑定属性不得在后台线程改)。
    /// <para>
    /// 用 <c>RxSchedulers.MainThreadScheduler</c> 而不是裸 <c>Dispatcher.UIThread.Post</c>:
    /// 与本类其它 VM 更新一致,并且在没有跑 Avalonia 消息循环的宿主(headless 测试)里也能落地
    /// —— 直接 Post 的作业在那种环境下永远不会被执行。
    /// </para>
    /// </summary>
    private void SetTreeSessionStatus(Guid profileId, SessionStatus status) =>
        RxSchedulers.MainThreadScheduler.Schedule(() => Sidebar.SessionTree?.SetSessionStatus(profileId, status));

    /// <summary>FTP 缺少登录凭据时才需要弹登录框;匿名登录不需要用户名与口令。</summary>
    private static bool RequiresFtpCredentials(SessionProfile profile) =>
        profile.Ftp?.Anonymous != true &&
        (string.IsNullOrWhiteSpace(profile.Username) || string.IsNullOrEmpty(profile.Password));

    /// <summary>返回一份把服务器证书指纹记为已信任的配置副本。</summary>
    private static SessionProfile WithTrustedCertificate(SessionProfile profile, string thumbprint)
    {
        FtpSettings settings = profile.Ftp?.Clone() ?? new FtpSettings();
        settings.TrustedCertificateThumbprint = thumbprint;
        profile.Ftp = settings;
        return profile;
    }

    /// <summary>
    /// 把配置写回仓储(仅对**已保存**的配置;「最近连接」重建的临时配置只在本次连接内有效)。
    /// 目前的用途是记住用户刚刚信任的服务器证书指纹,FTPS 与插件协议共用。
    /// </summary>
    private async Task PersistProfileIfSavedAsync(SessionProfile profile)
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            if (await _sessionRepository.GetSessionAsync(profile.Id).ConfigureAwait(true) is not null)
            {
                await _sessionRepository.SaveSessionAsync(profile).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 信任指纹没落盘不影响本次连接,下次会再问一遍。
        }
    }

    /// <summary>
    /// 连接侧边栏"最近连接"条目:有 id 时解析已保存配置,否则从记录的主机/端口/用户名重建临时配置。
    /// </summary>
    public async Task<TerminalTabViewModel?> TryConnectRecentAsync(
        RecentConnectionEntry entry,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(entry);
        SessionProfile? profile = null;
        if (entry.ProfileId is { } profileId && _sessionRepository is not null)
        {
            try
            {
                profile = await _sessionRepository.GetSessionAsync(profileId);
            }
            catch
            {
                // 配置读取失败时退回到临时档案。
            }
        }
        profile ??= new()
        {
            ConnectionType = entry.ConnectionType,
            Name = entry.Name,
            Host = entry.Host,
            Port = entry.Port,
            Username = entry.Username,
            AuthMethod = AuthMethod.Password,
        };
        return await TryConnectProfileAsync(profile, cancellationToken);
    }

    private static string DescribeConnectionError(Exception ex, SessionProfile profile)
    {
        string target = string.IsNullOrWhiteSpace(profile.Host)
            ? profile.Name
            : $"{profile.Username}@{profile.Host}:{profile.Port}";
        // 提取 Tmds.Ssh ConnectFailedException 中的具体原因(若存在),以便用户诊断。
        string detail = ExtractTmdsReason(ex.Message) ?? ex.Message;
        // 直接匹配 Core 的中立异常族(VelaSsh*Exception)。
        // 曾经这里按类型名字符串匹配 SSH.NET 的旧名("SshAuthenticationException" 等),
        // 迁到 Tmds.Ssh 后实际类型已是 VelaSshAuthenticationException,没有一个分支能命中,
        // 所有连接错误都掉进兜底文案。派生类型必须排在基类型前面。
        return ex switch
        {
            VelaSshAuthenticationException => $"{Strings.Format("Msg_AuthFailed", target)}\n{detail}",
            // TimeoutException 来自 SshConnectionService:底层库内部超时(调用方并未取消)时它对外
            // 统一抛这个类型。不列进来的话真超时会掉进兜底文案,显示一句英文原始消息。
            VelaSshOperationTimeoutException or TimeoutException => Strings.Format("Msg_ConnectTimeout", target),
            VelaSshConnectionException => $"{Strings.Format("Msg_ConnectFailed", target)}\n{detail}",
            SocketException => Strings.Format("Msg_NetworkError", target),
            _ => Strings.Format("Msg_ConnectGenericFailed", target, detail),
        };
    }

    /// <summary>
    /// Tmds.Ssh 的 ConnectFailedException 消息固定以该前缀开头,
    /// 格式为 "The connection could not be established - {reason} - {description}"。
    /// 提取后缀部分用于更精确的错误提示;不属于该格式的返回 null。
    /// </summary>
    private static string? ExtractTmdsReason(string message)
    {
        const string prefix = "The connection could not be established - ";
        return !message.AsSpan().StartsWith(prefix, StringComparison.Ordinal)
            ? null
            : message[prefix.Length..];
    }

    private void ConfigureTerminal(
        ITerminalEmulator emulator,
        AppSettings settings,
        TerminalType terminalType,
        bool forceUtf8 = false
    )
    {
        if (emulator is VelaTerminalControl control)
        {
            control.TerminalType = terminalType;
            // 侧栏右键菜单改动 → 持久化(-= 再 += 保证单次订阅,即使本方法重入)。
            control.GutterOptionsChanged -= OnGutterOptionsChanged;
            control.GutterOptionsChanged += OnGutterOptionsChanged;
            // Ctrl+滚轮缩放字号 → 持久化(同上,单次订阅)。
            control.FontSizeChanged -= OnTerminalFontSizeChanged;
            control.FontSizeChanged += OnTerminalFontSizeChanged;
        }
        ApplyLiveTerminalSettings(emulator, settings, forceUtf8);
    }

    /// <summary>
    /// Ctrl+滚轮缩放字号后写回设置(400ms 尾沿合并:连续滚动只保存一次);
    /// SaveSettingsAsync 会广播到所有已打开标签,使各标签字号保持一致。
    /// </summary>
    private void OnTerminalFontSizeChanged(double size)
    {
        if (_settingsService is null)
        {
            return;
        }
        _pendingFontSize = (int)Math.Round(size);
        if (_fontSizePersistDebounce is null)
        {
            _fontSizePersistDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
            _fontSizePersistDebounce.Tick += (_, _) =>
            {
                _fontSizePersistDebounce!.Stop();
                PersistTerminalFontSize(_pendingFontSize);
            };
        }
        _fontSizePersistDebounce.Stop();
        _fontSizePersistDebounce.Start();
    }

    private void PersistTerminalFontSize(int size)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                if (settings.TerminalFontSize == size)
                {
                    return;
                }
                settings.TerminalFontSize = size;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前会话。
            }
        });
    }

    /// <summary>侧栏右键菜单切换部件后写回设置;SaveSettingsAsync 会广播到所有已打开标签,保持一致。</summary>
    private void OnGutterOptionsChanged(bool timestamp, bool number, bool fold, bool blank)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                TerminalBehaviorOptions b = settings.TerminalBehavior;
                if (
                    b.ShowLineTimestamp == timestamp
                    && b.ShowLineNumber == number
                    && b.ShowFoldMarker == fold
                    && b.GutterBlank == blank
                )
                {
                    return;
                }
                b.ShowLineTimestamp = timestamp;
                b.ShowLineNumber = number;
                b.ShowFoldMarker = fold;
                b.GutterBlank = blank;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前会话。
            }
        });
    }

    /// <summary>
    /// 把设置里的裸字体族名解析为可寻址的 FontFamily 名:内置字体(随程序分发,
    /// fonts:VelaShell 集合)必须带集合 URI 前缀才能被字体管理器命中,系统字体名原样返回。
    /// 这使设置页的自由文本框既能填 "Cascadia Mono" 这类内置族,也能填任意系统字体。
    /// </summary>
    private static string ResolveTerminalFontFamily(string name) =>
        name is "Cascadia Mono"
            ? $"fonts:VelaShell#{name}"
            : name;

    /// <summary>
    /// 可在活动会话上安全更改的设置:回滚深度、字体、字号、主机输出编码以及完整的
    /// 终端行为/配色选项集。在标签创建时应用,并每次保存设置后重新应用到所有已打开的标签(#3/#15/#21)。
    /// </summary>
    private void ApplyLiveTerminalSettings(
        ITerminalEmulator emulator,
        AppSettings settings,
        bool forceUtf8 = false
    )
    {
        emulator.ScrollbackLines = settings.ScrollbackLines;
        if (emulator is not VelaTerminalControl control)
        {
            return;
        }
        // 本地终端(ConPTY)输出恒为 UTF-8,不套用面向远端主机的编码设置。
        control.SetEncoding(forceUtf8 ? Encoding.UTF8 : ResolveEncoding(settings.TerminalEncoding));
        if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
        {
            control.FontFamily = new(
                $"{ResolveTerminalFontFamily(settings.TerminalFont.Trim())}, fonts:VelaShell#Cascadia Mono, JetBrains Mono, Consolas, monospace"
            );
        }
        if (settings.TerminalFontSize > 0)
        {
            control.FontSize = settings.TerminalFontSize;
        }
        // 背景图开启时,终端控件自绘填充置全透明(不画背景),终端 tint 改由 TerminalHost 边框单层承担
        // (VelaBgTerminal 令牌半透明,MainWindow 负责)。若这里仍按不透明度上色,会与该边框两层叠加、
        // 保存后终端又变得几乎不透明。未开启背景图则恒为不透明(行为不变)。
        bool backgroundImageActive = !string.IsNullOrWhiteSpace(settings.Appearance.BackgroundImagePath);
        control.BackgroundOpacity = backgroundImageActive ? 0.0 : 1.0;
        TerminalBehaviorOptions behavior = settings.TerminalBehavior;
        control.LineHeight = behavior.LineHeight;
        control.ContentPadding = behavior.Padding;
        control.CursorStyle = behavior.CursorStyle;
        control.CursorBlink = behavior.CursorBlink;
        control.BellMode = behavior.BellMode;
        control.AllowRemoteClipboardWrite = behavior.AllowRemoteClipboardWrite;
        control.ScrollOnOutput = behavior.ScrollOnOutput;
        control.ShowLineTimestamp = behavior.ShowLineTimestamp;
        control.ShowLineNumber = behavior.ShowLineNumber;
        control.ShowFoldMarker = behavior.ShowFoldMarker;
        control.GutterBlank = behavior.GutterBlank;
        control.GutterMenu = new(
            Strings.Get("Gutter_LineNumber"),
            Strings.Get("Gutter_Timestamp"),
            Strings.Get("Gutter_FoldMarker"),
            Strings.Get("Gutter_Blank")
        );
        control.ScrollOnKeystroke = behavior.ScrollOnKeystroke;
        control.CopyOnSelect = behavior.CopyOnSelect;
        control.RightClickPaste = behavior.RightClickPaste;
        control.TrimTrailingWhitespaceOnCopy = behavior.TrimTrailingWhitespaceOnCopy;
        control.DoubleClickSelectsWord = behavior.DoubleClickSelectsWord;
        control.ConfirmMultilinePaste = behavior.ConfirmMultilinePaste;
        control.MultilinePasteConfirmation = MultilinePasteConfirmer;
        control.CtrlCCopiesWhenSelected = behavior.CtrlCCopiesWhenSelected;
        control.ImeEnabled = behavior.ImeSupport;
        control.LocalEchoEnabled = behavior.LocalEcho;

        // 现有两种传输的对端都自己回显:SSH 是远端 PTY,本地终端是 ConPTY 里的 shell。
        // 因此这两类标签上强制忽略「本地回显」开关 —— 否则用户为串口设备打开它之后,
        // 所有 SSH 与本地标签都会变成每个字符两遍。
        // 将来接入 Telnet 半双工 / 串口时,在此按传输置 false,让它们走正常逻辑。
        // (主机显式 CSI 12 l 要求终端回显时仍然生效,不受本项影响。)
        control.PeerEchoesInput = true;

        // 当前具名主题配套的整套终端配色(VelaDark→Dracula、Nord→Nord…),
        // 再叠上用户自定义的那几个单色(没改过的颜色一律跟随主题)。
        control.ThemePalette = TerminalAppearanceMapper.BuildThemePalette(ActiveUiTheme.Terminal);
        control.PaletteOverrides = TerminalAppearanceMapper.BuildPaletteOverrides(
            settings.Appearance
        );
    }

    /// <summary>
    /// 当前实际生效的界面主题:「跟随系统」按应用的实际变体落到 VelaDark / VelaLight。
    /// 没有主题服务(单测)时同样按变体兜底。
    /// </summary>
    private static UiTheme ActiveUiThemeFor(IThemeService? themeService) =>
        UiThemeCatalog.Resolve(
            themeService?.CurrentTheme,
            Avalonia.Application.Current?.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light
        );

    private UiTheme ActiveUiTheme => ActiveUiThemeFor(_themeService);

    /// <summary>主题变了 → 把新主题的终端配色下发到所有已打开的终端标签。</summary>
    private void RefreshTerminalThemePalette() =>
        RxSchedulers.MainThreadScheduler.Schedule(() =>
            ApplyLiveSettingsToOpenTabs(_latestSettings ?? new AppSettings())
        );

    /// <summary>
    /// 把终端默认背景不透明度实时应用到所有已打开的终端标签(背景图即时预览用:拖动滑杆时视图层直接调,
    /// 不经保存/重建标签)。opacity=1 即不透明,还原默认。
    /// </summary>
    public void ApplyTerminalBackgroundOpacityToAllTabs(double opacity)
    {
        foreach (TerminalTabViewModel tab in TabBar.Tabs.OfType<TerminalTabViewModel>())
        {
            if (tab.TerminalEmulator.Control is VelaTerminalControl control)
            {
                control.BackgroundOpacity = opacity;
            }
        }
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _latestSettings = settings;

        // SaveSettingsAsync 可能在线程池的回调中完成;字体/字号涉及布局,
        // 因此编组到 UI 线程(主调度器即 Avalonia 的 Dispatcher)。
        RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                ApplyShellPreferences(settings);
                ApplyLiveSettingsToOpenTabs(settings);

                // 已打开的文件浏览器同步最新的传输选项(冲突策略/并发/带宽等)与
                // “显示隐藏文件”状态(设置审计 C-04:设置中心与工具栏共用一个来源)。
                // 面板按会话缓存后,当前实例与全部缓存实例都要广播到。
                FileBrowser.TransferOptions = settings.Transfer;
                FileBrowser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
                ApplyColumnVisibility(FileBrowser, settings.Transfer);
                foreach (FileBrowserViewModel browser in _fileBrowserCache.Values)
                {
                    browser.TransferOptions = settings.Transfer;
                    browser.ShowHiddenFiles = settings.Transfer.ShowHiddenFiles;
                    ApplyColumnVisibility(browser, settings.Transfer);
                }
                RevealActiveSessionInSidebar();
            }
        );
    }

    /// <summary>
    /// 文件浏览器工具栏切换“显示隐藏文件”后写回持久化设置(设置审计 C-04),
    /// 使设置中心与工具栏共用 Transfer.ShowHiddenFiles 这一个状态来源。
    /// </summary>
    private void PersistShowHiddenFiles(bool value)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                if (settings.Transfer.ShowHiddenFiles == value)
                {
                    return;
                }
                settings.Transfer.ShowHiddenFiles = value;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前浏览。
            }
        });
    }

    /// <summary>
    /// 文件浏览器表头右键切换列显示后写回持久化设置(设置审计 C-04),
    /// 使各会话的面板与下次启动共用 Transfer 的列显示这一个状态来源。
    /// </summary>
    /// <param name="columnKey">列键("size"/"permissions"/"owner"/"group"/"type"/"modified")。</param>
    /// <param name="visible">该列切换后的可见性。</param>
    private void PersistColumnVisibility(string columnKey, bool visible)
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                if (!TrySetColumnVisibility(settings.Transfer, columnKey, visible))
                {
                    return;
                }
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 写回失败只影响下次启动的初始值,不打断当前浏览。
            }
        });
    }

    /// <summary>
    /// 把列键对应的设置项置为 <paramref name="visible" />;值本就相同(或列键无法识别)
    /// 时返回 false,调用方据此跳过一次无谓的落盘。
    /// </summary>
    private static bool TrySetColumnVisibility(
        TransferOptions transfer,
        string columnKey,
        bool visible
    )
    {
        switch (columnKey)
        {
            case "size" when transfer.ShowSizeColumn != visible:
                transfer.ShowSizeColumn = visible;
                return true;
            case "permissions" when transfer.ShowPermissionsColumn != visible:
                transfer.ShowPermissionsColumn = visible;
                return true;
            case "owner" when transfer.ShowOwnerColumn != visible:
                transfer.ShowOwnerColumn = visible;
                return true;
            case "group" when transfer.ShowGroupColumn != visible:
                transfer.ShowGroupColumn = visible;
                return true;
            case "type" when transfer.ShowTypeColumn != visible:
                transfer.ShowTypeColumn = visible;
                return true;
            case "modified" when transfer.ShowModifiedColumn != visible:
                transfer.ShowModifiedColumn = visible;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把设置里的列显示状态铺到某个文件浏览器面板(设置保存后广播用)。</summary>
    private static void ApplyColumnVisibility(
        FileBrowserViewModel browser,
        TransferOptions transfer
    )
    {
        browser.ShowSizeColumn = transfer.ShowSizeColumn;
        browser.ShowPermissionsColumn = transfer.ShowPermissionsColumn;
        browser.ShowOwnerColumn = transfer.ShowOwnerColumn;
        browser.ShowGroupColumn = transfer.ShowGroupColumn;
        browser.ShowTypeColumn = transfer.ShowTypeColumn;
        browser.ShowModifiedColumn = transfer.ShowModifiedColumn;
    }

    /// <summary>
    /// 一键切换整条侧栏(Ctrl+Shift+L / 命令面板):任一(时间/行号)开着就全部关掉,都关着则全部打开。
    /// 只想单独显示时间或行号的用户走设置页两个独立开关。写回持久化设置即可 ——
    /// SaveSettingsAsync 会触发 SettingsSaved → OnSettingsSaved,自动应用到所有已打开的终端标签。
    /// </summary>
    private void ToggleLineGutter()
    {
        if (_settingsService is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = await _settingsService
                    .GetSettingsAsync()
                    .ConfigureAwait(false);
                bool anyOn =
                    settings.TerminalBehavior.ShowLineTimestamp
                    || settings.TerminalBehavior.ShowLineNumber;
                settings.TerminalBehavior.ShowLineTimestamp = !anyOn;
                settings.TerminalBehavior.ShowLineNumber = !anyOn;
                await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            catch
            {
                // 切换失败只影响本次操作,不打断当前会话。
            }
        });
    }

    /// <summary>
    /// 把当前设置的终端外观应用到一个**插件**的终端视图。
    /// <para>
    /// 走的是宿主自己那条路(<see cref="ApplyLiveTerminalSettings" />),不是另抄一份 ——
    /// 用户调过一次终端字体,不该因为换到插件的面板里就得再调一次;
    /// 以后加的外观项也自动跟着走,不会有一处忘了同步。
    /// </para>
    /// </summary>
    internal void ApplyTerminalAppearanceToPluginView(ITerminalEmulator emulator) =>
        ApplyLiveTerminalSettings(emulator, _latestSettings ?? new AppSettings());

    /// <summary>把一份设置应用到所有已打开的终端标签(保存与外观预览共用)。</summary>
    private void ApplyLiveSettingsToOpenTabs(AppSettings settings)
    {
        foreach (TerminalTabViewModel tab in TabBar.Tabs.OfType<TerminalTabViewModel>())
        {
            ApplyLiveTerminalSettings(tab.TerminalEmulator, settings, tab.LocalShell is not null);
        }
    }

    private static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// 将当前活动终端标签的连接详情投射到状态栏中,使左下角的指示器始终反映用户正在看的标签。
    /// </summary>
    /// <summary>
    /// 把后台活动账本接到状态栏的圆环上。
    /// <para>
    /// 账本在后台线程上报(插件装载、内容校验、预读都不在 UI 线程),而状态栏属性是给绑定读的,
    /// 因此这里是唯一的切线程点。<c>Post</c> 而不是 <c>InvokeAsync</c>:圆环晚一帧更新
    /// 没有任何影响,但让一条后台链去等 UI 线程就有卡住它的可能。
    /// </para>
    /// <para>本视图模型与账本同为应用级单例、同生共死,故不解挂事件。</para>
    /// </summary>
    /// <param name="activity">后台活动账本;无界面单测传 null 时整块跳过。</param>
    private void WireBackgroundActivity(IBackgroundActivityService? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.Changed += () =>
            Dispatcher.UIThread.Post(() => StatusBar.ApplyBackgroundActivities(activity.Activities),
                DispatcherPriority.Background);
        StatusBar.ApplyBackgroundActivities(activity.Activities);
    }

    private void UpdateStatusBarForActiveTab()
    {
        TerminalTabViewModel? tab = ActiveTerminalTab;
        if (tab is null)
        {
            StatusBar.Status = Strings.Ready;
            StatusBar.StatusText = Strings.Ready;
            StatusBar.ConnectionInfo = string.Empty;
            StatusBar.Latency = string.Empty;
            StatusBar.WindowSize = string.Empty;
            return;
        }
        bool connected = tab.ConnectionStatus == SessionStatus.Connected;
        StatusBar.Status = connected ? Strings.Connected : Strings.Disconnected;
        StatusBar.StatusText = StatusBar.Status;
        StatusBar.ConnectionInfo = tab.ConnectionSummary;
        StatusBar.TerminalType = tab.TerminalTypeName;
        StatusBar.Encoding = tab.EncodingName;
        StatusBar.WindowSize = $"{tab.TerminalEmulator.Columns}×{tab.TerminalEmulator.Rows}";
        // 设计 gzmsb sbLatency 的写法是 "Latency: 12ms"(前缀由视图 StringFormat 提供)。
        StatusBar.Latency = tab.Latency is { } latency
            ? $"{(int)latency.TotalMilliseconds}ms"
            : string.Empty;
    }

    private void SetActiveFromDocument(DockDocument? dockDocument)
    {
        if (dockDocument is SftpDocument or PluginWorkspaceDocument)
        {
            ActiveTerminalTab = null;
            UpdateStatusBarForActiveTab();
            // 用空白占位替换底部文件浏览器,使网格行塌缩;
            // 终端的浏��器保留在 _fileBrowserCache 中,保持其真实的 IsVisible 状态。
            if (FileBrowser.SessionId != Guid.Empty || FileBrowser.IsVisible)
            {
                FileBrowser = CreatePlaceholderFileBrowser();
            }
            return;
        }
        if (
            dockDocument is not TerminalDocument document
            || !TabBar.Tabs.Contains(document.Terminal)
        )
        {
            return;
        }
        ActiveTerminalTab = document.Terminal;
        if (!ReferenceEquals(TabBar.ActiveTab, document.Terminal))
        {
            TabBar.ActiveTab = document.Terminal;
        }
        RebindFileBrowser();
    }

    private Task GetOrCreateSftpCloseTask(SftpDocument document)
    {
        lock (_sftpCloseTasksSync)
        {
            if (_sftpCloseTasks.TryGetValue(document, out Task? existing))
            {
                return existing;
            }

            Task task = CloseSftpDocumentCoreAsync(document);
            _sftpCloseTasks[document] = task;
            _ = task.ContinueWith(
                _ => RemoveSftpCloseTask(document, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private void RemoveSftpCloseTask(SftpDocument document, Task task)
    {
        lock (_sftpCloseTasksSync)
        {
            if (_sftpCloseTasks.TryGetValue(document, out Task? current) && ReferenceEquals(current, task))
            {
                _sftpCloseTasks.Remove(document);
            }
        }
    }

    internal bool HasPendingStandaloneSftpDocuments()
    {
        lock (_sftpCloseTasksSync)
        {
            return _sftpCloseTasks.Count > 0
                || Layout.AllDocuments().OfType<SftpDocument>().Any();
        }
    }

    private async Task CloseSftpDocumentCoreAsync(SftpDocument document)
    {
        try
        {
            await document.ViewModel.CloseAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
                Sidebar.SessionTree?.SetSessionStatus(
                    document.ViewModel.Profile.Id,
                    SessionStatus.Disconnected));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => LastConnectionError = ex.Message);
        }
    }

    internal async Task CloseStandaloneSftpDocumentsAsync()
    {
        Task[] closeTasks;
        lock (_sftpCloseTasksSync)
        {
            Task[] trackedTasks = [.. _sftpCloseTasks.Values];
            Task[] currentDocumentTasks = [.. Layout
                .AllDocuments()
                .OfType<SftpDocument>()
                .Select(GetOrCreateSftpCloseTask)];
            closeTasks = [.. trackedTasks.Concat(currentDocumentTasks).Distinct()];
        }
        await Task.WhenAll(closeTasks);
    }

    /// <summary>
    /// TabBar → 工作区反向同步:Ctrl+Tab / Ctrl+Shift+Tab 走 TabBar 的逻辑集合切换标签,
    /// 文档区必须跟着切到对应文档(原 Dock 集成缺这半边,快捷键切标签时画面不动)。
    /// </summary>
    private void SyncWorkspaceToActiveTab(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }
        TerminalDocument? document = Layout
            .AllDocuments()
            .OfType<TerminalDocument>()
            .FirstOrDefault(d => ReferenceEquals(d.Terminal, tab));
        if (document is not null && !ReferenceEquals(Layout.ActiveDocument, document))
        {
            Layout.ActivateDocument(document);
        }
    }

    private void OnDocumentClosed(TerminalDocument document)
    {
        TerminalTabViewModel tab = document.Terminal;
        if (TabBar.Tabs.Contains(tab))
        {
            TabBar.CloseTabCommand.Execute(tab).Subscribe();
        }
        StopSessionLogging(tab);
        CloseSftpForTab(tab);
        tab.Dispose();
        // Dispose 只拆终端传输;底层 SSH 客户端也要断开释放。
        TeardownSshSession(tab.SessionId);

        // 关闭标签不会再触发 ConnectionStatus 变更(已 Dispose),这里按剩下的标签重算
        // 树上的状态:同配置还有其他标签时取它们的合并状态,一个不剩才回到未连接。
        // 标签从标签栏移除时 OnTabsCollectionChanged 已经算过一次,这里是幂等兜底
        // ——文档关闭时标签可能已经不在标签栏里了。
        if (tab.Profile is { Id: var profileId } && profileId != Guid.Empty)
        {
            RefreshSessionStatus(profileId);
        }
    }

    /// <summary>
    /// 拆除正在关闭的标签对应会话的 SFTP 通道,并在浏览器当前绑定到该会话时将面板替换为空白占位。
    /// 面板仍在显示该会话时,取消绑定并隐藏 —— 关闭 SSH 标签不应留下一个活动的、可操作的 SFTP 面板(#22)。
    /// </summary>
    private void CloseSftpForTab(TerminalTabViewModel tab)
    {
        if (_sftpService is null || tab.SessionId == Guid.Empty)
        {
            return;
        }
        Guid closedSessionId = tab.SessionId;

        // 在拆除 SFTP 通道前先驱逐:使在飞的列举被取消,而非与客户端释放争抢
        // (否则 SSH.NET 会从 ListDirectory 内部抛 NRE)。
        EvictFileBrowser(closedSessionId);
        _ = _sftpService.CloseSessionAsync(closedSessionId);
    }
}

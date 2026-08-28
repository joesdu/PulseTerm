using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>设置左侧导航项(图标为 PathIcon 几何)。</summary>
public sealed record SettingsSection(string Name, string Icon);

/// <summary>关于页的开源依赖条目(项目主页 + 许可证页面可点击跳转)。</summary>
public sealed record DependencyInfo(string Name, string License, string Url, string LicenseUrl);

/// <summary>设置窗口的视图模型:承载全部偏好项的绑定、分组页导航、外观即时预览与加载/保存流程。</summary>
public class SettingsViewModel : ReactiveObject
{
    private readonly IHostKeyService? _hostKeyService;
    private readonly ILocalizationService? _localizationService;
    private readonly ISettingsPreviewService? _previewService;

    private readonly IRecentConnectionService? _recentConnections;
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IUpdateService? _updateService;
    private readonly JsonSerializerOptions jsonOption = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly JsonSerializerOptions exportJsonOption = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ———— 外观即时预览(改动立即可见,保存才落盘,取消/关窗回滚) ————
    /// <summary>打开设置时的基线快照:未保存关闭时用它恢复主题与外观。</summary>
    private AppSettings _baseline = new();

    private int _colorSchemeIndex = -1;
    private AppearanceOptions? _hookedAppearance;

    private AppSettings _loaded = new();
    private bool _previewed;
    private bool _saved;

    /// <summary>首次载入完成前(以及 ApplyToViewModel 批量回填期间)抑制预览广播。</summary>
    private bool _suppressPreview = true;

    /// <summary>注入设置/主题及各可选服务,建立命令、外观即时预览订阅与换语言重建逻辑。</summary>
    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        ILocalizationService? localizationService = null,
        ISshKeyService? sshKeyService = null,
        IRecentConnectionService? recentConnections = null,
        ISettingsPreviewService? previewService = null,
        IHostKeyService? hostKeyService = null,
        IGistSyncService? gistSyncService = null,
        IUpdateService? updateService = null,
        QuickCommandsViewModel? snippets = null,
        IQuickCommandRepository? quickCommandRepository = null
    )
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localizationService = localizationService;
        _recentConnections = recentConnections;
        _previewService = previewService;
        _hostKeyService = hostKeyService;
        _updateService = updateService;

        // 换语言时重建构造期求值的标签列表(左侧导航、快捷键参考页):本 VM 是单例,
        // 这些数组在启动语言下冻结,不重建就停留在旧语言(例如切英文保存后
        // 重开设置,左侧菜单仍是中文)。两者均为单例,订阅无泄漏。
        localizationService?.LanguageChanged += _ =>
        {
            int selectedSection = SelectedSectionIndex;
            Sections = BuildSections();
            // 分组标题要跟着换语言,只能整表重建;折叠态挂在分组对象上,按稳定 id 搬到新表,
            // 否则切一次语言就把用户收起来的分组全打开了。
            Dictionary<string, bool> expansion = ShortcutGroups.ToDictionary(
                group => group.Id,
                group => group.IsExpanded,
                StringComparer.Ordinal
            );
            ShortcutGroups = ShortcutCatalog.Build();
            foreach (ShortcutGroup group in ShortcutGroups)
            {
                group.IsExpanded = expansion.GetValueOrDefault(group.Id, true);
            }
            this.RaisePropertyChanged(nameof(Sections));
            this.RaisePropertyChanged(nameof(ShortcutGroups));
            this.RaisePropertyChanged(nameof(ShortcutMacNote));
            // 分组重建后过滤结果指向旧数组,必须跟着重算,否则快捷键页停在旧语言。
            RefreshShortcutView();
            SelectedSectionIndex = selectedSection;
        };

        // 外观即时预览:主题/强调色直接走 IThemeService(应用即生效);
        // Appearance 对象被整体替换(配色方案/载入)或其单项被绑定修改时广播预览快照。
        // 主题变化后重算配色方案下拉:“(默认)”标注移到新主题的默认方案上,
        // 跟随态的选中项同步跳转(暗 Dracula / 亮 Solarized Light)。
        this.WhenAnyValue(x => x.Theme, x => x.AccentColor)
            .Subscribe(_ =>
            {
                PreviewThemeLive();
                if (!_suppressPreview)
                {
                    RefreshColorSchemeDisplay();
                }
            });
        this.WhenAnyValue(x => x.Appearance)
            .Subscribe(appearance =>
            {
                HookAppearance(appearance);
                BroadcastPreview();
            });

        // 代理类型除经 ProxyTypeIndex 写入外还可能被直接改动(载入回填、测试),
        // 派生的下拉索引与可编辑标志都要跟着刷新。
        this.WhenAnyValue(x => x.Proxy)
            .Subscribe(proxy =>
            {
                _hookedProxy?.PropertyChanged -= OnProxyItemChanged;
                _hookedProxy = proxy;
                proxy?.PropertyChanged += OnProxyItemChanged;
            });
        SshKeys = new(sshKeyService);
        Snippets =
            snippets
            ?? (
                quickCommandRepository is null
                    ? null
                    : new QuickCommandsViewModel(quickCommandRepository)
            );
        Sync = gistSyncService is null ? null : new SyncViewModel(gistSyncService);
        RemoveKnownHostCommand = ReactiveCommand.CreateFromTask<KnownHost>(RemoveKnownHostAsync);
        ClearTrustedLaunchTargetsCommand = ReactiveCommand.Create(() =>
        {
            Security.TrustedExternalLaunchTargets.Clear();
            RefreshTrustedLaunchTargets();
        });
        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ResetCommand = ReactiveCommand.Create(ResetToDefaults);
        SetAccentCommand = ReactiveCommand.Create<string>(hex => AccentColor = hex);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        CheckUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync);
        RestartToUpdateCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            UpdateStatus = Strings.Get("SetAbout_Applying");
            try
            {
                // 解包是磁盘密集操作,放到线程池执行,UI 保持响应;
                // 成功后服务内部拉起外置换版进程并请求本进程退出。
                await Task.Run(() => _updateService?.ApplyUpdateAndRestart());
            }
            catch
            {
                // 解包失败时应用目录一个字节都没动(换版尚未开始),应用继续以当前版本运行。
                // 这一步之后的失败发生在外置进程里,由它回滚并落盘原因,下次启动时展示。
                UpdateReady = false;
                UpdateStatus = Strings.Get("SetAbout_ApplyFailed");
            }
        });
        RepairUpdateCommand = ReactiveCommand.CreateFromTask(RepairUpdateStateAsync);

        // 商店版(MSIX)装在只读的 WindowsApps 下,更新由 Microsoft Store 接管:
        // 应用内的检查/下载/换版/修复一律无意义,整块换成一句说明,免得用户白点。
        UpdatesManagedByStore = _updateService?.IsStoreManaged ?? false;
        ShowInAppUpdateControls = !UpdatesManagedByStore;
        if (UpdatesManagedByStore)
        {
            UpdateStatus = Strings.Get("SetAbout_StoreManagedUpdates");
        }
        // 上一轮换版由外置进程执行,它报的错没法当场弹给用户,只能落盘留到这时候如实展示。
        else if (_updateService?.LastUpdateError is { Length: > 0 } lastError)
        {
            UpdateStatus = Strings.Format("SetAbout_PreviousUpdateFailed", lastError);
        }
    }

    /// <summary>更新由 Microsoft Store 接管(商店版 MSIX);为真时关于页只显示说明,不提供更新操作。</summary>
    public bool UpdatesManagedByStore { get; }

    /// <summary>是否展示应用内更新操作(检查更新 / 重启并更新 / 修复更新状态)。</summary>
    public bool ShowInAppUpdateControls { get; }

    /// <summary>
    /// 强制重置更新状态:回滚没换完的换版、清掉暂存目录与遗留文件,让下一次检查更新从干净起点开始。
    /// 意外中断(断电、强杀进程、杀软拦截)后更新流程卡住时的自愈出口。
    /// </summary>
    private async Task RepairUpdateStateAsync()
    {
        if (_updateService is null)
        {
            UpdateStatus = Strings.Get("Msg_UpdateServiceNotAvailable");
            return;
        }
        UpdateReady = false;
        UpdateStatus = Strings.Get("SetAbout_Repairing");
        bool clean = await Task.Run(_updateService.RepairUpdateState);
        UpdateStatus = Strings.Get(clean ? "SetAbout_RepairDone" : "SetAbout_RepairFailed");
    }

    // ———— 顶层字段(既有行为:保存后立即生效) ————
    // 默认值一律与 AppSettings 模型保持一致(设置审计 C-05/C-06):
    // VM 仅是绑定层,载入时会被 ApplyToViewModel 覆盖,不得自行声明业务默认值。

    /// <summary>界面语言(区域代码,如 "zh-CN");保存后即时切换,无需重启。</summary>
    public string Language
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = new AppSettings().Language;

    /// <summary>主题模式("dark"/"light"/"system");改动即时预览。</summary>
    public string Theme
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "dark";

    /// <summary>终端字体族名称。</summary>
    public string TerminalFont
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Cascadia Mono";

    /// <summary>终端字号(磅)。</summary>
    public int TerminalFontSize
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 14;

    /// <summary>终端回滚缓冲的最大行数。</summary>
    public int ScrollbackLines
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = new AppSettings().ScrollbackLines;

    /// <summary>新建 SSH 连接时的默认端口。</summary>
    public int DefaultPort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 22;

    /// <summary>终端类型标识(TERM 值,如 "xterm-256color")。</summary>
    public string TerminalType
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "xterm-256color";

    /// <summary>终端字符编码(如 "UTF-8")。</summary>
    public string TerminalEncoding
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "UTF-8";

    /// <summary>强调色覆盖(十六进制,例如 "#00D4AA");为空则使用主题默认值。</summary>
    public string AccentColor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ———— 分组选项(设计 §14 各页;POCO 直接 TwoWay 绑定) ————

    /// <summary>常规页选项(POCO,直接 TwoWay 绑定)。</summary>
    public GeneralOptions General
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>外观页选项(POCO);整体替换或单项修改均触发即时预览。</summary>
    public AppearanceOptions Appearance
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>终端行为页选项(光标样式、响铃模式等)。</summary>
    public TerminalBehaviorOptions TerminalBehavior
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>文件传输页选项(冲突策略、默认下载目录等)。</summary>
    public TransferOptions Transfer
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>安全页选项。</summary>
    public SecurityOptions Security
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>密钥页选项(如默认认证密钥)。</summary>
    public KeyOptions Keys
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>代理页选项(POCO,直接 TwoWay 绑定)。</summary>
    public ProxyOptions Proxy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    /// <summary>密钥管理页。</summary>
    public SshKeyManagerViewModel SshKeys { get; }

    /// <summary>
    /// 安全审计页“已信任主机”列表(SonnetDB known_hosts 集合)。删除即时生效,
    /// 不随“保存设置”走:这是信任数据管理,不是偏好设置。
    /// </summary>
    public ObservableCollection<KnownHost> KnownHosts { get; } = [];

    /// <summary>是否存在已信任主机(用于控制空状态显示)。</summary>
    public bool HasKnownHosts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 已信任主机列表隐藏主机地址与端口(截图防泄露)。刻意不持久化:
    /// 仅会话内状态,每次打开设置都恢复为隐藏,需手动关闭才显示明文。
    /// </summary>
    public bool MaskKnownHostAddresses
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>删除一条已信任主机指纹;下次连接该主机将重新执行首次指纹流程。</summary>
    public ReactiveCommand<KnownHost, RxVoid> RemoveKnownHostCommand { get; }

    /// <summary>
    /// 外部登录里被用户勾过「不再询问」的目标数量(<c>scheme://user@host:port</c>)。
    /// 只显示条数而不铺一张表:这些目标本就是一次性登录的落点,用户要的是「清掉」而不是逐条端详。
    /// </summary>
    public int TrustedLaunchTargetCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否存在已信任的外部登录目标(用于控制清空按钮的显示)。</summary>
    public bool HasTrustedLaunchTargets
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>清空已信任的外部登录目标;此后每次外部拉起都重新确认(随「保存」生效)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearTrustedLaunchTargetsCommand { get; }

    /// <summary>把信任目标的派生显示状态拉回与 <see cref="Security" /> 一致。</summary>
    private void RefreshTrustedLaunchTargets()
    {
        TrustedLaunchTargetCount = Security.TrustedExternalLaunchTargets.Count;
        HasTrustedLaunchTargets = TrustedLaunchTargetCount > 0;
    }

    /// <summary>代码片段页(quick_commands 集合);无存储时为 null。</summary>
    public QuickCommandsViewModel? Snippets { get; }

    /// <summary>云同步页(GitHub Gist 多端同步);无同步服务时为 null。</summary>
    public SyncViewModel? Sync { get; }

    /// <summary>按设计 §14 构建的左侧导航分区。换语言时经 <see cref="BuildSections" /> 重建。</summary>
    public SettingsSection[] Sections { get; private set; } = BuildSections();

    private static SettingsSection[] BuildSections() =>
        [
            new(
                Strings.Get("SetVm_SectionGeneral"),
                "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"
            ),
            new(
                Strings.Get("SetVm_SectionAppearance"),
                "M12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16c2.76 0 5-2.24 5-5 0-4.42-4.03-8-9-8zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 9 6.5 9 8 9.67 8 10.5 7.33 12 6.5 12zm3-4C8.67 8 8 7.33 8 6.5S8.67 5 9.5 5s1.5.67 1.5 1.5S10.33 8 9.5 8zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 5 14.5 5s1.5.67 1.5 1.5S15.33 8 14.5 8zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 9 17.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z"
            ),
            new(
                Strings.Get("SetVm_SectionTerminal"),
                "M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V8h16v10zm-2-1h-6v-2h6v2zM7.5 17l-1.41-1.41L8.67 13l-2.59-2.59L7.5 9l4 4-4 4z"
            ),
            new(
                Strings.Get("SetVm_SectionKeys"),
                "M12.65 10C11.83 7.67 9.61 6 7 6c-3.31 0-6 2.69-6 6s2.69 6 6 6c2.61 0 4.83-1.67 5.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z"
            ),
            new(
                Strings.Get("SetVm_SectionShortcuts"),
                "M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z"
            ),
            new(
                Strings.Get("SetVm_SectionTransfer"),
                "M16 17.01V10h-2v7.01h-3L15 21l4-3.99h-3zM9 3 5 6.99h3V14h2V6.99h3L9 3z"
            ),
            new(
                Strings.Get("SetVm_SectionSecurity"),
                "M12 1 3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4z"
            ),
            new(
                Strings.Get("SetVm_SectionProxy"),
                "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z"
            ),
            new(
                Strings.Get("SetVm_SectionSnippets"),
                "M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0 4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z"
            ),
            new(
                Strings.Get("SetVm_SectionSync"),
                "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM14 13v4h-4v-4H7l5-5 5 5h-3z"
            ),
            new(
                Strings.Get("SetVm_SectionAbout"),
                "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"
            ),
            new(
                Strings.Get("SetVm_SectionSupport"),
                "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"
            ),
        ];

    /// <summary>当前选中的左侧导航分页下标。</summary>
    public int SelectedSectionIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>支持的界面语言(顺序即语言下拉的条目顺序)。</summary>
    public string[] AvailableLanguages { get; } = ["zh-CN", "en", "zh-TW", "ja", "ko"];

    /// <summary>主题下拉可选值。</summary>
    public string[] AvailableThemes { get; } = ["dark", "light", "system"];

    // xterm-256color 是首选/推荐配置,排在最前。
    /// <summary>终端类型下拉可选值(推荐项 xterm-256color 置首)。</summary>
    public string[] AvailableTerminalTypes { get; } =
    [
        "xterm-256color",
        "xterm",
        "vt520",
        "vt420",
        "vt340",
        "vt320",
        "vt220",
        "vt102",
        "vt100",
        "vt52",
    ];

    /// <summary>终端编码下拉可选值。</summary>
    public string[] AvailableEncodings { get; } =
    ["UTF-8", "GBK", "GB18030", "Big5", "Shift_JIS", "EUC-KR", "ISO-8859-1"];

    /// <summary>更新通道下拉可选值。</summary>
    public string[] AvailableUpdateChannels { get; } = ["stable", "preview"];

    /// <summary>光标样式下拉可选值。</summary>
    public string[] AvailableCursorStyles { get; } = ["bar", "block", "underline"];

    /// <summary>响铃模式下拉可选值。</summary>
    public string[] AvailableBellModes { get; } = ["system", "none", "visual"];

    /// <summary>传输冲突策略下拉可选值。</summary>
    public string[] AvailableConflictPolicies { get; } = ["ask", "overwrite", "skip", "rename"];

    /// <summary>标签栏位置下拉可选值。</summary>
    public string[] AvailableTabBarPositions { get; } = ["top", "bottom"];

    /// <summary>侧栏位置下拉可选值。</summary>
    public string[] AvailableSidebarPositions { get; } = ["left", "right"];

    /// <summary>启动窗口状态下拉可选值。</summary>
    public string[] AvailableWindowStates { get; } = ["remember", "maximized", "default"];

    // ———— 关于页(真实构建信息) ————

    /// <summary>版本号取自程序集 InformationalVersion(由 Directory.Build.props 的
    /// Version 统一供给,含 -beta 等预发布后缀),不再手工硬编码。</summary>
    public string AppVersion { get; } =
        "v"
        + (
            Assembly
                .GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0]
            ?? "0.0.0"
        );

    /// <summary>关于页显示的 UI 框架版本(版本取自实际引用的 NuGet 包,见 <see cref="PackageVersions" />)。</summary>
    public static string AboutFramework => Describe("Avalonia UI", "Avalonia");

    /// <summary>关于页显示的 .NET 运行时版本。</summary>
    public static string AboutRuntime =>
        $".NET {Environment.Version.Major}.{Environment.Version.Minor}";

    /// <summary>关于页显示的 SSH 库版本(版本取自实际引用的 NuGet 包,见 <see cref="PackageVersions" />)。</summary>
    public static string AboutSshLibrary => Describe("Tmds.Ssh", "Tmds.Ssh");

    /// <summary>
    /// 拼 "名称 版本";版本读不到时只显示名称 —— 关于页少个版本号可以接受,
    /// 显示一个可能已经过时的写死数字不行。
    /// </summary>
    private static string Describe(string displayName, string packageId) =>
        PackageVersions.Of(packageId) is { } version ? $"{displayName} {version}" : displayName;

    /// <summary>关于页显示的操作系统版本与架构。</summary>
    public static string AboutOs =>
        $"{Environment.OSVersion.VersionString} ({DescribeArchitecture(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture)})";

    /// <summary>
    /// 架构显示文本。取真实架构名而非「是不是 64 位」——
    /// 原先的 <c>Is64BitOperatingSystem ? "x64" : "x86"</c> 会把 arm64 报成 x64、arm 报成 x86。
    /// </summary>
    /// <remarks>
    /// 进程架构与系统架构不一致时两者都显示(如 x64 版跑在 arm64 Windows / Apple Silicon 的仿真层上)。
    /// 这不是冗余信息:自更新按**进程**架构选产物(见 <see cref="Services.Update.UpdateManifest.CurrentRid" />),
    /// 装错架构的人会一直留在仿真轨上拿不到原生版本,关于页是最容易看出来的地方。
    /// 架构名直接取枚举名小写,与 RID 写法一致(X64→x64、Arm64→arm64),
    /// 于是将来出现的新架构(loongarch64、riscv64…)无需改这里也能正确显示。
    /// </remarks>
    internal static string DescribeArchitecture(Architecture process, Architecture os) =>
        process == os
            ? Name(os)
            : Strings.Format("SetAbout_ArchEmulated", Name(process), Name(os));

    private static string Name(Architecture architecture) =>
        architecture.ToString().ToLowerInvariant();

    /// <summary>关于页显示的配置文件所在目录。</summary>
    public static string AboutConfigPath => new VelaShellStoragePaths().RootDirectory;

    /// <summary>
    /// 关于页贡献者(设计 kGwqX;数据来自仓库真实提交者,新增贡献者在此追加)。
    /// 头像在 LoadAsync 时后台拉取。
    /// </summary>
    public ContributorViewModel[] Contributors { get; } =
        [
            new("joesdu"), new("tsaiggo"), new("pengqian089"), new("Cyaim")
        ];

    /// <summary>开源依赖(真实技术栈)。</summary>
    public DependencyInfo[] AboutDependencies { get; } =
    [
        new(
            "Avalonia UI",
            "MIT",
            "https://github.com/AvaloniaUI/Avalonia",
            "https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md"
        ),
        new(
            "Tmds.Ssh",
            "MIT",
            "https://github.com/Tmds/Tmds.Ssh",
            "https://github.com/Tmds/Tmds.Ssh/blob/main/LICENSE"
        ),
        new(
            "ReactiveUI",
            "MIT",
            "https://github.com/reactiveui/ReactiveUI",
            "https://github.com/reactiveui/ReactiveUI/blob/main/LICENSE"
        ),
        new(
            "SonnetDB",
            "MIT",
            "https://github.com/IoTSharp/SonnetDB",
            "https://github.com/IoTSharp/SonnetDB/blob/main/LICENSE"
        ),
        new(
            "FluentFTP",
            "MIT",
            "https://github.com/robinrodricks/FluentFTP",
            "https://github.com/robinrodricks/FluentFTP/blob/master/LICENSE.TXT"
        ),
        new(
            "MaxMind.Db",
            "Apache-2.0",
            "https://github.com/maxmind/MaxMind-DB-Reader-dotnet",
            "https://github.com/maxmind/MaxMind-DB-Reader-dotnet/blob/main/LICENSE"
        ),
    ];

    /// <summary>载入设置命令:从服务读取配置并回填视图模型。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>保存设置命令:回写并落盘,主题/语言即时生效后关闭窗口。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>取消命令:请求关闭窗口(未保存改动由关闭流程回滚)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>恢复默认命令:将所有设置回到出厂值并即时预览。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetCommand { get; }

    /// <summary>设置强调色命令:以传入的十六进制色值更新 <see cref="AccentColor" />。</summary>
    public ReactiveCommand<string, RxVoid> SetAccentCommand { get; }

    /// <summary>清除历史记录命令:清空连接历史。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearHistoryCommand { get; }

    /// <summary>检查更新命令:检查 → 下载(带进度)→ 就绪后提示重启。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CheckUpdatesCommand { get; }

    /// <summary>重启并应用已下载更新命令(仅在 <see cref="UpdateReady" /> 为真时有意义)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RestartToUpdateCommand { get; }

    /// <summary>修复更新状态命令:清掉卡住的更新残留,让检查更新能重新走通。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RepairUpdateCommand { get; }

    /// <summary>检查更新的状态提示文本。</summary>
    public string UpdateStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>更新已下载完毕、可重启完成安装(控制“重启并更新”按钮的显隐)。</summary>
    public bool UpdateReady
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 检查更新完整流程:未接服务 / 非安装版 / 已最新 / 发现更新 四态。发现更新则带进度下载,
    /// 完成后置 <see cref="UpdateReady" />,由关于页“重启并更新”按钮触发 <see cref="RestartToUpdateCommand" />。
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is null)
        {
            UpdateStatus = Strings.Get("Msg_UpdateServiceNotAvailable");
            return;
        }
        if (UpdatesManagedByStore)
        {
            UpdateStatus = Strings.Get("SetAbout_StoreManagedUpdates");
            return;
        }
        UpdateReady = false;
        UpdateStatus = Strings.Get("SetAbout_Checking");
        // 上一轮的失败提示到此为止:用户已经在重新尝试,再挂着旧错误只会误导。
        _updateService.ClearUpdateError();
        bool hasUpdate;
        try
        {
            hasUpdate = await _updateService.CheckForUpdateAsync();
        }
        catch
        {
            UpdateStatus = Strings.Get("SetAbout_UpdateCheckFailed");
            return;
        }
        if (!hasUpdate)
        {
            UpdateStatus = Strings.Get("SetAbout_UpToDate");
            return;
        }
        if (!_updateService.CanSelfUpdate)
        {
            // 有新版本但应用目录不可写(如装在 Program Files),只能提示手动下载。
            UpdateStatus = Strings.Format(
                "SetAbout_UpdateAvailableManual",
                _updateService.AvailableVersion ?? string.Empty
            );
            return;
        }
        try
        {
            UpdateStatus = Strings.Format("SetAbout_Downloading", 0);
            // Progress 在 UI 线程创建,回调自动回到 UI 线程,可安全更新绑定属性。
            Progress<int> progress = new(p =>
                UpdateStatus = Strings.Format("SetAbout_Downloading", p)
            );
            await _updateService.DownloadUpdateAsync(progress);
        }
        catch
        {
            UpdateStatus = Strings.Get("SetAbout_DownloadFailed");
            return;
        }
        UpdateReady = true;
        UpdateStatus = Strings.Format(
            "SetAbout_UpdateReady",
            _updateService.AvailableVersion ?? string.Empty
        );
    }

    /// <summary>清除历史记录的状态提示文本。</summary>
    public string ClearHistoryStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>外观页强调色色板(设计 ZAbb9)。</summary>
    public string[] AccentSwatches { get; } =
    ["#00D4AA", "#3498DB", "#9B59B6", "#E74C3C", "#F39C12", "#1ABC9C", "#E91E63"];

    // ———— 终端配色方案预设(§12.5) ————

    /// <summary>
    /// 方案名列表,当前主题的默认方案带“(默认)”后缀
    /// (暗 = Dracula,亮 = Solarized Light),随主题切换动态刷新。
    /// </summary>
    public string[] AvailableColorSchemes
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = BuildSchemeNames(0);

    /// <summary>程序化刷新选中项(主题切换/载入)时抑制“套用方案”写回,防止把跟随态钉死。</summary>
    private bool _suppressSchemeApply;

    /// <summary>当前主题下的默认方案下标:暗 = Dracula(0),亮 = Solarized Light。</summary>
    private int ThemeDefaultSchemeIndex =>
        IsLightThemeActive
            ? Math.Max(
                0,
                Array.FindIndex(TerminalColorScheme.BuiltIn, s => s.Name == "Solarized Light")
            )
            : 0;

    /// <summary>“跟随系统”时以应用实际生效的主题变体判定亮/暗。</summary>
    private bool IsLightThemeActive =>
        Theme == "light"
        || (
            Theme == "system"
            && Avalonia.Application.Current?.ActualThemeVariant
                == Avalonia.Styling.ThemeVariant.Light
        );

    private static string[] BuildSchemeNames(int defaultIndex) =>
        [
            .. TerminalColorScheme.BuiltIn.Select(
                (s, i) =>
                    i == defaultIndex ? $"{s.Name}{Strings.Get("SetVm_DefaultSuffix")}" : s.Name
            ),
        ];

    /// <summary>
    /// 选择预设即把整套颜色写入 Appearance(保存后生效);-1 = 未选择(改过单色)。
    /// 选择带“(默认)”的方案 = 恢复出厂值、终端跟随主题;跟随态下选中项随主题
    /// 自动落在对应主题的默认方案上(暗 Dracula / 亮 Solarized Light)。
    /// </summary>
    public int ColorSchemeIndex
    {
        get => _colorSchemeIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _colorSchemeIndex, value);
            if (_suppressSchemeApply || value < 0 || value >= TerminalColorScheme.BuiltIn.Length)
            {
                return;
            }
            // 克隆后整体替换:引用变化才能保证 Appearance.X 路径绑定全部刷新。
            AppearanceOptions updated =
                JsonSerializer.Deserialize<AppearanceOptions>(JsonSerializer.Serialize(Appearance))
                ?? new AppearanceOptions();

            // 选中当前主题的默认方案 = 回到出厂值(Dracula 色值,零覆盖,跟随主题);
            // 其余方案按其色值写入(与出厂差异成为覆盖,主题切换不再改变终端配色)。
            TerminalColorScheme
                .BuiltIn[value == ThemeDefaultSchemeIndex ? 0 : value]
                .ApplyTo(updated);
            Appearance = updated;

            // 写回后重算显示:出厂值命中会折射到当前主题的默认方案位。
            RefreshColorSchemeDisplay();
        }
    }

    /// <summary>
    /// 重算方案下拉的条目后缀与选中项:出厂值(跟随主题)显示为当前主题默认方案,
    /// 显式方案按整套颜色反向匹配,改过单色则显示“未选择”(-1)。
    /// </summary>
    private void RefreshColorSchemeDisplay()
    {
        int defaultIndex = ThemeDefaultSchemeIndex;
        bool following = TerminalColorScheme.BuiltIn[0].Matches(Appearance); // 出厂值 = Dracula 色值
        int desired = following
            ? defaultIndex
            : Array.FindIndex(TerminalColorScheme.BuiltIn, s => s.Matches(Appearance));
        _suppressSchemeApply = true;
        try
        {
            // 先换条目再定选中:ItemsSource 替换会让 ComboBox 短暂把 -1 写回来,抑制期内无害。
            AvailableColorSchemes = BuildSchemeNames(defaultIndex);
            _colorSchemeIndex = desired;
            this.RaisePropertyChanged(nameof(ColorSchemeIndex));
        }
        finally
        {
            _suppressSchemeApply = false;
        }
    }

    /// <summary>
    /// 快捷键参考页的完整分组(只读展示),取自 <see cref="ShortcutCatalog" /> ——
    /// 该表是全应用快捷键的唯一事实来源,与 <c>docs/快捷键参考.md</c> 同源。
    /// 本属性保留全量,过滤后的展示用 <see cref="FilteredShortcutGroups" />。
    /// </summary>
    public ShortcutGroup[] ShortcutGroups { get; private set; } = ShortcutCatalog.Build();

    /// <summary>
    /// 快捷键页的搜索词:同时匹配动作名、键位与生效条件备注(见 <see cref="ShortcutItem.SearchText" />)。
    /// 空串等于不过滤。
    /// </summary>
    public string ShortcutFilter
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RefreshShortcutView();
        }
    } = string.Empty;

    /// <summary>
    /// 按 <see cref="ShortcutFilter" /> 过滤后的分组;整组条目都被滤掉时该组不出现。
    /// 元素与 <see cref="ShortcutGroups" /> 是同一批分组对象(折叠态挂在对象上,
    /// 因此过滤前后不会丢),被滤掉的条目由各分组的 <see cref="ShortcutGroup.FilteredItems" /> 表达。
    /// 首次访问时尚未搜索过,直接就是全量表。
    /// </summary>
    public ShortcutGroup[] FilteredShortcutGroups
    {
        get => field ??= ShortcutGroups;
        private set;
    }

    /// <summary>过滤后是否还有条目(为 false 时页面显示「没有匹配的快捷键」)。</summary>
    public bool HasShortcutMatches => FilteredShortcutGroups.Length > 0;

    /// <summary>页脚计数文案:未过滤时是总数,过滤后是命中数。</summary>
    public string ShortcutCountText =>
        Strings.Format("Sc_Count", FilteredShortcutGroups.Sum(group => group.FilteredItems.Count));

    /// <summary>macOS 上终端内改用 Command 的说明(全局键位仍是 Ctrl,见 KeyboardShortcutService)。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:将成员标记为 static", Justification = "<挂起>")]
    public string ShortcutMacNote => Strings.Get("Sc_MacNote");

    /// <summary>搜索期间被临时强制展开的分组,其原折叠态暂存于此,搜索清空后原样还回去。</summary>
    private readonly Dictionary<string, bool> _shortcutExpansionBeforeSearch = [with(StringComparer.Ordinal)];

    private bool _shortcutSearchActive;

    /// <summary>
    /// 重算过滤结果与其派生文案;换语言与改搜索词都经这里。
    /// 折叠态的处理与快捷命令面板一致:搜索命中的分组临时强制展开(否则搜到了却看不见),
    /// 搜索清空时把用户原来的折叠态还回去 —— 搜一次不该把人手动收起来的分组全打开。
    /// </summary>
    private void RefreshShortcutView()
    {
        string query = ShortcutFilter.Trim();
        bool active = query.Length > 0;
        if (active && !_shortcutSearchActive)
        {
            _shortcutExpansionBeforeSearch.Clear();
            foreach (ShortcutGroup group in ShortcutGroups)
            {
                _shortcutExpansionBeforeSearch[group.Id] = group.IsExpanded;
            }
        }
        else if (!active && _shortcutSearchActive)
        {
            foreach (ShortcutGroup group in ShortcutGroups)
            {
                group.IsExpanded = _shortcutExpansionBeforeSearch.GetValueOrDefault(group.Id, true);
            }
            _shortcutExpansionBeforeSearch.Clear();
        }
        _shortcutSearchActive = active;

        List<ShortcutGroup> visible = [];
        foreach (ShortcutGroup group in ShortcutGroups)
        {
            group.FilteredItems.Clear();
            foreach (ShortcutItem item in group.Items)
            {
                if (!active || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    group.FilteredItems.Add(item);
                }
            }
            if (group.FilteredItems.Count == 0)
            {
                continue;
            }
            if (active)
            {
                group.IsExpanded = true;
            }
            visible.Add(group);
        }
        FilteredShortcutGroups = [.. visible];
        this.RaisePropertyChanged(nameof(FilteredShortcutGroups));
        this.RaisePropertyChanged(nameof(HasShortcutMatches));
        this.RaisePropertyChanged(nameof(ShortcutCountText));
    }

    // ———— 下拉的索引映射(POCO 字符串 ↔ ComboBox SelectedIndex) ————

    /// <summary>主题下拉选中项与 <see cref="Theme" /> 字符串之间的索引映射。</summary>
    public int ThemeIndex
    {
        get =>
            Theme switch
            {
                "light" => 1,
                "system" => 2,
                _ => 0,
            };
        set
        {
            Theme = value switch
            {
                1 => "light",
                2 => "system",
                _ => "dark",
            };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>语言下拉选中项与 <see cref="Language" /> 之间的索引映射。</summary>
    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(AvailableLanguages, Language));
        set
        {
            if (value >= 0 && value < AvailableLanguages.Length)
            {
                Language = AvailableLanguages[value];
            }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>更新通道下拉选中项与 <see cref="GeneralOptions.UpdateChannel" /> 之间的索引映射。</summary>
    public int UpdateChannelIndex
    {
        get => General.UpdateChannel == "preview" ? 1 : 0;
        set
        {
            General.UpdateChannel = value == 1 ? "preview" : "stable";
            this.RaisePropertyChanged();
        }
    }

    /// <summary>光标样式下拉选中项与 <see cref="TerminalBehaviorOptions.CursorStyle" /> 之间的索引映射。</summary>
    public int CursorStyleIndex
    {
        get =>
            TerminalBehavior.CursorStyle switch
            {
                "block" => 1,
                "underline" => 2,
                _ => 0,
            };
        set
        {
            TerminalBehavior.CursorStyle = value switch
            {
                1 => "block",
                2 => "underline",
                _ => "bar",
            };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>响铃模式下拉选中项与 <see cref="TerminalBehaviorOptions.BellMode" /> 之间的索引映射。</summary>
    public int BellModeIndex
    {
        get =>
            TerminalBehavior.BellMode switch
            {
                "none" => 1,
                "visual" => 2,
                _ => 0,
            };
        set
        {
            TerminalBehavior.BellMode = value switch
            {
                1 => "none",
                2 => "visual",
                _ => "system",
            };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>冲突策略下拉选中项与 <see cref="TransferOptions.ConflictPolicy" /> 之间的索引映射。</summary>
    public int ConflictPolicyIndex
    {
        get =>
            Transfer.ConflictPolicy switch
            {
                "overwrite" => 1,
                "skip" => 2,
                "rename" => 3,
                _ => 0,
            };
        set
        {
            Transfer.ConflictPolicy = value switch
            {
                1 => "overwrite",
                2 => "skip",
                3 => "rename",
                _ => "ask",
            };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>代理类型下拉选中项与 <see cref="ProxyOptions.Type" /> 之间的索引映射。</summary>
    public int ProxyTypeIndex
    {
        get =>
            Proxy.Type switch
            {
                "system" => 1,
                "http" => 2,
                "socks5" => 3,
                _ => 0,
            };
        set
        {
            Proxy.Type = value switch
            {
                1 => "system",
                2 => "http",
                3 => "socks5",
                _ => "none",
            };
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsProxyEditable));
        }
    }

    /// <summary>代理连接参数(主机/端口/凭据/DNS)是否可编辑:仅 http / socks5 类型开放。</summary>
    public bool IsProxyEditable => Proxy.Type is "http" or "socks5";

    /// <summary>标签栏位置下拉选中项与 <see cref="AppearanceOptions.TabBarPosition" /> 之间的索引映射。</summary>
    public int TabBarPositionIndex
    {
        get => Appearance.TabBarPosition == "bottom" ? 1 : 0;
        set
        {
            Appearance.TabBarPosition = value == 1 ? "bottom" : "top";
            this.RaisePropertyChanged();
        }
    }

    /// <summary>侧栏位置下拉选中项与 <see cref="AppearanceOptions.SidebarPosition" /> 之间的索引映射。</summary>
    public int SidebarPositionIndex
    {
        get => Appearance.SidebarPosition == "right" ? 1 : 0;
        set
        {
            Appearance.SidebarPosition = value == 1 ? "right" : "left";
            this.RaisePropertyChanged();
        }
    }

    /// <summary>启动窗口状态下拉选中项与 <see cref="AppearanceOptions.StartupWindowState" /> 之间的索引映射。</summary>
    public int WindowStateIndex
    {
        get =>
            Appearance.StartupWindowState switch
            {
                "maximized" => 1,
                "default" => 2,
                _ => 0,
            };
        set
        {
            Appearance.StartupWindowState = value switch
            {
                1 => "maximized",
                2 => "default",
                _ => "remember",
            };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>保存/取消后由窗口关闭。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>文件夹选择器直接改写 Transfer POCO 后,通知绑定刷新。</summary>
    public void RaisePropertyChangedForTransfer() => this.RaisePropertyChanged(nameof(Transfer));

    /// <summary>常规页“清除历史记录”:清空 SonnetDB 连接历史。</summary>
    private async Task ClearHistoryAsync()
    {
        if (_recentConnections is null)
        {
            return;
        }
        try
        {
            await _recentConnections.ClearAsync();
            ClearHistoryStatus = Strings.Get("Msg_HistoryCleared");
        }
        catch (Exception ex)
        {
            ClearHistoryStatus = Strings.Format("Msg_ClearFailed", ex.Message);
        }
    }

    /// <summary>配置导出为 JSON 文本(常规页“导出”)。</summary>
    public string BuildExportJson() => JsonSerializer.Serialize(_loaded, exportJsonOption);

    /// <summary>从导出的 JSON 导入配置(常规页“导入”);格式非法时返回 false。</summary>
    public bool TryApplyImportedJson(string json)
    {
        try
        {
            AppSettings? imported = JsonSerializer.Deserialize<AppSettings>(json, jsonOption);
            if (imported is null)
            {
                return false;
            }
            imported.Normalize();
            _loaded = imported;
            ApplyToViewModel(imported);
            PreviewCurrent();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task LoadAsync()
    {
        _loaded = await _settingsService.GetSettingsAsync();

        // 外观即时预览的基线:未保存关闭时回滚到这份快照。
        _baseline = JsonClone(_loaded);
        _saved = false;
        _previewed = false;
        // 已信任主机的地址脱敏每次打开设置都回到默认隐藏(不持久化,防截图泄露)。
        MaskKnownHostAddresses = true;

        // 关于页贡献者头像:后台拉取,失败保留首字母占位,不阻塞设置载入。
        foreach (ContributorViewModel contributor in Contributors)
        {
            _ = contributor.LoadAvatarAsync();
        }

        if (Sync is not null)
        {
            await Sync.LoadAsync();
        }

        // 必须先填充密钥名列表再回填设置:“默认认证密钥”下拉是
        // SelectedItem TwoWay 绑定,ItemsSource 为空时回填值匹配不到会被
        // ComboBox 强制清成 null 并写回模型,已保存的选择就此丢失。
        await SshKeys.RefreshAsync();
        ApplyToViewModel(_loaded);
        this.RaisePropertyChanged(nameof(Keys)); // 列表就位后重新评估选中项
        await RefreshKnownHostsAsync();
        if (Snippets is not null)
        {
            await Snippets.LoadAsync();
        }
    }

    private async Task RefreshKnownHostsAsync()
    {
        if (_hostKeyService is null)
        {
            return;
        }
        try
        {
            List<KnownHost> hosts = await _hostKeyService.GetKnownHostsAsync();
            KnownHosts.Clear();
            // 先置可见再入列:IsVisible=false 期间灌进 ItemsControl 的行会占住布局高度却
            // 画不出来。列表限高滚动后这条尤其致命——表现为“有滚动条但内容一片空白”。
            HasKnownHosts = hosts.Count > 0;
            foreach (
                KnownHost host in hosts
                    .OrderBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(h => h.Port)
            )
            {
                KnownHosts.Add(host);
            }
        }
        catch
        {
            // 列表加载失败不阻塞设置页其余功能。
        }
        HasKnownHosts = KnownHosts.Count > 0;
    }

    private async Task RemoveKnownHostAsync(KnownHost host)
    {
        if (_hostKeyService is null)
        {
            return;
        }
        try
        {
            await _hostKeyService.RemoveKnownHostAsync(host.Host, host.Port);
            KnownHosts.Remove(host);
        }
        catch
        {
            // 删除失败保持列表现状。
        }
        HasKnownHosts = KnownHosts.Count > 0;
    }

    private void ApplyToViewModel(AppSettings settings)
    {
        // 批量回填不逐项触发预览;结束后恢复(调用方需要预览时显式 PreviewCurrent)。
        _suppressPreview = true;
        Language = settings.Language;
        Theme = settings.Theme;
        AccentColor = settings.AccentColor;
        TerminalFont = settings.TerminalFont;
        TerminalFontSize = settings.TerminalFontSize;
        ScrollbackLines = settings.ScrollbackLines;
        DefaultPort = settings.DefaultPort;
        TerminalType = settings.TerminalType;
        TerminalEncoding = settings.TerminalEncoding;
        General = settings.General;
        Appearance = settings.Appearance;
        TerminalBehavior = settings.TerminalBehavior;
        Transfer = settings.Transfer;
        Security = settings.Security;
        Keys = settings.Keys;
        Proxy = settings.Proxy;
        RefreshTrustedLaunchTargets();

        // 配色方案下拉:重算“(默认)”标注与选中项(出厂值折射到当前主题默认方案;
        // 显式方案反向匹配;改过单色显示“未选择”)。
        RefreshColorSchemeDisplay();

        // 分组对象整体替换后,派生的下拉索引一并刷新。
        this.RaisePropertyChanged(nameof(ThemeIndex));
        this.RaisePropertyChanged(nameof(LanguageIndex));
        this.RaisePropertyChanged(nameof(UpdateChannelIndex));
        this.RaisePropertyChanged(nameof(CursorStyleIndex));
        this.RaisePropertyChanged(nameof(BellModeIndex));
        this.RaisePropertyChanged(nameof(ConflictPolicyIndex));
        this.RaisePropertyChanged(nameof(TabBarPositionIndex));
        this.RaisePropertyChanged(nameof(SidebarPositionIndex));
        this.RaisePropertyChanged(nameof(WindowStateIndex));
        this.RaisePropertyChanged(nameof(ProxyTypeIndex));
        this.RaisePropertyChanged(nameof(IsProxyEditable));
        _suppressPreview = false;
    }

    /// <summary>恢复默认(常规页按钮):所有设置回到出厂值,外观即时预览,保存后落盘。</summary>
    private void ResetToDefaults()
    {
        _loaded = new();
        ApplyToViewModel(_loaded);
        PreviewCurrent();
    }

    // ———— 外观即时预览实现 ————

    /// <summary>主题/强调色即时生效(IThemeService 本就是应用即生效,保存前调用即预览)。</summary>
    private void PreviewThemeLive()
    {
        if (_suppressPreview)
        {
            return;
        }
        _previewed = true;
        _themeService.SetTheme(Theme);
        try
        {
            _themeService.SetAccent(AccentColor);
        }
        catch (ArgumentException)
        {
            /* 非法色值:保持现状 */
        }
    }

    private ProxyOptions? _hookedProxy;

    /// <summary>Proxy.Type 被直接改动时,同步派生的下拉索引与「参数可编辑」标志。</summary>
    private void OnProxyItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProxyOptions.Type))
        {
            this.RaisePropertyChanged(nameof(ProxyTypeIndex));
            this.RaisePropertyChanged(nameof(IsProxyEditable));
        }
    }

    /// <summary>跟踪当前 Appearance 对象的单项修改(POCO 直绑,靠其 INPC 感知)。</summary>
    private void HookAppearance(AppearanceOptions? appearance)
    {
        _hookedAppearance?.PropertyChanged -= OnAppearanceItemChanged;
        _hookedAppearance = appearance;
        appearance?.PropertyChanged += OnAppearanceItemChanged;
    }

    private Avalonia.Threading.DispatcherTimer? _previewDebounce;

    private void OnAppearanceItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppearanceOptions.WindowOpacityPercent))
        {
            if (_suppressPreview || _previewService is null)
            {
                return;
            }
            _previewed = true;
            _previewService.PreviewWindowOpacity(Appearance.WindowOpacityPercent);
            return;
        }
        // 背景图/终端背景不透明度走即时通道:与窗口不透明度同理,绕过防抖 JSON 快照,拖动线性平滑,
        // 且不触发背景图重新解码(否则每帧读盘,滑杆卡顿、数值跳变)。
        if (e.PropertyName is nameof(AppearanceOptions.BackgroundImageOpacity)
            or nameof(AppearanceOptions.ContentBackgroundOpacity))
        {
            if (_suppressPreview || _previewService is null)
            {
                return;
            }
            _previewed = true;
            _previewService.PreviewBackgroundOpacity(
                Appearance.BackgroundImageOpacity, Appearance.ContentBackgroundOpacity);
            return;
        }
        SchedulePreviewBroadcast();
    }

    /// <summary>
    /// 合并外观单项修改的预览广播到 50ms 尾沿:拖动滑杆时 INPC 每次微调都来一发,
    /// 而每次广播要做两次全量 JSON 克隆(<see cref="BroadcastPreview" />),
    /// 不合并会在 UI 线程形成序列化风暴,拖动明显掉帧。
    /// </summary>
    private void SchedulePreviewBroadcast()
    {
        if (_suppressPreview || _previewService is null)
        {
            return;
        }
        if (_previewDebounce is null)
        {
            _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(50) };
            _previewDebounce.Tick += (_, _) =>
            {
                _previewDebounce!.Stop();
                BroadcastPreview();
            };
        }
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>
    /// 广播外观预览快照:以基线为底、仅叠加外观相关字段,
    /// 避免把其他设置页未保存的改动一并预览出去。
    /// </summary>
    private void BroadcastPreview()
    {
        if (_suppressPreview || _previewService is null)
        {
            return;
        }
        _previewed = true;
        AppSettings snapshot = JsonClone(_baseline);
        snapshot.Theme = Theme;
        snapshot.AccentColor = AccentColor;
        snapshot.Appearance = JsonClone(Appearance);
        _previewService.Preview(snapshot);
    }

    private void PreviewCurrent()
    {
        PreviewThemeLive();
        BroadcastPreview();
    }

    /// <summary>窗口以任意方式关闭时由视图调用:未保存而预览过 → 回滚到打开时的基线。</summary>
    public void NotifyClosed()
    {
        _previewDebounce?.Stop();
        if (_saved || !_previewed)
        {
            return;
        }
        _previewed = false;
        _themeService.SetTheme(_baseline.Theme);
        try
        {
            _themeService.SetAccent(_baseline.AccentColor);
        }
        catch (ArgumentException) { }
        _previewService?.Preview(_baseline);
    }

    private static T JsonClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private async Task SaveAsync()
    {
        // 分组选项对象被绑定直接修改;顶层字段从 VM 回写。
        _loaded.Language = Language;
        _loaded.Theme = Theme;
        _loaded.AccentColor = AccentColor;
        _loaded.TerminalFont = TerminalFont;
        _loaded.TerminalFontSize = TerminalFontSize;
        _loaded.ScrollbackLines = ScrollbackLines;
        _loaded.DefaultPort = DefaultPort;
        _loaded.TerminalType = TerminalType;
        _loaded.TerminalEncoding = TerminalEncoding;
        _loaded.General = General;
        _loaded.Appearance = Appearance;
        _loaded.TerminalBehavior = TerminalBehavior;
        _loaded.Transfer = Transfer;
        _loaded.Security = Security;
        _loaded.Keys = Keys;
        _loaded.Proxy = Proxy;
        await _settingsService.SaveSettingsAsync(_loaded);

        // 即时生效 —— 主题、强调色与语言均无需重启即可应用(#2/#3/#4)。
        _themeService.SetTheme(Theme);
        try
        {
            _themeService.SetAccent(AccentColor);
        }
        catch (ArgumentException)
        {
            /* 非法十六进制:保留原先值 */
        }
        _localizationService?.SetLanguage(Language);

        // 已保存:外观预览转正,窗口关闭时不再回滚;基线同步到已保存状态。
        _saved = true;
        _baseline = JsonClone(_loaded);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

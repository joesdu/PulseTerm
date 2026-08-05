using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Common.Input;
using VelaShell.Common.Localization;
using VelaShell.Controls.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Features.Sessions;
using VelaShell.Features.Settings;
using VelaShell.Features.Sftp;
using VelaShell.Features.Update;
using VelaShell.Infrastructure.DependencyInjection;
using VelaShell.Presentation.DependencyInjection;
using VelaShell.Presentation.ViewModels;
using VelaShell.Shell;

namespace VelaShell;

/// <summary>
/// 应用入口:构建 DI 容器、接线本地化/主题/强调色的热更新,并在框架初始化完成后
/// 创建主窗口、恢复启动窗口状态、挂载托盘与云同步,退出时释放服务。
/// </summary>
public class App : Application
{
    private ServiceProvider? _serviceProvider;
    private AppSettings? _startupSettings;
    private readonly SyncDebounceLifecycle _syncDebounce = new();
    private IThemeService? _themeService;
    private TrayIconService? _trayIconService;

    /// <summary>当前应用的 DI 服务容器;在 <see cref="Initialize" /> 完成前为 <c>null</c>。</summary>
    public IServiceProvider? Services => _serviceProvider;

    /// <summary>托盘图标当前是否挂载(主窗口据此决定“关闭时最小化到托盘”是否可用)。</summary>
    public bool TrayIconActive => _trayIconService?.IsActive == true;

    /// <summary>加载 XAML、构建 DI 容器,并接线主题/本地化等应用级服务。</summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _serviceProvider = new ServiceCollection()
            .AddVelaShellPresentation()
            .AddVelaShellControls()
            .AddVelaShellInfrastructure()
            .AddSingleton<IThemeService>(_ => new ThemeService("system"))
            .AddSingleton<ISettingsPreviewService, SettingsPreviewService>()
            .AddSingleton<IHostKeyPrompt, HostKeyPromptDialogService>()
            .AddSingleton<ILocalizationService, LocalizationService>()
            .AddSingleton<IKeyboardShortcutService, KeyboardShortcutService>()
            // 应用内自动更新:更新源 = 本仓库 GitHub Releases 的 latest.json 清单(无需自建服务器),
            // 便携式原地换版,不限定安装位置。通道跟随设置页的 stable/preview 开关;
            // beta 阶段(尚无正式版)stable 通道自动放宽到最新预发布。
            .AddSingleton<IUpdateService>(sp => new UpdateService(
                "https://github.com/joesdu/VelaShell",
                channelProvider: async () =>
                    (await sp.GetRequiredService<ISettingsService>().GetSettingsAsync()).General.UpdateChannel
            ))
            .AddSingleton(sp => new WindowLayoutStore(sp.GetService<IAppDataStore>()))
            .AddSingleton<QuickCommandsViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .BuildServiceProvider();
        _themeService = _serviceProvider.GetRequiredService<IThemeService>();

        // 本地化字符串的实时重绑定({loc:Localize})跟随 DI 服务(#4)。
        ILocalizationService localization =
            _serviceProvider.GetRequiredService<ILocalizationService>();
        LocalizedStrings.Instance.Attach(localization);

        // UI 线程的线程级文化在 Dispatcher 顶层回调里补设:异步命令(设置保存)里
        // 设置的文化随 ExecutionContext 回卷丢失,而 UI 线程启动时已显式设置过文化,
        // DefaultThreadCurrentUICulture 对它无效。这里保证 C# 侧 Strings.Get 与
        // 日期/数字格式化在换语言后于 UI 线程取到新文化(绑定取词本身不依赖它,
        // LocalizationService 自持文化)。
        localization.LanguageChanged += lang =>
            Dispatcher.UIThread.Post(() =>
            {
                var culture = new System.Globalization.CultureInfo(lang);
                System.Globalization.CultureInfo.CurrentUICulture = culture;
                System.Globalization.CultureInfo.CurrentCulture = culture;
            });
        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.AccentChanged += ApplyAccent;
        ApplyThemeVariant(_themeService.CurrentTheme);
        ApplyAccent(_themeService.AccentColor);
    }

    /// <summary>框架初始化完成后应用已持久化的偏好并创建主窗口。</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        ApplyPersistedPreferences();
        QuickCommandLoadResult? quickCommandLoad = null;
        if (_serviceProvider?.GetService<IQuickCommandRepository>() is { } quickCommandRepository)
        {
            // 快捷命令迁移必须先于 UI 加载和启动自动同步,避免旧本地/远端结构竞态。
            quickCommandLoad = quickCommandRepository.LoadAsync().GetAwaiter().GetResult();
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel =
                _serviceProvider?.GetRequiredService<MainWindowViewModel>()
                ?? new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            // 启动时窗口状态(设置 → 外观):记住上次 / 最大化 / 默认大小。
            ApplyStartupWindowState(mainWindow, _startupSettings);

            // 开机自启动与设置保持同步(用户可能在外部改过注册表)。
            StartupRegistration.Apply(_startupSettings?.General.LaunchAtStartup == true);

            // 过期会话/传输日志清理(设置 → 常规/文件传输 → 日志保留天数),后台执行。
            // 真的放到后台:目录枚举+删除是磁盘 IO,日志多时同步跑会拖慢首帧。
            int logRetentionDays = _startupSettings?.General.LogRetentionDays ?? 30;
            string? transferLogDirectory = _startupSettings?.Transfer.LogDirectory;
            int transferLogRetentionDays = _startupSettings?.Transfer.TransferLogRetentionDays ?? 30;
            _ = Task.Run(() =>
            {
                SessionLogService.CleanupExpired(logRetentionDays);
                TransferLogService.CleanupExpired(transferLogDirectory, transferLogRetentionDays);
            });

            // 过期会话录制清理(随终端会话日志的保留天数)。
            if (_serviceProvider?.GetService<ISessionRecordingStore>() is { } recordingStore)
            {
                int retentionDays = _startupSettings?.General.LogRetentionDays ?? 30;
                _ = Task.Run(() => recordingStore.CleanupExpiredAsync(retentionDays));
            }

            // 托盘图标(关闭时最小化到托盘);设置保存后热更新挂载状态。
            _trayIconService = new(this);
            _trayIconService.ShowRequested += () =>
            {
                mainWindow.Show();
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }
                mainWindow.Activate();
            };
            _trayIconService.ExitRequested += mainWindow.ForceClose;
            _trayIconService.SetEnabled(_startupSettings?.General.MinimizeToTray == true);
            if (_serviceProvider?.GetService<ISettingsService>() is { } settingsService)
            {
                settingsService.SettingsSaved += settings =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        StartupRegistration.Apply(settings.General.LaunchAtStartup);
                        _trayIconService?.SetEnabled(settings.General.MinimizeToTray);
                    });
            }

            // 云同步(设置 → 云同步):启动后台拉取一次;设置保存后标记本地改动并防抖推送。
            // 迁移标记并入 WireAutoSync 的后台链执行("先标记、后同步"的顺序不变),
            // 不再用 GetResult() 在 UI 线程上等一次磁盘写。
            if (_serviceProvider?.GetService<IGistSyncService>() is { } syncService)
            {
                WireAutoSync(
                    syncService,
                    _serviceProvider.GetService<ISettingsService>(),
                    _serviceProvider.GetService<IQuickCommandRepository>(),
                    markLocalChangedFirst: quickCommandLoad?.Migrated == true
                );
            }

            // 退出时释放容器,确保 SonnetDB 引擎正常关闭(WAL/段刷盘);
            // 并清理「默认编辑器打开」遗留的 remote-edit 临时文件。
            desktop.Exit += (_, _) =>
            {
                _trayIconService?.Dispose();
                ExternalEditSessionManager.CleanupAll();
                DisposeServicesOnExit();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 自动同步接线(设置 → 云同步,开启“自动同步”时):
    /// 启动后台执行一次智能同步(通常表现为拉取);设置保存后标记本地改动,
    /// 防抖 5 秒再推送(应用远端数据触发的保存由服务内部的 IsApplyingRemote 过滤)。
    /// 全部静默执行,失败不打扰用户 —— 下次手动同步时会看到具体错误。
    /// </summary>
    private void WireAutoSync(
        IGistSyncService syncService,
        ISettingsService? settingsService,
        IQuickCommandRepository? quickCommandRepository,
        bool markLocalChangedFirst = false
    )
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (markLocalChangedFirst)
                {
                    // 快捷命令迁移改动了本地数据:必须先落"本地已变"标记,再做启动同步,
                    // 否则拉取可能把刚迁移的结构覆盖回旧远端。
                    await syncService.MarkLocalChangedAsync();
                }
                SyncSettings config = await syncService.GetSyncSettingsAsync();
                if (config is { Enabled: true, AutoSync: true })
                {
                    await syncService.SyncNowAsync();
                }
            }
            catch
            {
                // 启动同步失败静默;设置页手动同步会给出错误详情。
            }
        });
        settingsService?.SettingsSaved += _ => QueueAutoSyncUnlessApplyingRemote(syncService);
        quickCommandRepository?.Changed += (_, _) =>
                QueueAutoSyncUnlessApplyingRemote(syncService);
    }

    private void QueueAutoSyncUnlessApplyingRemote(IGistSyncService syncService)
    {
        if (!syncService.IsApplyingRemote)
        {
            QueueAutoSync(syncService);
        }
    }

    private void QueueAutoSync(IGistSyncService syncService)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await syncService.MarkLocalChangedAsync();
                SyncSettings config = await syncService.GetSyncSettingsAsync();
                if (config is not { Enabled: true, AutoSync: true })
                {
                    return;
                }

                // 防抖:连续保存只推送最后一次。
                if (!_syncDebounce.TrySwapNew(out CancellationToken token))
                {
                    return; // 已关闭;不要再启动新的防抖任务。
                }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                if (!_syncDebounce.TryStartCurrent(() => syncService.SyncNowAsync(CancellationToken.None), token, out Task? syncTask))
                {
                    return; // 已在延迟期间关闭或被取代。
                }
                await syncTask!;
            }
            catch (OperationCanceledException)
            {
                // 被更晚的保存取代,正常。
            }
            catch
            {
                // 自动推送失败静默,手动同步可见错误。
            }
        });
    }

    /// <summary>
    /// 关闭时释放 DI 容器。拆除过程会断开所有仍在线的 SSH/SFTP 会话 —— 每次 <c>Disconnect()</c>
    /// 都是一次阻塞的网络往返 —— 并冲刷 SonnetDB 引擎。旧代码在 UI 线程上通过 <c>Dispose()</c>
    /// 同步执行这一步,因此一个缓慢或无响应的连接会让进程在窗口关闭后仍然存活很久。
    /// 现改为带短超时的异步释放(这也是 IAsyncDisposable 服务的正确处置路径),
    /// 使应用能及时退出;进程拆除时任何仍在关闭中的套接字由操作系统回收。
    /// </summary>
    private void DisposeServicesOnExit()
    {
        _syncDebounce.Shutdown();
        ServiceProvider? provider = _serviceProvider;
        _serviceProvider = null;
        if (provider is null)
        {
            return;
        }
        try
        {
            provider.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 尽力关闭:绝不阻塞或中断退出路径。
        }
    }

    /// <summary>
    /// 在第一个窗口显示之前应用已持久化的语言 / 主题 / 强调色,
    /// 使应用以用户选定的外观启动,而不出现可见的重新换肤闪烁。
    /// </summary>
    private void ApplyPersistedPreferences()
    {
        if (_serviceProvider is null)
        {
            return;
        }
        try
        {
            AppSettings settings = _serviceProvider
                .GetRequiredService<ISettingsService>()
                .GetSettingsAsync()
                .GetAwaiter()
                .GetResult();
            _startupSettings = settings;
            _serviceProvider
                .GetRequiredService<ILocalizationService>()
                .SetLanguage(settings.Language);
            if (!string.IsNullOrWhiteSpace(settings.Theme))
            {
                _themeService?.SetTheme(settings.Theme);
            }
            if (!string.IsNullOrWhiteSpace(settings.AccentColor))
            {
                _themeService?.SetAccent(settings.AccentColor);
            }
        }
        catch
        {
            // 损坏的设置绝不能阻断启动;将应用默认值。
        }
    }

    /// <summary>启动时窗口状态(设置 → 外观 → 启动时窗口状态),在窗口显示前应用以免闪动。</summary>
    private static void ApplyStartupWindowState(MainWindow window, AppSettings? settings)
    {
        if (settings is null)
        {
            return;
        }
        switch (settings.Appearance.StartupWindowState)
        {
            case "maximized":
                window.WindowState = WindowState.Maximized;
                break;
            case "default":
                break;
            default: // 记住上次窗口状态
                AppearanceOptions a = settings.Appearance;
                if (a is { LastWindowWidth: >= 800, LastWindowHeight: >= 500 })
                {
                    window.Width = a.LastWindowWidth;
                    window.Height = a.LastWindowHeight;
                }
                if (a.LastWindowMaximized)
                {
                    window.WindowState = WindowState.Maximized;
                }
                break;
        }
    }

    private void OnThemeChanged(string themeName)
    {
        ApplyThemeVariant(themeName);
    }

    private void ApplyThemeVariant(string themeName)
    {
        RequestedThemeVariant = themeName.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }

    /// <summary>
    /// 通过在应用层级遮蔽主题强调色画刷,实时应用强调色覆盖;
    /// 每个 <c>DynamicResource VelaAccent</c> 无需重启即更新(#3)。
    /// null/空值会移除覆盖,恢复主题默认的强调色。
    /// </summary>
    private void ApplyAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            Resources.Remove("VelaAccent");
            Resources.Remove("VelaAccentDim");
            Resources.Remove("VelaAccentForeground");
            return;
        }
        if (!Color.TryParse(hex, out Color color))
        {
            return;
        }
        Resources["VelaAccent"] = new SolidColorBrush(color);
        // 暗色变体:相同色相、约 19% 不透明度,对应设计稿中的 #RRGGBB30 令牌。
        Resources["VelaAccentDim"] = new SolidColorBrush(
            new Color(0x30, color.R, color.G, color.B)
        );

        // 自定义强调色的配对前景按亮度自动选:亮底深字、深底浅字,
        // 避免用户挑深色 accent 后按钮文字(令牌随主题固定)对比不足。
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        Resources["VelaAccentForeground"] = new SolidColorBrush(
            luminance > 0.55 ? Color.Parse("#0A0E14") : Color.Parse("#FFFBEB")
        );
    }
}

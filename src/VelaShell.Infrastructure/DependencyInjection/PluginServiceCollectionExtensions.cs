using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Localization;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.DependencyInjection;

/// <summary>插件运行时的 DI 装配。</summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// 注册进程内插件运行时(<see cref="PluginManager" />)。发现根目录为
    /// 应用目录与 <c>~/.velashell/plugins</c>;命令能力经 UI 层注册的
    /// <c>Func&lt;string, IPluginLogger, ICommandsApi&gt;</c> 桥接(缺席时命令注册退化为空操作)。
    /// 注册本身零开销:PluginManager 直到 <see cref="PluginManager.StartAsync" /> 才做任何 I/O。
    /// </summary>
    public static IServiceCollection AddVelaShellPlugins(this IServiceCollection services, string hostVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 插件数据后端:SonnetDB 单集合、按插件 id 命名空间隔离(KV + 机密),卸载整体清除。
        services.AddSingleton<IPluginDataStore>(sp => new SonnetDbPluginDataStore(
            sp.GetRequiredService<SonnetDbEngine>(),
            sp.GetService<Core.Data.ISecretProtector>()));
        services.AddSingleton(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new PluginTrustRepository(
                sp.GetRequiredService<SonnetDbEngine>(),
                sp.GetRequiredService<Core.Data.ISecretProtector>(),
                Path.Combine(paths.RootDirectory, "trusted-plugin-publishers.json"));
        });
        services.AddSingleton<PluginManager>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            var options = new PluginManagerOptions
            {
                PluginRoots =
                [
                    Path.Combine(AppContext.BaseDirectory, "plugins"),
                    paths.UserPluginDirectory
                ],
                // 开发期挂载:启动参数 --dev-root、环境变量 VELA_PLUGIN_DEV_ROOT,
                // 或 <数据根>/plugins.dev.txt 里登记的插件工程输出目录。
                // 默认三处都空 → 这里得到空表,发现期一个额外目录都不扫。
                DevPluginRoots = DevPluginRootResolver.Resolve(
                    paths.RootDirectory, Startup.VelaShellStartupArguments.Current.DevPluginRoots),
                DebugPluginIds = DevPluginRootResolver.ResolveDebugPluginIds(
                    Startup.VelaShellStartupArguments.Current.DebugPluginIds),
                // 开发期插件从影子副本装载:工程的 bin 因此不被运行中的宿主锁住,
                // 可以边跑边重编,改完在管理页点"重新加载"即可(Windows 上尤其关键)。
                DevShadowRootDirectory = paths.DevPluginShadowDirectory,
                DevDisabledStateFile = paths.DevPluginDisabledFile,
                DevAutoReload = Startup.VelaShellStartupArguments.Current.DevWatch,
                DiagnosticsDirectory = paths.LogsDirectory,
                DataRootDirectory = Path.Combine(paths.RootDirectory, "plugin-data"),
                UserPluginRoot = paths.UserPluginDirectory,
                TrustRepository = sp.GetRequiredService<PluginTrustRepository>(),
                HostVersion = hostVersion,
                Connections = sp.GetService<ISshConnectionService>(),
                // 已保存配置 + 开会话的实现:插件"自己按保存的配置连一台"这条路
                // (蓝图 §Sessions)所需的两块。后者由 UI 层注册,headless 宿主没有,
                // 于是开会话一律拒绝。
                SessionProfiles = sp.GetService<Core.Data.ISessionRepository>(),
                SessionOpener = sp.GetService<IPluginSessionOpener>(),
                Sftp = sp.GetService<ISftpService>(),
                Theme = sp.GetService<IThemeService>(),
                Localization = sp.GetService<ILocalizationService>(),
                CommandsFactory = sp.GetService<Func<string, IPluginLogger, ICommandsApi>>(),
                UiFactory = sp.GetService<Func<string, IPluginLogger, IUiApi>>(),
                ThemeTokensProvider = sp.GetService<Func<Task<IReadOnlyList<PluginSdk.Rpc.ThemeTokenDto>>>>(),
                // "跟随系统"时把系统明暗问出来:插件的 IHostThemeApi.Current 要报一套
                // **已解析**的主题,不能把 "system" 原样丢给插件(那正是老契约的毛病)。
                SystemPrefersDark = sp.GetService<SystemDarkModeProbe>(),
                DataStore = sp.GetService<IPluginDataStore>(),
                SecretProtector = sp.GetService<Core.Data.ISecretProtector>(),
                Clipboard = sp.GetService<PluginSdk.Clipboard.IClipboardApi>(),
                EmbedHost = sp.GetService<Plugins.Isolated.IPluginEmbedHost>(),
                TerminalFactory = sp.GetService<Func<string, IPluginLogger, PluginSdk.Terminal.ITerminalApi>>(),
                TerminalView = sp.GetService<PluginSdk.TerminalView.ITerminalViewApi>(),
                // 协议注册表:清单声明的协议页签在发现期登记于此,插件激活后补上实现。
                ProtocolRegistry = sp.GetService<Plugins.Protocols.PluginProtocolRegistry>(),
                // 后台活动账本:插件的校验/激活/预读都在状态栏右下角的圆环上有交代。
                Activity = sp.GetService<IBackgroundActivityService>(),
                // 冷启动预读的排障急停开关(与 VELASHELL_DISABLE_PLUGINS 同一体例)。
                PrewarmLazyPlugins = Environment.GetEnvironmentVariable("VELASHELL_DISABLE_PLUGIN_PREWARM") != "1"
            };
            return new(options);
        });
        return services;
    }
}

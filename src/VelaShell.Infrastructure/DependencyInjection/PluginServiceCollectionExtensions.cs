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
        services.AddSingleton<Plugins.IPluginDataStore>(sp => new Persistence.SonnetDbPluginDataStore(
            sp.GetRequiredService<Persistence.SonnetDbEngine>(),
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
                Sftp = sp.GetService<ISftpService>(),
                Theme = sp.GetService<IThemeService>(),
                Localization = sp.GetService<ILocalizationService>(),
                CommandsFactory = sp.GetService<Func<string, IPluginLogger, ICommandsApi>>(),
                UiFactory = sp.GetService<Func<string, IPluginLogger, IUiApi>>(),
                ThemeTokensProvider = sp.GetService<Func<Task<IReadOnlyList<PluginSdk.Rpc.ThemeTokenDto>>>>(),
                DataStore = sp.GetService<Plugins.IPluginDataStore>(),
                SecretProtector = sp.GetService<Core.Data.ISecretProtector>(),
                Clipboard = sp.GetService<PluginSdk.Clipboard.IClipboardApi>(),
                EmbedHost = sp.GetService<Plugins.Isolated.IPluginEmbedHost>(),
                TerminalFactory = sp.GetService<Func<string, IPluginLogger, PluginSdk.Terminal.ITerminalApi>>(),
                TerminalView = sp.GetService<PluginSdk.TerminalView.ITerminalViewApi>(),
                // 协议注册表:清单声明的协议页签在发现期登记于此,插件激活后补上实现。
                ProtocolRegistry = sp.GetService<Plugins.Protocols.PluginProtocolRegistry>()
            };
            return new(options);
        });
        return services;
    }
}

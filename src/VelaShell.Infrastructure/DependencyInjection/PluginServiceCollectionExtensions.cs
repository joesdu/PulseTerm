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
    /// 应用目录与用户数据目录下的 <c>plugins/</c>;命令能力经 UI 层注册的
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
        services.AddSingleton<PluginManager>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            var options = new PluginManagerOptions
            {
                PluginRoots =
                [
                    Path.Combine(AppContext.BaseDirectory, "plugins"),
                    Path.Combine(paths.RootDirectory, "plugins")
                ],
                DataRootDirectory = Path.Combine(paths.RootDirectory, "plugin-data"),
                UserPluginRoot = Path.Combine(paths.RootDirectory, "plugins"),
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
                TerminalFactory = sp.GetService<Func<string, IPluginLogger, PluginSdk.Terminal.ITerminalApi>>()
            };
            return new(options);
        });
        return services;
    }
}

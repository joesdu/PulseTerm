using Microsoft.Extensions.DependencyInjection;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Plugins;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.DependencyInjection;

/// <summary>表现层(视图模型与工作流服务)的依赖注入注册扩展。</summary>
public static class PresentationServiceCollectionExtensions
{
    /// <summary>向容器注册 VelaShell 表现层所需的视图模型与工作流服务。</summary>
    /// <param name="services">要注册服务的服务集合。</param>
    /// <returns>返回同一服务集合以支持链式调用。</returns>
    public static IServiceCollection AddVelaShellPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<IConnectionWorkflowService, ConnectionWorkflowService>();
        services.AddSingleton<IConnectionDiagnosticsService, ConnectionDiagnosticsService>();
        services.AddSingleton<ITunnelWorkflowService, TunnelWorkflowService>();
        // 命令注册表提为容器单例:主窗口视图模型与插件命令桥共享同一实例,
        // 插件命令才能出现在命令面板里。
        services.AddSingleton<ICommandRegistry, CommandRegistry>();
        // 插件运行时(Infrastructure)对 UI 层无依赖,经此工厂拿到每插件的命令能力。
        services.AddSingleton<Func<string, IPluginLogger, ICommandsApi>>(sp =>
            (pluginId, log) => new PluginCommandsApi(pluginId, sp.GetRequiredService<ICommandRegistry>(), log));
        return services;
    }
}

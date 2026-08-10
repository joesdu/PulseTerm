using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 命令能力不可用时(无 UI 宿主、测试环境)的空实现:注册被接受但不生效,
/// 只记一条警告 —— 插件不必为 headless 场景写分支。
/// </summary>
internal sealed class NullCommandsApi(IPluginLogger log) : ICommandsApi
{
    private sealed class NoopRegistration : IDisposable
    {
        public void Dispose() { }
    }

    public IDisposable Register(PluginCommandDescriptor command)
    {
        log.Warn($"Command '{command.Id}' not registered: command capability is unavailable in this host.");
        return new NoopRegistration();
    }

    public bool TryExecute(string commandId) => false;
}

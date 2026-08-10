using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;
using VelaShell.Presentation.Commands;

namespace VelaShell.Presentation.Plugins;

/// <summary>
/// 插件命令能力(<see cref="ICommandsApi" />)对宿主命令注册表的桥接:
/// 强制 <c>&lt;pluginId&gt;.</c> 前缀(防插件间冒名),命令体在后台线程执行且
/// 异常只记入插件日志;实例释放(插件停用)时注销本插件的全部命令。
/// </summary>
public sealed class PluginCommandsApi(string pluginId, ICommandRegistry registry, IPluginLogger log)
    : ICommandsApi, IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <inheritdoc />
    public IDisposable Register(PluginCommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.Id.StartsWith(pluginId + ".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Command id '{command.Id}' must start with '{pluginId}.' (plugin command ids are namespaced by plugin id).",
                nameof(command));
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            registry.Register(new(
                command.Id,
                command.Title,
                command.Category,
                Execute: () => RunGuarded(command)));
            _registered.Add(command.Id);
        }
        return new Registration(this, command.Id);
    }

    /// <inheritdoc />
    public bool TryExecute(string commandId) => registry.Execute(commandId);

    /// <summary>
    /// 命令体转投线程池:菜单/命令面板在 UI 线程触发,插件代码一律不上 UI 线程 ——
    /// 慢命令不冻结界面,异常不穿透宿主。
    /// </summary>
    private void RunGuarded(PluginCommandDescriptor command)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await command.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error($"Command '{command.Id}' threw.", ex);
            }
        });
    }

    private void Unregister(string commandId)
    {
        lock (_gate)
        {
            if (_registered.Remove(commandId))
            {
                registry.Unregister(commandId);
            }
        }
    }

    /// <summary>注销本插件注册的全部命令。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (string id in _registered)
            {
                registry.Unregister(id);
            }
            _registered.Clear();
        }
    }

    private sealed class Registration(PluginCommandsApi owner, string commandId) : IDisposable
    {
        public void Dispose() => owner.Unregister(commandId);
    }
}

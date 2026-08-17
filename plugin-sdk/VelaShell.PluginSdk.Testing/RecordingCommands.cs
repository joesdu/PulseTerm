using VelaShell.PluginSdk.Commands;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="ICommandsApi" /> 的记录实现:注册进内存列表,测试可直接调用命令体。
/// </summary>
public sealed class RecordingCommands : ICommandsApi
{
    private readonly Dictionary<string, PluginCommandDescriptor> _commands = [with(StringComparer.Ordinal)];

    /// <summary>当前已注册命令的快照。</summary>
    public IReadOnlyList<PluginCommandDescriptor> Registered => [.. _commands.Values];

    /// <summary>宿主侧命令的模拟表:<see cref="TryExecute" /> 对这些 id 返回 true。</summary>
    public HashSet<string> HostCommands { get; } = [with(StringComparer.Ordinal)];

    /// <summary><see cref="TryExecute" /> 的调用记录。</summary>
    public List<string> ExecutedIds { get; } = [];

    /// <inheritdoc />
    public IDisposable Register(PluginCommandDescriptor command)
    {
        _commands[command.Id] = command;
        return new Registration(this, command.Id);
    }

    /// <inheritdoc />
    public bool TryExecute(string commandId)
    {
        ExecutedIds.Add(commandId);
        return _commands.ContainsKey(commandId) || HostCommands.Contains(commandId);
    }

    /// <summary>直接运行一条已注册命令的命令体(测试驱动用)。</summary>
    public Task RunAsync(string commandId, CancellationToken cancellationToken = default)
        => _commands.TryGetValue(commandId, out PluginCommandDescriptor? command)
            ? command.ExecuteAsync(cancellationToken)
            : throw new KeyNotFoundException($"Command '{commandId}' is not registered.");

    private sealed class Registration(RecordingCommands owner, string id) : IDisposable
    {
        public void Dispose() => owner._commands.Remove(id);
    }
}

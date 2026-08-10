namespace VelaShell.Presentation.Commands;

/// <summary>
/// 一条可执行的 app 命令。注册表是菜单栏、命令面板与键盘快捷键共享的单一来源
/// (设计稿 §4A.1),因此每个入口展示的名称、提示与行为都一致。
/// </summary>
/// <param name="Id">用于注册、查找与调用命令的稳定唯一标识。</param>
/// <param name="Title">面向用户、已本地化的可读显示名称。</param>
/// <param name="Category">分组标签(例如用于命令面板或菜单分区)。</param>
/// <param name="Execute">命令被调用时执行的操作。</param>
/// <param name="CanExecute">可选可用性谓词;<see langword="null" /> 表示始终可用。</param>
/// <param name="Shortcut">可选的键盘快捷键提示/绑定。</param>
/// <param name="Icon">显示在命令旁的图标标识。</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    Action Execute,
    Func<bool>? CanExecute = null,
    string? Shortcut = null,
    string? Icon = null)
{
    /// <summary>命令当前是否启用(依据 <see cref="CanExecute" />)。</summary>
    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}

/// <summary>
/// 支撑菜单栏、命令面板与快捷键的 app 命令共享注册表。
/// </summary>
public interface ICommandRegistry
{
    /// <summary>全部已注册命令,按注册顺序排列。</summary>
    IReadOnlyList<CommandDescriptor> All { get; }

    /// <summary>注册(或按 id 替换)一条命令。</summary>
    void Register(CommandDescriptor command);

    /// <summary>注销一条命令;返回该命令此前是否存在。</summary>
    bool Unregister(string id);

    /// <summary>按 id 查找命令,未注册时返回 <see langword="null" />。</summary>
    CommandDescriptor? Find(string id);

    /// <summary>当命令存在且已启用时执行它,并返回是否成功运行。</summary>
    bool Execute(string id);
}

/// <summary>
/// 默认基于内存的 <see cref="ICommandRegistry" />,保留注册顺序。
/// 线程安全:插件在后台线程注册/注销命令,与 UI 线程的枚举并发发生。
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = [with(StringComparer.Ordinal)];
    private readonly List<string> _order = [];
    private readonly Lock _gate = new();

    /// <summary>注册(或按 id 替换)命令,保留首次出现的顺序。</summary>
    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (!_commands.ContainsKey(command.Id))
            {
                _order.Add(command.Id);
            }
            _commands[command.Id] = command;
        }
    }

    /// <summary>注销一条命令;返回该命令此前是否存在。</summary>
    public bool Unregister(string id)
    {
        lock (_gate)
        {
            if (!_commands.Remove(id))
            {
                return false;
            }
            _order.Remove(id);
            return true;
        }
    }

    /// <summary>按 id 查找命令,未注册时返回 <see langword="null" />。</summary>
    public CommandDescriptor? Find(string id)
    {
        lock (_gate)
        {
            return _commands.GetValueOrDefault(id);
        }
    }

    /// <summary>全部已注册命令,按注册顺序排列(锁内快照)。</summary>
    public IReadOnlyList<CommandDescriptor> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _order.Select(id => _commands[id])];
            }
        }
    }

    /// <summary>当命令存在且已启用时执行它,并返回是否成功运行。执行本身在锁外进行。</summary>
    public bool Execute(string id)
    {
        CommandDescriptor? command = Find(id);
        if (command is null || !command.IsEnabled)
        {
            return false;
        }
        command.Execute();
        return true;
    }
}

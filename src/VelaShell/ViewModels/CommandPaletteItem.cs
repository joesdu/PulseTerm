using System.Collections.ObjectModel;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>
/// 命令面板中的单个条目 —— 既可以是待连接的会话,也可以是待执行的动作。
/// </summary>
public sealed class CommandPaletteItem(
    string category,
    string title,
    Action invoke,
    string? hint = null,
    string? tag = null,
    bool isSession = false)
    : ReactiveObject
{
    /// <summary>分组桶,作为分组表头展示(如“会话”、“命令”)。</summary>
    public string Category { get; } = category;

    /// <summary>条目的主要显示文本(会话或动作名称)。</summary>
    public string Title { get; } = title;

    /// <summary>尾部提示文本:键盘快捷键,或会话的“Enter 连接”。</summary>
    public string? Hint { get; } = hint;

    /// <summary>可选的彩色徽章(如环境标签)。</summary>
    public string? Tag { get; } = tag;

    /// <summary>当本条目代表待连接的会话(而非命令)时为 true。</summary>
    public bool IsSession { get; } = isSession;

    /// <summary>选中该条目时执行的动作。</summary>
    public Action Invoke { get; } = invoke;

    /// <summary>当本项是当前键盘选中项(驱动高亮)时为 true。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>命令面板结果的分类分组。</summary>
public sealed class CommandPaletteGroup(string category)
{
    /// <summary>作为分组表头展示的分类名称。</summary>
    public string Category { get; } = category;

    /// <summary>属于该分类的命令面板条目。</summary>
    public ObservableCollection<CommandPaletteItem> Items { get; } = [];
}

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
    bool isSession = false,
    string? id = null)
    : ReactiveObject
{
    /// <summary>分组桶,作为分组表头展示(如“会话”、“命令”)。</summary>
    public string Category { get; } = category;

    /// <summary>
    /// 稳定标识(命令 id 或 <c>session:{guid}</c>),用于记录"最近用过哪几条"。
    /// 没有给 id 的条目退化为按标题记 —— 不理想但不影响其它条目。
    /// </summary>
    public string Id { get; } = id ?? title;

    /// <summary>本次查询的相关度分数,由 <see cref="PaletteScorer" /> 给出;越大越靠前。</summary>
    public int Score { get; set; }

    /// <summary>在未排序的原始条目表里的位置;同分时按它稳定排序(否则每次按键结果会乱跳)。</summary>
    public int OriginalIndex { get; set; }

    /// <summary>标题里应当高亮的字符区间(起点, 长度)。</summary>
    public IReadOnlyList<(int Start, int Length)> Highlights
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

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

/// <summary>
/// 扁平结果列表里的一行分组表头。
/// </summary>
/// <remarks>
/// 结果原先是"分组的 <c>ItemsControl</c> 里再套一层条目的 <c>ItemsControl</c>",于是
/// <b>一条也虚拟化不了</b> —— 保存了几百台机器的用户,每敲一个字符都要把全部结果的控件树
/// 重建一遍。摊平成单列表 + 表头行之后可以直接用 <c>ListBox</c> 的
/// <c>VirtualizingStackPanel</c>,只为看得见的那十几行生成容器。
/// </remarks>
/// <param name="Category">分类名称。</param>
public sealed record CommandPaletteHeader(string Category);

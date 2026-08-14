using VelaShell.PluginSdk.Protocols;

namespace VelaShell.ViewModels;

/// <summary>
/// 文件浏览器右键菜单里的一条协议专属动作。
/// <para>
/// 动作是**声明式**的(随协议描述一起给出),因此菜单在按下右键那一帧就能画出来 ——
/// 换成"每次右键回调插件问一遍"会多出一次异步往返,菜单会明显地慢半拍。
/// </para>
/// </summary>
/// <param name="action">插件声明的动作。</param>
/// <param name="target">右键命中的条目;在目录空白处触发时为 null。</param>
public sealed class ProtocolActionViewModel(ProtocolAction action, RemoteFileInfoViewModel? target)
{
    /// <summary>动作 id。</summary>
    public string Id { get; } = action.Id;

    /// <summary>菜单文案。</summary>
    public string Title { get; } = action.Title;

    /// <summary>适用范围。</summary>
    public ProtocolActionScope Scope { get; } = action.Scope;

    /// <summary>右键命中的条目。</summary>
    public RemoteFileInfoViewModel? Target { get; } = target;

    /// <summary>
    /// 该动作对当前条目是否适用。不适用就不进菜单,而不是灰着 ——
    /// 灰掉的菜单项只会让人反复去点它。
    /// </summary>
    /// <param name="entry">候选条目;为 null 表示目录空白处。</param>
    /// <returns>是否适用。</returns>
    public bool AppliesTo(RemoteFileInfoViewModel? entry) =>
        entry is null or { IsParentEntry: true }
            ? Scope.HasFlag(ProtocolActionScope.Background)
            : Scope.HasFlag(entry.IsDirectory ? ProtocolActionScope.Directory : ProtocolActionScope.File);
}

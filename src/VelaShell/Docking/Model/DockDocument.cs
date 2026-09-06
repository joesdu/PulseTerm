namespace VelaShell.Docking.Model;

/// <summary>
/// 一个可停靠文档(= 一个标签页)。对应原 Dock.Model 的 Document,只保留
/// 本应用实际用到的成员:浮动/固定(Pin)按产品决策永久禁用,故不建模。
/// </summary>
public abstract class DockDocument : DockElement
{
    /// <summary>文档的唯一标识,用于在工作区内定位与去重。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>标签页显示的标题;变更时触发属性通知以刷新界面。</summary>
    public string Title
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>是否允许用户关闭该文档标签,默认允许。</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>
    /// 这个文档是不是一次**会话**(而不是一块工具面板)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 会话文档代表"用户此刻在哪台机器上":终端、独立 SFTP、插件渲染的工作台
    /// (Redis / MySQL…)都是。切到会话文档意味着换了工作对象 —— 状态栏该跟着换,
    /// 底部那个属于上一条会话的文件浏览器该收起来。
    /// </para>
    /// <para>
    /// 工具面板不是:AI 聊天、以及任何插件用 <c>PanelDisplayMode.Document</c> 开出来的面板。
    /// 它们是<b>在当前会话上做事的工具</b>,切过去不改变"我在哪台机器上"。
    /// 把状态栏清空、把用户特意打开的文件浏览器收走,只是让界面在焦点之间闪来闪去。
    /// </para>
    /// <para>
    /// <b>做成文档自己声明的属性,而不是在宿主里按类型列一张白名单</b>:那张表在
    /// 新增文档类型时必然被忘掉,而忘掉的后果是一个默认值决定的、症状与原因毫不相干的
    /// 交互 bug(已经发生过一次:插件面板被卷进"不是终端就收起"那条规则里)。
    /// 默认 <c>false</c> —— 新类型多半是面板,而"面板被误判成会话"的后果比反过来更烦人。
    /// </para>
    /// </remarks>
    public bool IsSessionDocument { get; init; }
}

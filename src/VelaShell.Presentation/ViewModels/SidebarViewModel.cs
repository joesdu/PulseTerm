using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;

namespace VelaShell.Presentation.ViewModels;

/// <summary>侧边栏视图模型:聚合会话树、快捷片段、最近连接以及设置/通知等入口命令。</summary>
public sealed class SidebarViewModel(
    IRecentConnectionService? recentConnectionService = null,
    QuickCommandRunnerViewModel? quickCommands = null
) : ReactiveObject
{
    /// <summary>最近连接列表的子视图模型。</summary>
    public RecentConnectionsViewModel RecentConnections { get; } = new(recentConnectionService);

    /// <summary>快捷代码片段运行区域;无应用数据存储时为 null。</summary>
    public QuickCommandRunnerViewModel? QuickCommands { get; } = quickCommands;

    /// <summary>是否在侧边栏中展示快捷命令区域。</summary>
    public bool IsQuickCommandsVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>快捷命令区域是否展开。</summary>
    public bool QuickCommandsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>快捷命令区域上次展开时的高度。</summary>
    public double QuickCommandsHeight
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 160;

    /// <summary>最近连接区域是否展开。</summary>
    public bool RecentConnectionsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>最近连接区域上次展开时的高度。</summary>
    public double RecentConnectionsHeight
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 180;

    /// <summary>当前会话树视图模型,未加载时为 null。</summary>
    public SessionTreeViewModel? SessionTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开通知的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NotificationsCommand { get; } =
        ReactiveCommand.Create(() => { });
}

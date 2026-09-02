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

    /// <summary>
    /// 整条侧边栏是否折叠成图标细条(标题栏折叠按钮 / Ctrl+B)。
    /// 折叠后侧栏只剩底部那几个入口图标,列宽由宿主窗口收到 40px ——
    /// 上面 <see cref="QuickCommandsExpanded" /> 等折的是侧栏**里面**的分区,与这个是两码事。
    /// </summary>
    public bool IsCollapsed
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前会话树视图模型,未加载时为 null。</summary>
    public SessionTreeViewModel? SessionTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 消息中心的未读条数(底部栏铃铛角标);0 表示不显示角标。
    /// 由宿主在 <see cref="Core.Notifications.INotificationCenter" /> 变化时推过来 ——
    /// 侧边栏只负责显示这个数字,不关心它从哪来。
    /// </summary>
    public int NotificationUnreadCount
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasUnreadNotifications));
        }
    }

    /// <summary>是否有未读消息(控制角标显隐)。</summary>
    public bool HasUnreadNotifications => NotificationUnreadCount > 0;

    /// <summary>用户点击底部栏铃铛,请求打开消息中心。</summary>
    public event EventHandler? NotificationsRequested;

    /// <summary>打开消息中心的命令(底部栏铃铛)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NotificationsCommand =>
        field ??= ReactiveCommand.Create(() => NotificationsRequested?.Invoke(this, EventArgs.Empty));
}

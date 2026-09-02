namespace VelaShell.Core.Models;

/// <summary>应用级持久化状态,记录最近连接、窗口布局与上次活动标签等会话信息。</summary>
public class AppState
{
    /// <summary>最近使用过的连接标识列表,按使用顺序保存以便快速重连。</summary>
    public List<string> RecentConnections { get; set; } = [];

    /// <summary>上次关闭时的窗口位置,用于下次启动还原;为 null 表示使用默认位置。</summary>
    public WindowPosition? WindowPosition { get; set; }

    /// <summary>上次关闭时的窗口尺寸,用于下次启动还原;为 null 表示使用默认尺寸。</summary>
    public WindowSize? WindowSize { get; set; }

    /// <summary>上次处于活动状态的标签页标识,用于恢复用户的工作上下文。</summary>
    public string? LastActiveTab { get; set; }

    /// <summary>快捷命令侧栏区域是否展开。</summary>
    public bool SidebarQuickCommandsExpanded { get; set; } = true;

    /// <summary>快捷命令侧栏区域上次展开时的高度。</summary>
    public double SidebarQuickCommandsHeight { get; set; } = 160;

    /// <summary>最近连接侧栏区域是否展开。</summary>
    public bool SidebarRecentConnectionsExpanded { get; set; } = true;

    /// <summary>最近连接侧栏区域上次展开时的高度。</summary>
    public double SidebarRecentConnectionsHeight { get; set; } = 180;

    /// <summary>
    /// 整条左侧资源管理器是否处于折叠状态(标题栏折叠按钮 / Ctrl+B)。
    /// 与上面两个区域内的展开状态是两码事:那两个折的是侧栏**里面**的分区,这个折的是侧栏本身。
    /// </summary>
    public bool SidebarCollapsed { get; set; }
}

/// <summary>窗口在屏幕上的位置坐标。</summary>
public class WindowPosition
{
    /// <summary>窗口左上角的横坐标(像素)。</summary>
    public int X { get; set; }

    /// <summary>窗口左上角的纵坐标(像素)。</summary>
    public int Y { get; set; }
}

/// <summary>窗口的尺寸大小。</summary>
public class WindowSize
{
    /// <summary>窗口宽度(像素)。</summary>
    public int Width { get; set; }

    /// <summary>窗口高度(像素)。</summary>
    public int Height { get; set; }
}

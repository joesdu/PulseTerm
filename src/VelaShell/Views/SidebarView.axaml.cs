using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>侧边栏视图:承载资源管理器、快捷命令、最近连接与底部设置入口。</summary>
public partial class SidebarView : UserControl
{
    private const double CollapsedHeight = 36;
    private const double MinimumExpandedHeight = 100;
    private const double MaximumRememberedHeight = 1200;

    /// <summary>会话树无论如何都要留下的高度(约两行)。</summary>
    private const double SessionTreeFloor = 60;

    /// <summary>展开状态下分隔条的高度。</summary>
    private const double SplitterHeight = 5;

    // SessionAndQuickGrid 的行序:0 = 会话树,1 = 分隔条,2 = 快捷命令区。
    private const int TreeRow = 0;
    private const int QuickSplitterRow = 1;
    private const int QuickRow = 2;

    private SidebarViewModel? _viewModel;
    private SessionTreeViewModel? _sessionTree;

    /// <summary>正在夹取行高;防止赋值再触发一轮而自我递归。</summary>
    private bool _clampingSections;

    /// <summary>创建侧边栏视图并加载其可视组件。</summary>
    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        QuickCommandsSplitter.DragCompleted += (_, _) => CaptureQuickCommandsHeight();
        RecentConnectionsSplitter.DragCompleted += (_, _) => CaptureRecentConnectionsHeight();
        // 侧栏变矮时重新分配三块区域的高度。记住的高度是绝对像素,窗口一小就装不下 ——
        // 不重算的话它们会各按各的下限排下去,直接压在一起(见 ClampSectionHeights)。
        SidebarSectionsGrid.SizeChanged += (_, _) => ClampSectionHeights();
    }

    /// <summary>用户请求打开“新建连接”配置弹窗时触发(顶部新建按钮)。</summary>
    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>由底部栏齿轮按钮触发,用于打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>由底部栏插件按钮触发,用于打开插件管理窗口。</summary>
    public event EventHandler? PluginsRequested;

    /// <summary>用户双击最近连接以重新连接时触发。</summary>
    public event EventHandler<RecentConnectionEntry>? RecentConnectRequested;

    /// <summary>用户在资源管理器「更多」菜单中选择「从其他工具导入会话」时触发。</summary>
    public event EventHandler? ImportSessionsRequested;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SidebarViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        ApplyQuickCommandsVisibility();
        ApplyRecentConnectionsState();
        ApplyCollapsedState();
        HookSessionTree();
    }

    /// <summary>
    /// 订阅会话树的连接事件,只为在折叠态浮层里连上之后把浮层收起来。
    /// 会话树视图模型是宿主后建的(<c>SidebarViewModel.SessionTree</c> 可被整体替换),
    /// 故随属性变化重新接线,并先摘掉旧的 —— 否则每换一次就多留一条订阅。
    /// </summary>
    private void HookSessionTree()
    {
        _sessionTree?.ConnectRequested -= OnSessionConnectRequested;
        _sessionTree = _viewModel?.SessionTree;
        _sessionTree?.ConnectRequested += OnSessionConnectRequested;
    }

    /// <summary>
    /// 连上就收起会话浮层。折叠态图的就是别让侧栏占着宽度,连完还杵在那儿挡着终端
    /// 就本末倒置了。展开态没有浮层,Hide 是空操作,不必分情况。
    /// </summary>
    private void OnSessionConnectRequested(SessionProfile profile) =>
        RailSessionsButton.Flyout?.Hide();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(SidebarViewModel.IsQuickCommandsVisible)
                or nameof(SidebarViewModel.QuickCommandsExpanded)
                or nameof(SidebarViewModel.QuickCommandsHeight)
        )
        {
            ApplyQuickCommandsVisibility();
        }
        if (
            e.PropertyName
            is nameof(SidebarViewModel.RecentConnectionsExpanded)
                or nameof(SidebarViewModel.RecentConnectionsHeight)
        )
        {
            ApplyRecentConnectionsState();
        }
        if (e.PropertyName is nameof(SidebarViewModel.IsCollapsed))
        {
            ApplyCollapsedState();
        }
        if (e.PropertyName is nameof(SidebarViewModel.SessionTree))
        {
            HookSessionTree();
        }
    }

    /// <summary>
    /// 在完整侧栏与 40px 图标细条之间切换。两副面孔互斥显示,列宽由宿主窗口
    /// (<see cref="MainWindow" />)收放 —— 这里只管显示哪一副。
    /// </summary>
    private void ApplyCollapsedState()
    {
        bool collapsed = _viewModel?.IsCollapsed == true;
        SidebarGrid.IsVisible = !collapsed;
        CollapsedRail.IsVisible = collapsed;
    }

    private void ExpandSidebar_Click(object? sender, RoutedEventArgs e) => _viewModel?.IsCollapsed = false;

    /// <summary>
    /// 折叠态细条贴哪一边(随设置 → 外观 → 侧边栏位置)。
    /// </summary>
    /// <remarks>
    /// 细条钉死 40px 而不是随列拉伸:折叠动画途中列宽一路从 260 收到 40,细条若跟着拉伸,
    /// 图标会在列里滑一段距离。钉住并贴紧侧栏的外缘之后,过程就只剩"面板从内侧退走",
    /// 细条本身纹丝不动 —— 这也正是它作为常驻入口该有的观感。
    /// </remarks>
    // 枚举名必须写全:在 UserControl 里裸写 HorizontalAlignment 会先解析到控件自己那个同名属性。
    public void SetRailEdge(bool right) =>
        CollapsedRail.HorizontalAlignment = right
            ? Avalonia.Layout.HorizontalAlignment.Right
            : Avalonia.Layout.HorizontalAlignment.Left;

    /// <summary>工具栏折叠按钮。只出现在展开态,所以直接置位而不是取反。</summary>
    private void CollapseSidebar_Click(object? sender, RoutedEventArgs e) => _viewModel?.IsCollapsed = true;

    private void OpenConnectionProfile_Click(object? sender, RoutedEventArgs e) => OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);

    private void OpenSettings_Click(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenPlugins_Click(object? sender, RoutedEventArgs e) => PluginsRequested?.Invoke(this, EventArgs.Empty);

    private void ImportSessions_Click(object? sender, RoutedEventArgs e) => ImportSessionsRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleQuickCommands_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        if (_viewModel.QuickCommandsExpanded)
        {
            CaptureQuickCommandsHeight();
        }
        _viewModel.QuickCommandsExpanded = !_viewModel.QuickCommandsExpanded;
    }

    private void ApplyQuickCommandsVisibility()
    {
        bool visible = _viewModel is { IsQuickCommandsVisible: true, QuickCommands: not null };
        RowDefinition splitterRow = SessionAndQuickGrid.RowDefinitions[QuickSplitterRow];
        RowDefinition quickCommandsRow = SessionAndQuickGrid.RowDefinitions[QuickRow];
        if (
            !visible
            && QuickCommandsSection.IsVisible
            && _viewModel?.QuickCommandsExpanded == true
            && quickCommandsRow.ActualHeight > CollapsedHeight
        )
        {
            CaptureQuickCommandsHeight();
        }
        QuickCommandsSection.IsVisible = visible;
        if (!visible)
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = 0;
            quickCommandsRow.Height = new(0);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
            return;
        }

        bool expanded = _viewModel?.QuickCommandsExpanded == true;
        QuickCommandsContent.IsVisible = expanded;
        QuickCommandsExpandedIcon.IsVisible = expanded;
        QuickCommandsCollapsedIcon.IsVisible = !expanded;
        if (expanded)
        {
            splitterRow.Height = new(5);
            quickCommandsRow.MinHeight = MinimumExpandedHeight;
            quickCommandsRow.Height = new(
                NormalizeHeight(_viewModel?.QuickCommandsHeight ?? 160, SessionAndQuickGrid, 160)
            );
            QuickCommandsDivider.IsVisible = true;
            QuickCommandsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = CollapsedHeight;
            quickCommandsRow.Height = new(CollapsedHeight);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
        }
        // 上面按"记住的高度"定的值可能装不下,交给夹取做最终裁决。
        ClampSectionHeights();
    }

    private void ToggleRecentConnections_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        if (_viewModel.RecentConnectionsExpanded)
        {
            CaptureRecentConnectionsHeight();
        }
        _viewModel.RecentConnectionsExpanded = !_viewModel.RecentConnectionsExpanded;
    }

    private void ApplyRecentConnectionsState()
    {
        bool expanded = _viewModel?.RecentConnectionsExpanded ?? true;
        RowDefinition recentRow = SidebarSectionsGrid.RowDefinitions[2];
        RecentConnectionsContent.IsVisible = expanded;
        RecentConnectionsExpandedIcon.IsVisible = expanded;
        RecentConnectionsCollapsedIcon.IsVisible = !expanded;
        RowDefinition splitterRow = SidebarSectionsGrid.RowDefinitions[1];
        if (expanded)
        {
            splitterRow.Height = new(5);
            recentRow.MinHeight = MinimumExpandedHeight;
            recentRow.Height = new(
                NormalizeHeight(
                    _viewModel?.RecentConnectionsHeight ?? 180,
                    SidebarSectionsGrid,
                    180
                )
            );
            RecentConnectionsDivider.IsVisible = true;
            RecentConnectionsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            recentRow.MinHeight = CollapsedHeight;
            recentRow.Height = new(CollapsedHeight);
            RecentConnectionsDivider.IsVisible = false;
            RecentConnectionsSplitter.IsVisible = false;
        }
        // 同上:记住的高度装不下时由夹取兜底。
        ClampSectionHeights();
    }

    private void CaptureQuickCommandsHeight()
    {
        if (_viewModel is null)
        {
            return;
        }
        double height = SessionAndQuickGrid.RowDefinitions[QuickRow].ActualHeight;
        if (height > CollapsedHeight)
        {
            _viewModel.QuickCommandsHeight = NormalizeHeight(height, SessionAndQuickGrid, 160);
        }
    }

    private void CaptureRecentConnectionsHeight()
    {
        if (_viewModel is null)
        {
            return;
        }
        double height = SidebarSectionsGrid.RowDefinitions[2].ActualHeight;
        if (height > CollapsedHeight)
        {
            _viewModel.RecentConnectionsHeight = NormalizeHeight(height, SidebarSectionsGrid, 180);
        }
    }

    /// <summary>
    /// 按当前可用高度重新分配「会话树 / 快捷命令 / 最近连接」三块的高度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这三块分处两层嵌套网格,各有各的 <c>MinHeight</c>,而记住的高度是<b>绝对像素</b>。
    /// 侧栏一矮,两边的下限加起来就超过了可用高度:外层只给「会话+快捷命令」那一行留
    /// 80px,内层却按 过滤框 + 会话树(80) + 分隔条 + 快捷命令(100) 排下去 ——
    /// 多出来的部分不会被压缩,而是直接画到「最近连接」身上,最近连接本身则被挤出可视区。
    /// 这不是加滚动条能救的:两块内容是真的重叠在一起。
    /// </para>
    /// <para>
    /// 分配顺序按"谁先让步"来定:会话树是主体,永远先留 <see cref="SessionTreeFloor" />;
    /// 两块可选区域在空间不够时逐步退到只剩表头(<see cref="CollapsedHeight" />),
    /// 而不是把别人挤走。空间够时各自拿回记住的高度,行为与以前一致。
    /// </para>
    /// </remarks>
    private void ClampSectionHeights()
    {
        double available = SidebarSectionsGrid.Bounds.Height;
        if (_clampingSections || !double.IsFinite(available) || available <= 0)
        {
            return;
        }
        _clampingSections = true;
        try
        {
            RowDefinition sessionRow = SidebarSectionsGrid.RowDefinitions[0];
            RowDefinition recentRow = SidebarSectionsGrid.RowDefinitions[2];
            RowDefinition treeRow = SessionAndQuickGrid.RowDefinitions[TreeRow];
            RowDefinition quickRow = SessionAndQuickGrid.RowDefinitions[QuickRow];

            bool quickVisible = QuickCommandsSection.IsVisible;
            bool quickExpanded = quickVisible && _viewModel?.QuickCommandsExpanded == true;
            bool recentExpanded = _viewModel?.RecentConnectionsExpanded ?? true;

            double sessionFloor = SessionTreeFloor;
            double quickFloor = quickVisible ? CollapsedHeight : 0;
            double quickSplitter = quickExpanded ? SplitterHeight : 0;
            double recentSplitter = recentExpanded ? SplitterHeight : 0;

            // 最近连接:先给它想要的,但上面那一整块的底线要留出来。
            double recentWanted = recentExpanded
                ? Math.Max(MinimumExpandedHeight, _viewModel?.RecentConnectionsHeight ?? 180)
                : CollapsedHeight;
            double roomForRecent = available - recentSplitter - sessionFloor - quickFloor - quickSplitter;
            double recentHeight = Math.Clamp(
                recentWanted, CollapsedHeight, Math.Max(CollapsedHeight, roomForRecent));

            // 会话 + 快捷命令那一格拿到的实际高度。
            double sessionArea = Math.Max(0, available - recentSplitter - recentHeight);

            // 快捷命令:在这一格里给它想要的,给会话树留下底线。
            double quickWanted = quickExpanded
                ? Math.Max(MinimumExpandedHeight, _viewModel?.QuickCommandsHeight ?? 160)
                : quickFloor;
            double roomForQuick = sessionArea - quickSplitter - sessionFloor;
            double quickHeight = quickVisible
                ? Math.Clamp(quickWanted, CollapsedHeight, Math.Max(CollapsedHeight, roomForQuick))
                : 0;

            // MinHeight 必须跟着降下来 —— 否则网格会把行重新顶回下限,前面算的全白算。
            SetRow(recentRow, recentHeight, recentExpanded ? MinimumExpandedHeight : CollapsedHeight);
            SetRow(quickRow, quickHeight, quickExpanded ? MinimumExpandedHeight : quickFloor);
            sessionRow.MinHeight = Math.Min(sessionFloor, sessionArea);
            treeRow.MinHeight = Math.Min(
                SessionTreeFloor,
                Math.Max(0, sessionArea - quickSplitter - quickHeight));
        }
        finally
        {
            _clampingSections = false;
        }
    }

    /// <summary>给一行定死高度,并把下限压到不高于它(下限高于高度就等于没夹取)。</summary>
    private static void SetRow(RowDefinition row, double height, double preferredMinimum)
    {
        row.MinHeight = Math.Min(preferredMinimum, height);
        if (Math.Abs(row.Height.Value - height) > 0.5 || !row.Height.IsAbsolute)
        {
            row.Height = new GridLength(height);
        }
    }

    private static double NormalizeHeight(double height, Grid owner, double fallback)
    {
        double value =
            double.IsFinite(height) && height >= MinimumExpandedHeight ? height : fallback;
        double maximum = MaximumRememberedHeight;
        if (owner.Bounds.Height > MinimumExpandedHeight + 85)
        {
            maximum = Math.Min(maximum, Math.Max(MinimumExpandedHeight, owner.Bounds.Height - 85));
        }
        return Math.Clamp(value, MinimumExpandedHeight, maximum);
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentConnectionItemViewModel item })
        {
            RecentConnectRequested?.Invoke(this, item.Entry);
        }
    }

    /// <summary>
    /// 清除最近连接是破坏性操作(整段连接历史不可恢复):先确认再执行
    /// (设置审计 §12 破坏性操作需确认,与常规设置页的“清除历史记录”同一处置)。
    /// </summary>
    private void ClearRecentConnections_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(owner,
            Strings.Get("Sidebar_ClearRecent"),
            Strings.Get("Sidebar_ClearRecentConfirm"),
            danger: true);
        if (confirmed)
        {
            _viewModel.RecentConnections.ClearCommand.Execute().Subscribe();
        }
});
}

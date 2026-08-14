using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>会话树视图:以分组树形式展示会话,支持展开折叠、双击连接、右键菜单与拖动分组。</summary>
public partial class SessionTreeView : UserControl
{
    /// <summary>
    /// 会话拖放载荷前缀(与 SFTP 那套 VFTP/VFTPL 同形)。只在本树内自产自销,
    /// 前缀的作用是别把外部拖进来的文本/文件当成会话节点。
    /// </summary>
    private const string SessionDragPrefix = "VSESS|";

    /// <summary>按下到移动超过该像素才算拖拽,避免误触打断双击连接(与文件面板一致)。</summary>
    private const double DragThreshold = 5;

    private SessionTreeViewModel? _viewModel;

    // 拖拽手势状态:按下时记住行与按下事件(DoDragDropAsync 需要原始的 PointerPressedEventArgs)。
    private SessionTreeNodeViewModel? _dragNode;
    private PointerPressedEventArgs? _dragPointerArgs;
    private Point _dragOrigin;
    private bool _isDragging;

    /// <summary>被拖会话的显示名,拖起时快照下来给幽灵标签用(拖放过程中不再依赖手势状态)。</summary>
    private string _dragLabel = string.Empty;

    /// <summary>初始化会话树视图并加载 XAML 组件。</summary>
    public SessionTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 拖动分组:接收落点在整棵树上(空白处 = 移出分组),发起在会话行上
        // (Session_PointerPressed 记录起点,移动超过阈值才真正开始拖)。
        DragDrop.SetAllowDrop(SessionTreeRoot, true);
        SessionTreeRoot.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        SessionTreeRoot.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        SessionTreeRoot.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
        SessionTreeRoot.AddHandler(PointerMovedEvent, OnTreePointerMoved);
        SessionTreeRoot.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Bubble, true);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SessionTreeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTreeViewModel.SelectedNode))
        {
            Dispatcher.UIThread.Post(BringSelectedSessionIntoView, DispatcherPriority.Loaded);
        }
    }

    private void BringSelectedSessionIntoView()
    {
        if (_viewModel?.SelectedNode is not { } selected)
        {
            return;
        }
        Border? row = this.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border =>
                border.Classes.Contains("session") && ReferenceEquals(border.DataContext, selected)
            );
        row?.BringIntoView();
    }

    /// <summary>单击分组行即切换展开/折叠(设计 FrJPu:chevron 随之翻转)。</summary>
    private void Group_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>
    /// 右键分组行时先选中它:分组菜单里的命令同样作用于 SelectedNode。
    /// 左键不在这里处理 —— 展开/折叠仍走 Group_Tapped,选中交给 TreeView 自己。
    /// </summary>
    private void Group_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            return;
        }
        if (
            sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node }
            && DataContext is SessionTreeViewModel viewModel
        )
        {
            viewModel.SelectedNode = node;
        }
    }

    /// <summary>双击会话行直接连接(分组行仅展开/折叠)。</summary>
    private void Session_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (
            sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            && DataContext is SessionTreeViewModel viewModel
        )
        {
            viewModel.SelectedNode = node;
            viewModel.RequestConnect(node.Id);
        }
    }

    /// <summary>
    /// 右键弹菜单前先选中所指行:菜单里的命令都作用于 SelectedNode,不选中会
    /// 对着上一次选择的会话执行。左键则记下拖拽起点(是否真拖由移动阈值决定)。
    /// </summary>
    private void Session_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is not Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            return;
        }
        PointerPointProperties properties = e.GetCurrentPoint(null).Properties;
        if (properties.IsRightButtonPressed)
        {
            viewModel.SelectedNode = node;
            return;
        }
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragNode = node;
        _dragPointerArgs = e;
        _dragOrigin = e.GetPosition(this);
        _isDragging = false;
    }

    // ── 拖动分组 ────────────────────────────────────────────────

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragNode is null || _dragPointerArgs is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetDragGesture();
            return;
        }
        Point current = e.GetPosition(this);
        if (
            Math.Abs(current.X - _dragOrigin.X) < DragThreshold
            && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold
        )
        {
            return;
        }
        _isDragging = true;
        _ = StartSessionDragAsync(_dragNode, _dragPointerArgs);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ResetDragGesture();

    private async Task StartSessionDragAsync(
        SessionTreeNodeViewModel node,
        PointerPressedEventArgs pointerArgs
    )
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(SessionDragPrefix + node.Id);
        data.Add(item);
        _dragLabel = node.Name;
        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Move);
        }
        finally
        {
            // 拖拽被取消(Esc/落在窗口外)时同样要收拾干净,否则高亮与幽灵标签会留在屏幕上。
            ClearDragFeedback();
            ResetDragGesture();
        }
    }

    private void ResetDragGesture()
    {
        _isDragging = false;
        _dragNode = null;
        _dragPointerArgs = null;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (
            TryGetDraggedSessionId(e) is not { } sessionId
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        Guid target = viewModel.ResolveDropTargetGroupId(FindNodeAt(e.Source));
        // 落回原分组等于什么都没发生:直接给"不可放置"光标,免得用户以为拖成功了。
        bool sameGroup = viewModel.FindGroupIdOfSession(sessionId) == target;
        e.DragEffects = sameGroup ? DragDropEffects.None : DragDropEffects.Move;
        ShowDragFeedback(_dragLabel, target, sameGroup, e.GetPosition(DragOverlay));
        e.Handled = true;
    }

    /// <summary>
    /// 更新拖拽过程中的三处落点提示:目标分组行点亮、落到未分组时把整棵树框起来、
    /// 跟随光标的幽灵标签写明“&lt;会话&gt; → &lt;目标&gt;”。
    /// </summary>
    /// <remarks>
    /// 拖放事件在无头测试里造不出来,因此这里是 internal:测试直接调它,验的是真实视觉状态
    /// (可见性、标签文案、被夹在叠层内的坐标),而不是"我相信 DragOver 会做对"。
    /// </remarks>
    /// <param name="draggedName">被拖会话的显示名。</param>
    /// <param name="targetGroupId">落点分组;<see cref="Guid.Empty" /> 表示未分组(树根)。</param>
    /// <param name="sameGroup">落点与当前所在分组相同(等于没动),此时只显示会话名、不给落点承诺。</param>
    /// <param name="position">光标在 <c>DragOverlay</c> 坐标系里的位置。</param>
    internal void ShowDragFeedback(
        string draggedName,
        Guid targetGroupId,
        bool sameGroup,
        Point position
    )
    {
        if (DataContext is not SessionTreeViewModel viewModel)
        {
            return;
        }
        HighlightDropTarget(viewModel, sameGroup ? Guid.Empty : targetGroupId);
        RootDropZone.IsVisible = !sameGroup && targetGroupId == Guid.Empty;
        DragGhostText.Text = sameGroup
            ? draggedName
            : $"{draggedName} → {viewModel.DescribeDropTarget(targetGroupId)}";
        DragGhost.IsVisible = true;

        // 先量一次再摆位:标签宽度随文案变,不夹住的话拖到右下角时会被裁掉半截。
        // 多留 1px:布局取整会把实际排布尺寸再涨到下一个整像素,按 DesiredSize 卡死会正好溢出。
        const double layoutRoundingSlack = 1;
        DragGhost.Measure(Size.Infinity);
        Size ghost = DragGhost.DesiredSize;
        double maxLeft = Math.Max(
            0,
            DragOverlay.Bounds.Width - ghost.Width - layoutRoundingSlack
        );
        double maxTop = Math.Max(
            0,
            DragOverlay.Bounds.Height - ghost.Height - layoutRoundingSlack
        );
        Canvas.SetLeft(DragGhost, Math.Clamp(position.X + 12, 0, maxLeft));
        Canvas.SetTop(DragGhost, Math.Clamp(position.Y + 16, 0, maxTop));
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        ClearDragFeedback();
        if (
            TryGetDraggedSessionId(e) is not { } sessionId
            || DataContext is not SessionTreeViewModel viewModel
        )
        {
            return;
        }
        e.Handled = true;
        await viewModel.MoveSessionToGroupAsync(
            sessionId,
            viewModel.ResolveDropTargetGroupId(FindNodeAt(e.Source))
        );
    }

    /// <summary>
    /// 只有指针真的离开整棵树时才熄灭高亮。
    /// DragLeave 是路由事件:在树内部跨元素时(行与行之间、行 Border 与里面的文本之间)
    /// 每次都会冒泡上来,若见一次清一次,就会与紧随其后的 DragOver 交替点亮/熄灭 ——
    /// 表现为拖着会话在目标分组上移动时持续闪烁。
    /// </summary>
    private void OnTreeDragLeave(object? sender, DragEventArgs e)
    {
        if (!new Rect(SessionTreeRoot.Bounds.Size).Contains(e.GetPosition(SessionTreeRoot)))
        {
            ClearDragFeedback();
        }
    }

    /// <summary>点亮目标分组行;<see cref="Guid.Empty" />(未分组/树根)不对应任何行,等同于全部熄灭。</summary>
    private static void HighlightDropTarget(SessionTreeViewModel viewModel, Guid groupId)
    {
        foreach (SessionTreeNodeViewModel node in viewModel.Nodes)
        {
            node.IsDropTarget = node.IsGroup && node.Id == groupId;
        }
    }

    /// <summary>熄灭全部落点提示(行高亮 + 根落点框 + 幽灵标签)。</summary>
    internal void ClearDragFeedback()
    {
        if (DataContext is SessionTreeViewModel viewModel)
        {
            HighlightDropTarget(viewModel, Guid.Empty);
        }
        RootDropZone.IsVisible = false;
        DragGhost.IsVisible = false;
    }

    /// <summary>从拖放载荷里取出会话 Id;不是本树发出的拖拽则返回 null。</summary>
    private static Guid? TryGetDraggedSessionId(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        if (text is null || !text.StartsWith(SessionDragPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        return Guid.TryParse(text[SessionDragPrefix.Length..], out Guid id) ? id : null;
    }

    /// <summary>
    /// 找出鼠标下的树节点:从命中的可视元素向上找第一个数据上下文是节点的控件。
    /// 树的空白处找不到节点,返回 null —— 落点解析(ResolveDropTargetGroupId)把它当作"未分组"。
    /// </summary>
    private static SessionTreeNodeViewModel? FindNodeAt(object? source) =>
        (source as Visual)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<SessionTreeNodeViewModel>()
            .FirstOrDefault();
}

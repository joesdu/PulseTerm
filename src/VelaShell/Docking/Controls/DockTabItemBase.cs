using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>共用的激活、选中、拖拽、关闭、拆分与标签位置行为。</summary>
public abstract class DockTabItemBase : UserControl
{
    private DockGroupControl? _owner;

    /// <summary>经数据上下文绑定到本标签的文档。</summary>
    protected DockDocument? Document => DataContext as DockDocument;
    /// <summary>拥有本标签所属组的 workspace。</summary>
    protected DockWorkspace? Workspace => _owner?.Workspace;
    /// <summary>包含本标签的停靠组。</summary>
    protected DockGroup? Group => _owner?.Group;

    /// <summary>定位所属组控件,并订阅激活文档变更。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<DockGroupControl>();
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }
        UpdateSelected();
    }

    /// <summary>取消组属性变更订阅并释放所属引用。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        _owner = null;
    }

    /// <summary>左键单击激活文档,按下时发起拖拽。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is not { } document || Workspace is not { } workspace)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            workspace.ActivateDocument(document);
            _owner?.WorkspaceControl?.DragController.OnTabPressed(this, e);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            workspace.ActivateDocument(document);
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockGroup.ActiveDocument))
        {
            UpdateSelected();
        }
    }

    private void UpdateSelected() =>
        PseudoClasses.Set(":selected", Document is not null && ReferenceEquals(Group?.ActiveDocument, Document));

    /// <summary>关闭当前标签的文档。</summary>
    // 走 RequestClose 而不是 CloseDocument:关闭已连接会话前的确认闸挂在前者上。
    protected void CloseTab_Click(object? sender, RoutedEventArgs e) => Workspace?.RequestClose(Document!);
    /// <summary>关闭组内除当前标签外的所有文档。</summary>
    protected void CloseOthers_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseOtherDocuments(Document!);
    /// <summary>关闭组内的全部文档。</summary>
    protected void CloseAll_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseAllDocuments(Document!);
    /// <summary>关闭当前标签左侧的全部文档。</summary>
    protected void CloseLeft_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseLeftDocuments(Document!);
    /// <summary>关闭当前标签右侧的全部文档。</summary>
    protected void CloseRight_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseRightDocuments(Document!);
    /// <summary>将文档水平拆分为新组。</summary>
    protected void SplitHorizontal_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Horizontal);
    /// <summary>将文档垂直拆分为新组。</summary>
    protected void SplitVertical_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Vertical);
    /// <summary>将标签移到组的顶部。</summary>
    protected void TabsTop_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Top);
    /// <summary>将标签移到组的左侧。</summary>
    protected void TabsLeft_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Left);
    /// <summary>将标签移到组的右侧。</summary>
    protected void TabsRight_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Right);

    private void SetTabsPosition(DockTabsPosition position)
    {
        if (Group is { } group)
        {
            group.TabsPosition = position;
        }
    }
}

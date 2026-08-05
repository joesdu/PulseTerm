using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>文件传输视图,展示传输进度与结果提示;表头可拖动,位置跨会话保留。</summary>
public partial class FileTransferView : UserControl
{
    /// <summary>按下拖拽手柄时的指针位置(父容器坐标),用于计算位移增量。</summary>
    private Point _dragOrigin;

    /// <summary>按下那一刻的面板偏移,拖拽期间按增量叠加。</summary>
    private double _dragStartOffsetX;

    private double _dragStartOffsetY;

    private bool _isDragging;

    /// <summary>初始化视图,接线指针悬停(暂停自动隐藏)与表头拖拽。</summary>
    public FileTransferView()
    {
        InitializeComponent();

        // 悬停在提示上会暂停其自动隐藏,以便查看结果;指针离开后
        // 3 秒倒计时恢复(§9)。
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);

        if (this.FindControl<Border>("DragHandle") is { } handle)
        {
            handle.PointerPressed += OnDragHandlePressed;
            handle.PointerMoved += OnDragHandleMoved;
            handle.PointerReleased += OnDragHandleReleased;
        }

        // 窗口缩放/最大化后,原先合法的位置可能已经越界 —— 重新夹回可视区,
        // 否则面板会停在看不见也够不着的地方。
        LayoutUpdated += (_, _) =>
        {
            if (!_isDragging)
            {
                ClampOffsetIntoView();
            }
        };
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        // 表头里还有"取消"和"关闭"按钮。Avalonia 里 Button 会把 PointerPressed 标记为已处理,
        // 默认订阅收不到 —— 但不要依赖这个隐式行为:按在按钮上就明确不起拖拽。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null)
        {
            return;
        }
        if (DataContext is not FileTransferViewModel vm
            || GetDragSpace() is not { } space
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _isDragging = true;
        _dragOrigin = e.GetPosition(space);
        _dragStartOffsetX = vm.PanelOffsetX;
        _dragStartOffsetY = vm.PanelOffsetY;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || DataContext is not FileTransferViewModel vm || GetDragSpace() is not { } space)
        {
            return;
        }
        Point current = e.GetPosition(space);
        ApplyOffset(vm,
            _dragStartOffsetX + (current.X - _dragOrigin.X),
            _dragStartOffsetY + (current.Y - _dragOrigin.Y));
        e.Handled = true;
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        _isDragging = false;
        e.Pointer.Capture(null);

        // 只在松手时落盘,而不是每次移动都写 —— 拖一次会产生上百个移动事件。
        (DataContext as FileTransferViewModel)?.PersistPanelPosition();
        e.Handled = true;
    }

    /// <summary>拖拽的参考坐标系:面板所在的父容器。</summary>
    private Visual? GetDragSpace() => this.GetVisualParent();

    /// <summary>把恢复出来的/当前的偏移夹回可视区(窗口尺寸变化后尤其必要)。</summary>
    private void ClampOffsetIntoView()
    {
        if (DataContext is FileTransferViewModel vm)
        {
            ApplyOffset(vm, vm.PanelOffsetX, vm.PanelOffsetY);
        }
    }

    /// <summary>
    /// 夹紧并写入偏移,保证面板整体留在父容器内。
    /// <para>
    /// <see cref="Visual.Bounds" /> 不含渲染变换,因此它就是"偏移为 0 时的锚定位置",
    /// 由此推出合法偏移区间 —— 无需把 XAML 里的对齐方式和边距硬编码进来。
    /// </para>
    /// </summary>
    private void ApplyOffset(FileTransferViewModel vm, double offsetX, double offsetY)
    {
        if (GetDragSpace() is not { } space || Bounds.Width <= 0 || space.Bounds.Width <= 0)
        {
            return;
        }
        Rect anchored = Bounds;
        vm.PanelOffsetX = Clamp(offsetX, -anchored.X, space.Bounds.Width - anchored.Width - anchored.X);
        vm.PanelOffsetY = Clamp(offsetY, -anchored.Y, space.Bounds.Height - anchored.Height - anchored.Y);
    }

    /// <summary>面板比容器还大时下限会超过上限,此时贴左上角而不是抛异常。</summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Behaviors;

/// <summary>
/// 给非模态浮层装上「拖表头挪位置」:把手柄上的指针位移写进
/// <see cref="IDraggablePanel" /> 的偏移量,松手时落盘,并在窗口尺寸变化后把浮层夹回可视区。
/// <para>
/// 抽出来是因为它有两个用户(文件传输提示、消息中心),而其中的夹紧与捕获逻辑细到
/// 「按在表头按钮上不许起拖」这一层 —— 复制第二份必然会漂。
/// </para>
/// <para>
/// 用法:<c>PanelDragHandler.Attach(this, DragHandle)</c>,浮层的 DataContext 实现
/// <see cref="IDraggablePanel" />,XAML 里把 <c>RenderTransform</c> 绑到那两个偏移量上。
/// </para>
/// </summary>
public sealed class PanelDragHandler
{
    private readonly Control _handle;

    private readonly Control _panel;

    /// <summary>按下拖拽手柄时的指针位置(父容器坐标),用于计算位移增量。</summary>
    private Point _dragOrigin;

    /// <summary>按下那一刻的面板偏移,拖拽期间按增量叠加。</summary>
    private double _dragStartOffsetX;

    private double _dragStartOffsetY;

    private bool _isDragging;

    private PanelDragHandler(Control panel, Control handle)
    {
        _panel = panel;
        _handle = handle;
    }

    /// <summary>把 <paramref name="handle" /> 接成 <paramref name="panel" /> 的拖拽手柄。</summary>
    public static PanelDragHandler Attach(Control panel, Control handle)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(handle);
        var dragHandler = new PanelDragHandler(panel, handle);
        handle.PointerPressed += dragHandler.OnPressed;
        handle.PointerMoved += dragHandler.OnMoved;
        handle.PointerReleased += dragHandler.OnReleased;

        // 窗口缩放/最大化后,原先合法的位置可能已经越界 —— 重新夹回可视区,
        // 否则面板会停在看不见也够不着的地方。
        panel.LayoutUpdated += (_, _) =>
        {
            if (!dragHandler._isDragging)
            {
                dragHandler.ClampIntoView();
            }
        };
        return dragHandler;
    }

    private IDraggablePanel? Target => _panel.DataContext as IDraggablePanel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // 表头里还有按钮(关闭、全部已读……)。Avalonia 里 Button 会把 PointerPressed 标记为
        // 已处理,默认订阅收不到 —— 但不要依赖这个隐式行为:按在按钮上就明确不起拖拽。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null)
        {
            return;
        }
        if (Target is not { } vm
            || GetDragSpace() is not { } space
            || !e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed)
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

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || Target is not { } vm || GetDragSpace() is not { } space)
        {
            return;
        }
        Point current = e.GetPosition(space);
        ApplyOffset(vm,
            _dragStartOffsetX + (current.X - _dragOrigin.X),
            _dragStartOffsetY + (current.Y - _dragOrigin.Y));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        _isDragging = false;
        e.Pointer.Capture(null);

        // 只在松手时落盘,而不是每次移动都写 —— 拖一次会产生上百个移动事件。
        Target?.PersistPanelPosition();
        e.Handled = true;
    }

    /// <summary>拖拽的参考坐标系:浮层所在的父容器。</summary>
    private Visual? GetDragSpace() => _panel.GetVisualParent();

    /// <summary>把恢复出来的/当前的偏移夹回可视区(窗口尺寸变化后尤其必要)。</summary>
    private void ClampIntoView()
    {
        if (Target is { } vm)
        {
            ApplyOffset(vm, vm.PanelOffsetX, vm.PanelOffsetY);
        }
    }

    /// <summary>
    /// 夹紧并写入偏移,保证浮层整体留在父容器内。
    /// <para>
    /// <see cref="Visual.Bounds" /> 不含渲染变换,因此它就是"偏移为 0 时的锚定位置",
    /// 由此推出合法偏移区间 —— 无需把 XAML 里的对齐方式和边距硬编码进来。
    /// </para>
    /// </summary>
    private void ApplyOffset(IDraggablePanel vm, double offsetX, double offsetY)
    {
        if (GetDragSpace() is not { } space || _panel.Bounds.Width <= 0 || space.Bounds.Width <= 0)
        {
            return;
        }
        Rect anchored = _panel.Bounds;
        vm.PanelOffsetX = Clamp(offsetX, -anchored.X, space.Bounds.Width - anchored.Width - anchored.X);
        vm.PanelOffsetY = Clamp(offsetY, -anchored.Y, space.Bounds.Height - anchored.Height - anchored.Y);
    }

    /// <summary>面板比容器还大时下限会超过上限,此时贴左上角而不是抛异常。</summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}

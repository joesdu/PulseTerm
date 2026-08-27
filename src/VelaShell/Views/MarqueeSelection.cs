using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 给 <see cref="ListBox" /> 加上资源管理器式的框选:按住左键拖出矩形,划过的行成片选中。
/// <para>
/// 几何在 <see cref="MarqueeSelectionMath" />(可脱离 UI 单测),这里只管接线:指针事件、
/// 矩形绘制、选中集同步、拖出边界时的自动滚动。
/// </para>
/// <para>
/// <b>能不能从按下位置起框</b>由每次按下的 start policy 决定。双栏拖放只占用
/// XAML 明确标记的 bounded <c>dnd-surface</c>,行和列的其它空白始终留给框选。
/// </para>
/// </summary>
internal sealed class MarqueeSelection
{
    /// <summary>升级成框选所需的拖动距离,低于此距离仍按普通点击处理。</summary>
    private const double Threshold = 4;

    /// <summary>取不到实测行高时的兜底值。</summary>
    private const double FallbackRowHeight = 24;

    private readonly ListBox _list;
    private readonly Border _overlay;
    private readonly Func<object, bool> _canSelect;
    private readonly Func<PointerPressedEventArgs, bool> _canStart;
    private readonly List<object> _baseSelection = [];
    private readonly List<object> _pressSelection = [];

    private bool _pointerDown;
    private bool _active;
    private Point _origin;
    private double _originOffset;

    private DispatcherTimer? _autoScroll;
    private double _autoScrollDelta;

    /// <summary>
    /// 自动滚动期间的当前指针位置。必须放字段里让计时器每次读:
    /// 闭包进去的话指针继续移动时矩形会卡在起框那一刻不再跟手。
    /// </summary>
    private Point _autoScrollPointer;

    private MarqueeSelection(
        ListBox list,
        Border overlay,
        Func<object, bool> canSelect,
        Func<PointerPressedEventArgs, bool> canStart)
    {
        _list = list;
        _overlay = overlay;
        _canSelect = canSelect;
        _canStart = canStart;
    }

    /// <summary>
    /// 把框选接到 <paramref name="list" /> 上,矩形画在 <paramref name="overlay" />
    /// (需与列表同处一个 Panel、<c>IsHitTestVisible=False</c>)。
    /// </summary>
    /// <param name="list">接收框选指针事件的列表。</param>
    /// <param name="overlay">绘制框选矩形的覆盖层。</param>
    /// <param name="canSelect">哪些数据项可以被框中(用来排除合成的 ".." 行)。</param>
    /// <param name="canStart">按下位置是否属于框选手势;每次按下都会重新判断。</param>
    public static MarqueeSelection Attach(
        ListBox list,
        Border overlay,
        Func<object, bool> canSelect,
        Func<PointerPressedEventArgs, bool> canStart
    )
    {
        var marquee = new MarqueeSelection(list, overlay, canSelect, canStart);

        // ListBoxItem mutates selection during its tunnel/bubble route. Snapshot before
        // that happens so Ctrl-marquee is additive to the selection at mouse-down time.
        list.AddHandler(InputElement.PointerPressedEvent, marquee.OnPointerPressedPreview, RoutingStrategies.Tunnel, true);
        // handledEventsToo 是必须的:ListBoxItem 会把 PointerPressed 标记为已处理
        // (它要用这一下做选中),不带这个标志就永远收不到按下,框选无从起头。
        list.AddHandler(InputElement.PointerPressedEvent, marquee.OnPointerPressed, RoutingStrategies.Bubble, true);
        list.AddHandler(InputElement.PointerMovedEvent, marquee.OnPointerMoved, RoutingStrategies.Bubble, true);
        list.AddHandler(InputElement.PointerReleasedEvent, marquee.OnPointerReleased, RoutingStrategies.Bubble, true);

        // 捕获被系统抢走(切窗口、拖出窗体)时也要收摊,否则矩形留在屏幕上、计时器空转。
        list.PointerCaptureLost += (_, _) => marquee.End();
        return marquee;
    }

    /// <summary>收起框选:停自动滚动、藏掉矩形。重复调用无害。</summary>
    public void End()
    {
        _pointerDown = false;
        _active = false;
        StopAutoScroll();
        _overlay.IsVisible = false;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (IsWithinScrollBar(e.Source))
        {
            return;
        }
        if (!_canStart(e))
        {
            return;
        }
        _pointerDown = true;
        _origin = e.GetPosition(_list);
        _originOffset = ScrollOffsetY;
        _baseSelection.Clear();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _baseSelection.AddRange(_pressSelection);
        }
    }

    private void OnPointerPressedPreview(object? sender, PointerPressedEventArgs e)
    {
        _pressSelection.Clear();
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || _list.SelectedItems is not { } selected)
        {
            return;
        }

        foreach (object? item in selected)
        {
            if (item is not null)
            {
                _pressSelection.Add(item);
            }
        }
    }

    /// <summary>按下位置是否落在列表自己的滚动条里(含滑块、轨道与两端按钮)。</summary>
    /// <remarks>
    /// 滚动条长在 <see cref="ListBox" /> 的模板内,它的 PointerPressed 照样冒泡到列表,
    /// 而框选是以 handledEventsToo 挂在列表上的 —— 不在这里拦掉,拖滚动条就会顺带拉出一个选区。
    /// 这不是各视图的 start policy 能表达的事(滚动条在任何面板里都不是框选面),
    /// 所以拦在公共入口,三处调用方一起受益。
    /// </remarks>
    private static bool IsWithinScrollBar(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDndSurface(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control control && control.Classes.Contains("dnd-surface"))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDndSurface(object? source, Visual? hitRoot, Point position)
    {
        if (IsDndSurface(source))
        {
            return true;
        }

        if (hitRoot is null)
        {
            return false;
        }

        foreach (Control control in hitRoot.GetVisualDescendants().OfType<Control>())
        {
            if (!control.Classes.Contains("dnd-surface") || !control.IsVisible)
            {
                continue;
            }

            Point? topLeft = control.TranslatePoint(new(), hitRoot);
            if (topLeft is { } point
                && new Rect(point, control.Bounds.Size).Contains(position))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }
        Point current = e.GetPosition(_list);
        if (!_active)
        {
            if (Math.Abs(current.X - _origin.X) < Threshold && Math.Abs(current.Y - _origin.Y) < Threshold)
            {
                return;
            }
            _active = true;
            e.Pointer.Capture(_list);
            _overlay.IsVisible = true;
        }
        Update(current);

        // 拖出上下边界就持续滚动,否则一屏之外的行永远框不到。
        double overshoot = current.Y < 0
            ? current.Y
            : current.Y > _list.Bounds.Height ? current.Y - _list.Bounds.Height : 0;
        SetAutoScroll(overshoot, current);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pointerDown = false;
        if (!_active)
        {
            return;
        }
        End();
        e.Pointer.Capture(null);

        // 框选拖动不该再被当成一次点击落到行上(否则松手瞬间选中集会被那一行顶掉)。
        e.Handled = true;
    }

    /// <summary>画出矩形,并把它覆盖到的行同步进选中集。</summary>
    private void Update(Point current)
    {
        double originY = _origin.Y + _originOffset - ScrollOffsetY;
        double left = Math.Max(Math.Min(_origin.X, current.X), 0);
        double top = Math.Max(Math.Min(originY, current.Y), 0);
        double right = Math.Min(Math.Max(_origin.X, current.X), _list.Bounds.Width);
        double bottom = Math.Min(Math.Max(originY, current.Y), _list.Bounds.Height);
        _overlay.Margin = new(left, top, 0, 0);
        _overlay.Width = Math.Max(right - left, 0);
        _overlay.Height = Math.Max(bottom - top, 0);

        // 命中判定走内容坐标(视口坐标 + 滚动偏移),这样滚动过程中已划过的行不会掉出来。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(
            _origin.Y + _originOffset,
            current.Y + ScrollOffsetY,
            RowHeight(),
            _list.ItemCount
        );
        ApplySelection(first, last);
    }

    private void ApplySelection(int first, int last)
    {
        if (_list.SelectedItems is not { } selection)
        {
            return;
        }
        var swept = new List<object>();
        for (int i = first; i >= 0 && i <= last; i++)
        {
            object? item = _list.ItemsView[i];
            if (item is not null && _canSelect(item))
            {
                swept.Add(item);
            }
        }
        var target = new HashSet<object>(MarqueeSelectionMath.MergeSelection(_baseSelection, swept));

        // 逐项增删而不是清空重建:清空会让绑定过去的选中集合抖动一次,
        // 列表跟着重画,拖动过程中肉眼可见地闪。
        for (int i = selection.Count - 1; i >= 0; i--)
        {
            if (selection[i] is { } existing && !target.Contains(existing))
            {
                selection.RemoveAt(i);
            }
        }
        foreach (object item in target)
        {
            if (!selection.Contains(item))
            {
                selection.Add(item);
            }
        }
    }

    /// <summary>实测行高(取第一个已实现的容器),取不到时用兜底值。</summary>
    private double RowHeight()
    {
        foreach (Control container in _list.GetRealizedContainers())
        {
            if (container.Bounds.Height > 0)
            {
                return container.Bounds.Height;
            }
        }
        return FallbackRowHeight;
    }

    private double ScrollOffsetY => _list.Scroll?.Offset.Y ?? 0;

    private void SetAutoScroll(double overshoot, Point current)
    {
        _autoScrollPointer = current;
        if (overshoot == 0)
        {
            StopAutoScroll();
            return;
        }

        // 越界越多滚得越快,但压在一行的量级上,免得一冲到底。
        _autoScrollDelta = Math.Clamp(overshoot / 2, -RowHeight(), RowHeight());
        if (_autoScroll is not null)
        {
            return;
        }
        _autoScroll = new(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (_list.Scroll is not { } scroll)
            {
                return;
            }
            double y = Math.Clamp(
                scroll.Offset.Y + _autoScrollDelta,
                0,
                Math.Max(scroll.Extent.Height - scroll.Viewport.Height, 0)
            );
            scroll.Offset = scroll.Offset.WithY(y);
            Update(_autoScrollPointer);
        });
        _autoScroll.Start();
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }
}

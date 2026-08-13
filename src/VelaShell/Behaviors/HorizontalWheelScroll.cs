using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Behaviors;

/// <summary>
/// 让只能横向滚动的容器(资源监视里的网卡/显卡卡片条)听懂普通滚轮:把上下滚动映射成左右滚动。
/// 用法:
/// <code>
/// &lt;ScrollViewer behaviors:HorizontalWheelScroll.Enabled="True"
///               HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled"&gt;
/// </code>
/// </summary>
/// <remarks>
/// Avalonia 自带的滚轮处理只把 <c>Delta.X</c> 记到横向偏移上,而普通鼠标滚轮只产生 <c>Delta.Y</c>
/// (横向分量要靠倾斜滚轮或触摸板横扫),于是横向卡片条在滚轮下纹丝不动 —— 只能拖那条滚动条。
/// <para>
/// 三条自保规则:纵向还能滚时不抢(那是纵向列表该干的事)、内容没超宽时不吞事件、
/// 已经滚到头时也不吞 —— 后两条让事件继续冒泡给外层容器,不至于把整页的滚动卡死在一张卡片条上。
/// </para>
/// </remarks>
public static class HorizontalWheelScroll
{
    /// <summary>一格滚轮走的像素数。取 64:一次滚动挪过大半张卡片(网卡卡片宽 288),又不至于甩过头。</summary>
    private const double WheelStep = 64;

    /// <summary>浮点比较容差:视口与内容尺寸是布局算出来的,差零点几像素不算"能滚"。</summary>
    private const double Epsilon = 0.5;

    /// <summary>附加在 <see cref="ScrollViewer" /> 上:是否把滚轮的上下滚动映射成左右滚动。</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Enabled", typeof(HorizontalWheelScroll));

    static HorizontalWheelScroll() =>
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnEnabledChanged);

    /// <summary>读取某个滚动容器是否启用了滚轮横滚。</summary>
    public static bool GetEnabled(ScrollViewer viewer) => viewer.GetValue(EnabledProperty);

    /// <summary>开启或关闭某个滚动容器的滚轮横滚。</summary>
    public static void SetEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // handledEventsToo:内层的 ScrollContentPresenter 只要判出"有可滚动的余量"就会把事件标记为
            // 已处理 —— 哪怕它一个像素都没挪(横向余量它只认 Delta.X)。不收已处理的事件就永远轮不到这里。
            viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, handledEventsToo: true);
        }
        else
        {
            viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        }
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        // 触摸板的横扫本来就带横向分量,直接用;普通滚轮只有纵向分量,借过来当横向使。
        double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (delta == 0)
        {
            return;
        }

        // 纵向还有余量时把滚轮还给纵向滚动 —— 除非用户本来就在横扫。
        if (e.Delta.X == 0 && viewer.Extent.Height - viewer.Viewport.Height > Epsilon)
        {
            return;
        }

        double range = viewer.Extent.Width - viewer.Viewport.Width;
        if (range <= Epsilon)
        {
            return;
        }

        double target = Math.Clamp(viewer.Offset.X - (delta * WheelStep), 0, range);
        if (Math.Abs(target - viewer.Offset.X) < Epsilon)
        {
            return;
        }

        viewer.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(target, viewer.Offset.Y));
        e.Handled = true;
    }
}

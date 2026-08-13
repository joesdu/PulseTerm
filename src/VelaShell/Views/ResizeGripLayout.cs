using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Views;

/// <summary>
/// 自绘窗体缩放抓取区的尺寸,按卡片当前形态给出。
/// </summary>
/// <remarks>
/// 抓取区必须跟卡片的外边距一起动:外边距是投影的画布,那圈透明留白既不属于卡片、
/// 也没有别的控件接管,抓取区窄于它就留下一圈"看得见、点不动"的死区;反过来卡片贴边时
/// (macOS 的不透明矩形)抓取区还按留白宽度铺,就会压在真实内容上吃掉边缘控件的点击
/// (最靠边的往往正是滚动条)。所以两个值由同一个 <c>rounded</c> 状态决定。
/// 抓取区的 Border 靠 <c>Tag</c> 标出所在边,与 XAML 里 BeginResizeDrag 用的是同一个标记。
/// </remarks>
internal static class ResizeGripLayout
{
    /// <summary>卡片带外边距时:抓取区正好铺满 16px 的投影留白。</summary>
    private const double GutterEdge = 16,
        GutterCorner = 22;

    /// <summary>卡片贴边时:抓取区压在内容上,只取够用的最小值。</summary>
    private const double FlushEdge = 5,
        FlushCorner = 10;

    /// <summary>按卡片形态调整抓取区厚度。</summary>
    /// <param name="grips">抓取区容器(XAML 里的 ResizeGrips)。</param>
    /// <param name="rounded">true = 普通态的圆角浮层(有外边距),false = 铺满的矩形。</param>
    public static void Apply(Panel? grips, bool rounded)
    {
        if (grips is null)
        {
            return;
        }
        double edge = rounded ? GutterEdge : FlushEdge;
        double corner = rounded ? GutterCorner : FlushCorner;
        foreach (Control child in grips.Children)
        {
            if (child is not Border { Tag: string tag })
            {
                continue;
            }
            switch (tag)
            {
                // 上下边:留出四角的宽度,否则角上的抓取区被边压住,拿不到斜向缩放。
                case "North" or "South":
                    child.Height = edge;
                    child.Margin = new Thickness(corner, 0);
                    break;
                case "West" or "East":
                    child.Width = edge;
                    child.Margin = new Thickness(0, corner);
                    break;
                default:
                    child.Width = corner;
                    child.Height = corner;
                    break;
            }
        }
    }
}

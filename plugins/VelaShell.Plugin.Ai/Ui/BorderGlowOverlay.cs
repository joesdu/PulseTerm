using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 沿着圆角矩形边框跑圈的两道流光。盖在输入框上,只在请求在途时点亮。
/// </summary>
/// <remarks>
/// <b>运动</b>:沿<i>路径</i>走,不是按角度转。锥形渐变那种按角度均匀转的做法,
/// 在扁长方形上会让光斑在左右短边磨蹭、在上下长边一闪而过 —— 试过,很别扭。
///
/// <b>配色</b>:整圈先铺一层暗色<i>轨道</i>,流光在轨道上由
/// <c>轨道色 → 青 → 强调色</c> 连续过渡,末端再淡回轨道色。
/// 关键是<b>用颜色淡回轨道色,而不是用透明度淡出</b> ——
/// 后者在深色底上会显出边界、叠几层还出台阶,正是之前"变丑"的原因。
///
/// <b>画法</b>:把流光切成若干小段,逐段取渐变上的颜色与粗细画短线(圆头笔帽,接缝自然填平)。
/// 虚线笔做不到这件事:一段虚线只能是一个纯色,叠层拼不出连续渐变。
/// </remarks>
public sealed class BorderGlowOverlay : Control
{
    /// <summary>刷新间隔 ≈ 30fps。再密对这么细的一条线也看不出差别,只是白烧 UI 线程。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(33);

    /// <summary>切段长度(像素)。越小越平滑、画得越多;6px 在圆头笔帽下已看不出接缝。</summary>
    private const double Step = 6;

    /// <summary>
    /// 线宽。<b>就是 1px、不加外晕</b> —— 参考图逐像素量过:亮线只占一行,上下两行都是纯背景色,
    /// 所谓"光晕"指的是那条平滑的颜色渐变本身,不是另铺一层更粗的光。加粗只会变成一条胖模糊的带子。
    /// </summary>
    private const double Weight = 1;

    private DispatcherTimer? _timer;
    private Rect _bounds;
    private double _radius;
    private double _perimeter;
    private double _topRun, _sideRun, _arcRun;

    /// <summary>流光走完一整圈的时长。</summary>
    public TimeSpan Cycle { get; set; } = TimeSpan.FromMilliseconds(3400);

    /// <summary>
    /// 流光长度(像素)。参考图上量得约 272px(轨道→青 96 + 青→品红 80 + 品红→轨道 96),
    /// 长而软才像"光",短了就是一截线头。
    /// </summary>
    public double CometLength { get; set; } = 272;

    /// <summary>要跟随的圆角半径(与被覆盖的边框一致)。</summary>
    public double CornerRadius { get; set; } = 4;

    /// <summary>流光最亮处的颜色。</summary>
    public Color Core { get; set; } = Color.FromRgb(0xBD, 0x93, 0xF9);

    /// <summary>流光中段的颜色(由它过渡到 <see cref="Core" />)。</summary>
    public Color Halo { get; set; } = Color.FromRgb(0x8B, 0xE9, 0xFD);

    /// <summary>轨道色:流光之外整圈的底色,也是流光两端要淡回去的那个颜色。</summary>
    public Color Rail { get; set; } = Color.FromRgb(0x44, 0x47, 0x5A);

    /// <summary>当前走过的比例(0–1),供测试观察它确实在动。</summary>
    public double Phase { get; private set; }

    /// <summary>是否正在跑。关掉即停表并擦干净。</summary>
    public bool IsRunning
    {
        get => _timer is not null;
        set
        {
            if (value == IsRunning)
            {
                return;
            }
            if (value)
            {
                Phase = 0;
                _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Tick());
                _timer.Start();
            }
            else
            {
                _timer?.Stop();
                _timer = null;
            }
            InvalidateVisual();
        }
    }

    private void Tick()
    {
        Phase = (Phase + (Interval / Cycle)) % 1.0;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (!IsRunning)
        {
            return;
        }
        // 路径压着边框中线走(1px 边框画在内侧,故内缩半像素),流光就正好骑在边框上
        Rect bounds = new Rect(Bounds.Size).Deflate(0.5);
        if (bounds.Width <= 8 || bounds.Height <= 8)
        {
            return;
        }
        Measure(bounds);

        // 整圈的暗色轨道:盖掉底下那圈(聚焦时正是强调色的)边框,流光才有的可跳,
        // 也给了两端"淡回去"的落点。
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Rail), Weight), BuildPath());

        double head = Phase * _perimeter;
        int steps = Math.Max(2, (int)(CometLength / Step));
        // 先铺一层压暗、加粗的同款渐变当光晕,再压一道细的本色 —— 两层用的是同一条连续曲线,
        // 所以不会出台阶(之前用不同透明度的硬边线叠,就是台阶的来源)。
        for (int i = 0; i < steps; i++)
        {
            double u = (double)i / steps; // 0 = 光头(行进方向那一端),1 = 光尾
            var pen = new Pen(new SolidColorBrush(Ramp(u)), Weight, lineCap: PenLineCap.Round);
            double from = head - (i * Step);
            double to = from - Step;
            // 两道流光恒隔半圈:同一段偏移半个周长再画一次
            context.DrawLine(pen, PointAt(from), PointAt(to));
            context.DrawLine(pen, PointAt(from + (_perimeter / 2)), PointAt(to + (_perimeter / 2)));
        }
    }

    /// <summary>
    /// 流光的颜色曲线,按最初那版渐变的比例复刻:
    /// <c>轨道 →(35%)→ 强调色 →(30%)→ 青 →(35%)→ 轨道</c>。
    /// 三段等宽、两端都从轨道色起步,所以整条带子软到看不出头尾在哪儿断 ——
    /// 这正是它像"光"而不像"线段"的原因。全程只动<b>颜色</b>不动透明度:
    /// 它是融进轨道,而不是浮在上面渐隐。
    /// </summary>
    private Color Ramp(double u) => u switch
    {
        < 0.35 => Lerp(Rail, Core, u / 0.35),
        < 0.65 => Lerp(Core, Halo, (u - 0.35) / 0.30),
        _ => Lerp(Halo, Rail, (u - 0.65) / 0.35)
    };

    private static Color Lerp(Color from, Color to, double t)
    {
        double k = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(from.R + ((to.R - from.R) * k)),
            (byte)(from.G + ((to.G - from.G) * k)),
            (byte)(from.B + ((to.B - from.B) * k)));
    }

    /// <summary>尺寸变了才重算周长与各段长度。</summary>
    private void Measure(Rect bounds)
    {
        if (_bounds == bounds)
        {
            return;
        }
        _bounds = bounds;
        _radius = Math.Clamp(CornerRadius, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        _topRun = bounds.Width - (2 * _radius);
        _sideRun = bounds.Height - (2 * _radius);
        _arcRun = Math.PI * _radius / 2;
        _perimeter = (2 * _topRun) + (2 * _sideRun) + (4 * _arcRun);
    }

    /// <summary>
    /// 路径上走过 <paramref name="distance" /> 像素处的点。
    /// 从<b>左上角</b>起、顺时针绕:上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角。
    /// 起点定在左上角,于是第二道流光(偏移半圈)正好从右下角出发,天然点对称。
    /// </summary>
    private Point PointAt(double distance)
    {
        double s = distance % _perimeter;
        if (s < 0)
        {
            s += _perimeter;
        }
        double x = _bounds.X, y = _bounds.Y, r = _radius;

        if (s < _topRun)
        {
            return new Point(x + r + s, y);
        }
        s -= _topRun;
        if (s < _arcRun)
        {
            return OnArc(x + _bounds.Width - r, y + r, -90, s);
        }
        s -= _arcRun;
        if (s < _sideRun)
        {
            return new Point(_bounds.Right, y + r + s);
        }
        s -= _sideRun;
        if (s < _arcRun)
        {
            return OnArc(x + _bounds.Width - r, y + _bounds.Height - r, 0, s);
        }
        s -= _arcRun;
        if (s < _topRun)
        {
            return new Point(_bounds.Right - r - s, _bounds.Bottom);
        }
        s -= _topRun;
        if (s < _arcRun)
        {
            return OnArc(x + r, y + _bounds.Height - r, 90, s);
        }
        s -= _arcRun;
        if (s < _sideRun)
        {
            return new Point(x, _bounds.Bottom - r - s);
        }
        s -= _sideRun;
        return OnArc(x + r, y + r, 180, s);
    }

    /// <summary>圆角上的点:从 <paramref name="startDegrees" /> 起顺时针走过 <paramref name="run" /> 像素。</summary>
    private Point OnArc(double centreX, double centreY, double startDegrees, double run)
    {
        double angle = ((startDegrees + (run / _arcRun * 90)) * Math.PI) / 180;
        return new Point(centreX + (_radius * Math.Cos(angle)), centreY + (_radius * Math.Sin(angle)));
    }

    /// <summary>整圈轨道的路径(只用来铺底色,起笔点无所谓)。</summary>
    private StreamGeometry BuildPath()
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            var size = new Size(_radius, _radius);
            path.BeginFigure(new Point(_bounds.X + _radius, _bounds.Y), false);
            path.LineTo(new Point(_bounds.Right - _radius, _bounds.Y));
            path.ArcTo(new Point(_bounds.Right, _bounds.Y + _radius), size, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(_bounds.Right, _bounds.Bottom - _radius));
            path.ArcTo(new Point(_bounds.Right - _radius, _bounds.Bottom), size, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(_bounds.X + _radius, _bounds.Bottom));
            path.ArcTo(new Point(_bounds.X, _bounds.Bottom - _radius), size, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(_bounds.X, _bounds.Y + _radius));
            path.ArcTo(new Point(_bounds.X + _radius, _bounds.Y), size, 0, false, SweepDirection.Clockwise);
            path.EndFigure(true);
        }
        return geometry;
    }
}

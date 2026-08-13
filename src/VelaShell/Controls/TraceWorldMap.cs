using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.ViewModels;

namespace VelaShell.Controls;

/// <summary>
/// 链路追踪的简易世界地图:等距圆柱投影,把有归属地的跃点落到经纬网上,并用弧线依次相连。
/// </summary>
/// <remarks>
/// 呈现上刻意区分确定性:起点与终点画实心点 + 实线,中间跳画空心点 + 虚线 ——
/// 骨干路由器的 IP 段登记的是运营商注册地址而非设备实际位置,城市级命中率在独立测试中
/// 不到五成,画成和终点一样确定会误导人。查不到位置的跳直接跳过,不塞到国家中心装作知道。
/// </remarks>
public sealed class TraceWorldMap : Control
{
    /// <summary>跃点集合(<see cref="TraceHopViewModel" />)。</summary>
    public static readonly StyledProperty<IEnumerable?> HopsProperty =
        AvaloniaProperty.Register<TraceWorldMap, IEnumerable?>(nameof(Hops));

    /// <summary>数据版本号;每轮采样递增一次,用来触发重绘。</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<TraceWorldMap, int>(nameof(Revision));

    static TraceWorldMap()
    {
        AffectsRender<TraceWorldMap>(HopsProperty, RevisionProperty);

        // 绕经线的弧会画到图幅之外,不裁就会溢到右侧的列表区上。
        ClipToBoundsProperty.OverrideDefaultValue<TraceWorldMap>(true);
    }

    /// <summary>跃点集合。</summary>
    public IEnumerable? Hops
    {
        get => GetValue(HopsProperty);
        set => SetValue(HopsProperty, value);
    }

    /// <summary>数据版本号。</summary>
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect full = new(Bounds.Size);
        if (full.Width <= 0 || full.Height <= 0)
        {
            return;
        }
        var view = MapView.Fit(full, Hops);
        IBrush land = Brush("VelaTraceLand", Color.FromRgb(0x33, 0x38, 0x50));
        IBrush border = Brush("VelaTraceBorder", Color.FromRgb(0x5C, 0x64, 0x88));
        IBrush labelBg = Brush("VelaTraceLabelBg", Color.FromArgb(0xD2, 0x15, 0x1A, 0x28));
        IBrush grid = Brush("VelaBorderPrimary", Colors.DimGray);
        IBrush muted = Brush("VelaTextMuted", Colors.Gray);
        IBrush route = Brush("VelaTraceLine", Color.FromRgb(0x8B, 0xE9, 0xFD));
        IBrush routeDim = Brush("VelaTraceLineDim", Color.FromRgb(0x4A, 0x7C, 0x8C));
        IBrush error = Brush("VelaError", Colors.IndianRed);
        IBrush labelBrush = Brush("VelaTextSecondary", Colors.LightGray);
        context.FillRectangle(Brush("VelaBgTerminal", Colors.Black), full);

        DrawLand(context, view, land, border);
        DrawGraticule(context, view, grid);

        List<(Point Point, TraceHopViewModel Hop)> located = [];
        if (Hops is { } items)
        {
            foreach (object? item in items)
            {
                if (item is TraceHopViewModel { HasLocation: true } hop)
                {
                    located.Add((view.Project(hop.Latitude, hop.Longitude), hop));
                }
            }
        }
        if (located.Count == 0)
        {
            DrawCentered(context, full, muted, Core.Resources.Strings.Get("Trace_MapNoData"));
            return;
        }

        // 视图已按整条链路居中(见 MapView.Fit),跨太平洋的链路会把地图中心挪到太平洋上,
        // 于是所有跃点都落在同一屏内,不再需要绕边特判 —— 上一版特判的两段弧端点接反了,
        // 画出来是两条横穿整张图的线。
        var solid = new Pen(route, 1.8);
        var dashed = new Pen(routeDim, 1.3) { DashStyle = new DashStyle([4, 3], 0) };
        for (int i = 1; i < located.Count; i++)
        {
            bool last = i == located.Count - 1;
            context.DrawGeometry(null, last ? solid : dashed, Arc(located[i - 1].Point, located[i].Point));
        }

        // 落点相同的连续跃点合成一个标注(骨干里同城好几跳很常见),标签写成 "6-7 中国/Guangzhou"。
        List<Rect> taken = [];
        for (int i = 0; i < located.Count; i++)
        {
            (Point point, TraceHopViewModel hop) = located[i];
            int last = i;
            while (last + 1 < located.Count && Near(located[last + 1].Point, point))
            {
                last++;
            }
            bool endpoint = i == 0 || last == located.Count - 1;
            IBrush fill = hop.IsSuspect ? error : endpoint ? route : Brushes.Transparent;
            var pen = new Pen(hop.IsSuspect ? error : endpoint ? route : routeDim, endpoint ? 2 : 1.2);
            double radius = endpoint ? 5 : 3.5;
            context.DrawEllipse(fill, pen, point, radius, radius);

            string place = hop.LocationText.Length > 0 ? hop.LocationText : hop.Host;
            string number = last > i
                                ? string.Create(CultureInfo.CurrentCulture, $"{located[i].Hop.Ttl}-{located[last].Hop.Ttl}")
                                : located[i].Hop.Ttl.ToString(CultureInfo.CurrentCulture);
            // 带上延迟:地图上最想一眼看出的就是"哪一跳开始变慢",光有地名说明不了问题。
            string latency = located[last].Hop.Average is { Length: > 0 } and not "-"
                                 ? $"  {located[last].Hop.Average}ms"
                                 : string.Empty;
            DrawLabel(context, full, taken, point, $"{number}  {place}{latency}", labelBrush, labelBg, radius);
            i = last;
        }
    }

    /// <summary>两个落点是否近到该合并标注(同城的相邻跃点通常只差几像素)。</summary>
    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 6 && Math.Abs(a.Y - b.Y) < 6;

    /// <summary>
    /// 画跃点标注。同一区域挤满标签会互相盖住,因此逐个尝试上下左右四个位置,
    /// 都被占了就放弃这一条 —— 少一个标签好过糊成一团。
    /// </summary>
    private static void DrawLabel(
        DrawingContext context,
        Rect panel,
        List<Rect> taken,
        Point anchor,
        string text,
        IBrush brush,
        IBrush background,
        double radius
    )
    {
        var label = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            brush
        );
        double gap = radius + 4;
        Point[] candidates =
        [
            new(anchor.X + gap, anchor.Y - (label.Height / 2)),
            new(anchor.X - gap - label.Width, anchor.Y - (label.Height / 2)),
            new(anchor.X - (label.Width / 2), anchor.Y - gap - label.Height),
            new(anchor.X - (label.Width / 2), anchor.Y + gap)
        ];
        foreach (Point candidate in candidates)
        {
            Rect box = new(candidate.X, candidate.Y, label.Width, label.Height);
            if (box.X < panel.X || box.Right > panel.Right || box.Y < panel.Y || box.Bottom > panel.Bottom)
            {
                continue;
            }
            if (taken.Any(existing => existing.Intersects(box)))
            {
                continue;
            }
            // 先垫一层半透明底片再写字:标注经常压在陆地上,直接写字会糊进底图。
            Rect chip = box.Inflate(new Thickness(5, 2));
            context.DrawRectangle(background, null, chip, 3, 3);
            taken.Add(chip.Inflate(2));
            context.DrawText(label, candidate);
            return;
        }
    }

    /// <summary>
    /// 等距圆柱投影的视口:以某个经纬度为中心、按每度多少像素等比缩放。
    /// 经度差会归一到 ±180°,因此把中心放到太平洋上时,跨 180° 经线的链路自然落在同一屏内。
    /// </summary>
    private readonly record struct MapView(Rect Panel, double CenterLon, double CenterLat, double Scale)
    {
        /// <summary>把经纬度投到控件坐标。</summary>
        public Point Project(double latitude, double longitude)
        {
            double dLon = Normalize(longitude - CenterLon);
            return new(
                Panel.Center.X + (dLon * Scale),
                Panel.Center.Y - ((latitude - CenterLat) * Scale)
            );
        }

        /// <summary>把经度差归一到 [-180, 180]。</summary>
        public static double Normalize(double degrees)
        {
            double value = (degrees + 180) % 360;
            return (value < 0 ? value + 360 : value) - 180;
        }

        /// <summary>
        /// 按整条链路自动取景:算出跃点的经纬包围盒(经度用相对中心的差,自动跨越 180° 经线),
        /// 留一圈边距后等比缩放填满面板。没有跃点时退回一个裁掉两极的全球视图 ——
        /// 极区没有网络设施,留着只是上下两条空白带。
        /// </summary>
        public static MapView Fit(Rect panel, IEnumerable? hops)
        {
            List<(double Lat, double Lon)> points = [];
            if (hops is not null)
            {
                foreach (object? item in hops)
                {
                    if (item is TraceHopViewModel { HasLocation: true } hop)
                    {
                        points.Add((hop.Latitude, hop.Longitude));
                    }
                }
            }
            if (points.Count == 0)
            {
                // 全球视图:横向铺满,纵向按需裁掉南北极。
                double worldScale = Math.Max(panel.Width / 360, panel.Height / 150);
                return new(panel, 0, 15, worldScale);
            }

            // 经度中心取单位圆上的向量平均,避免 113°E 与 118°W 被平均成 0°(非洲外海)。
            double sumX = 0, sumY = 0;
            foreach ((_, double lon) in points)
            {
                double radians = lon * Math.PI / 180;
                sumX += Math.Cos(radians);
                sumY += Math.Sin(radians);
            }
            double centerLon = Math.Atan2(sumY, sumX) * 180 / Math.PI;

            double minLon = double.MaxValue, maxLon = double.MinValue;
            double minLat = double.MaxValue, maxLat = double.MinValue;
            foreach ((double lat, double lon) in points)
            {
                double dLon = Normalize(lon - centerLon);
                minLon = Math.Min(minLon, dLon);
                maxLon = Math.Max(maxLon, dLon);
                minLat = Math.Min(minLat, lat);
                maxLat = Math.Max(maxLat, lat);
            }
            double centerLat = (minLat + maxLat) / 2;
            centerLon = Normalize(centerLon + ((minLon + maxLon) / 2));

            // 留边:链路两端不贴着窗口边缘,标签也要地方放。
            double lonSpan = Math.Max(maxLon - minLon, 8) * 1.8;
            double latSpan = Math.Max(maxLat - minLat, 8) * 1.8;
            double scale = Math.Min(panel.Width / lonSpan, panel.Height / latSpan);

            // 下限:不比整幅世界更远;上限:不要放大到只剩几个像素的海岸线。
            scale = Math.Clamp(scale, panel.Width / 360, 12);
            return new(panel, centerLon, Math.Clamp(centerLat, -60, 75), scale);
        }
    }

    /// <summary>两点之间的弧线:向上鼓一个与距离成比例的弧,避免长链路画成一条直线。</summary>
    private static StreamGeometry Arc(Point from, Point to)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false);
            Point mid = new((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            double lift = Math.Min(60, Point.Distance(from, to) * 0.22);
            ctx.QuadraticBezierTo(new(mid.X, mid.Y - lift), to);
            ctx.EndFigure(false);
        }
        return geometry;
    }

    /// <summary>
    /// 画各国轮廓。数据是 Natural Earth 110m 国家边界(公有领域),构建时已转成紧凑的
    /// "经度,纬度 经度,纬度 …" 逐环文本(284 环 / 10630 点 / 128KB),避免运行时解析 GeoJSON。
    /// 用国家而不是整块陆地:逐国填充 + 描边,国境线才画得出来 —— 只用陆地轮廓的话,
    /// 整个欧亚大陆就是一坨没有内部分界的色块。
    /// </summary>
    private void DrawLand(DrawingContext context, MapView view, IBrush fill, IBrush stroke)
    {
        // 省界只在放大到区域尺度后才画:全球视图下它们只是密密麻麻的噪点,还白白多画 5 万个点。
        var pen = new Pen(stroke, 0.6);
        if (view.Scale >= ProvinceDetailScale)
        {
            var thin = new Pen(stroke, 0.4) { LineCap = PenLineCap.Round };
            foreach (Geometry geometry in Cached(view, ProvinceRings.Value, ref _provinceCache))
            {
                context.DrawGeometry(fill, thin, geometry);
            }
            // 省界层已经把陆地铺满,国界层只补一道更重的描边。
            foreach (Geometry geometry in Cached(view, CountryRings.Value, ref _countryCache))
            {
                context.DrawGeometry(null, pen, geometry);
            }
            return;
        }
        foreach (Geometry geometry in Cached(view, CountryRings.Value, ref _countryCache))
        {
            context.DrawGeometry(fill, pen, geometry);
        }
    }

    /// <summary>省界起画的缩放门槛(像素/度)。低于此值是全球/洲际视图,画省界只会糊。</summary>
    private const double ProvinceDetailScale = 2.2;

    private LayerCache? _countryCache;
    private LayerCache? _provinceCache;

    /// <summary>一层边界在某个视图下投影好的几何,视图不变就一直复用。</summary>
    private sealed record LayerCache(MapView View, IReadOnlyList<Geometry> Geometries);

    /// <summary>
    /// 取某一层在当前视图下的几何。视图是值相等的记录,只要取景没变(链路稳定后就不再变),
    /// 每帧都直接复用上次投影的结果 —— 两层加起来 11 万个点,每轮重投一次是不能接受的。
    /// </summary>
    private static IReadOnlyList<Geometry> Cached(MapView view, IReadOnlyList<double[]> rings, ref LayerCache? cache)
    {
        if (cache is { } hit && hit.View == view)
        {
            return hit.Geometries;
        }
        IReadOnlyList<Geometry> built = Build(view, rings);
        cache = new(view, built);
        return built;
    }

    private static List<Geometry> Build(MapView view, IReadOnlyList<double[]> rings)
    {
        List<Geometry> result = [];
        if (rings.Count == 0)
        {
            return result;
        }
        double world = 360 * view.Scale;
        double[] dLon = [];
        foreach (double[] ring in rings)
        {
            // 关键:先把这个环自身的经度"展开"成连续序列。直接对每个点做 ±180° 归一化,
            // 会把跨缝的环从中间撕开,画出来是横贯全图的长条 —— 前一版就是这个症状。
            // 环上相邻两点的经度差本来就很小,逐点累加即可得到不含跳变的连续经度。
            int count = ring.Length / 2;
            if (dLon.Length < count)
            {
                dLon = new double[count];
            }
            dLon[0] = MapView.Normalize(ring[0] - view.CenterLon);
            for (int i = 1; i < count; i++)
            {
                dLon[i] = dLon[i - 1] + MapView.Normalize(ring[i * 2] - ring[(i - 1) * 2]);
            }
            // 视图中心可以在任意经度,一个环因此可能被 ±180° 那条缝切开。上一版在缝处断开
            // 另起一笔,但闭合子路径会给海岸线补上一条直边,南极洲那种横跨整圈的环尤其难看。
            // 改为把同一个环整体平移 -360°/0°/+360° 各画一遍,由裁剪决定谁可见 ——
            // 没有缝,填充也不会破。
            for (int copy = -1; copy <= 1; copy++)
            {
                double shift = copy * world;
                var geometry = new StreamGeometry();
                double minX = double.MaxValue, maxX = double.MinValue;
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    Point start = Plot(view, dLon[0], ring[1], shift);
                    ctx.BeginFigure(start, true);
                    minX = maxX = start.X;
                    for (int i = 1; i < count; i++)
                    {
                        Point point = Plot(view, dLon[i], ring[(i * 2) + 1], shift);
                        minX = Math.Min(minX, point.X);
                        maxX = Math.Max(maxX, point.X);
                        ctx.LineTo(point);
                    }
                    ctx.EndFigure(true);
                }
                // 整体在屏外的副本直接跳过,省掉大部分绘制。
                if (maxX >= view.Panel.X && minX <= view.Panel.Right)
                {
                    result.Add(geometry);
                }
            }
        }
        return result;

        // 用展开后的经度差直接定位,绕过 Project 里的归一化。
        static Point Plot(MapView view, double dLon, double latitude, double shift) =>
            new(
                view.Panel.Center.X + (dLon * view.Scale) + shift,
                view.Panel.Center.Y - ((latitude - view.CenterLat) * view.Scale)
            );
    }

    /// <summary>国界环(Natural Earth 50m,公有领域)。每个环是扁平的 [lon, lat, lon, lat, …]。</summary>
    private static readonly Lazy<IReadOnlyList<double[]>> CountryRings =
        new(() => LoadRings("avares://VelaShell/Assets/world-countries.txt"));

    /// <summary>省/州界环(Natural Earth 50m admin-1)。</summary>
    private static readonly Lazy<IReadOnlyList<double[]>> ProvinceRings =
        new(() => LoadRings("avares://VelaShell/Assets/world-provinces.txt"));

    private static List<double[]> LoadRings(string uri)
    {
        try
        {
            using Stream stream = Avalonia.Platform.AssetLoader.Open(new(uri));
            using StreamReader reader = new(stream);
            List<double[]> rings = [];
            while (reader.ReadLine() is { } line)
            {
                string[] pairs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pairs.Length < 4)
                {
                    continue;
                }
                double[] flat = new double[pairs.Length * 2];
                int n = 0;
                foreach (string pair in pairs)
                {
                    int comma = pair.IndexOf(',', StringComparison.Ordinal);
                    if (comma <= 0
                        || !double.TryParse(pair[..comma], CultureInfo.InvariantCulture, out double lon)
                        || !double.TryParse(pair[(comma + 1)..], CultureInfo.InvariantCulture, out double lat))
                    {
                        continue;
                    }
                    flat[n++] = lon;
                    flat[n++] = lat;
                }
                if (n >= 8)
                {
                    Array.Resize(ref flat, n);
                    rings.Add(flat);
                }
            }
            return rings;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            // 底图缺失只是少了轮廓,经纬网和落点照画。
            return [];
        }
    }

    private static void DrawGraticule(DrawingContext context, MapView view, IBrush brush)
    {
        var pen = new Pen(brush, 0.5);
        Rect panel = view.Panel;
        for (int lon = -180; lon < 180; lon += 30)
        {
            double x = view.Project(0, lon).X;
            if (x >= panel.X && x <= panel.Right)
            {
                context.DrawLine(pen, new(x, panel.Y), new(x, panel.Bottom));
            }
        }
        for (int lat = -60; lat <= 60; lat += 30)
        {
            double y = view.Project(lat, view.CenterLon).Y;
            if (y >= panel.Y && y <= panel.Bottom)
            {
                context.DrawLine(pen, new(panel.X, y), new(panel.Right, y));
            }
        }
    }

    private static void DrawCentered(DrawingContext context, Rect bounds, IBrush brush, string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            brush
        )
        {
            MaxTextWidth = Math.Max(40, bounds.Width - 24),
            TextAlignment = TextAlignment.Center
        };
        context.DrawText(
            formatted,
            new((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2)
        );
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}

using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Localization;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>链路追踪窗口的布局回归:左地图右列表,地图只落有归属地的跃点。</summary>
[TestClass]
[TestCategory("TraceUI")]
public sealed class TraceRouteUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TraceRouteUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Window_PutsTheMapOnTheLeftAndTheHopListOnTheRight()
    {
        OnUi(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            var vm = new TraceRouteViewModel(null);
            vm.PointAt("154.12.41.59", "洛杉矶节点");
            Populate(vm);

            var window = new TraceRouteWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            TraceWorldMap map = window.GetVisualDescendants().OfType<TraceWorldMap>().Single();
            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.IsGreaterThan(0, map.Bounds.Width, "地图没有拿到宽度。");
            // 左地图右列表:地图整体在列表左侧。
            double mapRight = map.TranslatePoint(new(map.Bounds.Width, 0), window)!.Value.X;
            double listLeft = list.TranslatePoint(new(0, 0), window)!.Value.X;
            Assert.IsLessThanOrEqualTo(listLeft, mapRight, "地图应完全位于列表左侧。");
            Assert.HasCount(5, vm.Hops);

            SaveFrame(window, "trace-route.png");
            window.Close();
        });
    }

    [TestMethod]
    public void RegionalRoute_ZoomsInFarEnoughForProvinceDetail()
    {
        OnUi(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            // 一条国内短链路:取景会放大到省界该画出来的尺度。
            var vm = new TraceRouteViewModel(null);
            vm.PointAt("202.97.43.42", "华南节点");
            AddHop(vm, 1, "192.168.1.1", null, null, null);
            AddHop(vm, 2, "202.97.94.114", 39.9042, 116.4074, "中国/北京");
            AddHop(vm, 3, "202.97.43.42", 23.1291, 113.2644, "中国/广州");
            AddHop(vm, 4, "14.147.205.98", 30.5728, 104.0668, "中国/成都");

            var window = new TraceRouteWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.AreEqual(3, vm.Hops.Count(h => h.HasLocation));
            SaveFrame(window, "trace-route-regional.png");
            window.Close();
        });
    }

    [TestMethod]
    public void HopsWithoutLocation_AreNotPlottedOnTheMap()
    {
        OnUi(() =>
        {
            var vm = new TraceRouteViewModel(null);
            Populate(vm);
            // 5 跳里只有 3 跳查到了归属地,其余不该被塞到国家中心装作知道。
            Assert.AreEqual(3, vm.Hops.Count(h => h.HasLocation));
        });
    }

    /// <summary>灌一条有真实经纬度的链路:北京 → 广州 → 洛杉矶,中间夹两跳查不到位置的。</summary>
    private static void Populate(TraceRouteViewModel vm)
    {
        (int Ttl, string Ip, double? Lat, double? Lon, string? Place)[] samples =
        [
            (1, "36.110.203.205", 39.9042, 116.4074, "中国/北京"),
            (2, "10.0.0.1", null, null, null),
            (3, "202.97.43.42", 23.1291, 113.2644, "中国/广州"),
            (4, "23.225.225.28", null, null, null),
            (5, "154.12.41.59", 34.0522, -118.2437, "美国/洛杉矶")
        ];
        foreach ((int ttl, string ip, double? lat, double? lon, string? place) in samples)
        {
            var hop = new TraceHop(ttl);
            hop.Add(new(ttl, IPAddress.Parse(ip), TimeSpan.FromMilliseconds(10 * ttl), ttl == 5, false));
            var row = new TraceHopViewModel(hop);
            row.Update(hop);
            if (lat is { } latitude && lon is { } longitude)
            {
                string[] parts = place!.Split('/');
                row.SetLocation(new(latitude, longitude, parts[1], parts[0], null));
            }
            vm.Hops.Add(row);
        }
    }

    /// <summary>往面板里塞一跳(可带归属地)。</summary>
    private static void AddHop(TraceRouteViewModel vm, int ttl, string ip, double? lat, double? lon, string? place)
    {
        var hop = new TraceHop(ttl);
        hop.Add(new(ttl, IPAddress.Parse(ip), TimeSpan.FromMilliseconds(8 * ttl), false, false));
        var row = new TraceHopViewModel(hop);
        row.Update(hop);
        if (lat is { } latitude && lon is { } longitude)
        {
            string[] parts = place!.Split('/');
            row.SetLocation(new(latitude, longitude, parts[1], parts[0], null));
        }
        vm.Hops.Add(row);
    }

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

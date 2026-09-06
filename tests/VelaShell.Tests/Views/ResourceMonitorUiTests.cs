using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Controls.Controls;
using VelaShell.Core.Localization;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源监视窗口的布局与数据回归:六个页面切换、热力网格拿到核心数、曲线控件真的有尺寸,
/// 以及"未探测到 GPU 时隐藏 GPU 入口"的降级路径。
/// </summary>
[TestClass]
[TestCategory("MonitorUI")]
public sealed partial class ResourceMonitorUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ResourceMonitorUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Overview_ShowsSixCardsAndFeedsEveryChart()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());

            // 滚动窗口只保留最近 60 个采样点,喂 70 次也不会涨。
            Assert.HasCount(60, vm.CpuHistory.Values);
            Assert.IsTrue(vm.IsAvailable, "解析出的指标应判定为可用。");
            Assert.IsTrue(vm.IsOverview, "默认应停在总览页。");
            Assert.IsTrue(vm.HasGpu, "样本里有两张 GPU。");

            // 六张卡片的曲线全部拿到数据,并且控件确实被布局出来(而不是塌成 0 尺寸)。
            TimeSeriesChart[] charts = [.. window.GetVisualDescendants().OfType<TimeSeriesChart>()
                .Where(c => c.Bounds.Width > 0 && c.Bounds.Height > 0)];
            Assert.IsGreaterThan(6, charts.Length, "总览页与左导航的曲线控件没有被布局。");

            // 曲线对象要真的拿到序列数据 —— 只断言控件存在的话,"网格画得出、线画不出"能一路绿到底。
            foreach (TimeSeriesChart chart in charts)
            {
                ChartSeries first = chart.Children.OfType<ChartSeries>().First();
                Assert.IsNotNull(first.Values, "曲线的 Values 绑定没有解析(DataContext 没继承下来)。");
                Assert.IsGreaterThan(1, first.Values!.Count, "曲线点数不足以画线。");
                bool found = window.TryFindResource("VelaShellBlue", out object? probe);
                Assert.IsNotNull(first.Stroke, $"曲线颜色没有解析。窗口能否找到 VelaShellBlue={found}/{probe}, 主题={window.ActualThemeVariant}, 父={first.Parent?.GetType().Name}, 视觉父={first.GetVisualParent()?.GetType().Name}");
                Assert.IsGreaterThan(0, first.Bounds.Width, "曲线子元素没有被布局到图表大小。");
            }

            // 窗口 Opened 会先拉一次,测试再驱动一次 —— 只断言曲线确实在积累数据。
            Assert.IsNotEmpty(vm.CpuHistory.Values);
            Assert.IsNotEmpty(vm.MemoryHistory.Values);
            Assert.IsNotEmpty(vm.NetRxHistory.Values);
            Assert.HasCount(2, vm.Gpus);

            SaveFrame(window, "resource-monitor-overview.png");
            window.Close();
        });
    }

    [TestMethod]
    public void CpuPage_LaysOutHeatGridWithOneCellPerLogicalCore()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());

            vm.SelectPageCommand.Execute("Cpu").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsTrue(vm.IsCpuPage);
            Assert.HasCount(2, vm.CorePercents);

            // 2 核 ≤ 32,按规范默认落在迷你折线视图;这里显式切回热力图再量。
            vm.SetCoreViewCommand.Execute("Heat").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            UsageHeatGrid grid = window.GetVisualDescendants().OfType<UsageHeatGrid>()
                .Single(g => g.Mode == CoreGridMode.HeatMap);
            Assert.IsGreaterThan(0, grid.Bounds.Width, "热力网格没有拿到宽度。");
            Assert.IsGreaterThan(0, grid.Bounds.Height, "热力网格高度应随核心数增长。");

            // 选中一个核心后副标题跟着变(点击固定的交互契约)。
            vm.SelectedCoreIndex = 1;
            Assert.Contains("CPU1", vm.CoreSubtitle);

            SaveFrame(window, "resource-monitor-cpu-heat.png");
            window.Close();
        });
    }

    [TestMethod]
    public void CpuPage_ShowsTheProcessorModelWithoutClippingIt()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Cpu").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.AreEqual("AMD EPYC 9754 96-Core Processor", vm.CpuModelText);
            Assert.IsTrue(vm.HasCpuModel);

            TextBlock model = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == vm.CpuModelText && t.IsEffectivelyVisible);

            // 型号名比卡片窄不了,只能靠换行 —— 单行硬排就会被卡片右边框切掉。
            Border host = model.GetVisualAncestors().OfType<Border>()
                .First(b => b.Classes.Contains("card"));
            double right = model.TranslatePoint(new(model.Bounds.Width, 0), host)!.Value.X;
            Assert.IsLessThanOrEqualTo(
                host.Bounds.Width - host.Padding.Right + 0.5,
                right,
                $"CPU 型号越过了卡片右边界(容器内宽 {host.Bounds.Width - host.Padding.Right:F0},文字右缘 {right:F0})。");
            Assert.IsGreaterThan(0, model.Bounds.Height, "CPU 型号没有排到高度。");

            // 型号下面的机器信息明细不能被挤掉。
            Assert.IsNotEmpty(vm.CpuDetails);

            window.Close();
        });
    }

    [TestMethod]
    public void CoreViews_SwitchBetweenHeatmapSparklineAndList()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Cpu").Subscribe();
            Dispatcher.UIThread.RunJobs();

            // 2 核 ≤ 32:默认迷你折线(规范"核心数 > 32 时自动热力图")。
            Assert.IsTrue(vm.IsSparkView, "少核机器应默认落在迷你折线视图。");

            UsageHeatGrid spark = window.GetVisualDescendants().OfType<UsageHeatGrid>()
                .Single(g => g.Mode == CoreGridMode.Sparkline);
            window.UpdateLayout();
            Assert.IsGreaterThan(0, spark.Bounds.Height, "迷你折线网格没有拿到高度。");
            Assert.IsNotNull(spark.Histories, "迷你折线要拿到逐核心历史。");
            Assert.IsGreaterThan(1, spark.Histories![0].Count, "逐核心历史点数不足以画线。");
            SaveFrame(window, "resource-monitor-cpu-spark.png");

            vm.SetCoreViewCommand.Execute("List").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsTrue(vm.IsListView);
            Assert.HasCount(2, vm.CoreRows);
            Assert.AreEqual("CPU0", vm.CoreRows[0].Label);
            SaveFrame(window, "resource-monitor-cpu-list.png");

            vm.SetCoreViewCommand.Execute("Heat").Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(vm.IsHeatView);

            window.Close();
        });
    }

    [TestMethod]
    public void CoreGrid_GrowsCellsOnFewCoresAndPacksThemOnMany()
    {
        OnUi(() =>
        {
            UseChinese();
            // 少核:格子应该长大填满容器,而不是缩在左上角一小撮。
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Cpu").Subscribe();
            vm.SetCoreViewCommand.Execute("Heat").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            UsageHeatGrid grid = window.GetVisualDescendants().OfType<UsageHeatGrid>()
                .Single(g => g.Mode == CoreGridMode.HeatMap);
            Assert.AreEqual(2, grid.EffectiveColumns, "2 个核心该横排两格,不是竖成一列。");
            Assert.IsGreaterThan(200, grid.EffectiveCellSize.Width, $"格子太窄:{grid.EffectiveCellSize}");
            Assert.IsGreaterThan(100, grid.EffectiveCellSize.Height, $"格子太矮:{grid.EffectiveCellSize}");
            window.Close();

            // 多核:退回密排 + 最小格高,靠滚动容纳,不能把格子压到看不清。
            (ResourceMonitorWindow many, ResourceMonitorWindowViewModel manyVm) =
                Open(WithGpu(), samples: 3, cores: 192);
            manyVm.SelectPageCommand.Execute("Cpu").Subscribe();
            manyVm.SetCoreViewCommand.Execute("Heat").Subscribe();
            Dispatcher.UIThread.RunJobs();
            many.UpdateLayout();

            UsageHeatGrid dense = many.GetVisualDescendants().OfType<UsageHeatGrid>()
                .Single(g => g.Mode == CoreGridMode.HeatMap);
            Assert.HasCount(192, manyVm.CorePercents);
            Assert.IsGreaterThan(6, dense.EffectiveColumns, "192 核该密排,不能只排几列。");
            Assert.IsGreaterThanOrEqualTo(dense.MinCellWidth, dense.EffectiveCellSize.Width);
            Assert.IsGreaterThanOrEqualTo(dense.MinCellHeight, dense.EffectiveCellSize.Height);
            // 放不下就该滚动:内容高度必须超过视口。
            ScrollViewer scroller = dense.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.IsGreaterThan(scroller.Viewport.Height, dense.Bounds.Height, "192 核应该撑出滚动区。");
            many.Close();
        });
    }

    [TestMethod]
    public void CoreSort_ByLoad_PutsTheBusiestCoreFirst()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());

            Assert.IsTrue(vm.IsSortByIndex, "默认按核心号排。");
            Assert.AreEqual("CPU0", vm.CoreLabels[0]);
            double busiest = vm.CorePercents.Max();

            vm.SetCoreSortCommand.Execute("Load").Subscribe();
            Dispatcher.UIThread.RunJobs();

            Assert.IsTrue(vm.IsSortByLoad);
            Assert.AreEqual(busiest, vm.CorePercents[0], 0.001, "按负载排序后第一格应是占用最高的核心。");
            // 标签跟着重排,否则热力图会把 CPU37 的数值标成 CPU0。
            Assert.HasCount(2, vm.CoreLabels);
            Assert.IsGreaterThanOrEqualTo(vm.CorePercents[^1], vm.CorePercents[0]);

            vm.SetCoreSortCommand.Execute("Index").Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("CPU0", vm.CoreLabels[0]);

            window.Close();
        });
    }

    [TestMethod]
    public void GpuPage_ShowsPlaceholderForMetricsTheCardDoesNotExpose()
    {
        OnUi(() =>
        {
            UseChinese();
            // 一台只有 Intel 核显的机器:没有 nvidia-smi,sysfs 也没有 gpu_busy_percent。
            SessionMetrics metrics = SessionMetrics.Parse(
                "__P__\n2\n__L__\n0.5 0.4 0.3 1/200 900\n__M__\n17179869184 4509715660\n" +
                "__GS__\ncard0|0x8086||||45000|12000000|1550000000||\n")!;
            metrics.CorePercents = [12.0, 8.0];
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(metrics, samples: 3);

            Assert.IsTrue(vm.HasGpu, "有 DRM 卡就应显示 GPU 页。");
            Assert.HasCount(1, vm.Gpus);
            GpuCardRow card = vm.Gpus[0];
            Assert.AreEqual(GpuVendor.Intel, card.Vendor);
            Assert.IsFalse(card.HasUtil, "Intel 核显不提供利用率。");
            Assert.AreEqual("—", card.UtilText, "拿不到的指标要显示占位符,不能显示 0%。");
            Assert.AreEqual("—", card.MemText);
            Assert.IsEmpty(card.UtilHistory.Values, "没有利用率就不该往曲线里推 0。");
            Assert.AreEqual("45 °C", card.TempText);

            window.Close();
        });
    }

    [TestMethod]
    public void EveryPage_BecomesVisibleWhenSelected()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());

            foreach ((string page, Func<bool> flag) in new (string, Func<bool>)[]
                     {
                         ("Cpu", () => vm.IsCpuPage),
                         ("Gpu", () => vm.IsGpuPage),
                         ("Memory", () => vm.IsMemoryPage),
                         ("Disk", () => vm.IsDiskPage),
                         ("Network", () => vm.IsNetworkPage),
                         ("Overview", () => vm.IsOverview)
                     })
            {
                vm.SelectPageCommand.Execute(page).Subscribe();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.IsTrue(flag(), $"切到 {page} 页后对应的可见性标志没有置位。");
                SaveFrame(window, $"resource-monitor-{page.ToLowerInvariant()}.png");
            }

            // 每页都要有内容被布局出来,不能切过去是一片空白。
            Assert.IsNotEmpty(vm.Disks);
            Assert.IsNotEmpty(vm.Nics);
            Assert.IsNotEmpty(vm.Partitions);
            Assert.IsNotEmpty(vm.TopMemoryProcesses);

            window.Close();
        });
    }

    [TestMethod]
    public void Overview_WithoutGpu_FallsBackToTwoByTwoWithoutHoles()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithoutGpu(), samples: 3);

            Assert.IsFalse(vm.HasGpu);
            Assert.AreEqual(2, vm.OverviewColumns, "无 GPU 时总览应回落两列。");

            Border[] cards = [.. window.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible)];
            Assert.HasCount(4, cards, "无 GPU 时总览应只剩四张卡。");

            // 2 × 2:两种 X、两种 Y,且四张卡尺寸一致 —— 若隐藏的 GPU 卡仍占着格子,
            // 右侧会空出两块,这里就会量到三种 X 或宽度不齐。
            Assert.HasCount(2, cards.Select(c => Math.Round(c.Bounds.X)).Distinct());
            Assert.HasCount(2, cards.Select(c => Math.Round(c.Bounds.Y)).Distinct());
            Assert.HasCount(1, cards.Select(c => Math.Round(c.Bounds.Width)).Distinct());
            Assert.IsGreaterThan(300, cards[0].Bounds.Width, "两列时每张卡应占到约一半宽度。");

            SaveFrame(window, "resource-monitor-overview-nogpu.png");
            window.Close();
        });
    }

    [TestMethod]
    public void NetworkPage_ListsTopConnectionsAndExplainsWhenTheyAreUnavailable()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Network").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsFalse(vm.ConnectionsUnavailable, "有逐连接速率时不该显示空态。");
            Assert.HasCount(2, vm.TopConnections);
            // 按收发合计降序;拿不到进程名的连接显示占位符而不是空白。
            Assert.Contains(c => c.Process == "—", vm.TopConnections);
            SaveFrame(window, "resource-monitor-network.png");
            window.Close();

            // ss 不可用的机器:列表留空,但要有一句解释。
            (ResourceMonitorWindow bare, ResourceMonitorWindowViewModel bareVm) =
                Open(WithGpu(), samples: 3, connections: false);
            Assert.IsTrue(bareVm.ConnectionsUnavailable);
            Assert.IsEmpty(bareVm.TopConnections);
            bare.Close();
        });
    }

    [TestMethod]
    public void EveryPage_BindsWithoutErrors()
    {
        OnUi(() =>
        {
            UseChinese();
            var sink = new BindingErrorSink();
            ILogSink? previous = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                // 先拿一条必错的绑定验明接收器确实在工作,否则下面的"没有错误"只是空跑。
                var canary = new TextBlock();
                canary.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Missing.Deeper"));
                var probe = new Window { Content = canary, DataContext = new object() };
                probe.Show();
                Dispatcher.UIThread.RunJobs();
                probe.Close();
                Assert.IsNotEmpty(sink.Errors, "绑定错误接收器没有生效,后面的断言会一直是空跑。");
                sink.Errors.Clear();

                // 无 GPU 的主机最容易踩:GPU 页整棵子树的绑定路径上,选中项一直是空的。
                (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithoutGpu(), samples: 2);
                foreach (string page in (string[])["Cpu", "Gpu", "Memory", "Disk", "Network", "Overview"])
                {
                    vm.SelectPageCommand.Execute(page).Subscribe();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                Pump(vm);
                window.UpdateLayout();
                window.Close();
            }
            finally
            {
                Logger.Sink = previous;
            }

            // 选中项为 null 时,编译绑定会对路径上的每一环刷一条错误,每帧几十条 —— 日志直接没法看。
            Assert.IsEmpty(sink.Errors, $"出现了 {sink.Errors.Count} 条绑定错误,例如:{sink.Errors.FirstOrDefault()}");
        });
    }

    /// <summary>只收集绑定相关的告警/错误,供上面的回归测试断言。</summary>
    private sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate + " | " + string.Join(", ", propertyValues));
            }
        }
    }

    [TestMethod]
    public void Legends_PutLabelAndReadingOnOneBaseline()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Memory").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 界面字体与等宽字体的默认行框高度差一两像素:名称与读数各自 VerticalAlignment=Center
            // 时行框中点一样、文字却错开一行像素。统一行高后每个图例项的行框必须完全等高。
            // 标题栏的主机名也挂着 legend 样式,但它不在卡片里,不参与这条规则。
            IReadOnlyList<TextBlock> legends = [.. window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("legend") && t.IsEffectivelyVisible
                            && t.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("card")))];
            Assert.IsGreaterThanOrEqualTo(3, legends.Count, "内存页应当有内存组合图例。");
            double height = legends[0].Bounds.Height;
            foreach (TextBlock item in legends)
            {
                Assert.AreEqual(height, item.Bounds.Height, 0.01, "图例项行框高度不一致,文字会错行。");
            }

            // 同一排里的每一项必须落在同一条水平线上,并且整排左对齐
            //(居中过一版,用户明确要求改回来)。
            foreach (IGrouping<StackPanel, TextBlock> row in legends.GroupBy(RowOf))
            {
                double top = row.First().TranslatePoint(new(0, 0), window)!.Value.Y;
                foreach (TextBlock item in row)
                {
                    Assert.AreEqual(top, item.TranslatePoint(new(0, 0), window)!.Value.Y, 0.01,
                        "图例项没有落在同一条水平线上。");
                }

                Border card = row.Key.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
                double left = row.Key.TranslatePoint(new(0, 0), card)!.Value.X;
                Assert.IsLessThanOrEqualTo(card.Padding.Left + card.BorderThickness.Left + 0.5, left,
                    $"图例没有左对齐(左缘 {left:F0})。");
            }

            // 图例项自己包一层 StackPanel(色块 + 文字),再上一层才是整排;没有内层时就用外层。
            static StackPanel RowOf(TextBlock text)
            {
                StackPanel[] panels = [.. text.GetVisualAncestors().OfType<StackPanel>()];
                return panels.Length > 1 ? panels[1] : panels[0];
            }

            window.Close();
        });
    }

    [TestMethod]
    public void MemoryPage_ShowsEveryProcessColumnAndFillsTheDetailCard()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Memory").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 共享 / 换出两列取自 /proc,ps 给不了 —— 探针少了那一段,这两列会全是占位符。
            ProcessRow top = vm.TopMemoryProcesses[0];
            Assert.AreEqual("2.0 GB", top.SharedText);
            Assert.AreEqual("1.0 GB", top.SwapText);
            Assert.IsTrue(top.PercentText.EndsWith('%'), $"占比列不是百分比:{top.PercentText}");

            // 表头与行的列数必须一致,否则读数会串列。
            IEnumerable<TextBlock> headers = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("col") && t.IsEffectivelyVisible);
            foreach (string column in stringArray)
            {
                Assert.Contains(h => h.Text == column, headers, $"内存页进程表缺少“{column}”列。");
            }

            // 明细行要铺满右卡:最后一行的底边不能离交换分区那块太远,
            // 否则读数全挤在右上角、卡片下半截一片空白。
            IReadOnlyList<TextBlock> keys = [.. window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("key") && t.IsEffectivelyVisible)];
            TextBlock swapKey = keys.Single(t => t.Text == "交换分区");
            TextBlock lastDetail = keys.Where(t => t.Text != "交换分区")
                .OrderByDescending(t => t.TranslatePoint(new(0, 0), window)!.Value.Y).First();
            double gap = swapKey.TranslatePoint(new(0, 0), window)!.Value.Y
                       - lastDetail.TranslatePoint(new(0, lastDetail.Bounds.Height), window)!.Value.Y;
            Assert.IsLessThan(60, gap, $"明细行没有铺满详情卡,离交换分区还差 {gap:F0} px。");

            SaveFrame(window, "resource-monitor-memory.png");
            window.Close();
        });
    }

    [TestMethod]
    public void DiskPage_PinsCapacityToTheCardBottomAndListsFilesystems()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Disk").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Button item = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("item") && b.IsEffectivelyVisible);
            IReadOnlyList<TextBlock> texts = [.. item.GetVisualDescendants().OfType<TextBlock>()];
            TextBlock name = texts.First(t => t.Text == vm.Disks[0].Name);
            TextBlock capacity = texts.First(t => t.Text == vm.Disks[0].CapacityText);

            // 名称贴顶、容量贴底:三行全塞进 StackPanel 时会一起挤在卡片上半截。
            double top = name.TranslatePoint(new(0, 0), item)!.Value.Y;
            double bottom = capacity.TranslatePoint(new(0, capacity.Bounds.Height), item)!.Value.Y;
            Assert.IsLessThan(14, top, $"磁盘名没有贴住卡片顶部(距顶 {top:F0} px)。");
            Assert.IsLessThan(14, item.Bounds.Height - bottom,
                $"容量读数没有贴住卡片底部(距底 {item.Bounds.Height - bottom:F0} px)。");

            // 上半区(磁盘列表 + 吞吐)要比下面的分区表高:磁盘条目是这一页的主角。
            Border listCard = item.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
            Border partCard = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible
                            && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "分区与挂载点"));
            Assert.IsGreaterThan(partCard.Bounds.Height, listCard.Bounds.Height,
                $"上半区没有比分区表高(上 {listCard.Bounds.Height:F0} / 下 {partCard.Bounds.Height:F0})。");

            // 文件系统列取自 df 的 fstype,探针少这一列时整列会是占位符。
            Assert.AreEqual("ext4", vm.Partitions[0].FsType);
            Assert.Contains(
                t => t.Text == "ext4" && t.IsEffectivelyVisible, partCard.GetVisualDescendants().OfType<TextBlock>(),
                "分区表没有显示文件系统类型。");

            window.Close();
        });
    }

    [TestMethod]
    public void DiskItem_KeepsEveryReadingInsideTheCard()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            vm.SelectPageCommand.Execute("Disk").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Button item = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("item") && b.IsEffectivelyVisible);

            // 卡片本身不能撑出所在的"物理磁盘"卡:撑出去的部分会被外层裁掉,
            // 表现为右侧读数只剩半个字。
            Border host = item.GetVisualAncestors().OfType<Border>()
                .First(b => b.Classes.Contains("card"));
            double itemRight = item.TranslatePoint(new(item.Bounds.Width, 0), host)!.Value.X;
            Assert.IsLessThanOrEqualTo(
                host.Bounds.Width - host.Padding.Right,
                itemRight,
                $"磁盘卡片撑出了外层容器(容器内宽 {host.Bounds.Width - host.Padding.Right:F0},卡片右缘 {itemRight:F0})。");

            // 逐个文字量右边界:型号过长时若不截断,它会压过右侧的活动率读数并被卡片边框切掉。
            foreach (TextBlock text in item.GetVisualDescendants().OfType<TextBlock>())
            {
                double right = text.TranslatePoint(new(text.Bounds.Width, 0), item)!.Value.X;
                Assert.IsLessThanOrEqualTo(
                    item.Bounds.Width,
                    right,
                    $"“{text.Text}”越过了卡片右边界(卡片宽 {item.Bounds.Width:F0},文字右缘 {right:F0})。");
                // 排到的宽度不能小于文字想要的宽度,否则字会在自己的框里被切掉半个。
                Assert.IsLessThanOrEqualTo(
                    text.Bounds.Width + 0.5,
                    text.DesiredSize.Width,
                    $"“{text.Text}”排到的宽度不够(想要 {text.DesiredSize.Width:F1},排到 {text.Bounds.Width:F1})。");
            }

            // 卡片必须落在滚动视口内:ScrollViewer.Padding 会让子项按视口宽排布、
            // 却按"视口宽减留白"报告容器宽,子项就被排得比容器还宽。
            ScrollViewer scroller = item.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.IsLessThanOrEqualTo(
                scroller.Viewport.Width,
                item.Bounds.Width,
                $"卡片比滚动视口还宽(视口 {scroller.Viewport.Width:F0},卡片 {item.Bounds.Width:F0}),右侧会被裁掉。");

            window.Close();
        });
    }

    [TestMethod]
    public void WithoutGpu_HidesTheGpuNavigationEntry()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithoutGpu());

            Assert.IsFalse(vm.HasGpu, "样本里没有 nvidia-smi 输出。");
            Assert.IsEmpty(vm.Gpus);

            Button? gpuNav = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("nav") && !b.IsVisible);
            Assert.IsNotNull(gpuNav, "无 GPU 时 GPU 导航项应当整项隐藏。");

            window.Close();
        });
    }

    [TestMethod]
    public void VirtualizedHost_StillShowsTheGpuCardWithPlaceholders()
    {
        OnUi(() =>
        {
            UseChinese();
            // KVM/ESXi/直通未装驱动:实时探针一个数也给不出,只有静态探针在 PCI 上看得见卡。
            SessionStaticInfo info = new()
            {
                GpuCount = 1,
                GpuCards = [new("0000:00:01.0", GpuVendor.Virtual, "", "0000:00:01.0", "virtio-pci")]
            };
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) =
                Open(WithoutGpu(), staticInfo: info);
            vm.SelectPageCommand.Execute("Gpu").Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsTrue(vm.HasGpu, "PCI 上看得见卡时不能把整个 GPU 页藏掉。");
            Assert.HasCount(1, vm.Gpus);
            Assert.AreEqual("虚拟显卡", vm.Gpus[0].Name);
            Assert.AreEqual("—", vm.Gpus[0].UtilText);
            Assert.AreEqual("—", vm.Gpus[0].MemText);

            // 槽位与驱动是这类卡唯一有用的信息,必须落到明细里。
            Assert.Contains(d => d.Value == "0000:00:01.0", vm.SelectedGpu.Details, "明细里没有 PCI 槽位。");
            Assert.Contains(d => d.Value == "virtio-pci", vm.SelectedGpu.Details, "明细里没有内核驱动。");

            window.Close();
        });
    }

    [TestMethod]
    public void StatusBar_ShowsOneIconButtonWiredToTheMonitorCommand()
    {
        OnUi(() =>
        {
            UseChinese();
            var sink = new BindingErrorSink();
            ILogSink? previous = Logger.Sink;
            Logger.Sink = sink;

            var main = new MainWindowViewModel();
            main.StatusBar.CpuUsage = "36%";
            var status = new StatusBarView { DataContext = main.StatusBar };
            // 刻意复现启动时序:视图先挂上去,窗口的 DataContext 后赋值 ——
            // 真实的 MainWindow 就是这个顺序(InitializeComponent 早于 DataContext)。
            var window = new Window { Width = 1200, Height = 40, Content = status };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.DataContext = main;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Logger.Sink = previous;

            // 命令必须挂在状态栏视图模型上:走 $parent[Window].DataContext 的话,
            // 视图加载早于窗口 DataContext 赋值,启动时就会刷一条绑定错误。
            Assert.IsEmpty(sink.Errors, $"状态栏出现绑定错误:{sink.Errors.FirstOrDefault()}");

            // 指标读数已收进弹窗:无后台活动时状态栏右侧只剩这一个可见按钮
            // (后台活动圆环按钮此刻 IsVisible=false,但仍留在视觉树上,故按可见性筛)。
            // 无后台活动时状态栏右侧有两个可见按钮:资源监视(带命令)与编码热切(带 Flyout)。
            // 读数本身仍不在状态栏上 —— 那是这条用例真正要守的东西。
            Button[] buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible)];
            Assert.HasCount(2, buttons, "状态栏右侧应为资源监视 + 编码两个按钮。");
            Assert.IsNotNull(buttons[0].Command, "资源监视按钮没绑到打开命令。");
            Assert.IsNotNull(buttons[1].Flyout, "编码按钮应挂着可选编码的菜单。");
            Assert.IsEmpty(
                window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "36%"),
                "读数不应再出现在状态栏上。");

            // 悬停仍能看到完整详情。
            Assert.Contains("CPU", main.StatusBar.MetricsTooltip);

            window.Close();
            main.StatusBar.Dispose();
        });
    }

    /// <summary>
    /// 后台活动圆环:无活动时整块收起,有活动时出现并给出摘要。
    /// 这条守的是这个功能的全部意义 —— 插件装载那几秒界面上必须有东西在转。
    /// </summary>
    [TestMethod]
    public void StatusBar_BackgroundRing_AppearsOnlyWhileSomethingIsRunning()
    {
        OnUi(() =>
        {
            UseChinese();
            var main = new MainWindowViewModel();
            var status = new StatusBarView { DataContext = main.StatusBar };
            var window = new Window { Width = 1200, Height = 40, Content = status };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.DataContext = main;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 收起时按钮不参与布局,其模板尚未展开 —— 此刻视觉树里连圆环都还没有。
            Assert.IsEmpty(window.GetVisualDescendants().OfType<CircularProgressRing>(),
                "没有后台活动时圆环不该出现在状态栏上。");

            main.StatusBar.ApplyBackgroundActivities([new(1, "正在加载插件", "Redis Client", null)]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            CircularProgressRing ring = Assert.ContainsSingle(
                window.GetVisualDescendants().OfType<CircularProgressRing>());
            Assert.IsTrue(ring.IsEffectivelyVisible, "有后台活动时圆环必须出现。");
            Assert.IsTrue(ring.IsIndeterminate, "进度不可知的活动应让圆环走不确定动画。");
            Assert.Contains(
                t => t.Text == "正在加载插件",
                window.GetVisualDescendants().OfType<TextBlock>(),
                "圆环旁应显示当前活动的摘要。");

            // 确定进度的活动改画实心弧,并按比例填充。
            main.StatusBar.ApplyBackgroundActivities([new(2, "正在校验插件", "Redis Client", 0.4)]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsFalse(ring.IsIndeterminate);
            Assert.AreEqual(0.4, ring.Value);

            main.StatusBar.ApplyBackgroundActivities([]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsFalse(ring.IsEffectivelyVisible, "活动结束后圆环必须收起,不能一直转。");

            window.Close();
            main.StatusBar.Dispose();
        });
    }

    [TestMethod]
    public void Pause_StopsFeedingTheHistoryBuffers()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu());

            int before = vm.CpuHistory.Values.Count;
            vm.IsPaused = true;
            Pump(vm);
            Assert.HasCount(before, vm.CpuHistory.Values, "暂停后不应再追加采样点。");

            vm.IsPaused = false;
            Pump(vm);
            Assert.HasCount(before + 1, vm.CpuHistory.Values, "恢复后应从当前时刻续接。");

            window.Close();
        });
    }

    [TestMethod]
    public void NetworkPage_OnALargeWindow_DetailsBandIsFullWidthAndStaysCompact()
    {
        // 改版前:详情是左边一张定宽卡,窗口放大后被拉高,几行明细在里面上下摊开
        //(用户反馈"太稀疏")。现在它是整行一条属性带,高度只由字段数决定。
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenLarge("Network");
            try
            {
                Border details = CardOf(window, Strings.Get("Monitor_NicDetails"));
                Border connections = CardOf(window, Strings.Get("Monitor_TopConnections"));

                Assert.AreEqual(connections.Bounds.Width, details.Bounds.Width, 1.0,
                    "详情与连接表现在是上下两条整行的带,宽度应当一样。");
                Assert.IsLessThan(220.0, details.Bounds.Height,
                    $"详情带应按内容定高,而不是被窗口高度拉稀(实际 {details.Bounds.Height:F0}px)。");
                Assert.IsGreaterThan(details.Bounds.Bottom, connections.Bounds.Top,
                    "连接表应排在详情带下面。");
                Assert.IsGreaterThan(details.Bounds.Height, connections.Bounds.Height);

                // 曲线与连接表按 2:1 分剩余高度:曲线写死高度时,750 的默认窗口连一行连接都放不下,
                // 最大化后曲线又不跟着长。这里钉住"两者都随窗口长大"这条性质。
                Border chart = CardOf(window, Strings.Get("Monitor_Throughput"));
                Assert.IsGreaterThan(400.0, chart.Bounds.Height,
                    $"1080 高的窗口里曲线应该跟着长(实际 {chart.Bounds.Height:F0}px)。");
                Assert.IsGreaterThan(chart.Bounds.Height / 3, connections.Bounds.Height,
                    "连接表不该被曲线挤没。");

                SaveFrame(window, "resource-monitor-network-large.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void GpuPage_OnALargeWindow_DetailsBandIsFullWidthAndStaysCompact()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenLarge("Gpu");
            try
            {
                Border details = CardOf(window, Strings.Get("Monitor_GpuDetails"));
                Border processes = CardOf(window, Strings.Get("Monitor_GpuProcesses"));

                Assert.AreEqual(processes.Bounds.Width, details.Bounds.Width, 1.0,
                    "GPU 详情与进程表改成上下两行后宽度应当一样。");
                Assert.IsLessThan(220.0, details.Bounds.Height,
                    $"GPU 详情带应按内容定高(实际 {details.Bounds.Height:F0}px)。");
                Assert.IsGreaterThan(details.Bounds.Bottom, processes.Bounds.Top,
                    "进程表应排在详情带下面。");

                SaveFrame(window, "resource-monitor-gpu-large.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void NicDetails_FillInWhatTheProbeKnows_AndNeverShowADash()
    {
        // 用户反馈:网卡详情里还挂着"—"。取不到的字段一律不出行,链路速率这种必出的
        // 字段则给一句人话(虚拟网卡"不适用"、物理网卡读不到才是"未知")。
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenLarge("Network");
            try
            {
                NicRow eth0 = vm.Nics.Single(n => n.Name == "eth0");
                NicRow eth1 = vm.Nics.Single(n => n.Name == "eth1");

                Assert.DoesNotContain(row => row.Value == "—", eth0.Details, "详情里不该再出现占位符。");
                Assert.DoesNotContain(row => row.Value == "—", eth1.Details, "详情里不该再出现占位符。");

                Assert.AreEqual("以太网", Value(eth0, "Monitor_NicType"));
                Assert.AreEqual("ixgbe", Value(eth0, "Monitor_NicDriver"));
                Assert.AreEqual("10 Gbps · 全双工", Value(eth0, "Monitor_NicLink"));
                Assert.AreEqual("2001:db8:1::31/64", Value(eth0, "Monitor_NicIpv6"), "IPv6 是这次新采的字段。");
                Assert.IsNotEmpty(Value(eth0, "Monitor_NicRxTotal"), "累计收发要一次成型,不能先塞占位符。");

                // eth1 走 virtio:sysfs 的 speed 是空的,写"未知"是误导 —— 它根本没有协商速率。
                Assert.AreEqual("虚拟", Value(eth1, "Monitor_NicType"));
                Assert.AreEqual("不适用", Value(eth1, "Monitor_NicLink"));
                // 没有 IPv6 的网卡不出这一行,而不是留个"—"占位。
                Assert.IsNull(eth1.Details.FirstOrDefault(r => r.Key == Strings.Get("Monitor_NicIpv6")));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>取某张网卡详情里某个字段的值;没有该行时返回空串。</summary>
    private static string Value(NicRow row, string key) =>
        row.Details.FirstOrDefault(r => r.Key == Strings.Get(key))?.Value ?? "";

    /// <summary>按卡片标题找到那张卡(标题文本的最近一层 Border.card)。</summary>
    private static Border CardOf(ResourceMonitorWindow window, string title)
    {
        TextBlock label = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == title && t.IsEffectivelyVisible);
        return label.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
    }

    /// <summary>把窗口撑到接近最大化的尺寸再切页 —— "放大后才稀疏"的问题只有大窗口量得出来。</summary>
    private static (ResourceMonitorWindow Window, ResourceMonitorWindowViewModel ViewModel) OpenLarge(string page)
    {
        (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu(), samples: 8, nics: 2);
        window.Width = 1720;
        window.Height = 1080;
        vm.SelectPageCommand.Execute(page).Subscribe();
        Pump(window);
        return (window, vm);
    }

    [TestMethod]
    public void NicStrip_WheelOverTheCards_ScrollsSideways()
    {
        // 横向滚动条没有滚轮就只能拖 —— 用户反馈的两件事之一(另一件是它遮住卡片)。
        StripCase("Network", "NicStrip", nics: 6, body: (window, strip, card) =>
        {
            Point overCard = card.TranslatePoint(new(40, 20), window)!.Value;

            window.MouseWheel(overCard, new(0, -1));
            Pump(window);
            double afterDown = strip.Offset.X;
            Assert.IsGreaterThan(0, afterDown, "滚轮下滚应把网卡卡片条往右带。");

            window.MouseWheel(overCard, new(0, 1));
            Pump(window);
            Assert.IsLessThan(afterDown, strip.Offset.X, "滚轮上滚应往回带。");

            // 到头就别再吞事件:连滚 40 格后停在右端,不越界。
            for (int i = 0; i < 40; i++)
            {
                window.MouseWheel(overCard, new(0, -1));
            }
            Pump(window);
            Assert.AreEqual(strip.Extent.Width - strip.Viewport.Width, strip.Offset.X, 0.5, "滚到底应正好停在右端。");
        });
    }

    [TestMethod]
    public void GpuStrip_WheelOverTheCards_ScrollsSideways()
    {
        StripCase("Gpu", "GpuStrip", gpus: 6, body: (window, strip, card) =>
        {
            window.MouseWheel(card.TranslatePoint(new(40, 20), window)!.Value, new(0, -1));
            Pump(window);
            Assert.IsGreaterThan(0, strip.Offset.X, "滚轮下滚应把显卡卡片条往右带。");
        });
    }

    [TestMethod]
    public void NicStrip_HorizontalScrollBar_DoesNotCoverTheCards()
    {
        // 悬浮式滚动条不占布局高度,会直接压在卡片最后 16px 上(底排的收发读数)。
        // 内容底部留白就是让给它的那一条 —— 留白没了,遮挡立刻回来。
        StripCase("Network", "NicStrip", nics: 6, body: (_, strip, card) =>
            AssertBarClearsCards(strip, card));
    }

    [TestMethod]
    public void GpuStrip_HorizontalScrollBar_DoesNotCoverTheCards()
    {
        StripCase("Gpu", "GpuStrip", gpus: 6, body: (_, strip, card) =>
            AssertBarClearsCards(strip, card));
    }

    /// <summary>横向滚动条的顶边必须落在卡片底边之下,否则它盖住的就是卡片最后一排读数。</summary>
    private static void AssertBarClearsCards(ScrollViewer strip, Button card)
    {
        ScrollBar bar = strip.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Orientation.Horizontal);
        Assert.IsTrue(bar.IsEffectivelyVisible, "卡片超出一屏时应该出现横向滚动条。");

        double barTop = bar.TranslatePoint(default, strip)!.Value.Y;
        double cardBottom = card.TranslatePoint(default, strip)!.Value.Y + card.Bounds.Height;

        Assert.IsGreaterThanOrEqualTo(cardBottom, barTop,
            $"滚动条顶边 {barTop} 压在卡片底边 {cardBottom} 之上,卡片底排读数会被遮掉。");
    }

    /// <summary>切到某一页,取出具名的横向卡片条与第一张卡片,跑完 body 后收摊。</summary>
    private static void StripCase(
        string page, string stripName, Action<ResourceMonitorWindow, ScrollViewer, Button> body,
        int nics = 2, int gpus = 2)
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) =
                Open(WithGpus(gpus), samples: 8, nics: nics);
            vm.SelectPageCommand.Execute(page).Subscribe();
            Pump(window);
            try
            {
                ScrollViewer strip = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(s => s.Name == stripName);
                Button card = strip.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Classes.Contains("item"));

                Assert.IsGreaterThan(strip.Viewport.Width, strip.Extent.Width,
                    "样本里的卡片没有多到超出一屏,量不出横向滚动的行为。");

                body(window, strip, card);
                SaveFrame(window, $"resource-monitor-{stripName.ToLowerInvariant()}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static (ResourceMonitorWindow Window, ResourceMonitorWindowViewModel ViewModel) Open(
        SessionMetrics metrics, int samples = 1, bool connections = true, int cores = 0,
        SessionStaticInfo? staticInfo = null, int nics = 2)
    {
        var vm = new ResourceMonitorWindowViewModel(
            new FakeMetricsService(metrics, connections, cores, staticInfo, nics), Guid.NewGuid(), "web-prod-01");
        var window = new ResourceMonitorWindow { DataContext = vm };
        window.Show();
        for (int i = 0; i < samples; i++)
        {
            Pump(vm);
        }
        window.UpdateLayout();
        return (window, vm);
    }

    /// <summary>把历史缓冲喂满一屏,让曲线真的有形状(截帧校对与滚动窗口回归都靠它)。</summary>
    private static (ResourceMonitorWindow Window, ResourceMonitorWindowViewModel ViewModel) OpenWarm(SessionMetrics metrics) =>
        Open(metrics, samples: 70);

    /// <summary>驱动一次采集并把派发队列跑干净(异步续体是 Post 回 UI 线程的)。</summary>
    private static void Pump(ResourceMonitorWindowViewModel vm)
    {
        _ = vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>带两张 GPU 的样本;逐核心/逐盘/逐网卡的瞬时值按采集器差分后的形态补齐。</summary>
    private static SessionMetrics WithGpu()
    {
        SessionMetrics metrics = SessionMetrics.Parse(FullProbeOutput)!;
        metrics.CorePercents = [42.0, 7.5];
        metrics.Cpu = new(22.0, 9.0, 1.2, 0);
        metrics.NicRates = [new("eth0", 18_400_000, 3_200_000), new("eth1", 600_000, 200_000)];
        metrics.DiskIoRates = [new("nvme0n1", 86_000_000, 24_000_000, 82), new("sda", 1_000_000, 500_000, 12)];
        metrics.HasNetRates = true;
        metrics.NetRxBytesPerSec = 19_000_000;
        metrics.NetTxBytesPerSec = 3_400_000;
        return metrics;
    }

    /// <summary>指定张数的 GPU 样本(卡片条一屏放不下时才量得出横向滚动)。</summary>
    private static SessionMetrics WithGpus(int count)
    {
        if (count == 2)
        {
            return WithGpu();
        }
        string lines = string.Join('\n', Enumerable.Range(0, count).Select(i =>
            $"{i}, NVIDIA A100-SXM4-80GB, GPU-{i}, {12 + (i * 9)}, {5 + i}, {6200 + (i * 900)}, 81920, {43 + i}, 96.00, 400.00, 35, 1215, 1593"));
        SessionMetrics metrics = SessionMetrics.Parse(GpuSectionPattern.Replace(FullProbeOutput, $"__GP__\n{lines}\n"))!;
        metrics.CorePercents = [42.0, 7.5];
        metrics.HasNetRates = true;
        return metrics;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"__GP__\n.*?\n(?=__GA__)", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex GpuSectionPattern { get; }

    /// <summary>没有 nvidia-smi 的主机:GPU 分段为空。</summary>
    private static SessionMetrics WithoutGpu()
    {
        int start = FullProbeOutput.IndexOf("__GP__", StringComparison.Ordinal);
        SessionMetrics metrics = SessionMetrics.Parse(FullProbeOutput[..start])!;
        metrics.CorePercents = [42.0, 7.5];
        return metrics;
    }

    private const string FullProbeOutput =
        "__P__\n2\n" +
        "__L__\n0.96 0.80 0.70 3/1234 5678\n" +
        "__M__\n17179869184 4509715660 8589934592 429496729\n" +
        "__D__\n549755813888 128849018880\n" +
        "__O__\nUbuntu 22.04.4 LTS\n" +
        "__K__\n6.8.0-40-generic\n" +
        "__S__\ncpu  1000 20 300 5000 100 10 5 2\n" +
        "__N__\n1000000 200000\n" +
        "__DL__\n/dev/nvme0n1p2 ext4 549755813888 128849018880 /\n/dev/sda1 xfs 1099511627776 549755813888 /data\n" +
        "__C__\ncpu0 500 10 150 2500 50 5 2 1\ncpu1 500 10 150 2500 50 5 3 1\n" +
        "__NI__\neth0 900000 180000\neth1 100000 20000\n" +
        // WiFi 那张读不到 speed(空字段)但载波已建立,必须判为已连接。
        "__NF__\neth0|b4:2e:99:0c:1a:77|9000|10000|up|1|full|0|0|0|0\neth1|b4:2e:99:0c:1a:78|1500||dormant|1|full|12|0|0|0\n" +
        "__IP__\nlo 127.0.0.1/8\neth0 10.0.2.31/24\neth1 192.168.124.192/24\n" +
        "__I6__\neth0 2001:db8:1::31/64\n" +
        "__MI__\nMemAvailable: 8000000\nBuffers: 500000\nCached: 3000000\nSReclaimable: 200000\nShmem: 100000\nDirty: 12000\n" +
        "__IO__\nnvme0n1|200000|100000|5000\nsda|50000|25000|1200\n" +
        "__CX__\n987654321\n" +
        "__UT__\n3115977.42\n" +
        "__FQ__\n3740\n" +
        "__PC__\n412\n" +
        "__GP__\n0, NVIDIA A100-SXM4-80GB, GPU-aaa, 94, 57, 68400, 81920, 71, 382.13, 400.00, [N/A], 1410, 1593\n" +
        "1, NVIDIA A100-SXM4-80GB, GPU-bbb, 12, 5, 6200, 81920, 43, 96.00, 400.00, 35, 1215, 1593\n" +
        "__GA__\nGPU-aaa, 7104, python, 62100\n" +
        // 进程表取 20 行:少了列表下半截空着,这里给足行数才量得出滚动与列宽。
        "__TM__\n 7104  94.0 65011712 python train.py --ddp\n 2481  11.8 15518924 postgres: checkpointer\n" +
        " 3902   4.2  9856432 java -Xmx8g -jar api.jar\n 1204   2.1  6501171 redis-server *:6379\n" +
        " 4517  18.6  5033164 clickhouse-server\n  881   0.9  1258291 nginx: worker process\n" +
        " 5730   1.4   943718 node /srv/gateway/index.js\n 6011   0.7   786432 python3 -m celery worker\n" +
        " 2290   0.5   655360 /usr/bin/containerd\n 1099   0.4   524288 /usr/bin/dockerd\n" +
        " 7788   0.3   393216 prometheus --config.file=/etc/prometheus.yml\n" +
        " 8123   0.2   262144 grafana-server\n" +
        "__TP__\n7104|2147483648|1073741824\n2481|536870912|0\n3902|629145600|0\n1204|104857600|0\n" +
        "4517|314572800|0\n881|419430400|0\n5730|104857600|0\n6011|83886080|\n2290|52428800|0\n" +
        "1099|41943040|0\n7788|31457280|0\n8123|20971520|0\n";
    private static readonly string[] stringArray = ["常驻内存", "占比", "共享", "交换", "CPU"];

    /// <summary>
    /// 采集服务替身(实现真实接口,不伪造生产类型)。每次调用在基准快照上叠一层确定性游走,
    /// 这样曲线会有真实形状 —— 固定值只能证明"没崩",证明不了图画对了。
    /// </summary>
    private sealed class FakeMetricsService(
        SessionMetrics metrics, bool connections = true, int cores = 0, SessionStaticInfo? staticInfo = null,
        int nics = 2)
        : ISessionMetricsService
    {
        private int _tick;

        public Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            GetMetricsAsync(sessionId, MetricsScope.Basic, cancellationToken);

        public Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, MetricsScope scope, CancellationToken cancellationToken = default)
        {
            int tick = _tick++;
            double wave = (Math.Sin(tick / 4.0) + 1) / 2; // 0-1 的确定性波形
            metrics.CpuPercent = 18 + (wave * 40);
            metrics.NetRxBytesPerSec = 6_000_000 + (wave * 24_000_000);
            metrics.NetTxBytesPerSec = 1_000_000 + ((1 - wave) * 8_000_000);
            metrics.Cpu = new(12 + (wave * 26), 4 + (wave * 8), 0.4 + wave, 0);
            metrics.CorePercents = [.. Enumerable.Range(0, cores > 0 ? cores : metrics.CoreCounters.Count)
                .Select(i => Math.Clamp((wave * 90) + (i * 17 % 30), 0, 100))];
            metrics.DiskIoRates =
            [
                new("nvme0n1", 20_000_000 + (wave * 160_000_000), 8_000_000 + ((1 - wave) * 70_000_000), 30 + (wave * 60)),
                new("sda", 1_000_000 * wave, 500_000, 12 * wave)
            ];
            // 网卡张数可调:横向卡片条的滚动回归要靠"多到一屏放不下"才量得出来。
            metrics.NicRates =
            [
                .. Enumerable.Range(0, nics).Select(i =>
                    new NetInterfaceRate($"eth{i}", 6_000_000 + (wave * 22_000_000) - (i * 500_000), 900_000 + ((1 - wave) * 6_000_000)))
            ];
            metrics.ConnectionRates = connections
                ? [
                    new("10.0.4.19:5432", "postgres", 2_000_000 + (wave * 7_000_000), 400_000),
                    new("203.0.113.7:443", "", 4_200_000 * wave, 1_900_000)
                ]
                : null;
            metrics.HasNetRates = true;
            return Task.FromResult<SessionMetrics?>(metrics);
        }

        public Task<SessionStaticInfo?> GetStaticInfoAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionStaticInfo?>(staticInfo ?? new SessionStaticInfo
            {
                CpuModel = "AMD EPYC 9754 96-Core Processor",
                Sockets = 1,
                CoresPerSocket = 96,
                ThreadsPerCore = 2,
                MaxMhz = 3710,
                Disks =
                [
                    new("nvme0n1", "SAMSUNG MZQL23T8HCLS", 3_840_755_982_336, false, "nvme"),
                    new("sda", "ST16000NM000J", 16_000_900_661_248, true, "sata")
                ],
                GpuDriver = "550.90.07",
                GpuCount = 2,
                // eth0 是真物理网卡(sysfs 有速率),eth1 走 virtio —— 它没有"链路速率"这回事,
                // 详情里该写"不适用"而不是一个"—"。
                Nics =
                [
                    new("eth0", 1, "ixgbe", false, true),
                    new("eth1", 1, "virtio_net", false, true)
                ]
            });
    }

    private static void UseChinese()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        _localization.SetLanguage("zh-CN");
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

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
}

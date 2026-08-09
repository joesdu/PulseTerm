using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Processes;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 任务管理器窗口的真实布局回归测试。这里守的是三条从截图里发现的问题:
/// 无边框窗口必须自带缩放抓取区、表头与数据行的列宽必须逐列一致、概览条的标签
/// 不能被挤成零宽(那次表现为界面上只剩两条没有标题的进度条)。
/// </summary>
[TestClass]
[TestCategory("ProcessManagerUI")]
public sealed class ProcessManagerUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ProcessManagerUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Window_IsResizable_AndCarriesItsOwnResizeGrips()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            // 无边框窗口没有系统边框可拖:少了这层 Panel,窗口就完全不能缩放。
            Assert.IsTrue(fixture.Window.CanResize);
            Panel grips = fixture.Window.GetVisualDescendants()
                .OfType<Panel>()
                .Single(panel => panel.Name == "ResizeGrips");
            Assert.IsTrue(grips.IsVisible);
            Assert.HasCount(8, grips.Children.OfType<Border>().ToList());
        });
    }

    [TestMethod]
    public void Window_DrawsItsOwnRoundedCard_LikeEveryOtherDialog()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            // 圆角靠自己画:窗口透明,里面是一张 8px 圆角卡片。指望 DWM 给被拥有的
            // 弹出窗加圆角是不成立的,那正是这个窗口曾经四角发方的原因。
            Border card = fixture.Find<Border>("RootCard");
            Assert.AreEqual(new CornerRadius(8), card.CornerRadius);
            Assert.IsTrue(card.ClipToBounds, "内容必须裁到圆角内,否则表格会画到圆角外面。");
        });
    }

    [TestMethod]
    public void Gauges_CarryDistinctColourClasses()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            ProgressBar cpu = fixture.Find<ProgressBar>("CpuGauge");
            ProgressBar memory = fixture.Find<ProgressBar>("MemoryGauge");
            Assert.IsTrue(cpu.Classes.Contains("cpu"));
            Assert.IsTrue(memory.Classes.Contains("mem"));
            Assert.AreNotEqual(
                (cpu.Foreground as ISolidColorBrush)?.Color,
                (memory.Foreground as ISolidColorBrush)?.Color,
                "CPU 与内存必须用不同色相区分。"
            );

            // 12.5% / 21.5% 都在阈值之下,不该染成告警色。
            Assert.IsFalse(cpu.Classes.Contains("warn"));
            Assert.IsFalse(cpu.Classes.Contains("crit"));
        });
    }

    [TestMethod]
    public void Gauge_TurnsCriticalAboveNinetyPercent()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            ProgressBar cpu = fixture.Find<ProgressBar>("CpuGauge");
            IBrush? normal = cpu.Foreground;

            fixture.Service.CpuPercent = 96;
            fixture.ViewModel.RefreshAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();

            Assert.IsTrue(fixture.ViewModel.IsCpuCritical);
            Assert.IsTrue(cpu.Classes.Contains("crit"));
            Assert.AreNotEqual(
                (normal as ISolidColorBrush)?.Color,
                (cpu.Foreground as ISolidColorBrush)?.Color
            );
        });
    }

    [TestMethod]
    public void HeaderAndRows_ShareTheSameColumnWidths()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            Grid header = fixture.Find<Grid>("HeaderColumns");
            Grid row = fixture.Window.GetVisualDescendants()
                .OfType<Grid>()
                .First(grid => grid.Name == "RowColumns");

            Assert.AreEqual(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
            for (int i = 0; i < header.ColumnDefinitions.Count; i++)
            {
                Assert.AreEqual(
                    header.ColumnDefinitions[i].Width,
                    row.ColumnDefinitions[i].Width,
                    $"第 {i} 列的表头与数据行列宽不一致,整张表会错位。"
                );
            }
        });
    }

    [TestMethod]
    public void NumericHeaders_AreRightAligned_LikeTheirCells()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            // 数字靠右、表头靠左是最初的表现;两者必须对齐到同一条边。
            List<Button> numeric = [.. fixture.Window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("col-header") && button.Classes.Contains("num"))];

            Assert.HasCount(5, numeric); // PID / CPU / 内存 / 线程 / 运行时长
            foreach (Button button in numeric)
            {
                Assert.AreEqual(HorizontalAlignment.Right, button.HorizontalContentAlignment);
            }
        });
    }

    [TestMethod]
    public void SummaryBar_KeepsItsLabelsVisible()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            TextBlock cpuLabel = fixture.Find<TextBlock>("CpuSummaryLabel");
            Assert.IsTrue(cpuLabel.IsVisible);
            Assert.IsGreaterThan(0, cpuLabel.Bounds.Width, "概览条的 CPU 标签被挤成了零宽。");
            Assert.IsNotEmpty(cpuLabel.Text ?? string.Empty);
        });
    }

    [TestMethod]
    public void SummaryBar_CentersTheGaugesOnTheSameLineAsItsLabels()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            TextBlock label = fixture.Find<TextBlock>("CpuSummaryLabel");
            ProgressBar gauge = fixture.Find<ProgressBar>("CpuGauge");
            double labelCenter = Center(fixture.Window, label);
            double gaugeCenter = Center(fixture.Window, gauge);

            Assert.AreEqual(
                labelCenter,
                gaugeCenter,
                0.6,
                $"占用指示条与标签没有水平居中对齐:标签中心 {labelCenter:F2},指示条中心 {gaugeCenter:F2}。"
            );

            // 控件盒子居中还不够:Fluent 的 ProgressBar 模板里,真正可见的轨道是一个内层
            // Border,它若没在盒子里居中,视觉上就是偏的 —— 用户看到的是轨道,不是盒子。
            foreach (Border track in gauge.GetVisualDescendants().OfType<Border>())
            {
                double trackCenter = Center(fixture.Window, track);
                Assert.AreEqual(
                    labelCenter,
                    trackCenter,
                    0.6,
                    $"指示条内部轨道偏离中心:标签中心 {labelCenter:F2},轨道中心 {trackCenter:F2},轨道高 {track.Bounds.Height:F2},盒高 {gauge.Bounds.Height:F2}。"
                );
            }
        });
    }

    /// <summary>控件在窗口坐标系里的垂直中心。</summary>
    private static double Center(Visual root, Visual control) =>
        control.TranslatePoint(new(0, control.Bounds.Height / 2), root)?.Y ?? double.NaN;

    [TestMethod]
    public void Rows_RenderTheSampledProcesses()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            // 内核线程默认不显示,所以 4 个样本里只出 3 行。
            Assert.HasCount(3, fixture.ViewModel.Processes);
            SaveFrame(fixture.Window, "process-manager-tree.png");

            fixture.ViewModel.ShowTree = false;
            Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();
            SaveFrame(fixture.Window, "process-manager-flat.png");
        });
    }

    /// <summary>建窗口、灌一份样本数据、渲染一帧的公共夹具。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

        private Fixture(ProcessManagerView window, ProcessManagerViewModel viewModel, StubProcessService service)
        {
            Window = window;
            ViewModel = viewModel;
            Service = service;
        }

        public ProcessManagerView Window { get; }

        public ProcessManagerViewModel ViewModel { get; }

        public StubProcessService Service { get; }

        public static Fixture Show()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            var service = new StubProcessService();
            var viewModel = new ProcessManagerViewModel(service, Guid.NewGuid(), "生产数据库");
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            var window = new ProcessManagerView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return new(window, viewModel, service);
        }

        public T Find<T>(string name)
            where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

        public void Dispose()
        {
            Window.Close();
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }

    internal sealed class StubProcessService : IRemoteProcessService
    {
        public double CpuPercent { get; set; } = 12.5;

        public Task<RemoteProcessSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteProcessSnapshot?>(new()
            {
                CpuCores = 4,
                CpuPercent = CpuPercent,
                MemTotalBytes = 1024L * 1024 * 1024,
                MemUsedBytes = 220L * 1024 * 1024,
                UptimeSeconds = 3600,
                Processes =
                [
                    new()
                    {
                        Pid = 1,
                        ParentPid = 0,
                        Name = "systemd",
                        CommandLine = "/sbin/init splash",
                        User = "root",
                        State = "Ss",
                        Threads = 1,
                        MemoryBytes = 13L * 1024 * 1024,
                        MemoryPercent = 1.3,
                        ElapsedSeconds = 3600,
                        CpuPercent = 0.4
                    },
                    new()
                    {
                        Pid = 1337,
                        ParentPid = 1,
                        Name = "java",
                        CommandLine = "/usr/bin/java -Xmx2g -jar /opt/app.jar",
                        User = "www-data",
                        State = "Rl",
                        Threads = 33,
                        MemoryBytes = 962L * 1024 * 1024,
                        MemoryPercent = 12.5,
                        ElapsedSeconds = 98_765,
                        CpuPercent = 42.7
                    },
                    new()
                    {
                        Pid = 2201,
                        ParentPid = 1337,
                        Name = "sh",
                        CommandLine = "/bin/sh -c /opt/health-check.sh",
                        User = "www-data",
                        State = "S",
                        Threads = 1,
                        MemoryBytes = 3L * 1024 * 1024,
                        MemoryPercent = 0.3,
                        ElapsedSeconds = 41,
                        CpuPercent = 1.2
                    },
                    new()
                    {
                        Pid = 2048,
                        ParentPid = 2,
                        Name = "[kworker/0:1H]",
                        CommandLine = "[kworker/0:1H]",
                        User = "root",
                        State = "I<",
                        Threads = 1
                    }
                ]
            });

        public Task<RemoteCommandOutcome> SignalAsync(
            Guid sessionId,
            IReadOnlyList<int> pids,
            ProcessSignal signal,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new RemoteCommandOutcome(true, string.Empty));

        public Task<RemoteCommandOutcome> ReniceAsync(
            Guid sessionId,
            int pid,
            int niceness,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new RemoteCommandOutcome(true, string.Empty));

        public void ResetBaseline(Guid sessionId) { }
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
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

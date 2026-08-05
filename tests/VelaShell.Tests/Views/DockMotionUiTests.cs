using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("DockMotionUi")]
public sealed class DockMotionUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockMotionUiTests).Assembly);

    [TestMethod]
    public void ContentSwitch_UpdatesTargetImmediately_AndRetainsViewIdentity()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var first = new TestDocument("first");
            var second = new TestDocument("second");
            workspace.AddDocument(first);
            workspace.AddDocument(second);

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 640, Height = 360, Content = dock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ReparentingHost host = dock.GetVisualDescendants().OfType<ReparentingHost>().Single();
            Control secondView = second.View ?? throw new AssertFailedException("Second document view was not created.");
            Assert.AreSame(secondView, host.Target);
            Assert.AreSame(host.Target, host.Child);
            AssertDockContentMotion(host);
            SaveOptionalFrame(window, "dock-motion-second-active.png");

            workspace.ActivateDocument(first);
            Assert.AreSame(first.View, host.Target);
            Assert.AreSame(host.Target, host.Child);
            Assert.IsTrue(host.Classes.Contains("settling"));
            Assert.IsNull(host.Transitions);
            // 落定起点刻意非 0:从全黑淡入会让每次切标签都像眨眼(0.65 = 立即可见 + 一丝浮起)。
            Assert.AreEqual(0.65, host.Opacity);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsFalse(host.Classes.Contains("settling"));
            Assert.IsNotNull(host.Transitions);
            SaveOptionalFrame(window, "dock-motion-first-active.png");
            workspace.ActivateDocument(second);
            workspace.ActivateDocument(first);
            Assert.AreSame(first.View, host.Target);
            Assert.AreSame(host.Target, host.Child);
            Assert.IsTrue(host.Classes.Contains("settling"));
            Assert.IsNull(host.Transitions);
            Assert.AreNotSame(secondView, first.View);
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(host.Classes.Contains("settling"));
            Assert.IsNotNull(host.Transitions);
            Assert.HasCount(2, host.Transitions);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ActiveTabIndicator_AlignsToActiveTab_AndFollowsActivation()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var first = new TestDocument("first-tab");
            var second = new TestDocument("second-tab");
            workspace.AddDocument(first);
            workspace.AddDocument(second);

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 640, Height = 360, Content = dock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            DockGroupControl groupControl = dock.GetVisualDescendants().OfType<DockGroupControl>().Single();
            Border indicator = groupControl.GetVisualDescendants().OfType<Border>()
                                           .Single(border => border.Name == "ActiveTabIndicator");
            ItemsControl tabs = groupControl.GetVisualDescendants().OfType<ItemsControl>()
                                            .Single(items => items.Name == "TabsHost");

            // 激活标签的滑动强调线必须可见,且几何对齐激活标签(取代逐标签顶线)。
            Assert.IsTrue(indicator.IsVisible);
            AssertIndicatorAlignedTo(indicator, tabs, workspace.ActiveDocument!);

            DockDocument other = ReferenceEquals(workspace.ActiveDocument, second) ? first : second;
            workspace.ActivateDocument(other);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertIndicatorAlignedTo(indicator, tabs, other);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void AssertIndicatorAlignedTo(Border indicator, ItemsControl tabs, DockDocument document)
    {
        // 读基值(过渡目标)而非属性现值:切换后位置/宽度经 180ms 过渡滑动,
        // 现值是动画中间值,基值才是应当停下的终点 —— 断言与真实时间彻底解耦。
        Control container = tabs.ContainerFromItem(document)
            ?? throw new AssertFailedException("Active tab has no realized container.");
        Visual panel = indicator.GetVisualParent()!;
        Point origin = container.TranslatePoint(default, panel) ?? default;
        double actualX = indicator.GetBaseValue(Visual.RenderTransformProperty).GetValueOrDefault()?.Value.M31 ?? -1;
        double actualWidth = indicator.GetBaseValue(Layoutable.WidthProperty).GetValueOrDefault(double.NaN);
        Assert.AreEqual(Math.Round(origin.X), actualX, 0.6, "滑动强调线应与激活标签左缘对齐。");
        Assert.AreEqual(Math.Round(container.Bounds.Width), actualWidth, 0.6, "滑动强调线宽度应等于激活标签宽度。");
    }

    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
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

    private static void AssertDockContentMotion(ReparentingHost host)
    {
        Assert.IsNotNull(host.Transitions);
        Assert.HasCount(2, host.Transitions);
        Assert.Contains(transition =>
            transition is DoubleTransition { Property: var property, Duration: var duration }
            && property == Visual.OpacityProperty
            && duration == TimeSpan.FromMilliseconds(140), host.Transitions);
        Assert.Contains(transition =>
            transition is TransformOperationsTransition { Property: var property, Duration: var duration }
            && property == Visual.RenderTransformProperty
            && duration == TimeSpan.FromMilliseconds(140), host.Transitions);
    }

    private sealed class TestDocument : DockDocument, IDockViewProvider
    {
        public TestDocument(string title) => Title = title;

        public Control? View { get; private set; }

        public Control CreateView() => View ??= new Border { Background = Brushes.Transparent };
    }
}

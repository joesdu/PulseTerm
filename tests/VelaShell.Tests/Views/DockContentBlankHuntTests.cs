using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Views;

/// <summary>
/// 2026-07-23 "点击回终端后内容消失"回归猎捕:穷举激活/切换/重挂序列,
/// 断言每一步之后内容宿主的不变量(基值透明度=1、无 settling 残留、Child=激活视图、有效可见)。
/// 任何序列违反不变量即为确定性罪证。
/// </summary>
[TestClass]
[TestCategory("DockBlankHunt")]
public sealed class DockContentBlankHuntTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockContentBlankHuntTests).Assembly);

    [TestMethod]
    public void ActivationSequences_NeverLeaveContentHostInvisible()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var docA = new TestDocument("alpha");
            var docB = new TestDocument("beta");
            workspace.AddDocument(docA);
            workspace.AddDocument(docB);

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 800, Height = 480, Content = dock };
            window.Show();
            Pump(window);

            ReparentingHost host = dock.GetVisualDescendants().OfType<ReparentingHost>().Single();
            AssertVisible(host, docB, "初始状态(后加入者激活)");

            // 序列 1:普通来回切换。
            workspace.ActivateDocument(docA);
            Pump(window);
            AssertVisible(host, docA, "A←B 切换后");
            workspace.ActivateDocument(docB);
            Pump(window);
            AssertVisible(host, docB, "B←A 切换后");

            // 序列 2:重复激活当前文档(点击回终端内容区走的正是这条:OnAnyPointerPressed
            // → workspace.ActivateDocument(当前激活文档))。
            workspace.ActivateDocument(docB);
            workspace.ActivateDocument(docB);
            Pump(window);
            AssertVisible(host, docB, "重复激活当前文档后");

            // 序列 3:同一帧内连续切换(双 PropertyChanged,考验动效代次守卫),
            // 中间不跑任何布局/派发。
            workspace.ActivateDocument(docA);
            workspace.ActivateDocument(docB);
            workspace.ActivateDocument(docA);
            Pump(window);
            AssertVisible(host, docA, "同帧三连切换后");

            // 序列 4:整棵 dock 从窗口摘下再挂回(标签拖拽/布局重建的路径)。
            window.Content = null;
            Pump(window);
            window.Content = dock;
            Pump(window);
            ReparentingHost rehost = dock.GetVisualDescendants().OfType<ReparentingHost>().Single();
            AssertVisible(rehost, docA, "整树摘下重挂后");

            // 序列 5:重挂后立即切换再切回。
            workspace.ActivateDocument(docB);
            Pump(window);
            workspace.ActivateDocument(docA);
            Pump(window);
            AssertVisible(rehost, docA, "重挂后再切换回 A");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertVisible(ReparentingHost host, TestDocument active, string stage)
    {
        Assert.AreSame(active.View, host.Target, $"{stage}:Target 应为激活文档的视图。");
        Assert.AreSame(host.Target, host.Child, $"{stage}:视图必须已被收养为 Child。");
        Assert.IsFalse(host.Classes.Contains("settling"), $"{stage}:settling 类残留(透明度会卡在 0.65)。");
        Assert.IsNotNull(host.Transitions, $"{stage}:Transitions 未恢复(说明动效收尾回调没跑)。");
        double baseOpacity = host.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault(host.Opacity);
        Assert.AreEqual(1.0, baseOpacity, 0.001, $"{stage}:内容宿主基值透明度必须回到 1。");
        Assert.IsTrue(host.IsEffectivelyVisible, $"{stage}:内容宿主必须有效可见。");
        Assert.IsTrue(active.View!.IsEffectivelyVisible, $"{stage}:激活视图必须有效可见。");
    }

    private sealed class TestDocument : DockDocument, IDockViewProvider
    {
        public TestDocument(string title) => Title = title;

        public Control? View { get; private set; }

        public Control CreateView() => View ??= new Border { Background = Brushes.Transparent, MinHeight = 10, MinWidth = 10 };
    }
}

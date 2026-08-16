using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.Services.Plugins;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Plugins")]
public sealed class PluginPanelUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginPanelUiTests).Assembly);

    private sealed class NullLog : IPluginLogger
    {
        public void Write(PluginLogLevel level, string message, Exception? exception = null) { }
    }

    /// <summary>占位标签(测试只关心它占着主组这件事)。</summary>
    private sealed class TestDocument : DockDocument;

    // ---- 停靠文档形态(进程内插件的原生 Avalonia 内容) ----

    [TestMethod]
    public void DocumentPanel_HostsNativeControl_AndUserCloseRaisesClosed()
    {
        _session.Dispatch(async () =>
        {
            var workspace = new DockWorkspace();
            var content = new Border { Tag = "plugin-content" };
            var panel = new PluginPanel("acme.demo", new NullLog(),
                new() { Title = "Demo" }, content, workspace);

            PluginDocument document = workspace.AllDocuments().OfType<PluginDocument>().Single();
            Assert.AreEqual("Demo", document.Title);
            Assert.AreEqual("acme.demo", document.PluginId);
            // 内容 = 插件自建控件原物(不包壳、不复制)。
            Assert.AreSame(content, document.CreateView());
            Assert.IsTrue(panel.IsOpen);

            // 用户语义关闭 → 面板感知并进入关闭态。
            bool closedRaised = false;
            panel.Closed += () => closedRaised = true;
            workspace.CloseDocument(document);
            Assert.IsFalse(panel.IsOpen);
            Assert.IsEmpty(workspace.AllDocuments());
            // Closed 在后台线程分发,给它一点时间。
            for (int i = 0; i < 100 && !closedRaised; i++)
            {
                await Task.Delay(10);
            }
            Assert.IsTrue(closedRaised);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void DocumentPanel_ProgrammaticClose_RemovesDocumentSilently()
    {
        _session.Dispatch(async () =>
        {
            var workspace = new DockWorkspace();
            bool userClosed = false;
            workspace.DocumentClosed += _ => userClosed = true;
            var panel = new PluginPanel("acme.demo", new NullLog(),
                new() { Title = "Demo" }, new Border(), workspace);

            await panel.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(panel.IsOpen);
            Assert.IsEmpty(workspace.AllDocuments());
            Assert.IsFalse(userClosed, "程序性关闭必须走 RemoveDocument(静默),不得触发用户语义的 DocumentClosed");
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// <see cref="PanelPlacement.Right" />:面板落在标签区最右侧独立一栏(VSCode 的聊天面板位置),
    /// 原有标签留在左边不被顶掉;这一栏比兄弟窄,整片区域还是留给终端。
    /// </summary>
    [TestMethod]
    public void DocumentPanel_RightPlacement_SplitsOffItsOwnColumn()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var terminal = new TestDocument { Id = "t1", Title = "终端" };
            workspace.AddDocument(terminal);

            var panel = new PluginPanel("acme.demo", new NullLog(),
                new() { Title = "Chat", Placement = PanelPlacement.Right }, new Border(), workspace);

            PluginDocument document = workspace.AllDocuments().OfType<PluginDocument>().Single();
            DockGroup panelGroup = workspace.FindGroup(document)!;
            Assert.AreNotSame(workspace.PrimaryGroup, panelGroup, "面板要自成一栏,而不是并进原标签组");
            Assert.AreSame(workspace.PrimaryGroup, workspace.FindGroup(terminal), "原有标签留在原位");

            var split = (DockSplit)workspace.Root;
            Assert.AreEqual(DockOrientation.Horizontal, split.Orientation);
            Assert.AreSame(panelGroup, split.Children[^1], "右侧 = 分栏里的最后一个子节点");
            // 默认三成宽:兄弟算 1 星,本栏 w 星 → w/(1+w) ≈ 0.3
            Assert.AreEqual(0.3 / 0.7, panelGroup.Proportion, 1e-9, "默认侧栏约占三成,而不是对半分");
            Assert.AreSame(document, workspace.ActiveDocument);
            Assert.IsTrue(panel.IsOpen);
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 比例必须真的落到<b>渲染出来的列宽</b>上,而不只是模型上的一个数。
    /// (曾经就是只对了一半:<c>DockTo</c> 改树时控件已经按均分建好了 Grid,
    /// 之后再写 <c>Proportion</c> 没人再读 —— 模型是 20%,界面还是 50%。)
    /// </summary>
    [TestMethod]
    public void DocumentPanel_PlacementRatio_ReachesTheRenderedColumnWidths()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            workspace.AddDocument(new TestDocument { Id = "t1", Title = "终端" });
            var control = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 1000, Height = 600, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            _ = new PluginPanel("acme.demo", new NullLog(),
                new() { Title = "Chat", Placement = PanelPlacement.Right, PlacementRatio = 0.2 },
                new Border(), workspace);
            window.Measure(window.ClientSize);
            window.Arrange(new Rect(window.ClientSize));
            Dispatcher.UIThread.RunJobs();

            Grid grid = control.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count > 1);
            double terminal = grid.ColumnDefinitions[0].Width.Value;
            double chat = grid.ColumnDefinitions[^1].Width.Value;
            Assert.IsTrue(grid.ColumnDefinitions[0].Width.IsStar && grid.ColumnDefinitions[^1].Width.IsStar);
            Assert.AreEqual(0.2, chat / (terminal + chat), 0.005, "侧栏应当渲染成两成宽");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>侧栏宽度可配:比例换算成星值,越界的值夹回可用区间。</summary>
    [TestMethod]
    public void DocumentPanel_PlacementRatio_SetsTheInitialColumnWidth()
    {
        _session.Dispatch(() =>
        {
            Assert.AreEqual(0.5 / 0.5, WidthOf(0.5), 1e-9, "一半就是一半");
            Assert.AreEqual(0.15 / 0.85, WidthOf(0.01), 1e-9, "过窄夹到下限,别拆出一条看不见的缝");
            Assert.AreEqual(0.85 / 0.15, WidthOf(2.0), 1e-9, "过宽夹到上限,别把兄弟挤没");
            return true;

            static double WidthOf(double ratio)
            {
                var workspace = new DockWorkspace();
                workspace.AddDocument(new TestDocument { Id = "t1", Title = "终端" });
                _ = new PluginPanel("acme.demo", new NullLog(),
                    new() { Title = "Chat", Placement = PanelPlacement.Right, PlacementRatio = ratio },
                    new Border(), workspace);
                PluginDocument document = workspace.AllDocuments().OfType<PluginDocument>().Single();
                return workspace.FindGroup(document)!.Proportion;
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>不给 Placement 时维持原样:并入当前标签组,只是多一个标签。</summary>
    [TestMethod]
    public void DocumentPanel_DefaultPlacement_StaysInTheTabStrip()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            workspace.AddDocument(new TestDocument { Id = "t1", Title = "终端" });
            _ = new PluginPanel("acme.demo", new NullLog(), new() { Title = "Chat" }, new Border(), workspace);

            Assert.IsInstanceOfType<DockGroup>(workspace.Root, "不该拆出分栏");
            Assert.HasCount(2, workspace.PrimaryGroup.Documents);
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- 完整调用路径:PluginUiApi(进程内插件运行时真正走的入口) ----

    [TestMethod]
    public void UiApi_DocumentMode_OpensDockedTabInMainWindowLayout()
    {
        _session.Dispatch(async () =>
        {
            // 与生产同构:DI 单例 MainWindowViewModel 的 Layout 即停靠工作区。
            var viewModel = new VelaShell.ViewModels.MainWindowViewModel();
            var api = new PluginUiApi("acme.demo", new NullLog(), () => viewModel);

            IPluginPanel panel = await api.ShowPanelAsync(
                new() { Title = "Demo Tab", DisplayMode = PluginSdk.Ui.PanelDisplayMode.Document },
                () => new TextBlock { Text = "from factory (UI thread)" });

            PluginDocument document = viewModel.Layout.AllDocuments().OfType<PluginDocument>().Single();
            Assert.AreEqual("Demo Tab", document.Title);
            Assert.IsTrue(panel.IsOpen);
            Assert.AreSame(viewModel.Layout.ActiveDocument, document, "新面板应成为激活标签");

            await panel.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.IsEmpty(viewModel.Layout.AllDocuments());
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- 独立窗口形态 ----

    [TestMethod]
    public void WindowPanel_HostsNativeControl_OpensAndCloses()
    {
        _session.Dispatch(async () =>
        {
            var content = new TextBlock { Text = "hello from plugin" };
            var panel = new PluginPanel("acme.demo", new NullLog(),
                new() { Title = "Demo", DisplayMode = PluginSdk.Ui.PanelDisplayMode.Window, WindowWidth = 400, WindowHeight = 300 },
                content, owner: null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(panel.IsOpen);

            await panel.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(panel.IsOpen);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 插件声明的标题栏动作按钮(<see cref="PanelOptions.TitleActions" />)要出现在最小化键<b>左侧</b>、
    /// 按给出的顺序排列,点了要回调 —— 与主窗体标题栏那排工具按钮同一位置。
    /// </summary>
    [TestMethod]
    public void WindowPanel_TitleActions_SitLeftOfMinimize_AndClickBack()
    {
        _session.Dispatch(async () =>
        {
            int clicked = 0;
            var panel = new PluginPanel("acme.demo", new NullLog(),
                new()
                {
                    Title = "Demo", DisplayMode = PluginSdk.Ui.PanelDisplayMode.Window,
                    TitleActions =
                    [
                        new PanelTitleAction("M0 0 L24 24", "first", () => clicked += 1),
                        new PanelTitleAction("M0 24 L24 0", "second", () => clicked += 10)
                    ]
                },
                new Border(), owner: null);
            Dispatcher.UIThread.RunJobs();
            try
            {
                // 窗口是面板的私有字段,测试里直接掏出来(不为它开公共口子)
                VelaShell.Views.PluginPanelWindow shell = (VelaShell.Views.PluginPanelWindow)panel.GetType()
                    .GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(panel)!;
                var strip = shell.GetControl<StackPanel>("CaptionButtons");
                Assert.HasCount(5, strip.Children, "两枚动作 + 最小化 / 最大化 / 关闭");
                Assert.AreEqual("first", ToolTip.GetTip(strip.Children[0]));
                Assert.AreEqual("second", ToolTip.GetTip(strip.Children[1]));
                Assert.IsNull(ToolTip.GetTip(strip.Children[2]), "第三枚就是原来的最小化键");
                ((Button)strip.Children[1]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(10, clicked);
            }
            finally
            {
                await panel.CloseAsync();
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}

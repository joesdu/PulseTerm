using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>左栏快捷片段区域在最小窗口高度下的布局与折叠回归测试。</summary>
[TestClass]
[TestCategory("SidebarUi")]
public class SidebarQuickCommandsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(SidebarQuickCommandsUiTests).Assembly
        );

    [TestMethod]
    public void MinimumHeight_QuickCommandsCanCollapseAndRestore()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var library = new QuickCommandsViewModel(repository);
            var runner = new QuickCommandRunnerViewModel(library);
            var viewModel = new SidebarViewModel(quickCommands: runner)
            {
                IsQuickCommandsVisible = true,
            };
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SessionAndQuickGrid")!;
            Button toggle = view.FindControl<Button>("QuickCommandsToggle")!;
            QuickCommandsView content = view.FindControl<QuickCommandsView>(
                "QuickCommandsContent"
            )!;

            Assert.IsTrue(content.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsFalse(content.IsVisible);
            Assert.AreEqual(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsTrue(content.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    [TestMethod]
    public void HiddenQuickCommands_ReclaimsPanelAndSplitterSpace()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var runner = new QuickCommandRunnerViewModel(new QuickCommandsViewModel(repository));
            var viewModel = new SidebarViewModel(quickCommands: runner);
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SessionAndQuickGrid")!;
            Border section = view.FindControl<Border>("QuickCommandsSection")!;
            GridSplitter splitter = view.FindControl<GridSplitter>("QuickCommandsSplitter")!;

            Assert.IsFalse(section.IsVisible);
            Assert.IsFalse(splitter.IsVisible);
            Assert.AreEqual(0, grid.RowDefinitions[1].ActualHeight);
            Assert.AreEqual(0, grid.RowDefinitions[2].ActualHeight);

            viewModel.IsQuickCommandsVisible = true;
            Relayout(window);
            Assert.IsTrue(section.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    [TestMethod]
    public void SidebarLayout_RestoresCollapseStateAndRememberedHeights()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var viewModel = new SidebarViewModel(
                quickCommands: new QuickCommandRunnerViewModel(
                    new QuickCommandsViewModel(repository)
                )
            )
            {
                IsQuickCommandsVisible = true,
                QuickCommandsExpanded = false,
                QuickCommandsHeight = 220,
                RecentConnectionsExpanded = false,
                RecentConnectionsHeight = 210,
            };
            var view = new SidebarView { DataContext = viewModel };
            var window = new Window
            {
                Width = 280,
                Height = 700,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid quickGrid = view.FindControl<Grid>("SessionAndQuickGrid")!;
            Grid sectionsGrid = view.FindControl<Grid>("SidebarSectionsGrid")!;
            Button quickToggle = view.FindControl<Button>("QuickCommandsToggle")!;
            Button recentToggle = view.FindControl<Button>("RecentConnectionsToggle")!;

            Assert.AreEqual(36, quickGrid.RowDefinitions[2].ActualHeight);
            Assert.AreEqual(36, sectionsGrid.RowDefinitions[2].ActualHeight);

            quickToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            recentToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);

            Assert.IsTrue(viewModel.QuickCommandsExpanded);
            Assert.IsTrue(viewModel.RecentConnectionsExpanded);
            Assert.AreEqual(220, quickGrid.RowDefinitions[2].ActualHeight, 1);
            Assert.AreEqual(210, sectionsGrid.RowDefinitions[2].ActualHeight, 1);

            quickGrid.RowDefinitions[2].Height = new(260);
            Relayout(window);
            quickToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.AreEqual(260, viewModel.QuickCommandsHeight, 1);
            Assert.AreEqual(36, quickGrid.RowDefinitions[2].ActualHeight);

            quickToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.AreEqual(260, quickGrid.RowDefinitions[2].ActualHeight, 1);
            window.Close();
        });
    }

    [TestMethod]
    public void SessionTreeProgrammaticSelection_DoesNotTakeKeyboardFocus()
    {
        OnUi(() =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            var treeViewModel = new SessionTreeViewModel(repository);
            SessionProfile profile = new()
            {
                Id = Guid.NewGuid(),
                Name = "server",
                Host = "server.example",
                Username = "root",
            };
            treeViewModel.AddSession(profile);
            var treeView = new SessionTreeView { DataContext = treeViewModel };
            var terminalFocusProxy = new TextBox();
            var panel = new Grid { RowDefinitions = [with("*,Auto")] };
            panel.Children.Add(treeView);
            Grid.SetRow(terminalFocusProxy, 1);
            panel.Children.Add(terminalFocusProxy);
            var window = new Window
            {
                Width = 320,
                Height = 400,
                Content = panel,
            };
            window.Show();
            Relayout(window);
            terminalFocusProxy.Focus();

            Assert.IsTrue(treeViewModel.SelectSession(profile.Id));
            Relayout(window);

            Assert.IsTrue(terminalFocusProxy.IsFocused);
            Assert.IsNotNull(
                treeView
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(border =>
                        border.Classes.Contains("session")
                        && ReferenceEquals(border.DataContext, treeViewModel.SelectedNode)
                    )
            );
            window.Close();
        });
    }

    [TestMethod]
    public void QuickCommands_RendersCollapsibleGroups()
    {
        OnUi(() =>
        {
            IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
            var runner = new QuickCommandRunnerViewModel(new QuickCommandsViewModel(repository));
            var view = new QuickCommandsView { DataContext = runner };
            var window = new Window
            {
                Width = 300,
                Height = 500,
                Content = view,
            };
            window.Show();
            Relayout(window);

            Assert.IsGreaterThan(
                1,
                view.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                    .Count()
            );
            window.Close();
        });
    }

    [TestMethod]
    public void MinimumHeight_RecentConnectionsCanCollapseAndRestore()
    {
        OnUi(() =>
        {
            var view = new SidebarView { DataContext = new SidebarViewModel() };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Grid grid = view.FindControl<Grid>("SidebarSectionsGrid")!;
            Button toggle = view.FindControl<Button>("RecentConnectionsToggle")!;
            ScrollViewer content = view.FindControl<ScrollViewer>("RecentConnectionsContent")!;
            GridSplitter splitter = view.FindControl<GridSplitter>("RecentConnectionsSplitter")!;

            Assert.IsTrue(content.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsFalse(content.IsVisible);
            Assert.IsFalse(splitter.IsVisible);
            Assert.AreEqual(36, grid.RowDefinitions[2].ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Relayout(window);
            Assert.IsTrue(content.IsVisible);
            Assert.IsTrue(splitter.IsVisible);
            Assert.IsGreaterThan(36, grid.RowDefinitions[2].ActualHeight);
            window.Close();
        });
    }

    /// <summary>
    /// 拖动分组的视图侧接线:树接收放置(AllowDrop),分组行随 IsDropTarget 换色,
    /// 且换色**不改变几何** —— 描边宽度常驻,点亮/熄灭只动颜色。
    /// 断言落到真实视觉状态(画刷 alpha、行内容位置)而不是只比 VM 的布尔值:
    /// Classes.droptarget 绑定写错时 VM 照样翻转、界面毫无反应;而描边宽度若跟着变,
    /// 行内容会左右各挪 1px,拖过去时肉眼就是在抖。
    /// </summary>
    [TestMethod]
    public void SessionTreeGroupRow_HighlightsDropTargetWithoutMovingContent()
    {
        OnUi(() =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            var viewModel = new SessionTreeViewModel(repository);
            var groupNode = new SessionTreeNodeViewModel(Guid.NewGuid(), "Production", true);
            viewModel.Nodes.Add(groupNode);
            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 400,
                Content = view,
            };
            window.Show();
            Relayout(window);

            TreeView tree = view.FindControl<TreeView>("SessionTreeRoot")!;
            Assert.IsTrue(DragDrop.GetAllowDrop(tree));

            Border row = view.GetVisualDescendants()
                             .OfType<Border>()
                             .First(border =>
                                 border.Classes.Contains("group")
                                 && ReferenceEquals(border.DataContext, groupNode)
                             );
            Rect contentBefore = row.Child!.Bounds;
            Assert.AreEqual(0, BorderAlpha(row), "平时描边应当是透明的");

            groupNode.IsDropTarget = true;
            Relayout(window);
            Assert.IsTrue(row.Classes.Contains("droptarget"));
            Assert.IsGreaterThan(0, BorderAlpha(row), "落点分组应当点亮 accent 描边");
            Assert.AreEqual(contentBefore, row.Child!.Bounds, "高亮不得挪动行内容");

            groupNode.IsDropTarget = false;
            Relayout(window);
            Assert.AreEqual(0, BorderAlpha(row));
            Assert.AreEqual(contentBefore, row.Child!.Bounds);
            window.Close();
        });
    }

    /// <summary>
    /// 拖拽时的落点提示:幽灵标签写明“会话 → 目标”,落到未分组时整棵树被框起来,
    /// 且标签始终被夹在叠层内(拖到右下角不会被裁掉半截)。
    /// 拖放事件在无头后端造不出来,因此直接调视图的 ShowDragFeedback ——
    /// 验的是真实视觉状态,而不是"相信 DragOver 会做对"。
    /// </summary>
    [TestMethod]
    public void SessionTreeDragFeedback_NamesTargetAndFramesUngroupedDrop()
    {
        OnUi(() =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            var viewModel = new SessionTreeViewModel(repository);
            var groupNode = new SessionTreeNodeViewModel(Guid.NewGuid(), "Production", true);
            viewModel.Nodes.Add(groupNode);
            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window
            {
                Width = 260,
                Height = 400,
                Content = view,
            };
            window.Show();
            Relayout(window);

            Canvas overlay = view.FindControl<Canvas>("DragOverlay")!;
            Border ghost = view.FindControl<Border>("DragGhost")!;
            TextBlock ghostText = view.FindControl<TextBlock>("DragGhostText")!;
            Border rootZone = view.FindControl<Border>("RootDropZone")!;

            // 叠层不能参与命中测试,否则落点永远算到它身上。
            Assert.IsFalse(overlay.IsHitTestVisible);
            Assert.IsFalse(ghost.IsVisible);
            Assert.IsFalse(rootZone.IsVisible);

            // 落到分组:标签写明目标,分组行点亮,不画根落点框。
            view.ShowDragFeedback("WebServer", groupNode.Id, sameGroup: false, new Point(20, 20));
            Relayout(window);
            Assert.IsTrue(ghost.IsVisible);
            Assert.IsTrue(ghostText.Text!.Contains("WebServer", StringComparison.Ordinal));
            Assert.IsTrue(
                ghostText.Text!.Contains("Production", StringComparison.Ordinal),
                $"幽灵标签应写明目标分组,实际:{ghostText.Text}"
            );
            Assert.IsTrue(groupNode.IsDropTarget);
            Assert.IsFalse(rootZone.IsVisible);

            // 落到未分组:框住整棵树,分组行熄灭;坐标越界时标签仍被夹在叠层内。
            view.ShowDragFeedback("WebServer", Guid.Empty, sameGroup: false, new Point(9999, 9999));
            Relayout(window);
            Assert.IsTrue(rootZone.IsVisible);
            Assert.IsFalse(groupNode.IsDropTarget);
            Assert.IsTrue(
                ghostText.Text!.Contains(Strings.Get("Svc_Ungrouped"), StringComparison.Ordinal),
                $"幽灵标签应写明未分组,实际:{ghostText.Text}"
            );
            Assert.IsLessThanOrEqualTo(
                overlay.Bounds.Width + 0.5, Canvas.GetLeft(ghost) + ghost.Bounds.Width,
                "幽灵标签右边缘越出了叠层"
            );
            Assert.IsLessThanOrEqualTo(
                overlay.Bounds.Height + 0.5, Canvas.GetTop(ghost) + ghost.Bounds.Height,
                $"幽灵标签下边缘越出了叠层:top={Canvas.GetTop(ghost)} h={ghost.Bounds.Height} overlay={overlay.Bounds.Height}"
            );

            // 落回自己所在的分组 = 没得可动:只显示会话名,不给任何落点承诺。
            view.ShowDragFeedback("WebServer", groupNode.Id, sameGroup: true, new Point(20, 20));
            Relayout(window);
            Assert.AreEqual("WebServer", ghostText.Text);
            Assert.IsFalse(rootZone.IsVisible);
            Assert.IsFalse(groupNode.IsDropTarget);

            view.ClearDragFeedback();
            Relayout(window);
            Assert.IsFalse(ghost.IsVisible);
            Assert.IsFalse(rootZone.IsVisible);
            window.Close();
        });
    }

    /// <summary>取行描边画刷的不透明度;画刷解析不到(主题字典没接上)时直接判失败而不是悄悄绿。</summary>
    private static byte BorderAlpha(Border row)
    {
        Assert.IsInstanceOfType<ISolidColorBrush>(row.BorderBrush, "描边画刷未解析");
        return ((ISolidColorBrush)row.BorderBrush!).Color.A;
    }

    /// <summary>
    /// 最近连接标题栏的两个动作按钮:清除在左、刷新在右(二者同属一个 StackPanel,
    /// 因此可直接比较 Bounds.X)。断言落在实际布局上,而不是 XAML 里的书写顺序。
    /// </summary>
    [TestMethod]
    public void RecentConnectionsHeader_ClearButtonSitsLeftOfRefresh()
    {
        OnUi(() =>
        {
            var view = new SidebarView { DataContext = new SidebarViewModel() };
            var window = new Window
            {
                Width = 260,
                Height = 464,
                Content = view,
            };
            window.Show();
            Relayout(window);
            Button clear = view.FindControl<Button>("RecentConnectionsClear")!;
            Button refresh = view.FindControl<Button>("RecentConnectionsRefresh")!;

            Assert.IsTrue(clear.IsVisible);
            Assert.IsGreaterThan(0, clear.Bounds.Width);
            Assert.AreSame(clear.Parent, refresh.Parent);
            Assert.IsLessThan(refresh.Bounds.X, clear.Bounds.X);
            window.Close();
        });
    }

    private static void Relayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void OnUi(Action body) =>
        _session
            .Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
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

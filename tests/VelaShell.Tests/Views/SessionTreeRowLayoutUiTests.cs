using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源管理器树的行几何:展开后的会话行前面<b>不许有那条空白</b>。
/// </summary>
/// <remarks>
/// 这棵树原先是 <c>TreeView</c>,而它给每一层预留一块缩进区与一枚内置箭头。本设计的箭头是
/// 自绘的、缩进是行内 padding,于是只能拿一串按模板部件名去关灯的样式把内置那套压掉 ——
/// 压不干净就在子行前面留下一条<b>点不着、也不跟着高亮</b>的空白,而且是"换个 Avalonia 版本
/// 部件改名了就悄悄回来"的那种。改成摊平的平列表之后这件事从根上不成立,这里把它钉住:
/// 每一行都从最左开始、铺满整行宽。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeRowLayoutUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(SessionTreeRowLayoutUiTests).Assembly
        );

    [TestMethod]
    public void ExpandedSessionRows_StartAtTheLeftEdge_AndSpanTheFullWidth()
    {
        OnUi(async () =>
        {
            (Window window, SessionTreeView view, SessionTreeViewModel viewModel) =
                await ShowAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("SessionTreeRoot");
                Assert.HasCount(2, viewModel.Rows, "分组行 + 展开着的那台会话");

                Border groupRow = Row(view, "group");
                Border sessionRow = Row(view, "session");

                // 关键的一条:子行的左边缘与分组行完全对齐(都是 0)。
                // 缩进是行<b>内</b>的 padding,不是把整行往右推 —— 推了就有那条空白。
                Assert.AreEqual(0, Left(groupRow, list), 0.5, "分组行该从最左开始");
                Assert.AreEqual(0, Left(sessionRow, list), 0.5,
                    "展开后的会话行前面不该有空白:那一块既点不着、也不跟着行高亮");

                // 而且两行都铺满整行宽 —— 自绘的悬停/选中底色要从最左画到最右
                Assert.AreEqual(list.Bounds.Width, groupRow.Bounds.Width, 1.0);
                Assert.AreEqual(list.Bounds.Width, sessionRow.Bounds.Width, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void CollapsingAGroup_TakesItsSessionRowsOutOfTheList()
    {
        OnUi(async () =>
        {
            (Window window, SessionTreeView view, SessionTreeViewModel viewModel) =
                await ShowAsync();
            try
            {
                ListBox list = view.GetControl<ListBox>("SessionTreeRoot");
                Assert.AreEqual(2, list.ItemCount);

                viewModel.Nodes.Single(node => node.IsGroup).IsExpanded = false;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.AreEqual(1, list.ItemCount, "折起来之后列表里只剩分组那一行");
                Assert.IsEmpty(
                    view.GetVisualDescendants()
                        .OfType<Border>()
                        .Where(border =>
                            border.Classes.Contains("session") && border.IsVisible
                        ),
                    "会话行整批离开列表,而不是留在原地被藏起来"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>一个分组 + 组内一台会话,装好并布过局。</summary>
    private static async Task<(Window Window, SessionTreeView View, SessionTreeViewModel ViewModel)> ShowAsync()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "Production" };
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "WebServer",
            Host = "web.example.com",
            Username = "admin",
            GroupId = group.Id,
        };
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        repository
            .GetAllSessionsAsync()
            .Returns(Task.FromResult(new List<SessionProfile> { session }));

        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();

        var view = new SessionTreeView { DataContext = viewModel };
        var window = new Window { Width = 260, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view, viewModel);
    }

    /// <summary>取出可见的那一类行(分组行与会话行同处一个模板,靠 class 区分)。</summary>
    private static Border Row(SessionTreeView view, string className) =>
        view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains(className) && border.IsVisible);

    /// <summary>某一行相对整棵树的左边缘。</summary>
    private static double Left(Border row, ListBox list) =>
        row.TranslatePoint(default, list)?.X ?? double.NaN;

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}

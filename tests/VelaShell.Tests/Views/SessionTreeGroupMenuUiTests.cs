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
/// 资源管理器树上分组行的右键菜单接线。
/// </summary>
/// <remarks>
/// 视图模型侧的删除逻辑由 <c>SessionTreeViewModelTests</c> 覆盖,这里守的是另一半:
/// 菜单项确实挂在分组行上、并且真的绑到了 <see cref="SessionTreeViewModel.DeleteGroupCommand" />。
/// ContextMenu 走的是非编译期校验的反射绑定(<c>$parent[TreeView]</c> 穿弹出层),
/// 写错了不会有任何编译错误 —— 只会在用户点下去时"菜单点了没反应"。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeGroupMenuUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(SessionTreeGroupMenuUiTests).Assembly
        );

    [TestMethod]
    public void GroupRow_ContextMenu_BindsDeleteGroupCommand()
    {
        OnUi(async () =>
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
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { session }));

            var viewModel = new SessionTreeViewModel(repository);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border groupRow = view.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("group")
                                  && border.DataContext is SessionTreeNodeViewModel { IsGroup: true });

            Assert.IsNotNull(groupRow.ContextMenu, "分组行必须有右键菜单,否则根本没有删除分组的入口。");
            MenuItem item = groupRow.ContextMenu.Items.OfType<MenuItem>().Single();

            // 打开菜单才会求值绑定:关掉的 ContextMenu 里 Command 恒为 null,直接断言会假通过。
            groupRow.ContextMenu.Open(groupRow);
            Dispatcher.UIThread.RunJobs();

            Assert.AreSame(viewModel.DeleteGroupCommand, item.Command,
                           "分组菜单的删除项必须绑到 DeleteGroupCommand。");

            groupRow.ContextMenu.Close();
            window.Close();
        });
    }

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}

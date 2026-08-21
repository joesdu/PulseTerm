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

    /// <summary>
    /// 在 UI 线程上跑一段。
    /// <para>
    /// <b>这里必须把 <paramref name="body" /> 包一层再交出去,不能直传。</b>
    /// <c>HeadlessUnitTestSession</c> 没有 <c>Func&lt;Task&gt;</c> 那一支重载,直传只能绑到
    /// <c>Dispatch&lt;T&gt;(Func&lt;T&gt;, …)</c> 上、T 推成 <c>Task</c> ——
    /// <c>GetResult()</c> 拿回的是**那个还没跑完的内层 Task**,第一个 <c>await</c> 之后的断言
    /// 全部落在没人接的地方。编译通过、测试恒绿。
    /// 实测:把 <c>Assert.Fail</c> 放在用例第一行,dotnet test 照样报全过。
    /// 包成 <c>async () =&gt; { await body(); return true; }</c> 才落到
    /// <c>Func&lt;Task&lt;T&gt;&gt;</c> 上,异常才随 Task 传回来。
    /// </para>
    /// </summary>
    /// <param name="body">要在 UI 线程上跑的活。</param>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}

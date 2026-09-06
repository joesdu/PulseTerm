using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 代码片段设置页里,每一行的「编辑」「删除」。
/// </summary>
/// <remarks>
/// 这两个动作原本是绑定 <c>$parent[UserControl].DataContext.Snippets.…</c>,因为路径里
/// 显式带了 <c>.DataContext</c>,页面还没挂进可视树那一刻就会刷绑定错误。改走代码后置之后
/// <b>行为必须一模一样</b> —— 这组用例就是钉住这一点的:光把日志弄干净、把功能弄坏了,
/// 是比原来更糟的结果。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class SnippetsPageRowActionsTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SnippetsPageRowActionsTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    /// <summary>点「编辑」把那一条载进编辑区。</summary>
    [TestMethod]
    public void ClickingEdit_LoadsThatSnippetIntoTheEditor()
    {
        OnUi(() =>
        {
            (SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandViewModel target = FirstEditable(snippets);

            RaiseClick(page, "EditSnippet_Click", target);

            Assert.AreSame(target, snippets.EditingCommand, "编辑区没有接到这一条。");
            Assert.AreEqual(target.Name, snippets.NewName);
            return Task.CompletedTask;
        });
    }

    /// <summary>点「删除」真的把那一条删掉。</summary>
    [TestMethod]
    public void ClickingDelete_RemovesThatSnippet()
    {
        OnUi(() =>
        {
            (SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandViewModel target = FirstEditable(snippets);
            Guid id = target.Id;

            RaiseClick(page, "DeleteSnippet_Click", target);
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(
                snippets.AllCommands.Any(c => c.Id == id),
                "删除没有生效 —— 绑定改成代码后置之后功能丢了。");
            return Task.CompletedTask;
        });
    }

    /// <summary>页面还没拿到视图模型时点下去,不能抛。</summary>
    [TestMethod]
    public void ClickingBeforeTheViewModelArrives_IsIgnored()
    {
        OnUi(() =>
        {
            var page = new SnippetsPage();
            // DataContext 尚未流入(懒建页面在挂进树之前就是这个状态)。
            RaiseClick(page, "EditSnippet_Click", null);
            RaiseClick(page, "DeleteSnippet_Click", null);
            return Task.CompletedTask;
        });
    }

    /// <summary>照 XAML 里的接线调用处理器:sender 是那一行的按钮,DataContext 即该条片段。</summary>
    private static void RaiseClick(SnippetsPage page, string handler, QuickCommandViewModel? row)
    {
        var button = new Button { DataContext = row };
        page.GetType()
            .GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(page, [button, new RoutedEventArgs()]);
    }

    private static QuickCommandViewModel FirstEditable(QuickCommandsViewModel snippets) =>
        snippets.AllCommands.FirstOrDefault(c => !c.IsBuiltIn)
        ?? throw new InvalidOperationException("夹具里没有可编辑的片段。");

    private static (SnippetsPage Page, QuickCommandsViewModel Snippets) Show()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        IThemeService theme = Substitute.For<IThemeService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var group = new QuickCommandGroup { Id = Guid.NewGuid(), Name = "部署" };
        var data = new QuickCommandData
        {
            Groups = [.. QuickCommandGroupCatalog.CreateSystemGroups(), group],
            Commands =
            [
                new QuickCommand
                {
                    Name = "重启 nginx",
                    CommandText = "systemctl restart nginx",
                    GroupId = group.Id
                }
            ]
        };
        repository.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new QuickCommandLoadResult(data)));

        var viewModel = new SettingsViewModel(settings, theme, quickCommandRepository: repository);
        viewModel.Snippets!.LoadAsync().GetAwaiter().GetResult();

        var page = new SnippetsPage { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.IsNotEmpty(page.GetVisualDescendants(), "页面没有渲染出来。");
        return (page, viewModel.Snippets);
    }

    private static void OnUi(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

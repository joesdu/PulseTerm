using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Docking;
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
}

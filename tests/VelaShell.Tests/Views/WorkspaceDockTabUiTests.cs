using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Tests.Views;

/// <summary>
/// 插件工作台文档的停靠标签页外壳。
/// <para>
/// 这个测试类存在的唯一理由是一个真实回归:<see cref="DockGroupControl" /> 里少了
/// <see cref="PluginWorkspaceDocument" /> 的 <c>DataTemplate</c>。Avalonia 找不到模板时
/// 不报错 —— 它退回 <c>ToString()</c>,于是用户连上 Redis 后看到的标签写着
/// 「VelaShell.Docking.PluginWorkspaceDocument」。编译器看不见这种缺失,
/// 视觉上又只有真的连一次才会暴露,所以这里把它钉住。
/// </para>
/// </summary>
[TestClass]
[TestCategory("WorkspaceDockTabUi")]
public sealed class WorkspaceDockTabUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WorkspaceDockTabUiTests).Assembly);

    [TestMethod]
    public void PluginWorkspaceDocument_RendersWorkspaceTabItem_NotTypeName()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginWorkspaceDocument document = CreateDocument("local-redis");
            workspace.AddDocument(document);

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 800, Height = 400, Content = dock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            WorkspaceDockTabItem tab = dock.GetVisualDescendants()
                                           .OfType<WorkspaceDockTabItem>()
                                           .SingleOrDefault()
                ?? throw new AssertFailedException(
                    "标签条上没有 WorkspaceDockTabItem —— DockGroupControl 缺少 PluginWorkspaceDocument 的 DataTemplate。");

            // 退回 ToString() 时标签上写的就是这个全名。任何一处文本命中它都是回归。
            string typeName = typeof(PluginWorkspaceDocument).FullName!;
            foreach (TextBlock text in dock.GetVisualDescendants().OfType<TextBlock>())
            {
                Assert.AreNotEqual(typeName, text.Text,
                    "标签页退回了 ToString():模板没有匹配上 PluginWorkspaceDocument。");
            }

            Assert.IsTrue(
                tab.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "local-redis"),
                "标签上没有连接名。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>状态圆点跟着插件报的状态走 —— 否则标签永远停在灰点上。</summary>
    [TestMethod]
    public void WorkspaceStatus_FlowsToDocumentStatus()
    {
        var plugin = new FakeWorkspace(ProtocolSessionState.Closed);
        var document = new PluginWorkspaceDocument(Profile("local-redis"), Guid.NewGuid(), "Redis", plugin);
        Assert.AreEqual(SessionStatus.Disconnected, document.Status);

        _session.Dispatch(() =>
        {
            plugin.Report(ProtocolSessionState.Connected);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(SessionStatus.Connected, document.Status);

            plugin.Report(ProtocolSessionState.Faulted);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(SessionStatus.Error, document.Status);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static PluginWorkspaceDocument CreateDocument(string name) =>
        new(Profile(name), Guid.NewGuid(), "Redis", new FakeWorkspace(ProtocolSessionState.Connected));

    private static SessionProfile Profile(string name) => new()
    {
        Name = name,
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = "redis",
        Host = "127.0.0.1",
        Port = 6379
    };

    /// <summary>只做标签页外壳需要的那点事:报状态、交一个控件。</summary>
    private sealed class FakeWorkspace(ProtocolSessionState initial) : IWorkspaceDocument
    {
        public WorkspaceStatus Status { get; private set; } = new(initial);

        public event EventHandler<WorkspaceStatus>? StatusChanged;

        public void Report(ProtocolSessionState state)
        {
            Status = new(state);
            StatusChanged?.Invoke(this, Status);
        }

        public object CreateView() => new TextBlock { Text = "redis panel" };

        public Task ReconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

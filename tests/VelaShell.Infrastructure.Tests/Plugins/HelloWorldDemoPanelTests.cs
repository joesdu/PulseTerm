using VelaShell.Plugin.HelloWorld;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 用 Testing 替身驱动 HelloWorld 示例(dogfood FakeUi):面板内容是编译期 AXAML
/// 控件,内容工厂在替身里保持惰性(本测试环境不装载 Avalonia 运行时),
/// 这里断言命令注册与面板生命周期;控件本身的行为由宿主 UI 测试覆盖。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class HelloWorldDemoPanelTests
{
    private static readonly string[] ExpectedCommandIds =
    [
        "velashell.hello-world.panel",
        "velashell.hello-world.panel-window",
        "velashell.hello-world.list-sessions",
        "velashell.hello-world.uptime"
    ];

    private static async Task<TestPluginContext> ActivateAsync()
    {
        var ctx = new TestPluginContext { PluginId = "velashell.hello-world" };
        await new HelloWorldPlugin().ActivateAsync(ctx, CancellationToken.None);
        return ctx;
    }

    [TestMethod]
    public async Task Activate_RegistersPanelAndUtilityCommands()
    {
        TestPluginContext ctx = await ActivateAsync();
        try
        {
            string[] ids = [.. ctx.RecordingCommands.Registered.Select(c => c.Id)];
            CollectionAssert.IsSubsetOf(ExpectedCommandIds, ids);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [TestMethod]
    public async Task OpenPanelCommands_UseRequestedDisplayModes()
    {
        TestPluginContext ctx = await ActivateAsync();
        try
        {
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.panel");
            Assert.AreEqual(PanelDisplayMode.Document, ctx.FakeUi.LastPanel.Options.DisplayMode);
            Assert.AreEqual("Hello World", ctx.FakeUi.LastPanel.Options.Title);

            // 已开着时不重复打开。
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.panel-window");
            Assert.HasCount(1, ctx.FakeUi.Panels);

            // 关闭后可以按新模式再开。
            await ctx.FakeUi.LastPanel.CloseAsync();
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.panel-window");
            Assert.HasCount(2, ctx.FakeUi.Panels);
            Assert.AreEqual(PanelDisplayMode.Window, ctx.FakeUi.LastPanel.Options.DisplayMode);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [TestMethod]
    public async Task UptimeCommand_RunsRemoteExec()
    {
        TestPluginContext ctx = await ActivateAsync();
        try
        {
            ctx.FakeSessions.AddConnected(host: "prod-1");
            ctx.FakeRemoteExec.Handler = (_, cmd) => cmd == "uptime" ? "up 42 days" : "";
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.uptime");
            Assert.Contains(e => e.Command == "uptime", ctx.FakeRemoteExec.Executed);
            Assert.Contains(e => e.Message.Contains("up 42 days"), ctx.CollectingLog.Entries);
        }
        finally
        {
            ctx.Dispose();
        }
    }
}

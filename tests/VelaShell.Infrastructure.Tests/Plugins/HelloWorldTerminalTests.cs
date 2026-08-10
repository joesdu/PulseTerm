using VelaShell.Plugin.HelloWorld;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>用 FakeTerminal 驱动 HelloWorld 的终端读取/搜索/回写命令。</summary>
[TestClass]
[TestCategory("Plugins")]
public class HelloWorldTerminalTests
{
    private static async Task<TestPluginContext> ActivateWithSessionAsync()
    {
        var ctx = new TestPluginContext { PluginId = "velashell.hello-world" };
        SessionInfo session = ctx.FakeSessions.AddConnected(host: "prod-1");
        ctx.FakeTerminal.Output[session.SessionId] =
        [
            "$ tail -f app.log",
            "INFO ready",
            "ERROR disk full",
            "WARN retrying"
        ];
        await new HelloWorldPlugin().ActivateAsync(ctx, CancellationToken.None);
        return ctx;
    }

    [TestMethod]
    public async Task GrepErrors_SearchesTerminalOutput()
    {
        TestPluginContext ctx = await ActivateWithSessionAsync();
        try
        {
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.grep-errors");
            Assert.IsTrue(ctx.CollectingLog.Entries.Any(e => e.Message.Contains("disk full")),
                "应在终端输出里搜到 error 行");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [TestMethod]
    public async Task EchoTerminal_WritesWhenAllowed()
    {
        TestPluginContext ctx = await ActivateWithSessionAsync();
        try
        {
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.echo-terminal");
            Assert.IsTrue(ctx.FakeTerminal.Writes.Any(w => w.Input.Contains("hello-from-plugin")));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [TestMethod]
    public async Task EchoTerminal_DegradesGracefullyWhenDenied()
    {
        TestPluginContext ctx = await ActivateWithSessionAsync();
        try
        {
            ctx.FakeTerminal.DenyWrites = true;
            await ctx.RecordingCommands.RunAsync("velashell.hello-world.echo-terminal"); // 不抛
            Assert.IsFalse(ctx.FakeTerminal.Writes.Any());
            Assert.IsTrue(ctx.CollectingLog.Entries.Any(e => e.Message.Contains("denied")));
        }
        finally
        {
            ctx.Dispose();
        }
    }
}

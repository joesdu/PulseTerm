using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Testing;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Plugins;

namespace VelaShell.Presentation.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginCommandsApiTests
{
    private static PluginCommandDescriptor Command(string id, Func<CancellationToken, Task>? body = null)
        => new(id, "Title", "Category", body ?? (_ => Task.CompletedTask));

    [TestMethod]
    public void Register_EnforcesPluginIdPrefix()
    {
        var api = new PluginCommandsApi("acme.plugin", new CommandRegistry(), new CollectingLogger());
        Assert.ThrowsExactly<ArgumentException>(() => api.Register(Command("other.command")));
        Assert.ThrowsExactly<ArgumentException>(() => api.Register(Command("acme.plugin"))); // 缺点号后缀
    }

    [TestMethod]
    public async Task Register_CommandAppearsInRegistry_AndBodyRunsGuarded()
    {
        var registry = new CommandRegistry();
        var log = new CollectingLogger();
        var api = new PluginCommandsApi("acme.plugin", registry, log);
        var ran = new TaskCompletionSource();
        api.Register(Command("acme.plugin.hello", _ =>
        {
            ran.SetResult();
            return Task.CompletedTask;
        }));

        Assert.IsNotNull(registry.Find("acme.plugin.hello"));
        Assert.IsTrue(registry.Execute("acme.plugin.hello"));
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Register_ThrowingBody_IsLoggedNotPropagated()
    {
        var registry = new CommandRegistry();
        var log = new CollectingLogger();
        var api = new PluginCommandsApi("acme.plugin", registry, log);
        api.Register(Command("acme.plugin.boom", _ => throw new InvalidOperationException("boom")));

        Assert.IsTrue(registry.Execute("acme.plugin.boom")); // UI 侧调用不抛
        // 命令体在后台执行,轮询等待错误日志出现。
        for (int i = 0; i < 100 && log.Entries.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(log.Entries.Any(e => e.Level == PluginLogLevel.Error && e.Exception is InvalidOperationException));
    }

    [TestMethod]
    public void DisposingRegistration_RemovesSingleCommand()
    {
        var registry = new CommandRegistry();
        var api = new PluginCommandsApi("acme.plugin", registry, new CollectingLogger());
        IDisposable registration = api.Register(Command("acme.plugin.one"));
        api.Register(Command("acme.plugin.two"));

        registration.Dispose();
        Assert.IsNull(registry.Find("acme.plugin.one"));
        Assert.IsNotNull(registry.Find("acme.plugin.two"));
    }

    [TestMethod]
    public void Dispose_RemovesAllPluginCommands_ButNotHostCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new("host.command", "Host", "Host", () => { }));
        var api = new PluginCommandsApi("acme.plugin", registry, new CollectingLogger());
        api.Register(Command("acme.plugin.one"));
        api.Register(Command("acme.plugin.two"));

        api.Dispose();
        Assert.IsNull(registry.Find("acme.plugin.one"));
        Assert.IsNull(registry.Find("acme.plugin.two"));
        Assert.IsNotNull(registry.Find("host.command"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => api.Register(Command("acme.plugin.late")));
    }
}

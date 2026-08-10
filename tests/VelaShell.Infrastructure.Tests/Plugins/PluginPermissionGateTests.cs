using VelaShell.Core.Data;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginPermissionGateTests
{
    /// <summary>内存 IAppDataStore(单文档存取即可)。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private readonly Dictionary<string, object> _docs = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(_docs.TryGetValue($"{collection}|{id}", out object? v) ? (T)v : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(new List<T>());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _docs[$"{collection}|{id}"] = value!;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _docs.Remove($"{collection}|{id}");
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedPrompt(PluginPermissionDecision decision) : IPluginPermissionPrompt
    {
        public int Calls { get; private set; }

        public Task<PluginPermissionDecision> RequestTerminalWriteAsync(string pluginId, string sessionLabel,
            string inputPreview, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    [TestMethod]
    public async Task Deny_DoesNotAllow_AndKeepsAsking()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.Deny);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "rm -rf", default));
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "rm -rf", default));
        Assert.AreEqual(2, prompt.Calls, "拒绝不记忆,每次都问");
    }

    [TestMethod]
    public async Task AllowOnce_AllowsThisTimeOnly()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowOnce);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls, "仅本次不记忆,每次都问");
    }

    [TestMethod]
    public async Task AllowSession_RemembersInMemory_ButNotPersisted()
    {
        var store = new MemoryStore();
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowSession);
        var gate = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(1, prompt.Calls, "本次运行内只问一次");

        // 新实例(模拟重启)= 会话授权不持久,重新问。
        var afterRestart = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls);
    }

    [TestMethod]
    public async Task AllowAlways_Persists_AndSurvivesRestart_UntilRevoked()
    {
        var store = new MemoryStore();
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowAlways);
        var gate = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(1, prompt.Calls);

        // 重启后仍允许,不再问。
        var afterRestart = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await afterRestart.HasGrantAsync("acme.p"));
        Assert.AreEqual(1, prompt.Calls, "始终允许已持久,不再问");

        // 撤销后重新问。
        await afterRestart.RevokeAsync("acme.p");
        Assert.IsFalse(await afterRestart.HasGrantAsync("acme.p"));
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls);
    }

    [TestMethod]
    public async Task NoPrompt_DeniesInsteadOfSilentlyAllowing()
    {
        var gate = new PluginPermissionGate(new MemoryStore(), prompt: null);
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
    }
}

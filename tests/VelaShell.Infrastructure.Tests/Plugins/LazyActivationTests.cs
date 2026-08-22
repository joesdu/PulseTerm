using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>惰性激活(蓝图 D7)与空闲回收(蓝图 04)的行为验证。</summary>
[TestClass]
[TestCategory("Plugins")]
public class LazyActivationTests
{
    private static readonly string[] GhostPluginOnly = ["ghost.plugin"];

    private string _root = null!;
    private string _dataRoot = null!;
    private RecordingCommands _commands = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
        _commands = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    private void StageFixture(string manifestExtras)
    {
        string dir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll",
              "contributes": { "commands": [
                { "id": "velashell.test-fixture.list-sessions", "title": "Test Fixture: List Sessions", "category": "Test Fixture" }
              ] }{{manifestExtras}} }
            """);
    }

    private PluginManager CreateManager(TimeSpan? idleTimeout = null) => new(new()
    {
        PluginRoots = [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0",
        ActivationTimeout = TimeSpan.FromSeconds(30),
        DeactivationTimeout = TimeSpan.FromSeconds(10),
        CommandsFactory = (_, _) => _commands,
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(15),
        IdleCheckInterval = TimeSpan.FromMilliseconds(300)
    });

    [TestMethod]
    public async Task LazyPlugin_StaysDiscovered_UntilPlaceholderCommandTriggersActivation()
    {
        StageFixture(""", "activationEvents": ["onCommand:velashell.test-fixture.list-sessions"]""");
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        // 发现期:不装载程序集,只有清单声明的占位命令。
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);
        Assert.IsFalse(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")),
            "惰性插件在触发前不应有任何激活痕迹");
        Assert.HasCount(1, _commands.Registered);

        // 触发占位命令 → 激活 → 真实命令替换占位并补齐其余注册。
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")));
        Assert.IsGreaterThan(1, _commands.Registered.Count, "激活后应出现插件注册的全部真实命令");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task IsolatedRecyclablePlugin_IdleRecycles_ThenReactivatesOnTrigger()
    {
        StageFixture(""", "hostMode": "isolated", "idlePolicy": "recyclable" """);
        PluginManager manager = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        int firstPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;

        // 静默等待:超过空闲阈值后应被回收(进程消失、状态回到 Discovered、占位命令回挂)。
        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Discovered
                                 && manager.GetIsolatedProcessId("velashell.test-fixture") is null,
            TimeSpan.FromSeconds(30), "空闲的可回收插件应被停用并回收进程");

        // 再次触发 → 重新拉起新进程。
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        int secondPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;
        Assert.AreNotEqual(firstPid, secondPid);

        await manager.DisposeAsync();
    }

    private sealed class RecordingDataStore : VelaShell.Infrastructure.Plugins.IPluginDataStore
    {
        public List<string> Present { get; } = [];
        public List<string> Purged { get; } = [];

        public VelaShell.PluginSdk.Storage.IPluginStorage CreateStorage(string pluginId) => new InMemoryStorage();
        public VelaShell.PluginSdk.Secrets.ISecretsApi CreateSecrets(string pluginId) => new FakeSecrets();
        public VelaShell.PluginSdk.TimeSeries.ITimeSeriesApi CreateTimeSeries(string pluginId) => new InMemoryTimeSeries();

        public Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([.. Present]);

        public Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default)
        {
            Purged.Add(pluginId);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Start_PurgesDataOfUninstalledPlugins_KeepsInstalledAndDisabled()
    {
        StageFixture("");
        // 盘上还有一个被禁用的插件(数据必须保留)。
        string disabledDir = Path.Combine(_root, "disabled-one");
        Directory.CreateDirectory(disabledDir);
        File.WriteAllText(Path.Combine(disabledDir, "plugin.json"),
            """{ "id": "acme.disabled", "version": "1.0.0", "displayName": "D", "entry": "D.dll" }""");
        File.WriteAllText(Path.Combine(disabledDir, ".disabled"), "");

        // 数据侧:已卸载的 ghost 在 DB 与数据目录都留有数据。
        var dataStore = new RecordingDataStore();
        dataStore.Present.AddRange(["velashell.test-fixture", "acme.disabled", "ghost.plugin"]);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "ghost.plugin"));
        File.WriteAllText(Path.Combine(_dataRoot, "ghost.plugin", "leftover.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dataRoot, "acme.disabled"));

        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => _commands,
            DataStore = dataStore
        });
        await manager.StartAsync();

        Assert.AreSequenceEqual(GhostPluginOnly, dataStore.Purged, "只清除已卸载插件的 DB 数据");
        Assert.IsFalse(Directory.Exists(Path.Combine(_dataRoot, "ghost.plugin")), "已卸载插件的数据目录应删除");
        Assert.IsTrue(Directory.Exists(Path.Combine(_dataRoot, "acme.disabled")), "禁用 ≠ 卸载,数据保留");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public void Manifest_RejectsUnknownActivationEvents_AndForeignCommandIds()
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onFileOpen:*.png"] }
            """), "未知激活事件必须拒绝");
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:other.plugin.cmd"],
              "contributes": { "commands": [ { "id": "other.plugin.cmd", "title": "T" } ] } }
            """), "越界命令前缀必须拒绝");
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:a.b.cmd"] }
            """), "onCommand 必须有对应的 contributes.commands 占位声明");

        PluginManifest ok = PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:a.b.cmd"],
              "contributes": { "commands": [ { "id": "a.b.cmd", "title": "T", "category": "C" } ] },
              "idlePolicy": "recyclable" }
            """);
        Assert.IsFalse(ok.ActivatesOnStartup);
        Assert.AreEqual(PluginIdlePolicy.Recyclable, ok.IdlePolicy);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }
}

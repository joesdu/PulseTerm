using VelaShell.Infrastructure.Plugins;
using VelaShell.TestPlugin;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>插件管理页支撑:启用/禁用 + Changed 事件 + .disabled 标记持久。</summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginManagerEnableDisableTests
{
    private string _root = null!;
    private string _dataRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
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

    private string StageFixture()
    {
        string dir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);
        return dir;
    }

    private PluginManager CreateManager() => new(new()
    {
        PluginRoots = [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0",
        CommandsFactory = (_, _) => new RecordingCommands()
    });

    [TestMethod]
    public async Task Disable_StopsActivePlugin_WritesMarker_AndRaisesChanged()
    {
        string dir = StageFixture();
        PluginManager manager = CreateManager();
        int changes = 0;
        manager.Changed += () => Interlocked.Increment(ref changes);
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);

        await manager.DisableAsync("velashell.test-fixture");
        Assert.AreEqual(PluginState.Disabled, manager.Plugins.Single().State);
        Assert.IsTrue(File.Exists(Path.Combine(dir, ".disabled")), "禁用应落 .disabled 标记(重启仍禁用)");
        Assert.IsGreaterThanOrEqualTo(1, changes);

        await manager.EnableAsync("velashell.test-fixture");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);
        Assert.IsFalse(File.Exists(Path.Combine(dir, ".disabled")), "启用应移除 .disabled 标记");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task DisabledMarker_KeepsPluginDisabledAcrossRestart()
    {
        string dir = StageFixture();
        File.WriteAllText(Path.Combine(dir, ".disabled"), "");

        PluginManager manager = CreateManager();
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Disabled, manager.Plugins.Single().State, "盘上有 .disabled 标记者启动即禁用");
        await manager.DisposeAsync();
    }
}

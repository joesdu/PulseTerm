using VelaShell.Infrastructure.Plugins;
using VelaShell.Plugin.HelloWorld;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginManagerTests
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
            // ALC 卸载是异步的,Windows 上偶尔还锁着 dll:留给临时目录清理。
        }
    }

    private PluginManagerOptions Options(params string[] roots) => new()
    {
        PluginRoots = roots.Length > 0 ? roots : [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0"
    };

    private string AddPlugin(string dirName, string manifestJson)
    {
        string dir = Path.Combine(_root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
        return dir;
    }

    [TestMethod]
    public void Discover_ClassifiesEveryFailureModeWithReason()
    {
        AddPlugin("ok", """{ "id": "a.ok", "version": "1.0.0", "displayName": "Ok", "entry": "Ok.dll" }""");
        AddPlugin("bad-json", "{ this is not json");
        AddPlugin("disabled", """{ "id": "a.disabled", "version": "1.0.0", "displayName": "D", "entry": "D.dll" }""");
        File.WriteAllText(Path.Combine(_root, "disabled", ".disabled"), "");
        AddPlugin("future", """{ "id": "a.future", "version": "1.0.0", "displayName": "F", "entry": "F.dll", "apiLevel": 99 }""");
        AddPlugin("needs-newer-host", """{ "id": "a.newer", "version": "1.0.0", "displayName": "N", "entry": "N.dll", "minHostVersion": "9.9.9" }""");
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest")); // 无 plugin.json:直接忽略

        var manager = new PluginManager(Options());
        manager.Discover();
        var byId = manager.Plugins.ToDictionary(p => p.Id);

        Assert.AreEqual(5, byId.Count);
        Assert.AreEqual(PluginState.Discovered, byId["a.ok"].State);
        Assert.AreEqual(PluginState.Invalid, byId["bad-json"].State);
        Assert.IsNotNull(byId["bad-json"].Error);
        Assert.AreEqual(PluginState.Disabled, byId["a.disabled"].State);
        Assert.AreEqual(PluginState.Incompatible, byId["a.future"].State);
        StringAssert.Contains(byId["a.future"].Error, "apiLevel");
        Assert.AreEqual(PluginState.Incompatible, byId["a.newer"].State);
        StringAssert.Contains(byId["a.newer"].Error, "9.9.9");
    }

    [TestMethod]
    public void Discover_DirectoryNameNeedNotMatchId()
    {
        // 随包分发的插件目录名把 id 里的点换成了短横(velashell.ai → velashell-ai),
        // 否则 macOS 的 codesign 会把 .app 内带点号的目录当嵌套 bundle 而签名失败
        // (见 plugins/Directory.Build.targets)。id 只认 plugin.json,目录名不参与任何逻辑 ——
        // 这条测试就是钉住这个前提,免得日后有人把发现逻辑改成按目录名取 id。
        AddPlugin("velashell-ai", """{ "id": "velashell.ai", "version": "1.0.0", "displayName": "AI", "entry": "Ai.dll" }""");

        var manager = new PluginManager(Options());
        manager.Discover();

        PluginDescriptor plugin = manager.Plugins.Single();
        Assert.AreEqual("velashell.ai", plugin.Id);
        Assert.AreEqual(PluginState.Discovered, plugin.State);
    }

    [TestMethod]
    public void Discover_DuplicateId_FirstRootWins()
    {
        string secondRoot = Path.Combine(Path.GetDirectoryName(_root)!, "plugins2");
        Directory.CreateDirectory(secondRoot);
        AddPlugin("dup", """{ "id": "a.dup", "version": "1.0.0", "displayName": "One", "entry": "One.dll" }""");
        string dir2 = Path.Combine(secondRoot, "dup");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "plugin.json"),
            """{ "id": "a.dup", "version": "2.0.0", "displayName": "Two", "entry": "Two.dll" }""");

        var manager = new PluginManager(Options(_root, secondRoot));
        manager.Discover();

        List<PluginDescriptor> dups = [.. manager.Plugins.Where(p => p.Id == "a.dup")];
        Assert.AreEqual(2, dups.Count);
        Assert.AreEqual(1, dups.Count(d => d.State == PluginState.Discovered));
        PluginDescriptor rejected = dups.Single(d => d.State == PluginState.Invalid);
        StringAssert.Contains(rejected.Error, "Duplicate");
    }

    [TestMethod]
    public async Task StartAsync_ActivatesRealPluginEndToEnd_AndDeactivatesOnDispose()
    {
        // 用真实的 HelloWorld 示例程序集端到端验证:可收集 ALC 装载、入口发现、
        // 激活(能力缺席时退化实现)、存储落盘、停用。
        string pluginDir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(pluginDir);
        string source = typeof(HelloWorldPlugin).Assembly.Location;
        File.Copy(source, Path.Combine(pluginDir, "VelaShell.Plugin.HelloWorld.dll"));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
            { "id": "velashell.hello-world", "version": "0.1.0", "displayName": "Hello",
              "entry": "VelaShell.Plugin.HelloWorld.dll" }
            """);

        var manager = new PluginManager(Options());
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.hello-world", "storage.json")),
            "激活应把激活计数写入插件存储");

        await manager.DisposeAsync();
        Assert.AreEqual(PluginState.Deactivated, manager.Plugins.Single().State);
    }

    [TestMethod]
    public async Task StartAsync_EntryAssemblyMissing_MarksFailedWithoutThrowing()
    {
        AddPlugin("ghost", """{ "id": "a.ghost", "version": "1.0.0", "displayName": "G", "entry": "Ghost.dll" }""");
        var manager = new PluginManager(Options());
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Failed, descriptor.State);
        StringAssert.Contains(descriptor.Error, "Ghost.dll");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_IsIdempotent()
    {
        AddPlugin("ok", """{ "id": "a.ok", "version": "1.0.0", "displayName": "Ok", "entry": "Ok.dll" }""");
        var manager = new PluginManager(Options());
        await manager.StartAsync();
        await manager.StartAsync();
        Assert.AreEqual(1, manager.Plugins.Count(p => p.Id == "a.ok"));
        await manager.DisposeAsync();
    }
}

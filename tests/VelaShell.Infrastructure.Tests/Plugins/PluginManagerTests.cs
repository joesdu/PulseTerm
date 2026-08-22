using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.TestPlugin;

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
        // apiLevel 只在**破坏性**变更时才动,所以"我用到了 SDK 1.1 新增的接口方法"没法用它表达。
        // 不拦的话插件会装上、激活,然后在第一次调用新方法时抛 MissingMethodException ——
        // 正是 apiLevel 当初要消灭的那种异常。
        AddPlugin("needs-newer-sdk", """{ "id": "a.newsdk", "version": "1.0.0", "displayName": "S", "entry": "S.dll", "minSdkVersion": "9.9.9" }""");
        AddPlugin("sdk-satisfied", $$"""{ "id": "a.sdkok", "version": "1.0.0", "displayName": "S2", "entry": "S2.dll", "minSdkVersion": "{{VelaPluginApi.SdkVersion}}" }""");
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest")); // 无 plugin.json:直接忽略

        var manager = new PluginManager(Options());
        manager.Discover();
        var byId = manager.Plugins.ToDictionary(p => p.Id);

        Assert.HasCount(7, byId);
        Assert.AreEqual(PluginState.Discovered, byId["a.ok"].State);
        Assert.AreEqual(PluginState.Invalid, byId["bad-json"].State);
        Assert.IsNotNull(byId["bad-json"].Error);
        Assert.AreEqual(PluginState.Disabled, byId["a.disabled"].State);
        Assert.AreEqual(PluginState.Incompatible, byId["a.future"].State);
        Assert.Contains("apiLevel", byId["a.future"].Error);
        Assert.AreEqual(PluginState.Incompatible, byId["a.newer"].State);
        Assert.Contains("9.9.9", byId["a.newer"].Error);
        Assert.AreEqual(PluginState.Incompatible, byId["a.newsdk"].State);
        Assert.Contains("SDK", byId["a.newsdk"].Error);
        // 要求的正是本宿主带的这一版 —— 必须放行,不能把"等于"判成"更老"。
        Assert.AreEqual(PluginState.Discovered, byId["a.sdkok"].State);
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
        Assert.HasCount(2, dups);
        Assert.ContainsSingle(d => d.State == PluginState.Discovered, dups);
        PluginDescriptor rejected = dups.Single(d => d.State == PluginState.Invalid);
        Assert.Contains("Duplicate", rejected.Error);
    }

    [TestMethod]
    public async Task StartAsync_ActivatesRealPluginEndToEnd_AndDeactivatesOnDispose()
    {
        // 用真实的夹具插件程序集端到端验证:可收集 ALC 装载、入口发现、
        // 激活(能力缺席时退化实现)、存储落盘、停用。
        string pluginDir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(pluginDir);
        string source = typeof(TestFixturePlugin).Assembly.Location;
        File.Copy(source, Path.Combine(pluginDir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);

        var manager = new PluginManager(Options());
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")),
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
        Assert.Contains("Ghost.dll", descriptor.Error);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_IsIdempotent()
    {
        AddPlugin("ok", """{ "id": "a.ok", "version": "1.0.0", "displayName": "Ok", "entry": "Ok.dll" }""");
        var manager = new PluginManager(Options());
        await manager.StartAsync();
        await manager.StartAsync();
        Assert.ContainsSingle(p => p.Id == "a.ok", manager.Plugins);
        await manager.DisposeAsync();
    }
}

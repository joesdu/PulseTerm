using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件开发内环:影子拷贝(运行时不锁工程 bin)、<c>ReloadAsync</c>(重编后就地换新)、
/// 开发期插件的禁用状态不写进构建产物目录。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class DevInnerLoopTests
{
    private string _base = null!;
    private string _devRoot = null!;
    private string _pluginDirectory = null!;
    private string _shadowRoot = null!;
    private string _disabledFile = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _devRoot = Path.Combine(_base, "dev");
        _pluginDirectory = Path.Combine(_devRoot, "net11.0");
        _shadowRoot = Path.Combine(_base, "dev-shadow");
        _disabledFile = Path.Combine(_base, "plugins.dev.disabled");
        WritePlugin(version: "0.1.0", displayName: "Dev");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ALC 卸载异步,dll 偶尔还锁着:留给临时目录。
        }
    }

    private void WritePlugin(string version, string displayName)
    {
        Directory.CreateDirectory(_pluginDirectory);
        string entry = Path.Combine(_pluginDirectory, "VelaShell.TestPlugin.dll");
        if (!File.Exists(entry))
        {
            File.Copy(typeof(TestFixturePlugin).Assembly.Location, entry);
        }
        File.WriteAllText(Path.Combine(_pluginDirectory, "plugin.json"), $$"""
            { "id": "acme.inner-loop", "version": "{{version}}", "displayName": "{{displayName}}",
              "hostMode": "inProcess", "entry": "VelaShell.TestPlugin.dll" }
            """);
    }

    private PluginManager CreateManager(bool shadow = true) => new(new()
    {
        PluginRoots = [],
        DevPluginRoots = [_devRoot],
        DevShadowRootDirectory = shadow ? _shadowRoot : null,
        DevDisabledStateFile = _disabledFile,
        DataRootDirectory = Path.Combine(_base, "plugin-data"),
        HostVersion = "1.0.0",
        CommandsFactory = (_, _) => new RecordingCommands()
    });

    [TestMethod]
    public async Task DevPlugin_LoadsFromShadowCopy_AndLeavesTheBuildOutputUnlocked()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        PluginDescriptor descriptor = manager.Plugins.Single(p => p.Id == "acme.inner-loop");
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsNotNull(descriptor.LoadDirectory);
        Assert.StartsWith(_shadowRoot, descriptor.LoadDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.AreNotEqual(descriptor.Directory, descriptor.LoadDirectory);

        // 这一条才是影子拷贝存在的理由:插件运行期间,工程 bin 里的入口 dll 必须仍可覆盖/删除,
        // 否则 Windows 上根本无法在宿主开着的时候重新编译。
        string sourceEntry = Path.Combine(_pluginDirectory, "VelaShell.TestPlugin.dll");
        File.Delete(sourceEntry);
        Assert.IsFalse(File.Exists(sourceEntry), "运行中的插件不应锁住工程构建产物");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Reload_PicksUpTheRebuiltManifest()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        Assert.AreEqual("0.1.0", manager.Plugins.Single(p => p.Id == "acme.inner-loop").Manifest!.Version);

        // 模拟一次重编:清单与产物都换了新版本。
        WritePlugin(version: "0.2.0", displayName: "Dev v2");
        Assert.IsTrue(await manager.ReloadAsync("acme.inner-loop"));

        PluginDescriptor reloaded = manager.Plugins.Single(p => p.Id == "acme.inner-loop");
        Assert.AreEqual(PluginState.Active, reloaded.State, reloaded.Error);
        Assert.AreEqual("0.2.0", reloaded.Manifest!.Version);
        Assert.AreEqual("Dev v2", reloaded.Manifest.DisplayName);
        Assert.IsTrue(reloaded.IsDevelopment, "重载不应丢掉 DEV 标记");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Reload_WithBrokenManifest_MarksInvalidInsteadOfVanishing()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        File.WriteAllText(Path.Combine(_pluginDirectory, "plugin.json"), "{ not json");
        Assert.IsFalse(await manager.ReloadAsync("acme.inner-loop"));

        // 插件仍在列表里,只是标了 Invalid:清单一时写坏不该表现成"插件凭空消失"。
        PluginDescriptor descriptor = manager.Plugins.Single(p => p.Directory == _pluginDirectory);
        Assert.AreEqual(PluginState.Invalid, descriptor.State);
        Assert.IsNotNull(descriptor.Error);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Disable_OfDevPlugin_DoesNotWriteIntoTheBuildOutput_ButPersists()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        await manager.DisableAsync("acme.inner-loop");

        Assert.IsFalse(File.Exists(Path.Combine(_pluginDirectory, ".disabled")),
            "开发期插件的禁用标记不该写进构建产物目录");
        Assert.IsTrue(File.Exists(_disabledFile));
        Assert.Contains("acme.inner-loop", File.ReadAllText(_disabledFile));
        await manager.DisposeAsync();

        // 重启后仍然禁用(状态记在数据根一侧,重编也不会把它抹掉)。
        PluginManager restarted = CreateManager();
        await restarted.StartAsync();
        Assert.AreEqual(PluginState.Disabled, restarted.Plugins.Single(p => p.Id == "acme.inner-loop").State);

        await restarted.EnableAsync("acme.inner-loop");
        Assert.AreEqual(PluginState.Active, restarted.Plugins.Single(p => p.Id == "acme.inner-loop").State);
        Assert.IsEmpty(File.ReadAllText(_disabledFile).Trim());
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task WithoutShadowRoot_LoadsInPlace()
    {
        // 影子拷贝是可关的:关掉时行为与引入前完全一致(生产路径就走这一条)。
        PluginManager manager = CreateManager(shadow: false);
        await manager.StartAsync();

        PluginDescriptor descriptor = manager.Plugins.Single(p => p.Id == "acme.inner-loop");
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.AreEqual(_pluginDirectory, descriptor.LoadDirectory);
        Assert.IsFalse(Directory.Exists(_shadowRoot));

        await manager.DisposeAsync();
    }
}

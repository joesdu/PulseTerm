using VelaShell.Infrastructure.Plugins;
using VelaShell.Plugin.HelloWorld;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 开发期插件挂载:<c>plugins.dev.txt</c> / <c>VELA_PLUGIN_DEV_ROOT</c> 的解析,
/// 以及从开发根发现出来的插件被标记为 <see cref="PluginDescriptor.IsDevelopment" />。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class DevPluginRootTests
{
    private string _base = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
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

    [TestMethod]
    public void Resolve_ListFile_SkipsCommentsAndBlankLines()
    {
        string[] lines =
        [
            "# 这是注释",
            "",
            @"C:\work\my-plugin\bin\Debug",
            "   ",
            "# 又一条注释"
        ];
        IReadOnlyList<string> roots = DevPluginRootResolver.Resolve(_base, _ => lines);

        Assert.ContainsSingle(roots);
        Assert.EndsWith(Path.Combine("my-plugin", "bin", "Debug"), roots[0]);
    }

    [TestMethod]
    public void Resolve_DuplicateEntries_AreCollapsed()
    {
        string[] lines = [@"C:\a\b", @"C:\a\b\", @"C:\A\B"];
        Assert.ContainsSingle(DevPluginRootResolver.Resolve(_base, _ => lines));
    }

    [TestMethod]
    public void Resolve_NoListFileAndNoEnvironment_IsEmpty()
    {
        // 生产路径:两个来源都空 → 发现期一个额外目录都不扫。
        Assert.IsEmpty(DevPluginRootResolver.Resolve(_base, _ => null));
    }

    [TestMethod]
    public void Resolve_MalformedLine_DoesNotThrow()
    {
        // 写坏的一行不该让宿主起不来 —— 跳过它,别的照常生效。
        string[] lines = ["\0not a path", @"C:\good"];
        IReadOnlyList<string> roots = DevPluginRootResolver.Resolve(_base, _ => lines);
        Assert.ContainsSingle(roots);
    }

    [TestMethod]
    public async Task Discover_PluginUnderDevRoot_IsFlaggedAsDevelopment()
    {
        string installed = Path.Combine(_base, "plugins");
        string devRoot = Path.Combine(_base, "dev");
        WritePlugin(Path.Combine(installed, "acme.installed"), "acme.installed");
        WritePlugin(Path.Combine(devRoot, "net11.0"), "acme.indevelopment");

        var manager = new PluginManager(new()
        {
            PluginRoots = [installed],
            DevPluginRoots = [devRoot],
            DataRootDirectory = Path.Combine(_base, "plugin-data"),
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands()
        });
        await manager.StartAsync();

        Assert.IsFalse(manager.Plugins.Single(p => p.Id == "acme.installed").IsDevelopment);
        PluginDescriptor dev = manager.Plugins.Single(p => p.Id == "acme.indevelopment");
        Assert.IsTrue(dev.IsDevelopment, "开发根下的插件应带 DEV 标记");
        Assert.AreEqual(PluginState.Active, dev.State, dev.Error);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Discover_SameIdInBothRoots_InstalledOneWins()
    {
        // 开发根排在正式根之后:同 id 先到先得,已安装的那份不会被本机开发中的顶掉。
        string installed = Path.Combine(_base, "plugins");
        string devRoot = Path.Combine(_base, "dev");
        WritePlugin(Path.Combine(installed, "acme.dup"), "acme.dup");
        WritePlugin(Path.Combine(devRoot, "acme.dup"), "acme.dup");

        var manager = new PluginManager(new()
        {
            PluginRoots = [installed],
            DevPluginRoots = [devRoot],
            DataRootDirectory = Path.Combine(_base, "plugin-data"),
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands()
        });
        await manager.StartAsync();

        PluginDescriptor winner = manager.Plugins.Single(p => p.Id == "acme.dup" && p.State != PluginState.Invalid);
        Assert.IsFalse(winner.IsDevelopment);

        await manager.DisposeAsync();
    }

    private static void WritePlugin(string directory, string id)
    {
        Directory.CreateDirectory(directory);
        File.Copy(typeof(HelloWorldPlugin).Assembly.Location,
            Path.Combine(directory, "VelaShell.Plugin.HelloWorld.dll"));
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "Dev", "hostMode": "inProcess",
              "entry": "VelaShell.Plugin.HelloWorld.dll" }
            """);
    }
}

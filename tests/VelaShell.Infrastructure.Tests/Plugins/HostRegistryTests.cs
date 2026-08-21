using VelaShell.PluginSdk.Hosting;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 宿主自我登记(<c>host.json</c>):宿主写、<c>vela-plugin</c> 读。
/// 重点在几条"坏掉也不能出事"的性质:文件损坏、多份安装并存、旧安装已被删掉。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class HostRegistryTests
{
    private string _base = null!;
    private string _registry = null!;
    private string _exeA = null!;
    private string _exeB = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        _registry = Path.Combine(_base, HostRegistry.FileName);
        _exeA = Path.Combine(_base, "VelaShellA.exe");
        _exeB = Path.Combine(_base, "VelaShellB.exe");
        File.WriteAllText(_exeA, "");
        File.WriteAllText(_exeB, "");
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
            // 尽力清理。
        }
    }

    private HostRegistryEntry Entry(string exePath, string version, int daysAgo = 0) => new()
    {
        ExePath = exePath,
        Version = version,
        ApiLevel = 1,
        SdkVersion = "1.4.0",
        AvaloniaVersion = "12.1.1",
        DataRoot = _base,
        LastSeen = DateTimeOffset.UtcNow.AddDays(-daysAgo)
    };

    [TestMethod]
    public void Upsert_ThenRead_RoundTrips()
    {
        Assert.IsTrue(HostRegistry.Upsert(Entry(_exeA, "1.4.2"), _registry));

        HostRegistryEntry host = HostRegistry.List(_registry).Single();
        Assert.AreEqual(_exeA, host.ExePath);
        Assert.AreEqual("1.4.2", host.Version);
        Assert.AreEqual("12.1.1", host.AvaloniaVersion);
        Assert.AreEqual(1, host.ApiLevel);
    }

    [TestMethod]
    public void Upsert_SameExecutableTwice_KeepsOneEntry()
    {
        HostRegistry.Upsert(Entry(_exeA, "1.4.2", daysAgo: 3), _registry);
        HostRegistry.Upsert(Entry(_exeA, "1.5.0"), _registry);

        HostRegistryEntry host = HostRegistry.List(_registry).Single();
        Assert.AreEqual("1.5.0", host.Version, "同一份安装再次启动应覆盖旧记录,而不是并列两条");
    }

    [TestMethod]
    public void Resolve_PrefersMostRecent_AndCanSelectByVersion()
    {
        // 正式安装 + 预览版并存:装了预览版的开发者开一下预览版,工具链不该就此改指预览版
        // ——所以既要按最近启动择新,也要能按版本号显式点名。
        HostRegistry.Upsert(Entry(_exeA, "1.4.2", daysAgo: 1), _registry);
        HostRegistry.Upsert(Entry(_exeB, "1.5.0-preview.1"), _registry);

        Assert.AreEqual(_exeB, HostRegistry.Resolve(null, _registry)!.ExePath);
        Assert.AreEqual(_exeA, HostRegistry.Resolve("1.4.2", _registry)!.ExePath);
        Assert.AreEqual(_exeA, HostRegistry.Resolve(_exeA, _registry)!.ExePath);
        Assert.IsNull(HostRegistry.Resolve("9.9.9", _registry));
    }

    [TestMethod]
    public void List_SkipsInstallationsThatAreGone()
    {
        HostRegistry.Upsert(Entry(_exeA, "1.4.2"), _registry);
        HostRegistry.Upsert(Entry(_exeB, "1.5.0"), _registry);
        File.Delete(_exeB); // 卸载 / 挪走

        Assert.ContainsSingle(HostRegistry.List(_registry));
        Assert.HasCount(2, HostRegistry.List(_registry, onlyExisting: false),
            "--all 仍应看得到已消失的那条(排障时要知道它曾经在)");
    }

    [TestMethod]
    public void Read_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(_registry, "{ this is not json");
        Assert.IsEmpty(HostRegistry.List(_registry));
        Assert.IsNull(HostRegistry.Resolve(null, _registry));

        // 而且还能被下一次登记修好:这个文件是缓存,不是资产。
        Assert.IsTrue(HostRegistry.Upsert(Entry(_exeA, "1.4.2"), _registry));
        Assert.ContainsSingle(HostRegistry.List(_registry));
    }

    [TestMethod]
    public void Read_MissingFile_ReturnsEmpty()
    {
        Assert.IsEmpty(HostRegistry.List(Path.Combine(_base, "nope", HostRegistry.FileName)));
    }

    [TestMethod]
    public void Upsert_WithoutExePath_IsRejected()
    {
        Assert.IsFalse(HostRegistry.Upsert(new() { Version = "1.0.0" }, _registry));
    }
}

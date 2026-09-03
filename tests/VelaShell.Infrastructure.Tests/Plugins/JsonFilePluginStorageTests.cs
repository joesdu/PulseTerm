using VelaShell.PluginSdk.Hosting;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class JsonFilePluginStorageTests
{
    private static readonly string[] NameKeyOnly = ["name"];
    private static readonly int[] Numbers = [1, 2, 3];

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    [TestMethod]
    public async Task SetGetRemove_RoundTrips()
    {
        JsonFilePluginStorage storage = new(_dir);
        await storage.SetAsync("count", 42);
        await storage.SetAsync("name", "vela");
        Assert.AreEqual(42, await storage.GetAsync<int>("count"));
        Assert.AreEqual("vela", await storage.GetAsync<string>("name"));
        Assert.IsNull(await storage.GetAsync<string>("missing"));
        Assert.IsTrue(await storage.RemoveAsync("count"));
        Assert.IsFalse(await storage.RemoveAsync("count"));
        // await 不能写在 Assert.AreSequenceEqual 的实参位置,理由见 AreSequenceEqualAwaitTests.cs。
        string[] remainingKeys = [.. await storage.GetKeysAsync()];
        Assert.AreSequenceEqual(NameKeyOnly, remainingKeys, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task Values_PersistAcrossInstances()
    {
        await new JsonFilePluginStorage(_dir).SetAsync("key", Numbers);
        int[]? roundTripped = await new JsonFilePluginStorage(_dir).GetAsync<int[]>("key");
        Assert.AreSequenceEqual(Numbers, roundTripped);
    }

    [TestMethod]
    public async Task CorruptFile_RecoversEmptyAndKeepsBackup()
    {
        string file = Path.Combine(_dir, "storage.json");
        await File.WriteAllTextAsync(file, "{ not valid json !!!");
        JsonFilePluginStorage storage = new(_dir);
        Assert.IsNull(await storage.GetAsync<string>("anything"));
        await storage.SetAsync("fresh", true);
        Assert.IsTrue(await storage.GetAsync<bool>("fresh"));
        Assert.IsTrue(File.Exists(file + ".bak"), "损坏文件应保留 .bak 供排查");
    }
}

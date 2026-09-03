using System.Text;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>插件数据的 SonnetDB 后端:类型化 KV、按插件隔离、机密加密落库、卸载整体清除。</summary>
[TestClass]
[TestCategory("Plugins")]
public class SonnetDbPluginDataStoreTests
{
    private sealed class Base64Protector : ISecretProtector
    {
        public string? Protect(string? plaintext) => string.IsNullOrEmpty(plaintext) || plaintext.StartsWith("enc:")
            ? plaintext
            : "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string? Unprotect(string? ciphertext) => ciphertext?.StartsWith("enc:") == true
            ? Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]))
            : ciphertext;
    }

    private static readonly int[] SnapshotValues = [1, 2, 3];
    private static readonly string[] CountAndSnapshotKeys = ["count", "snapshot"];
    private static readonly string[] BothPluginIds = ["acme.one", "acme.two"];
    private static readonly string[] SecondPluginIdOnly = ["acme.two"];

    private string _dbDir = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbPluginDataStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _engine = new(_dbDir);
        _store = new(_engine, new Base64Protector());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_dbDir, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    private sealed record Snapshot(string Name, int[] Values);

    [TestMethod]
    public async Task Storage_RoundTripsTypedValues_KeysAndRemove()
    {
        IPluginStorage storage = _store.CreateStorage("acme.one");
        await storage.SetAsync("count", 42);
        await storage.SetAsync("snapshot", new Snapshot("s1", SnapshotValues));
        Assert.AreEqual(42, await storage.GetAsync<int>("count"));
        Snapshot? snapshot = await storage.GetAsync<Snapshot>("snapshot");
        Assert.AreEqual("s1", snapshot!.Name);
        Assert.AreSequenceEqual(SnapshotValues, snapshot.Values);
        // await 不能写在 Assert.AreSequenceEqual 的实参位置,理由见 AreSequenceEqualAwaitTests.cs。
        string[] keys = [.. await storage.GetKeysAsync()];
        Assert.AreSequenceEqual(CountAndSnapshotKeys, keys, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.IsTrue(await storage.RemoveAsync("count"));
        Assert.IsFalse(await storage.RemoveAsync("count"));
        Assert.AreEqual(0, await storage.GetAsync<int>("count"));
    }

    [TestMethod]
    public async Task Storage_IsIsolatedPerPlugin()
    {
        IPluginStorage first = _store.CreateStorage("acme.one");
        IPluginStorage second = _store.CreateStorage("acme.two");
        await first.SetAsync("shared-key", "belongs-to-one");
        await second.SetAsync("shared-key", "belongs-to-two");

        Assert.AreEqual("belongs-to-one", await first.GetAsync<string>("shared-key"));
        Assert.AreEqual("belongs-to-two", await second.GetAsync<string>("shared-key"));
        Assert.HasCount(1, await first.GetKeysAsync());
        // 键名玩前缀花样也逃不出自己的命名空间(插件 id 字符集不含分隔符 '|')。
        Assert.IsNull(await first.GetAsync<string>("|acme.two|kv|shared-key"));
    }

    [TestMethod]
    public async Task Secrets_AreEncryptedAtRest_AndIsolated()
    {
        ISecretsApi secrets = _store.CreateSecrets("acme.one");
        await secrets.SetAsync("token", "hunter2-plaintext");
        Assert.AreEqual("hunter2-plaintext", await secrets.GetAsync("token"));

        // 落库的是密文:直接扫原始文档验证。
        List<string> rawDocs = await _engine.WithCollectionAsync(SonnetDbEngine.PluginDataCollection,
            store => store.Scan().Select(row => row.Json).ToList());
        Assert.DoesNotContain(json => json.Contains("hunter2-plaintext"), rawDocs, "机密必须加密落库");
        Assert.Contains(json => json.Contains("enc:"), rawDocs);

        Assert.IsNull(await _store.CreateSecrets("acme.two").GetAsync("token"), "机密同样按插件隔离");
        Assert.IsTrue(await secrets.DeleteAsync("token"));
        Assert.IsFalse(await secrets.DeleteAsync("token"));
    }

    [TestMethod]
    public async Task Purge_RemovesOnlyTargetPlugin()
    {
        await _store.CreateStorage("acme.one").SetAsync("k", 1);
        await _store.CreateSecrets("acme.one").SetAsync("s", "v");
        await _store.CreateStorage("acme.two").SetAsync("k", 2);

        // 同上:await 先落到局部变量。
        string[] before = [.. await _store.ListPluginIdsAsync()];
        Assert.AreSequenceEqual(BothPluginIds, before);
        await _store.PurgeAsync("acme.one");
        string[] afterPurge = [.. await _store.ListPluginIdsAsync()];
        Assert.AreSequenceEqual(SecondPluginIdOnly, afterPurge);
        Assert.AreEqual(0, await _store.CreateStorage("acme.one").GetAsync<int>("k"));
        Assert.IsNull(await _store.CreateSecrets("acme.one").GetAsync("s"));
        Assert.AreEqual(2, await _store.CreateStorage("acme.two").GetAsync<int>("k"));
    }

    [TestMethod]
    public async Task Secrets_WithoutProtector_RefuseInsteadOfPlaintext()
    {
        var withoutProtector = new SonnetDbPluginDataStore(_engine, protector: null);
        ISecretsApi secrets = withoutProtector.CreateSecrets("acme.one");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => secrets.SetAsync("k", "v"));
    }
}

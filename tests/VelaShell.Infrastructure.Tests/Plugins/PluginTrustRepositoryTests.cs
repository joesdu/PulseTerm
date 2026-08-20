using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Packaging;

namespace VelaShell.Infrastructure.Tests.Plugins;

internal sealed class TestSecretProtector : ISecretProtector
{
    public string? Protect(string? plaintext) => plaintext is null
        ? null
        : "enc1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string? ciphertext)
    {
        if (ciphertext is null || !ciphertext.StartsWith("enc1:", StringComparison.Ordinal))
        {
            return ciphertext;
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[5..]));
        }
        catch (FormatException)
        {
            return ciphertext;
        }
    }
}

[TestClass]
[TestCategory("Plugins")]
public sealed class PluginTrustRepositoryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // SonnetDB 的后台句柄若仍在收尾，临时目录留给系统清理。
        }
    }

    [TestMethod]
    public async Task SaveAndReload_ProtectsTrustStateInSonnetDb()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_root, "db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo());
        var state = new PluginTrustState
        {
            LegacyInstallMigrationCompleted = true,
            Publishers = [new(publicKey, VpxContainer.PublicKeyFingerprint(publicKey), DateTimeOffset.UtcNow)]
        };

        await repository.SaveAsync(state);

        string stored = (await engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection,
            collection => collection.Get("plugin_trust_v1")?.Json))!;
        Assert.DoesNotContain(publicKey, stored, "SonnetDB 文档不应暴露信任公钥或可编辑明文。");
        Assert.Contains("enc1:", stored);
        PluginTrustState reloaded = await repository.LoadAsync();
        Assert.AreEqual(publicKey, Assert.ContainsSingle(reloaded.Publishers).PublicKey);
    }

    [TestMethod]
    public async Task Load_TamperedCiphertext_FailsClosed()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_root, "db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        await repository.SaveAsync(new PluginTrustState());
        await engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, collection =>
        {
            collection.Upsert("plugin_trust_v1", JsonSerializer.Serialize(new { Payload = "enc1:AAAA" }));
            return null;
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => repository.LoadAsync());
    }

    [TestMethod]
    public async Task Load_MigratesLegacyJsonOnce_ThenRemovesItFromActiveUse()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo());
        string legacy = Path.Combine(_root, "trusted-plugin-publishers.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new[] { publicKey }));
        using var engine = new SonnetDbEngine(Path.Combine(_root, "db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector(), legacy);

        PluginTrustState state = await repository.LoadAsync();

        Assert.AreEqual(publicKey, Assert.ContainsSingle(state.Publishers).PublicKey);
        Assert.IsFalse(File.Exists(legacy));
        Assert.IsTrue(File.Exists(legacy + ".migrated"));
    }
}

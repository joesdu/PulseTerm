using System.Text;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class ProtectedSecretsCapabilityTests
{
    /// <summary>可逆的测试保护器:带前缀的 base64,便于断言"落盘的不是明文"。</summary>
    private sealed class Base64Protector : ISecretProtector
    {
        public string? Protect(string? plaintext) => string.IsNullOrEmpty(plaintext) || plaintext.StartsWith("enc:")
            ? plaintext
            : "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string? Unprotect(string? ciphertext) => ciphertext?.StartsWith("enc:") == true
            ? Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]))
            : ciphertext;
    }

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
    public async Task SetGetDelete_RoundTrips_AndPersistsAcrossInstances()
    {
        ISecretsApi secrets = new ProtectedSecretsCapability(_dir, new Base64Protector());
        await secrets.SetAsync("api-token", "s3cret-value");
        Assert.AreEqual("s3cret-value", await secrets.GetAsync("api-token"));
        Assert.IsNull(await secrets.GetAsync("missing"));

        // 新实例(模拟重启)仍能解出。
        ISecretsApi reopened = new ProtectedSecretsCapability(_dir, new Base64Protector());
        Assert.AreEqual("s3cret-value", await reopened.GetAsync("api-token"));

        Assert.IsTrue(await reopened.DeleteAsync("api-token"));
        Assert.IsFalse(await reopened.DeleteAsync("api-token"));
        Assert.IsNull(await reopened.GetAsync("api-token"));
    }

    [TestMethod]
    public async Task SecretsFile_NeverContainsPlaintext()
    {
        ISecretsApi secrets = new ProtectedSecretsCapability(_dir, new Base64Protector());
        await secrets.SetAsync("password", "hunter2-plaintext");
        string onDisk = await File.ReadAllTextAsync(Path.Combine(_dir, "secrets.json"));
        Assert.IsFalse(onDisk.Contains("hunter2-plaintext"), "机密必须加密落盘");
        StringAssert.Contains(onDisk, "enc:");
    }

    [TestMethod]
    public async Task WithoutProtector_SecretsCapabilityRefusesInsteadOfPlaintext()
    {
        ISecretsApi unavailable = new UnavailableSecrets();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unavailable.SetAsync("k", "v"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unavailable.GetAsync("k"));
    }
}

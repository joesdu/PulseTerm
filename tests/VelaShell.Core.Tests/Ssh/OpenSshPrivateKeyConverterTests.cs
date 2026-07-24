using System.Reflection;
using System.Security.Cryptography;
using Tmds.Ssh;
using VelaShell.Infrastructure.DependencyInjection;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 传统 PEM 私钥兼容:Tmds.Ssh 只认 OpenSSH 格式,导入的 PKCS#1/PKCS#8/加密 PKCS#8/SEC1 会被
/// 判 Unsupported format 而跳过 publickey 导致登录失败。<see cref="OpenSshPrivateKey.TryConvertToOpenSsh" />
/// 须把这些格式转成 Tmds 能加载的 OpenSSH 格式(用户反馈用 PKCS#1 的 id_rsa 登不上,正是这条)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class OpenSshPrivateKeyConverterTests
{
    // Tmds.Ssh 真正加载一遍转换结果;不抛即证明格式被接受。
    private static async Task AssertTmdsLoads(char[] openSshPem)
    {
        var cred = new PrivateKeyCredential(openSshPem, (string?)null, "test");
        MethodInfo load = typeof(PrivateKeyCredential).GetMethod("LoadKeyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        object valueTask = load.Invoke(cred, [CancellationToken.None])!;
        var t = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await t;
    }

    [TestMethod]
    public async Task Rsa_Pkcs1_Converts_AndLoads() // 用户反馈的确切格式:-----BEGIN RSA PRIVATE KEY-----
    {
        using var rsa = RSA.Create(2048);
        char[]? converted = OpenSshPrivateKey.TryConvertToOpenSsh(rsa.ExportRSAPrivateKeyPem(), null);
        Assert.IsNotNull(converted);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", new string(converted));
        await AssertTmdsLoads(converted);
    }

    [TestMethod]
    public async Task Rsa_Pkcs8_Converts_AndLoads()
    {
        using var rsa = RSA.Create(2048);
        char[]? converted = OpenSshPrivateKey.TryConvertToOpenSsh(rsa.ExportPkcs8PrivateKeyPem(), null);
        Assert.IsNotNull(converted);
        await AssertTmdsLoads(converted);
    }

    [TestMethod]
    public async Task Rsa_EncryptedPkcs8_WithPassphrase_Converts_AndLoads()
    {
        using var rsa = RSA.Create(2048);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        string pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("s3cret", pbe);
        char[]? converted = OpenSshPrivateKey.TryConvertToOpenSsh(pem, "s3cret");
        Assert.IsNotNull(converted);
        await AssertTmdsLoads(converted);
    }

    [TestMethod]
    [DataRow(256)]
    [DataRow(384)]
    [DataRow(521)]
    public async Task Ecdsa_Sec1_Converts_AndLoads(int keySize)
    {
        ECCurve curve = keySize switch
        {
            256 => ECCurve.NamedCurves.nistP256,
            384 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521
        };
        using var ec = ECDsa.Create(curve);
        char[]? converted = OpenSshPrivateKey.TryConvertToOpenSsh(ec.ExportECPrivateKeyPem(), null);
        Assert.IsNotNull(converted);
        await AssertTmdsLoads(converted);
    }

    [TestMethod]
    public void AlreadyOpenSsh_ReturnsNull_ForPassThrough()
    {
        // 已是 OpenSSH 格式的私钥应原样交给 Tmds(返回 null),不做无谓转换。
        string openssh = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";
        Assert.IsNull(OpenSshPrivateKey.TryConvertToOpenSsh(openssh, null));
    }

    [TestMethod]
    public async Task BuildPrivateKeyCredential_OnPkcs1File_ProducesLoadableCredential()
    {
        // 端到端:把 PKCS#1 私钥写到文件,走 AddCredential 的真实构造路径,结果必须能被 Tmds 加载。
        string path = Path.GetTempFileName();
        try
        {
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
            Credential cred = InfrastructureServiceCollectionExtensions.BuildPrivateKeyCredential(path, null);
            Assert.IsInstanceOfType<PrivateKeyCredential>(cred);
            MethodInfo load = typeof(PrivateKeyCredential).GetMethod("LoadKeyAsync",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            object valueTask = load.Invoke(cred, [CancellationToken.None])!;
            await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

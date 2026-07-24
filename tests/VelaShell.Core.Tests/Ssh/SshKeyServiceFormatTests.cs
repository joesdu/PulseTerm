using System.Reflection;
using Tmds.Ssh;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 生成的私钥必须是 Tmds.Ssh 能加载的 OpenSSH 格式。曾用 <c>ExportRSAPrivateKeyPem()</c> 写 PKCS#1
/// (-----BEGIN RSA PRIVATE KEY-----),Tmds.Ssh 0.23 判 "Unsupported format" 而跳过 publickey,
/// 用户用本应用生成的密钥无法登录(诊断第 4 步:no methods failed、skipped publickey)。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class SshKeyServiceFormatTests
{
    [TestMethod]
    public async Task GeneratedRsaKey_IsOpenSshFormat_AndLoadableByTmdsSsh()
    {
        string dir = Path.Combine(Path.GetTempPath(), "velashell-keytest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new SshKeyService(dir);
            SshKeyInfo info = await svc.GenerateRsaKeyAsync("id_test", 2048);

            string pem = await File.ReadAllTextAsync(info.PrivateKeyPath);
            Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", pem,
                "私钥必须是 OpenSSH 格式,否则 Tmds.Ssh 无法加载");

            // Tmds.Ssh 真正加载一遍:能取出密钥即证明格式被接受(不抛 Unsupported format)。
            var cred = new PrivateKeyCredential(info.PrivateKeyPath, (string?)null, "test");
            MethodInfo load = typeof(PrivateKeyCredential).GetMethod("LoadKeyAsync",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            object valueTask = load.Invoke(cred, [CancellationToken.None])!;
            var t = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
            await t; // 不抛即通过
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

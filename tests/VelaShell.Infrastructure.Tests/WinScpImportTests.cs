using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;

namespace VelaShell.Infrastructure.Tests;

/// <summary>WinSCP 会话导入:0xA3 密码编解码往返 + 本机真实数据验证。</summary>
[TestClass]
public class WinScpImportTests
{
    private const int PwMagic = 0xA3;
    private const int PwFlag = 0xFF;

    /// <summary>用 WinSCP 的编码方式(含 用户名+主机名 前缀)构造密文,<see cref="WinScpCrypto.Decrypt" /> 应还原明文。</summary>
    [TestMethod]
    [DataRow("root", "192.168.1.1", "hunter2")]
    [DataRow("admin", "example.com", "p@ss W0rd!")]
    [DataRow("u", "h", "x")]
    public void Crypto_RoundTrip_Recovers(string user, string host, string password)
    {
        string encoded = Encode(user, host, password);
        Assert.AreEqual(password, WinScpCrypto.Decrypt(host, user, encoded));
    }

    /// <summary>空/非法十六进制输入安全返回 null。</summary>
    [TestMethod]
    public void Crypto_HandlesEmptyAndGarbage()
    {
        Assert.IsNull(WinScpCrypto.Decrypt("h", "u", null));
        Assert.IsNull(WinScpCrypto.Decrypt("h", "u", ""));
        Assert.IsNull(WinScpCrypto.Decrypt("h", "u", "ZZZZ"));   // 非十六进制
        Assert.IsNull(WinScpCrypto.Decrypt("h", "u", "A"));      // 数据不足
    }

    /// <summary>INI 中未启用主密码时,含密码会话应被解出;来源可为文件路径。</summary>
    [TestMethod]
    public async Task Scan_FromIni_DecodesPassword()
    {
        string host = "10.0.0.5";
        string user = "deploy";
        string password = "S3cr3t!";
        string ini = Path.Combine(Path.GetTempPath(), $"winscp-test-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(ini,
            $"""
             [Configuration\Security]
             UseMasterPassword=0
             [Sessions\prod%20box]
             HostName={host}
             UserName={user}
             PortNumber=2222
             FSProtocol=5
             Password={Encode(user, host, password)}
             """);
        try
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
            var service = new WinScpImportService(repository);

            SessionImportScan scan = await service.ScanAsync(ini);

            Assert.IsFalse(scan.MasterPasswordEnabled);
            Assert.AreEqual(1, scan.Items.Count);
            ImportedSession item = scan.Items[0];
            Assert.AreEqual("prod box", item.Name);       // %20 已解码
            Assert.AreEqual(host, item.Host);
            Assert.AreEqual(2222, item.Port);
            Assert.AreEqual(user, item.Username);
            Assert.AreEqual(password, item.Password);
            Assert.IsTrue(item.PasswordRecovered);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    /// <summary>启用主密码时,不尝试解密,密码留空。</summary>
    [TestMethod]
    public async Task Scan_FromIni_MasterPassword_SkipsDecryption()
    {
        string ini = Path.Combine(Path.GetTempPath(), $"winscp-mpw-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(ini,
            """
            [Configuration\Security]
            UseMasterPassword=1
            [Sessions\box]
            HostName=host
            UserName=user
            Password=ABCDEF0123
            """);
        try
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
            var service = new WinScpImportService(repository);

            SessionImportScan scan = await service.ScanAsync(ini);

            Assert.IsTrue(scan.MasterPasswordEnabled);
            Assert.AreEqual(1, scan.Items.Count);
            Assert.IsFalse(scan.Items[0].PasswordRecovered);
            Assert.IsTrue(scan.Items[0].HasEncryptedPassword);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    /// <summary>本机真实 WinSCP 数据的端到端验证:含密码会话应至少有一条被解出。未安装/主密码/无密码时 Inconclusive。</summary>
    [TestMethod]
    public async Task Scan_RealWinScp_RecoversAtLeastOnePassword()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var service = new WinScpImportService(repository);

        if (service.DetectDefaultSource() is null)
        {
            Assert.Inconclusive("本机未安装 WinSCP 或无保存的会话。");
            return;
        }

        SessionImportScan scan = await service.ScanAsync(null);
        if (scan.MasterPasswordEnabled)
        {
            Assert.Inconclusive("WinSCP 启用了主密码,无法验证解密。");
            return;
        }

        var withPassword = scan.Items.Where(i => i is { IsSupported: true, HasEncryptedPassword: true }).ToList();
        if (withPassword.Count == 0)
        {
            Assert.Inconclusive("没有含已保存密码的 WinSCP 会话可供验证。");
            return;
        }

        Assert.IsTrue(
            withPassword.Any(i => i.PasswordRecovered),
            "含密码的 WinSCP 会话应至少有一条被成功解出;若全部失败,说明 0xA3 解码或字段读取与真实 WinSCP 不符。");
    }

    /// <summary>WinSCP 0xA3 编码(flag=FF 新格式,无随机填充):明文 = 用户名+主机名+密码。</summary>
    private static string Encode(string user, string host, string password)
    {
        string clear = user + host + password;
        var bytes = new List<byte> { PwFlag, 0x00, (byte)clear.Length, 0x00 };
        bytes.AddRange(clear.Select(static c => (byte)c));

        var sb = new StringBuilder(bytes.Count * 2);
        foreach (byte value in bytes)
        {
            int x = (~value & 0xFF) ^ PwMagic;
            sb.Append("0123456789ABCDEF"[(x >> 4) & 0xF]);
            sb.Append("0123456789ABCDEF"[x & 0xF]);
        }
        return sb.ToString();
    }
}

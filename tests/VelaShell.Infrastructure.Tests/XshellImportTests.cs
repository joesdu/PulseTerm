using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;

namespace VelaShell.Infrastructure.Tests;

/// <summary>Xshell 会话导入:INI 解析、RC4、密码还原(多版本密钥 + 本机真实数据验证)。</summary>
[TestClass]
public class XshellImportTests
{
    /// <summary>分节解析:各字段只在其所属节内取值,错误节里的同名键不得覆盖。</summary>
    [TestMethod]
    public void IniParser_ReadsSectionScopedFields()
    {
        string[] lines =
        [
            "[SessionInfo]", "Version=6.0",
            "[CONNECTION]", "Host=192.168.1.10", "Port=2222", "Protocol=SSH",
            "[CONNECTION:AUTHENTICATION]", "UserName=root", "Password=QUJDQQ==", "Method=0", "UserKey=",
            "[TERMINAL]", "Host=ignore-me", "Rows=24"
        ];

        XshellSessionFile result = XshellIniParser.Parse(lines);

        Assert.AreEqual("6.0", result.Version);
        Assert.AreEqual("192.168.1.10", result.Host);
        Assert.AreEqual(2222, result.Port);
        Assert.AreEqual("SSH", result.Protocol);
        Assert.AreEqual("root", result.UserName);
        Assert.AreEqual("QUJDQQ==", result.EncryptedPassword);
    }

    /// <summary>RC4 是对称变换:同密钥两次变换还原原文。</summary>
    [TestMethod]
    public void Rc4_IsSymmetric()
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("unit-test-key"));
        byte[] plain = Encoding.UTF8.GetBytes("hunter2!§你好");

        byte[] cipher = Rc4.Transform(key, plain);
        byte[] round = Rc4.Transform(key, cipher);

        CollectionAssert.AreEqual(plain, round);
    }

    /// <summary>
    /// 多版本密钥:分别用 v5/6/7.0 方案(name+sid)与 v7/8 方案(reverse(sid)+name)构造 blob,
    /// TryDecryptPassword 都应还原 —— 证明依次试探密钥可兼容 6/7/8。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "Xshell 5/6/7.0 key = SHA256(name+sid)")]
    [DataRow(true, DisplayName = "Xshell 7/8 key = SHA256(reverse(sid)+name)")]
    public void Crypto_RoundTrip_AcrossVersions(bool reverseScheme)
    {
        const string user = "tester";
        const string sid = "S-1-5-21-11-22-33-1001";
        const string password = "P@ssw0rd-9";

        byte[] key = reverseScheme
            ? SHA256.HashData(Encoding.UTF8.GetBytes(new string([.. sid.Reverse()]) + user))
            : SHA256.HashData(Encoding.UTF8.GetBytes(user + sid));
        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] blob = [.. Rc4.Transform(key, plain), .. SHA256.HashData(plain)];

        Assert.AreEqual(password, XshellCrypto.TryDecryptPassword(Convert.ToBase64String(blob), user, sid));
    }

    /// <summary>校验被破坏或 SID 不符时拒绝(不返回可能是垃圾的明文);空/损坏输入安全返回 null。</summary>
    [TestMethod]
    public void Crypto_RejectsTamperedAndGarbage()
    {
        const string user = "tester";
        const string sid = "S-1-5-21-11-22-33-1001";
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(user + sid));
        byte[] plain = Encoding.UTF8.GetBytes("secret");
        byte[] body = Rc4.Transform(key, plain);

        byte[] broken = [.. body, .. new byte[32]];
        Assert.IsNull(XshellCrypto.TryDecryptPassword(Convert.ToBase64String(broken), user, sid));

        byte[] valid = [.. body, .. SHA256.HashData(plain)];
        Assert.IsNull(XshellCrypto.TryDecryptPassword(Convert.ToBase64String(valid), user, "S-1-5-21-9-9-9-9"));

        Assert.IsNull(XshellCrypto.TryDecryptPassword(null, "u", "s"));
        Assert.IsNull(XshellCrypto.TryDecryptPassword("", "u", "s"));
        Assert.IsNull(XshellCrypto.TryDecryptPassword("not-base64!!!", "u", "s"));
        Assert.IsNull(XshellCrypto.TryDecryptPassword("QUJD", "u", "s")); // 太短(<32B)
    }

    /// <summary>
    /// 针对本机真实 Xshell 数据的端到端验证:含加密密码的会话应至少有一条被成功还原。
    /// 无 Xshell / 启用主密码 / 无带密码会话时置为 Inconclusive(绝不把凭据写入仓库)。
    /// </summary>
    [TestMethod]
    public async Task Scan_RealXshellSessions_RecoversAtLeastOnePassword()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var service = new XshellImportService(repository);

        string? source = service.DetectDefaultSource();
        if (source is null)
        {
            Assert.Inconclusive("本机未安装 Xshell 或无法定位 Sessions 目录。");
            return;
        }

        SessionImportScan scan = await service.ScanAsync(null);
        if (scan.MasterPasswordEnabled)
        {
            Assert.Inconclusive("Xshell 启用了主密码,无法在无主密码时验证解密。");
            return;
        }

        var withPassword = scan.Items.Where(i => i is { IsSupported: true, HasEncryptedPassword: true }).ToList();
        if (withPassword.Count == 0)
        {
            Assert.Inconclusive("没有含已保存密码的会话可供验证。");
            return;
        }

        Assert.IsTrue(
            withPassword.Any(i => i.PasswordRecovered),
            "含加密密码的会话应至少有一条被成功还原;若全部失败,说明密钥派生或尾部校验假设与真实 Xshell 不符。");
    }
}

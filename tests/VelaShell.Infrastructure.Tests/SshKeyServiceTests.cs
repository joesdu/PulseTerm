using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class SshKeyServiceTests : IDisposable
{
    private readonly SshKeyService _service;
    private readonly string _sshDir;

    /// <summary>扮演 ~/.ssh 之外的地方(用户从别处挑文件导入)。</summary>
    private readonly string _external;

    public SshKeyServiceTests()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell_sshkeys_{Guid.NewGuid():N}");
        _sshDir = Path.Combine(root, "ssh");
        _external = Path.Combine(root, "elsewhere");
        _service = new(_sshDir);
    }

    public void Dispose()
    {
        string? root = Path.GetDirectoryName(_sshDir);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Generate_List_Delete_RoundTrips()
    {
        SshKeyInfo generated = await _service.GenerateRsaKeyAsync("test_key", 2048);
        Assert.AreEqual("test_key", generated.Name);
        Assert.AreEqual("RSA 2048", generated.Type);
        Assert.StartsWith("SHA256:", generated.Fingerprint);
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key")));
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key.pub")));
        List<SshKeyInfo> listed = await _service.ListKeysAsync();
        Assert.HasCount(1, listed);
        Assert.AreEqual(generated.Fingerprint, listed[0].Fingerprint, "列举解析的指纹应与生成时一致");
        Assert.AreEqual("RSA 2048", listed[0].Type, "公钥 blob 解析出的位数应一致");
        Assert.StartsWith("ssh-rsa ", listed[0].PublicKeyLine);
        await _service.DeleteKeyAsync("test_key");
        Assert.IsEmpty(await _service.ListKeysAsync());
    }

    [TestMethod]
    public async Task Generate_DuplicateName_Throws()
    {
        await _service.GenerateRsaKeyAsync("dup", 2048);
        await Assert.ThrowsExactlyAsync<IOException>(() => _service.GenerateRsaKeyAsync("dup", 2048));
    }

    [TestMethod]
    public async Task List_EmptyDirectory_ReturnsEmpty() => Assert.IsEmpty(await _service.ListKeysAsync());

    // ———————————————————— 导入:不许伪报成功 ————————————————————
    //
    // 导入以前是"能抄就抄":源文件不在就跳过复制,却照样返回一条 Unknown 条目。
    // 界面于是说"已导入 xxx",而 ~/.ssh 里什么都没多 —— 用户下次连接才发现密钥根本不存在。

    /// <summary>正常导入:私钥 + 公钥都在,导入后能被列举出来。</summary>
    [TestMethod]
    public async Task Import_WithBothFiles_ShowsUpInTheList()
    {
        (string privatePath, _) = await CreateExternalKeyAsync("outside_key");

        SshKeyInfo? imported = await _service.ImportKeyAsync(privatePath);

        Assert.IsNotNull(imported);
        Assert.AreEqual("outside_key", imported.Name);
        Assert.StartsWith("SHA256:", imported.Fingerprint, "应当报告真实解析出的指纹,而不是 Unknown。");
        Assert.ContainsSingle(await _service.ListKeysAsync(), "导入后刷新列表应当看得到它。");
    }

    /// <summary>源私钥不存在:必须报错,而不是返回一条查无实据的条目。</summary>
    [TestMethod]
    public async Task Import_WhenThePrivateKeyIsMissing_Throws()
    {
        string missing = Path.Combine(_external, "not_here");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _service.ImportKeyAsync(missing));
        Assert.IsEmpty(await _service.ListKeysAsync());
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "not_here")), "失败却留下了半份文件。");
    }

    /// <summary>
    /// 只有私钥、没有 .pub:明确拒绝。
    /// </summary>
    /// <remarks>
    /// 列举是按 <c>*.pub</c> 走的,所以只导私钥会"导入成功 → 列表里没有" ——
    /// 看上去就像程序把密钥弄丢了。当场说清比事后困惑好。
    /// </remarks>
    [TestMethod]
    public async Task Import_WithoutTheMatchingPublicKey_Throws()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("lonely");
        File.Delete(publicPath);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _service.ImportKeyAsync(privatePath));
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "lonely")), "拒绝之后不该留下私钥副本。");
    }

    /// <summary>挑中一个普通文本文件:不是私钥就不该被当成密钥收下。</summary>
    [TestMethod]
    public async Task Import_OfSomethingThatIsNotAKey_Throws()
    {
        Directory.CreateDirectory(_external);
        string bogus = Path.Combine(_external, "notes.txt");
        await File.WriteAllTextAsync(bogus, "just some notes\n");
        await File.WriteAllTextAsync(bogus + ".pub", "ssh-rsa AAAA notes\n");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(bogus));
        Assert.IsEmpty(await _service.ListKeysAsync());
    }

    /// <summary>.pub 解析不出来时整体回滚,不留下一份进不了列表的私钥。</summary>
    [TestMethod]
    public async Task Import_WithAnUnreadablePublicKey_RollsBack()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("bad_pub");
        await File.WriteAllTextAsync(publicPath, "this is not a public key\n");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(privatePath));
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "bad_pub")), "私钥副本没有随失败一起清掉。");
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "bad_pub.pub")));
    }

    /// <summary>同名已存在:返回 null(不是异常),且不覆盖现有密钥。</summary>
    [TestMethod]
    public async Task Import_WhenTheNameIsTaken_ReturnsNullAndKeepsTheExistingKey()
    {
        SshKeyInfo existing = await _service.GenerateRsaKeyAsync("taken", 2048);
        (string privatePath, _) = await CreateExternalKeyAsync("taken");

        Assert.IsNull(await _service.ImportKeyAsync(privatePath));
        Assert.AreEqual(existing.Fingerprint, (await _service.ListKeysAsync()).Single().Fingerprint);
    }

    /// <summary>导入的私钥要收成仅属主可读写,否则 OpenSSH 直接拒用。</summary>
    [TestMethod]
    public async Task Import_TightensPermissionsOnThePrivateKey()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix 权限位在 Windows 上不适用。");
            return;
        }
        (string privatePath, _) = await CreateExternalKeyAsync("perm_key");
        File.SetUnixFileMode(
            privatePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await _service.ImportKeyAsync(privatePath);

        UnixFileMode mode = File.GetUnixFileMode(Path.Combine(_sshDir, "perm_key"));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    /// <summary>在 ~/.ssh 之外先造一对真密钥,再把它当作"外部密钥"导入。</summary>
    private async Task<(string PrivatePath, string PublicPath)> CreateExternalKeyAsync(string name)
    {
        var source = new SshKeyService(_external);
        await source.GenerateRsaKeyAsync(name, 2048);
        return (Path.Combine(_external, name), Path.Combine(_external, name + ".pub"));
    }
}

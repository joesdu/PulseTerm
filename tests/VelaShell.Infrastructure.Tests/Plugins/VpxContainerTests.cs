using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.PluginSdk.Packaging;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// <c>.vpx</c> 容器格式:魔数、摘要、掩码、签名。
/// 这里的每条断言都是**格式的地面真值** —— 一旦容器布局变动而这些还全绿,
/// 说明改的是测试而不是格式,那正是"改后缀就能解压"重新溜回来的路径。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class VpxContainerTests
{
    private string _work = null!;

    [TestInitialize]
    public void Setup()
    {
        _work = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    /// <summary>造一个最小的插件目录(清单 + 一个假入口文件)。</summary>
    private string CreatePluginDirectory(string id = "acme.packaged")
    {
        string dir = Path.Combine(_work, "src-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "Packaged", "entry": "Fake.dll" }
            """);
        File.WriteAllBytes(Path.Combine(dir, "Fake.dll"), [.. Enumerable.Range(0, 4096).Select(i => (byte)i)]);
        return dir;
    }

    private string PackFile(string name = "pkg.vpx") => Path.Combine(_work, name);

    [TestMethod]
    public void Pack_ThenOpenPayload_RoundTripsTheZip()
    {
        string source = CreatePluginDirectory();
        string package = PackFile();

        VpxContainer.Pack(source, package);

        Assert.IsTrue(VpxContainer.IsVpx(package), "包应以 VPX 魔数开头");
        Assert.IsFalse(ContainsZipLocalHeader(File.ReadAllBytes(package).AsSpan(0, 4)), "包头不该是 zip 特征字节");

        using Stream payload = VpxContainer.OpenPayload(package, out VpxPackageInfo info);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.IsNotNull(archive.GetEntry("plugin.json"));
        Assert.IsNotNull(archive.GetEntry("Fake.dll"));
        Assert.AreEqual(VpxContainer.CurrentFormatVersion, info.FormatVersion);
        Assert.IsTrue(info.Flags.HasFlag(VpxFlags.Masked), "默认应开掩码");
    }

    [TestMethod]
    public void Pack_MaskedPayload_DoesNotExposeZipSignature()
    {
        // 掩码的全部意义:通用解压工具在包体里嗅不到 zip 的本地文件头。
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package);

        byte[] bytes = File.ReadAllBytes(package);
        ReadOnlySpan<byte> body = bytes.AsSpan(VpxContainer.HeaderSize);
        Assert.IsFalse(ContainsZipLocalHeader(body), "掩码后的载荷里不该出现 PK\\x03\\x04");
    }

    [TestMethod]
    public void Pack_WithoutMask_KeepsPlainZipPayload()
    {
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package, new() { Mask = false });

        byte[] bytes = File.ReadAllBytes(package);
        Assert.IsTrue(ContainsZipLocalHeader(bytes.AsSpan(VpxContainer.HeaderSize)));
        // 但整个文件依然不是 zip:开头是容器头,通用工具照样打不开。
        Assert.IsTrue(VpxContainer.IsVpx(package));
        using Stream payload = VpxContainer.OpenPayload(package);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.IsNotNull(archive.GetEntry("plugin.json"));
    }

    [TestMethod]
    public void OpenPayload_PlainZipRenamedToVpx_IsRejectedWithActionableMessage()
    {
        string zip = Path.Combine(_work, "renamed.vpx");
        ZipFile.CreateFromDirectory(CreatePluginDirectory(), zip);

        Assert.IsFalse(VpxContainer.IsVpx(zip));
        VpxFormatException ex = Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.OpenPayload(zip));
        Assert.Contains("vela-plugin pack", ex.Message, "错误里要给出补救办法");
    }

    [TestMethod]
    public void OpenPayload_TamperedPayload_IsRejected()
    {
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package);

        // 动载荷中间的一个字节:长度不变,只有摘要能发现。
        byte[] bytes = File.ReadAllBytes(package);
        bytes[VpxContainer.HeaderSize + 100] ^= 0xFF;
        File.WriteAllBytes(package, bytes);

        VpxFormatException ex = Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.OpenPayload(package));
        Assert.Contains("digest", ex.Message);
    }

    [TestMethod]
    public void OpenPayload_CorruptHeader_IsRejectedBeforeReadingPayload()
    {
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package);

        byte[] bytes = File.ReadAllBytes(package);
        bytes[8] ^= 0xFF; // 载荷长度字段:头部 CRC 应当发现
        File.WriteAllBytes(package, bytes);

        VpxFormatException ex = Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.OpenPayload(package));
        Assert.Contains("header", ex.Message);
    }

    [TestMethod]
    public void OpenPayload_Truncated_IsRejected()
    {
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package);

        byte[] bytes = File.ReadAllBytes(package);
        File.WriteAllBytes(package, bytes[..(bytes.Length / 2)]);

        Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.OpenPayload(package));
    }

    [TestMethod]
    public void OpenPayload_NotAPackageAtAll_IsRejected()
    {
        string junk = Path.Combine(_work, "junk.vpx");
        File.WriteAllText(junk, "this is definitely not a plugin package");
        Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.OpenPayload(junk));
    }

    [TestMethod]
    public void Signature_RoundTrips_AndIsTrustedForItsOwnKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package, new() { SigningKey = key });

        var info = VpxContainer.ReadInfo(package);
        Assert.IsNotNull(info.Signature);
        Assert.AreEqual(VpxContainer.SignatureAlgorithm, info.Signature.Algorithm);

        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Assert.AreEqual(VpxSignatureState.Trusted, VpxContainer.VerifySignature(info, [publicKey]));
        Assert.AreEqual(VpxSignatureState.Trusted, VpxContainer.VerifySignature(info), "不给信任集合时,有效签名即算受信");
    }

    [TestMethod]
    public void Signature_FromAnotherKey_IsUntrustedButValid()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string package = PackFile();
        VpxContainer.Pack(CreatePluginDirectory(), package, new() { SigningKey = signer });

        var info = VpxContainer.ReadInfo(package);
        Assert.AreEqual(VpxSignatureState.Untrusted,
            VpxContainer.VerifySignature(info, [Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo())]));
    }

    [TestMethod]
    public void Signature_OverPayloadSwappedAfterSigning_IsInvalid()
    {
        // 攻击模型:拿一个签过名的包,把载荷换成自己的,再把头部的长度与摘要一并改对。
        // 签名覆盖的是整个头部,所以这样改完签名必然对不上。
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string good = PackFile("good.vpx");
        string evil = PackFile("evil.vpx");
        VpxContainer.Pack(CreatePluginDirectory("acme.good"), good, new() { SigningKey = key });
        VpxContainer.Pack(CreatePluginDirectory("acme.evil"), evil);

        byte[] goodBytes = File.ReadAllBytes(good);
        byte[] evilBytes = File.ReadAllBytes(evil);
        var goodInfo = VpxContainer.ReadInfo(good);
        // 恶意包的头部 + 正牌包的签名块:头部变了,签名自然失效。
        byte[] forged =
        [
            .. evilBytes.AsSpan(0, VpxContainer.HeaderSize + (int)VpxContainer.ReadInfo(evil).PayloadLength),
            .. goodBytes.AsSpan(VpxContainer.HeaderSize + (int)goodInfo.PayloadLength)
        ];
        // 把"已签名"标志打上,好让读取器去解析尾部的签名块。
        forged[6] |= (byte)VpxFlags.Signed;
        FixHeaderChecksum(forged);
        string package = PackFile("forged.vpx");
        File.WriteAllBytes(package, forged);

        var info = VpxContainer.ReadInfo(package);
        Assert.AreEqual(VpxSignatureState.Invalid, VpxContainer.VerifySignature(info));
    }

    [TestMethod]
    public void Pack_DirectoryWithoutManifest_IsRejected()
    {
        string dir = Path.Combine(_work, "empty");
        Directory.CreateDirectory(dir);
        Assert.ThrowsExactly<VpxFormatException>(() => VpxContainer.Pack(dir, PackFile()));
    }

    private static bool ContainsZipLocalHeader(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>重算头部 CRC(测试构造伪包时用;算法与容器内实现一致)。</summary>
    private static void FixHeaderChecksum(Span<byte> header)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in header[..56])
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[56..], ~crc);
    }
}

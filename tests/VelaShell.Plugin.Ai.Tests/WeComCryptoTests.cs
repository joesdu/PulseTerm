using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Plugin.Ai.Bridge.Channels.WeCom;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 企业微信回调的签名与加解密。
/// </summary>
/// <remarks>
/// <b>这是一道安全边界:</b>验签是唯一能证明"这条回调真的来自企业微信"的东西,
/// 而解密的报文布局(16 随机 + 4 字节大端长度 + 正文 + corpid、按 32 字节补位)
/// 写错一个常数,表现就是"配置回调地址时一直提示失败",从日志里根本看不出是哪一步。
/// </remarks>
[TestClass]
public sealed class WeComCryptoTests
{
    /// <summary>
    /// 43 个字符的 EncodingAESKey(Base64 字母表),补一个 "=" 后解出来正好 32 字节。
    /// </summary>
    /// <remarks>
    /// 末位刻意用 "E":补一个 "=" 之后最后一组只有 3 个字符,.NET 9 起会校验那一组的
    /// 尾部空位必须为 0,末位换成 "G" 这类低两位非零的字符会被判成非法 Base64。
    /// 企业微信自己生成的 key 天然满足这一点。
    /// </remarks>
    private const string SampleKey = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFE";

    [TestMethod]
    public void ParseKey_Yields32Bytes()
        => Assert.HasCount(32, WeComCrypto.ParseKey(SampleKey));

    [TestMethod]
    public void ParseKey_RejectsAKeyOfTheWrongLength()
        => Assert.ThrowsExactly<ArgumentException>(() => WeComCrypto.ParseKey("tooshort"));

    [TestMethod]
    public void EncryptThenDecrypt_RoundTrips()
    {
        byte[] key = WeComCrypto.ParseKey(SampleKey);
        const string message = "<xml><Content>重启一下 nginx</Content></xml>";

        string cipher = WeComCrypto.Encrypt(key, message, "ww1234567890");
        (string plain, string receiveId) = WeComCrypto.Decrypt(key, cipher);

        Assert.AreEqual(message, plain);
        Assert.AreEqual("ww1234567890", receiveId);
    }

    /// <summary>
    /// 独立按官方文档的布局拼一段明文,验证解密读的是同一套常数
    /// (而不是"我加密怎么写、我解密就怎么读"的自圆其说)。
    /// </summary>
    [TestMethod]
    public void Decrypt_ReadsTheDocumentedLayout()
    {
        byte[] key = WeComCrypto.ParseKey(SampleKey);
        byte[] text = Encoding.UTF8.GetBytes("hello 世界");
        byte[] corp = Encoding.UTF8.GetBytes("wwCORP");
        byte[] body = new byte[16 + 4 + text.Length + corp.Length];
        RandomNumberGenerator.Fill(body.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16, 4), text.Length);
        text.CopyTo(body, 20);
        corp.CopyTo(body, 20 + text.Length);
        // 补位块是 32 而不是 AES 的 16
        int pad = 32 - (body.Length % 32);
        byte[] padded = new byte[body.Length + pad];
        body.CopyTo(padded, 0);
        padded.AsSpan(body.Length).Fill((byte)pad);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        string cipher = Convert.ToBase64String(aes.EncryptCbc(padded, key.AsSpan(0, 16).ToArray(), PaddingMode.None));

        (string plain, string receiveId) = WeComCrypto.Decrypt(key, cipher);

        Assert.AreEqual("hello 世界", plain);
        Assert.AreEqual("wwCORP", receiveId);
    }

    /// <summary>
    /// 长度不是分组整数倍的密文要抛,不能吐出半截明文。
    /// </summary>
    /// <remarks>
    /// 刻意不测"翻转一个字节":那样解出来的补位字节是随机的,大多数时候确实会被挡住,
    /// 但偶尔会撞上一个合法值 —— 一个偶尔红一次的用例比没有还糟。
    /// 真正挡住篡改的是验签(见 <see cref="WeComCrypto.Verify" />),不是解密。
    /// </remarks>
    [TestMethod]
    public void Decrypt_ThrowsOnAMalformedPayload()
    {
        byte[] key = WeComCrypto.ParseKey(SampleKey);
        string malformed = Convert.ToBase64String(new byte[20]); // 20 不是 16 的整数倍

        Assert.ThrowsExactly<CryptographicException>(() => WeComCrypto.Decrypt(key, malformed));
    }

    /// <summary>签名是把四个串<b>排序</b>后再拼,所以参数顺序不影响结果。</summary>
    [TestMethod]
    public void Sign_IsOrderIndependent()
    {
        string a = WeComCrypto.Sign("tok", "1700000000", "nonce1", "cipher");
        string b = WeComCrypto.Sign("cipher", "nonce1", "tok", "1700000000");

        Assert.AreEqual(a, b);
    }

    /// <summary>算法钉死在 SHA1(排序后拼接)的十六进制小写上。</summary>
    [TestMethod]
    public void Sign_IsSha1OfTheSortedConcatenation()
    {
        string[] parts = ["tok", "1700000000", "nonce1", "cipher"];
        Array.Sort(parts, StringComparer.Ordinal);
        string expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(parts))))
                                 .ToLowerInvariant();

        Assert.AreEqual(expected, WeComCrypto.Sign("tok", "1700000000", "nonce1", "cipher"));
    }

    [TestMethod]
    public void Verify_AcceptsTheRealSignatureAndRejectsEverythingElse()
    {
        string signature = WeComCrypto.Sign("tok", "1700000000", "nonce1", "cipher");

        Assert.IsTrue(WeComCrypto.Verify("tok", "1700000000", "nonce1", "cipher", signature));
        Assert.IsFalse(WeComCrypto.Verify("tok", "1700000000", "nonce1", "cipher", signature[..^1] + "0"));
        Assert.IsFalse(WeComCrypto.Verify("other-token", "1700000000", "nonce1", "cipher", signature));
        Assert.IsFalse(WeComCrypto.Verify("tok", "1700000000", "nonce1", "cipher", ""));
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Plugin.Ai.Bridge.Channels.WeCom;

/// <summary>
/// 企业微信回调的签名与消息加解密(即官方 SDK 里的 WXBizMsgCrypt)。
/// </summary>
/// <remarks>
/// 官方只发 Java/PHP/Python/Go/C++ 五种参考实现,.NET 那份年久失修,而算法本身很短,
/// 所以照官方文档自己写一份:
/// <list type="bullet">
/// <item>密钥 = <c>Base64(EncodingAESKey + "=")</c>,32 字节;IV 取密钥的前 16 字节。</item>
/// <item>AES-256-CBC,<b>补位块是 32 字节</b>(不是 AES 的 16),所以解密按无补位做完再自己剥。</item>
/// <item>明文 = 16 字节随机数 + 4 字节大端长度 + 正文 + receiveid(corpid)。</item>
/// <item>签名 = SHA1(把 token / timestamp / nonce / 密文 四个串按字典序排好再拼起来)。</item>
/// </list>
/// </remarks>
internal static class WeComCrypto
{
    /// <summary>补位块大小。企业微信这里用 32,不是 AES 的 16。</summary>
    private const int PadBlock = 32;

    /// <summary>
    /// 把 43 个字符的 EncodingAESKey 还原成 32 字节密钥。
    /// 填错(长度不对、混进非 Base64 字符)一律抛 <see cref="ArgumentException" /> ——
    /// 调用方只需要认一种失败,而不是既要防 <see cref="FormatException" /> 又要防别的。
    /// </summary>
    public static byte[] ParseKey(string encodingAesKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingAesKey);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodingAesKey.Trim() + "=");
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("EncodingAESKey is not a valid 43-character Base64 string.",
                nameof(encodingAesKey), ex);
        }
        return key.Length == 32
            ? key
            : throw new ArgumentException("EncodingAESKey must decode to 32 bytes.", nameof(encodingAesKey));
    }

    /// <summary>算签名(小写十六进制)。</summary>
    public static string Sign(string token, string timestamp, string nonce, string encrypted)
    {
        string[] parts = [token, timestamp, nonce, encrypted];
        Array.Sort(parts, StringComparer.Ordinal);
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 校验签名。<b>用定时安全比较</b> —— 这是唯一能证明"这条回调真的来自企业微信"的东西,
    /// 逐字符早退等于把它变成可以试探的。
    /// </summary>
    public static bool Verify(string token, string timestamp, string nonce, string encrypted, string signature)
    {
        byte[] expected = Encoding.ASCII.GetBytes(Sign(token, timestamp, nonce, encrypted));
        byte[] presented = Encoding.ASCII.GetBytes(signature ?? "");
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>解密,返回正文与 receiveid(调用方应比对 receiveid 是不是自己的 corpid)。</summary>
    public static (string Message, string ReceiveId) Decrypt(byte[] key, string base64)
    {
        byte[] cipher = Convert.FromBase64String(base64);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key.AsSpan(0, 16).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        byte[] plain = aes.DecryptCbc(cipher, aes.IV, PaddingMode.None);

        int pad = plain[^1];
        if (pad is < 1 or > PadBlock || pad > plain.Length)
        {
            throw new CryptographicException("WeCom payload has an invalid padding byte.");
        }
        ReadOnlySpan<byte> body = plain.AsSpan(0, plain.Length - pad);
        if (body.Length < 20)
        {
            throw new CryptographicException("WeCom payload is too short.");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(body.Slice(16, 4));
        if (length < 0 || 20 + length > body.Length)
        {
            throw new CryptographicException("WeCom payload declares a length that does not fit.");
        }
        return (Encoding.UTF8.GetString(body.Slice(20, length)),
            Encoding.UTF8.GetString(body[(20 + length)..]));
    }

    /// <summary>加密(自测与回包用;正常应答回调只要回空串,所以线上路径用不到它)。</summary>
    public static string Encrypt(byte[] key, string message, string receiveId)
    {
        byte[] text = Encoding.UTF8.GetBytes(message);
        byte[] id = Encoding.UTF8.GetBytes(receiveId);
        byte[] body = new byte[16 + 4 + text.Length + id.Length];
        RandomNumberGenerator.Fill(body.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16, 4), text.Length);
        text.CopyTo(body, 20);
        id.CopyTo(body, 20 + text.Length);

        int pad = PadBlock - (body.Length % PadBlock);
        if (pad == 0)
        {
            pad = PadBlock;
        }
        byte[] padded = new byte[body.Length + pad];
        body.CopyTo(padded, 0);
        padded.AsSpan(body.Length).Fill((byte)pad);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return Convert.ToBase64String(aes.EncryptCbc(padded, key.AsSpan(0, 16).ToArray(), PaddingMode.None));
    }
}

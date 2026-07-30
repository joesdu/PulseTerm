using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// Xshell 会话密码的解密算法。密钥由当前 Windows 用户的用户名 + SID 派生,故只有以
/// 「创建这些会话的同一 Windows 账户」运行时才能还原明文,且仅在 Xshell 未启用主密码时适用。
/// 不同 Xshell 版本使用不同的密钥派生方式(见公开工具 SharpXDecrypt 与 Xpass);由于密文尾部
/// 带 32 字节 SHA-256(明文) 校验,这里依次尝试所有已知派生方式、取校验通过者 —— 从而无需依赖
/// <c>.xsh</c> 里可能不准确的 <c>Version</c> 字段即可兼容 5/6/7/8 及以后版本。
/// </summary>
internal static class XshellCrypto
{
    // 早期版本(2/3/4/5.0)使用的固定口令,MD5 后作为 RC4 密钥。
    private const string LegacySalt = "!X@s#h$e%l^l&";

    /// <summary>
    /// 尝试还原一条 Xshell 加密密码;逐一试探已知密钥方案,尾部校验通过即返回明文,全部失败返回 <c>null</c>。
    /// </summary>
    /// <param name="encryptedBase64">会话文件中的 <c>Password=</c> 原值(Base64)。</param>
    /// <param name="userName">当前 Windows 用户名(不含域前缀)。</param>
    /// <param name="sid">当前 Windows 用户 SID 字符串。</param>
    public static string? TryDecryptPassword(string? encryptedBase64, string userName, string sid)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            return null;
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(encryptedBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        // 布局:RC4(明文) ‖ SHA256(明文);尾部 32 字节是明文校验哈希,不参与 RC4。
        if (data.Length <= 0x20)
        {
            return null;
        }
        int bodyLength = data.Length - 0x20;
        ReadOnlySpan<byte> trailer = data.AsSpan(bodyLength, 0x20);

        foreach (byte[] key in CandidateKeys(userName, sid))
        {
            var body = new byte[bodyLength];
            Array.Copy(data, 0, body, 0, bodyLength);
            byte[] plain = Rc4.Transform(key, body);

            if (SHA256.HashData(plain).AsSpan().SequenceEqual(trailer))
            {
                // 明文按 UTF-8 还原(与 ASCII 口令完全一致;非 ASCII 口令依赖原始编码,属少数情形)。
                return Encoding.UTF8.GetString(plain);
            }
        }
        return null;
    }

    /// <summary>按「最常见优先」的顺序产出各版本的候选 RC4 密钥。</summary>
    private static IEnumerable<byte[]> CandidateKeys(string userName, string sid)
    {
        // Xshell 5 / 6 / 7.0。
        yield return SHA256.HashData(Encoding.UTF8.GetBytes(userName + sid));
        // Xshell 7.x / 8.x:SHA256(reverse(sid) + userName)。
        yield return SHA256.HashData(Encoding.UTF8.GetBytes(Reverse(sid) + userName));
        // Xshell 5.1 / 5.2。
        yield return SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        // 早期(2/3/4/5.0)固定口令。
        yield return MD5.HashData(Encoding.ASCII.GetBytes(LegacySalt));
    }

    private static string Reverse(string value) => new([.. value.Reverse()]);
}

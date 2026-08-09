using System.Text;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// WinSCP 保存密码的解码算法(可逆混淆,非真正加密):以 <c>0xA3</c> 取反异或,种子为
/// <c>用户名 + 主机名</c>。不绑定机器,任何账户/机器均可解 —— 除非 WinSCP 启用了主密码。
/// 算法与公开实现(anoopengineer/winscppasswd 等)一致。
/// </summary>
internal static class WinScpCrypto
{
    private const int PwMagic = 0xA3;
    private const int PwFlag = 0xFF;

    /// <summary>解码一条 WinSCP 密码;输入非法或数据不足时返回 <c>null</c>。</summary>
    /// <param name="host">会话的 HostName(与用户名一起构成种子/校验前缀)。</param>
    /// <param name="username">会话的 UserName。</param>
    /// <param name="encrypted">注册表/INI 中 <c>Password</c> 的十六进制字符串原值。</param>
    public static string? Decrypt(string host, string username, string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return null;
        }
        int[] nibbles = new int[encrypted.Length];
        for (int i = 0; i < encrypted.Length; i++)
        {
            int v = HexValue(encrypted[i]);
            if (v < 0)
            {
                return null;
            }
            nibbles[i] = v;
        }

        int index = 0;
        int Next()
        {
            if (index + 1 >= nibbles.Length)
            {
                return -1;
            }
            int a = nibbles[index];
            int b = nibbles[index + 1];
            index += 2;
            return ~(((a << 4) + b) ^ PwMagic) & 0xFF;
        }

        int flag = Next();
        if (flag < 0)
        {
            return null;
        }
        int length;
        if (flag == PwFlag)
        {
            _ = Next(); // 版本/占位字节,忽略。
            length = Next();
        }
        else
        {
            length = flag;
        }
        if (length < 0)
        {
            return null;
        }
        int skip = Next();
        if (skip < 0)
        {
            return null;
        }
        index += skip * 2; // 跳过前导填充。

        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            int value = Next();
            if (value < 0)
            {
                return null;
            }
            builder.Append((char)value);
        }
        string clear = builder.ToString();

        // 新版格式:明文以 用户名+主机名 作前缀,校验后剥离。
        if (flag == PwFlag)
        {
            string key = username + host;
            if (clear.Length < key.Length)
            {
                return null;
            }
            clear = clear[key.Length..];
        }
        return clear;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}

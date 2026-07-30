namespace VelaShell.Infrastructure.Import;

/// <summary>RC4 流密码(对称:加密与解密同一变换)。仅供 Xshell 密码还原使用。</summary>
internal static class Rc4
{
    /// <summary>用 <paramref name="key" /> 对 <paramref name="data" /> 做 RC4 变换并返回结果。</summary>
    public static byte[] Transform(byte[] key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        if (key.Length == 0)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }
        var s = new int[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = i;
        }
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        int a = 0, b = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) % 256;
            b = (b + s[a]) % 256;
            (s[a], s[b]) = (s[b], s[a]);
            output[i] = (byte)(data[i] ^ s[(s[a] + s[b]) % 256]);
        }
        return output;
    }
}

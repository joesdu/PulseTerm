using System.Text;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 一个键名。Redis 的键是**二进制安全的字节串**,不保证是合法 UTF-8 ——
/// 因此显示与操作必须分开走:界面按 redis-cli 的规矩转义显示(<c>\xNN</c>),
/// 而 <c>RENAME</c>/<c>DEL</c>/<c>GET</c> 一律用原始字节。
/// <para>
/// 多数 Redis 图形客户端在这里静默改坏用户的键:把字节串按 UTF-8 解码成字符串再编回去,
/// 非法序列会被替换字符 U+FFFD 顶掉,于是"重命名"出来的是另一个键。
/// </para>
/// </summary>
public sealed class RedisKeyName : IEquatable<RedisKeyName>
{
    private readonly byte[] _raw;

    /// <summary>从原始字节构造。</summary>
    /// <param name="raw">键的原始字节。</param>
    public RedisKeyName(byte[] raw)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        Display = Escape(raw);
        IsUtf8 = TryDecodeUtf8(raw, out string? text);
        Text = text ?? Display;
    }

    /// <summary>从字符串构造(用户在界面上输入的新键名)。</summary>
    /// <param name="text">键名文本。</param>
    public RedisKeyName(string text) : this(Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))))
    {
    }

    /// <summary>转义后的显示形式(可直接粘进控制台)。</summary>
    public string Display { get; }

    /// <summary>原始字节是否为合法 UTF-8。</summary>
    public bool IsUtf8 { get; }

    /// <summary>
    /// 按 UTF-8 解出的文本;不是合法 UTF-8 时**退回转义形式**而不是带替换字符的近似值
    /// —— 一个看起来正常却其实不对的键名比一串 <c>\xNN</c> 危险得多。
    /// </summary>
    public string Text { get; }

    /// <summary>原始字节的只读视图。</summary>
    public ReadOnlySpan<byte> Raw => _raw;

    /// <summary>转成 SE.Redis 的键(零拷贝语义:库内部同样持字节)。</summary>
    /// <returns>库使用的键。</returns>
    public RedisKey ToRedisKey() => _raw;

    /// <summary>把键名按 <paramref name="delimiter" /> 切成层级段;分隔符不出现时得到单段。</summary>
    /// <param name="delimiter">分隔符。</param>
    /// <returns>层级段。</returns>
    public string[] Segments(string delimiter) =>
        string.IsNullOrEmpty(delimiter) || !Text.Contains(delimiter, StringComparison.Ordinal)
            ? [Text]
            : Text.Split(delimiter, StringSplitOptions.None);

    /// <inheritdoc />
    public bool Equals(RedisKeyName? other) => other is not null && _raw.AsSpan().SequenceEqual(other._raw);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RedisKeyName);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // 逐字节的稳定哈希:键名是去重的依据(SCAN 在 rehash 期间会返回重复键),
        // 不能用 Text —— 两个不同的字节串可能解出同一个替换字符串。
        var hash = new HashCode();
        hash.AddBytes(_raw);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Display;

    /// <summary>按 redis-cli 的规矩转义:可打印 ASCII 原样,其余走 <c>\xNN</c> 与常见转义。</summary>
    private static string Escape(byte[] raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (byte b in raw)
        {
            switch (b)
            {
                case (byte)'\\': builder.Append("\\\\"); break;
                case (byte)'"': builder.Append("\\\""); break;
                case (byte)'\n': builder.Append("\\n"); break;
                case (byte)'\r': builder.Append("\\r"); break;
                case (byte)'\t': builder.Append("\\t"); break;
                case (byte)'\a': builder.Append("\\a"); break;
                case (byte)'\b': builder.Append("\\b"); break;
                case >= 0x20 and < 0x7F: builder.Append((char)b); break;
                default: builder.Append("\\x").Append(b.ToString("x2")); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>严格 UTF-8 解码(非法序列抛异常而不是替换),用于判断能否原样显示。</summary>
    private static bool TryDecodeUtf8(byte[] raw, out string? text)
    {
        try
        {
            string decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw);
            // 控制字符即便是合法 UTF-8 也不该直接进列表:它们会把行高、对齐甚至光标搞乱。
            // 这里**必须把 text 置空**再返回 false —— 留着解码结果的话,调用方那句
            // `text ?? Display` 就会拿到带控制字符的原文,回落根本没有发生。
            if (decoded.Any(char.IsControl))
            {
                text = null;
                return false;
            }
            text = decoded;
            return true;
        }
        catch (ArgumentException)
        {
            text = null;
            return false;
        }
    }
}

using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis;

/// <summary>值编辑区的呈现形态。</summary>
public enum RedisValueFormat
{
    /// <summary>原样文本(仅当字节是合法 UTF-8 时可用)。保存时按 UTF-8 编码。</summary>
    Text,

    /// <summary>redis-cli 口径的转义(<c>\xNN</c>)。**可逆**,因此二进制值也能安全编辑。</summary>
    Escaped,

    /// <summary>十六进制转储(带偏移与 ASCII 侧栏)。只读 —— 它是给人看的排版,不是可回写的表示。</summary>
    Hex
}

/// <summary>
/// 值的字节 ↔ 文本转换。
/// <para>
/// 这一层存在的唯一理由:**永远不要写回用户没打算写的字节**。
/// Redis 的值和键一样是二进制安全的字节串,而多数图形客户端在这里静默改坏数据 ——
/// 把字节按 UTF-8 解码成字符串显示,用户点一下保存,再按 UTF-8 编回去。
/// 非法序列在解码时已经被替换字符顶掉,于是"保存"实际上是"用一段近似值覆盖原值"。
/// </para>
/// <para>
/// 这里的做法:显示与回写走**同一种可逆表示**。合法 UTF-8 走原样文本;
/// 不合法就走 redis-cli 的转义,而转义是能原样解回字节的 —— 因此"看到什么就存回什么"
/// 在两种情形下都成立。十六进制只读,它是排版不是表示。
/// </para>
/// </summary>
public static class RedisValueText
{
    /// <summary>
    /// 这段字节能不能当普通文本显示。
    /// <para>
    /// 与键名的判定**刻意不同**:值允许含换行、回车、制表符。键名带换行会把列表的行高搞乱,
    /// 所以那边一律转义;而值编辑器本来就是多行输入框,把一段多行 JSON 显示成
    /// <c>{\n  "a": 1\n}</c> 才是错的 —— 那会逼用户对着转义符编辑一段本可以直接读的文本。
    /// </para>
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>可以当文本显示。</returns>
    public static bool IsTextSafe(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string decoded;
        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (ArgumentException)
        {
            return false;
        }
        foreach (char ch in decoded)
        {
            // 换行/回车/制表放行,其余控制字符(NUL、BEL、转义序列…)一律按二进制处理:
            // 它们在文本框里不可见却真实存在,编辑时会被悄悄吃掉或复制丢失。
            if (char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>按 redis-cli 的规矩转义(可直接粘进控制台)。</summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>转义文本。</returns>
    public static string Escape(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
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
                default: builder.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture)); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 把转义文本解回字节(<see cref="Escape" /> 的逆)。
    /// <para>
    /// **认不出的转义一律报错,绝不猜**。把 <c>\q</c> 当成字面量 <c>q</c> 或者原样保留反斜杠,
    /// 都是在用户没察觉的情况下改动他要写的字节 —— 那正是这一层要杜绝的事。
    /// </para>
    /// </summary>
    /// <param name="text">转义文本。</param>
    /// <param name="bytes">解出的字节。</param>
    /// <param name="error">失败原因(键名口径的英文短句,由调用方本地化包装)。</param>
    /// <returns>解析成功。</returns>
    public static bool TryUnescape(string text, out byte[] bytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        var buffer = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch != '\\')
            {
                // 非转义字符按 UTF-8 落字节:这样"转义模式"里照样能直接键入中文。
                buffer.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
                continue;
            }
            if (i + 1 >= text.Length)
            {
                bytes = [];
                error = $"dangling backslash at offset {i}";
                return false;
            }
            char next = text[++i];
            switch (next)
            {
                case '\\': buffer.Add((byte)'\\'); break;
                case '"': buffer.Add((byte)'"'); break;
                case 'n': buffer.Add((byte)'\n'); break;
                case 'r': buffer.Add((byte)'\r'); break;
                case 't': buffer.Add((byte)'\t'); break;
                case 'a': buffer.Add((byte)'\a'); break;
                case 'b': buffer.Add((byte)'\b'); break;
                case 'x':
                    if (i + 2 >= text.Length
                        || !byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsed))
                    {
                        bytes = [];
                        error = $"bad \\x escape at offset {i - 1}";
                        return false;
                    }
                    buffer.Add(parsed);
                    i += 2;
                    break;
                default:
                    bytes = [];
                    error = $"unknown escape \\{next} at offset {i - 1}";
                    return false;
            }
        }
        bytes = [.. buffer];
        error = null;
        return true;
    }

    /// <summary>
    /// 十六进制转储:偏移 + 16 字节一行 + ASCII 侧栏。
    /// <para>看二进制值时真正有用的是"它长什么样"(魔数、结构、有没有嵌着可读片段),
    /// 而不是一长串连续的十六进制。</para>
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>转储文本。</returns>
    public static string HexDump(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        const int PerLine = 16;
        var builder = new StringBuilder(raw.Length / PerLine * 78 + 16);
        for (int offset = 0; offset < raw.Length; offset += PerLine)
        {
            builder.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < PerLine; i++)
            {
                builder.Append(offset + i < raw.Length
                    ? raw[offset + i].ToString("x2", CultureInfo.InvariantCulture)
                    : "  ");
                builder.Append(i == PerLine / 2 - 1 ? "  " : " ");
            }
            builder.Append(" |");
            for (int i = 0; i < PerLine && offset + i < raw.Length; i++)
            {
                byte b = raw[offset + i];
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            builder.Append("|\n");
        }
        return builder.ToString();
    }

    /// <summary>按形态把字节渲染成编辑区里的文本。</summary>
    /// <param name="raw">原始字节。</param>
    /// <param name="format">形态。</param>
    /// <returns>显示文本。</returns>
    public static string Render(byte[] raw, RedisValueFormat format)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return format switch
        {
            RedisValueFormat.Text => Encoding.UTF8.GetString(raw),
            RedisValueFormat.Hex => HexDump(raw),
            _ => Escape(raw)
        };
    }

    /// <summary>
    /// 值打开时该用哪种形态:能当文本就用文本,否则用转义。
    /// <para>不回落到"带替换字符的文本" —— 一个看起来正常却其实不对的值,比一串
    /// <c>\xNN</c> 危险得多(与键名那边同一条判断)。</para>
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>默认形态。</returns>
    public static RedisValueFormat Detect(byte[] raw) =>
        IsTextSafe(raw) ? RedisValueFormat.Text : RedisValueFormat.Escaped;
}

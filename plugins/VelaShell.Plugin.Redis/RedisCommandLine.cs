using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 控制台输入行 → 命令与参数。按 redis-cli 的规矩:空白分隔,双引号内认转义
/// (<c>\n \r \t \\ \" \xNN</c>),单引号内一律字面量。
/// <para>
/// 单独成类并单测:分词错一点点的表现是"我明明带了引号,为什么值被切开了" ——
/// 而参数里带空格的键值在 Redis 里非常常见。
/// </para>
/// </summary>
public static class RedisCommandLine
{
    /// <summary>把一行输入切成参数。</summary>
    /// <param name="line">输入行。</param>
    /// <param name="args">切出的参数(第一个是命令名)。</param>
    /// <param name="error">切分失败的原因(引号没闭合之类);成功时为空串。</param>
    /// <returns>是否切分成功。</returns>
    public static bool TrySplit(string? line, out IReadOnlyList<string> args, out string error)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        error = string.Empty;
        string text = line ?? string.Empty;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                continue;
            }
            if (c == '"')
            {
                inToken = true;
                if (!TryReadDoubleQuoted(text, ref i, current, out error))
                {
                    args = [];
                    return false;
                }
                continue;
            }
            if (c == '\'')
            {
                inToken = true;
                if (!TryReadSingleQuoted(text, ref i, current, out error))
                {
                    args = [];
                    return false;
                }
                continue;
            }
            inToken = true;
            current.Append(c);
        }
        if (inToken)
        {
            result.Add(current.ToString());
        }
        args = result;
        return true;
    }

    private static bool TryReadDoubleQuoted(string text, ref int index, StringBuilder sink, out string error)
    {
        error = string.Empty;
        for (int i = index + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                index = i;
                return true;
            }
            if (c != '\\')
            {
                sink.Append(c);
                continue;
            }
            if (i + 1 >= text.Length)
            {
                break;
            }
            char escaped = text[++i];
            switch (escaped)
            {
                case 'n': sink.Append('\n'); break;
                case 'r': sink.Append('\r'); break;
                case 't': sink.Append('\t'); break;
                case 'a': sink.Append('\a'); break;
                case 'b': sink.Append('\b'); break;
                case '\\': sink.Append('\\'); break;
                case '"': sink.Append('"'); break;
                case 'x' when i + 2 < text.Length
                              && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out byte hex):
                    // \xNN 是把二进制值敲进控制台的唯一途径 —— 键与值都是字节串,
                    // 没有它,用户就没法操作任何非文本的键。
                    sink.Append((char)hex);
                    i += 2;
                    break;
                default:
                    // 认不出的转义按字面量处理(与 redis-cli 一致),不报错。
                    sink.Append(escaped);
                    break;
            }
        }
        error = "unbalanced-quotes";
        return false;
    }

    private static bool TryReadSingleQuoted(string text, ref int index, StringBuilder sink, out string error)
    {
        error = string.Empty;
        for (int i = index + 1; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                index = i;
                return true;
            }
            sink.Append(text[i]);
        }
        error = "unbalanced-quotes";
        return false;
    }
}

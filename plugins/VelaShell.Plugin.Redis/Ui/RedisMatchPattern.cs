using System.Text;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 过滤条文本 → <c>SCAN MATCH</c> 模式。
/// <para>
/// 单独成类是为了能被单测钉住:这是**用户输入到服务端命令**的唯一转换点,
/// 而它错一点点的表现就是"我明明有这个键,为什么搜不到" —— 界面上最难自证的一类 bug。
/// </para>
/// </summary>
public static class RedisMatchPattern
{
    /// <summary>按匹配方式生成 <c>MATCH</c> 模式。</summary>
    /// <param name="mode">匹配方式。</param>
    /// <param name="text">用户输入(前后空白会被去掉)。</param>
    /// <returns>模式;输入为空时是 <c>*</c>。</returns>
    public static string Build(RedisMatchMode mode, string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (mode == RedisMatchMode.Glob)
        {
            // 通配模式下用户输入的**就是**模式,一个字符都不许改 —— 那正是他选这个模式的意思。
            return trimmed.Length == 0 ? "*" : trimmed;
        }
        if (trimmed.Length == 0)
        {
            return "*";
        }
        string escaped = Escape(trimmed);
        return mode == RedisMatchMode.Prefix ? escaped + "*" : "*" + escaped + "*";
    }

    /// <summary>
    /// 转义通配元字符。
    /// <para>
    /// 前缀/包含两种模式下**必须**转义:想找字面量 <c>a*b</c> 的用户否则会得到一堆无关的键,
    /// 而他绝不会想到是自己输入里的星号被当成了通配符。
    /// </para>
    /// </summary>
    /// <param name="text">原文。</param>
    /// <returns>转义后的文本。</returns>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            // Redis 的 glob 元字符:* ? [ ] \ 以及字符类里的 ^。
            if (c is '*' or '?' or '[' or ']' or '\\' or '^')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Ai.Bridge.Channels.Telegram;

/// <summary>
/// 把模型输出的 Markdown 转成 Telegram 认的那一小撮 HTML。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 HTML 而不是 MarkdownV2。</b>MarkdownV2 要求把
/// <c>_ * [ ] ( ) ~ ` &gt; # + - = | { } . !</c> 全部转义,漏一个整条消息就是
/// <c>400 can't parse entities</c> —— 而模型的输出里这些字符成堆(列表的 <c>-</c>、
/// 标题的 <c>#</c>、句号的 <c>.</c>)。更糟的是流式那条路发的是<b>半截</b>文本,
/// 一个还没闭合的 <c>**</c> 就能让整轮回复发不出去。
/// HTML 只有三个字符要转义(<c>&amp; &lt; &gt;</c>),标签由我们自己配对生成,
/// 结构上不可能不闭合。
/// </para>
/// <para>
/// Telegram 认的标签就那么几个:<c>b i u s code pre a blockquote</c>。
/// 列表和标题它压根没有,所以在这里降级成排版字符(<c>•</c> 与加粗)——
/// 保留意思,不假装有它没有的东西。
/// </para>
/// </remarks>
internal static partial class TelegramHtml
{
    /// <summary>转换整段文本。</summary>
    public static string Convert(string markdown)
    {
        var sb = new StringBuilder();
        bool fenced = false;
        foreach (string raw in (markdown ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (Fence().Match(line) is { Success: true } fence)
            {
                // 围栏代码块整段原样保留:里面的 * 和 _ 是代码,不是格式
                sb.Append(fenced ? "</code></pre>" : Open(fence.Groups[1].Value.Trim())).Append('\n');
                fenced = !fenced;
                continue;
            }
            if (fenced)
            {
                sb.Append(Escape(line)).Append('\n');
                continue;
            }
            sb.Append(Block(line)).Append('\n');
        }
        if (fenced)
        {
            // 流式发的是半截文本,围栏可能还没闭合。补上,别让标签漏出去。
            sb.Append("</code></pre>\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Open(string language)
        => language.Length > 0
            ? $"<pre><code class=\"language-{Escape(language)}\">"
            : "<pre><code>";

    /// <summary>一行块级排版:标题、列表、引用。</summary>
    private static string Block(string line)
    {
        if (Heading().Match(line) is { Success: true } heading)
        {
            // Telegram 没有标题,退成加粗 —— 保留"这是一节的开头"这个意思
            return $"<b>{Inline(heading.Groups[2].Value)}</b>";
        }
        if (Bullet().Match(line) is { Success: true } bullet)
        {
            return $"{bullet.Groups[1].Value}• {Inline(bullet.Groups[3].Value)}";
        }
        if (Quote().Match(line) is { Success: true } quote)
        {
            return $"<blockquote>{Inline(quote.Groups[1].Value)}</blockquote>";
        }
        return Inline(line);
    }

    /// <summary>
    /// 行内记号。<b>行内代码要先摘出来</b> —— 里面的 <c>*</c> 与 <c>_</c> 是代码本身,
    /// 再去当格式解释就会把代码改坏。
    /// </summary>
    private static string Inline(string text)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match code in InlineCode().Matches(text))
        {
            sb.Append(Marks(text[at..code.Index]));
            sb.Append("<code>").Append(Escape(code.Groups[1].Value)).Append("</code>");
            at = code.Index + code.Length;
        }
        sb.Append(Marks(text[at..]));
        return sb.ToString();
    }

    /// <summary>
    /// 加粗 / 斜体 / 删除线 / 链接。
    /// </summary>
    /// <remarks>
    /// <b>先转义再插标签</b>,顺序反过来的话我们自己生成的 <c>&lt;b&gt;</c> 会被转义掉。
    /// 每条正则都要求成对出现,配不上的记号就当普通字符留着 —— 半截的
    /// <c>**</c> 在 HTML 里只是两个星号,不是一个语法错误。
    /// </remarks>
    private static string Marks(string text)
    {
        string s = Escape(text);
        s = Link().Replace(s, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        s = Bold().Replace(s, "<b>$1</b>");
        s = Strike().Replace(s, "<s>$1</s>");
        // 星号与下划线分成两条:写成一条带分支的正则,下划线那支捕获的是第 2 组,
        // 于是 $1 永远是空的 —— _soon_ 会变成一个空的 <i></i>,内容凭空消失。
        s = ItalicStar().Replace(s, "<i>$1</i>");
        s = ItalicUnderscore().Replace(s, "<i>$1</i>");
        return s;
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"^\s*```(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+])\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^&gt;\s?(.*)$|^>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    /// <summary>行内链接。地址里的 <c>)</c> 少见,不为它做转义栈。</summary>
    [GeneratedRegex(@"\[([^\]\n]*)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Strike();

    /// <summary>单星号的斜体。加粗已经先被吃掉了,所以这里不会撞上 <c>**</c>。</summary>
    [GeneratedRegex(@"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex ItalicStar();

    /// <summary>
    /// 单下划线的斜体。
    /// </summary>
    /// <remarks>
    /// 两侧那两个环视是为了放过 <c>snake_case_name</c> —— 服务器上的路径与标识符里
    /// 下划线成堆,把它们当成斜体记号会把名字改坏,而用户会照着复制。
    /// </remarks>
    [GeneratedRegex(@"(?<![\w_])_(?=\S)([^_\n]+?)(?<=\S)_(?![\w_])")]
    private static partial Regex ItalicUnderscore();
}

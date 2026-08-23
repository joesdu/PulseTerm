using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Ai.Agent.Web;

/// <summary>
/// 把网页 HTML 压成给模型看的 Markdown 风格纯文本。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意不引 HTML 解析库</b>(AngleSharp / HtmlAgilityPack)。这里的产物不是要渲染的 DOM,
/// 而是喂给模型的一段文本:结构只需要保到"标题 / 段落 / 列表 / 代码块 / 链接"这一层,
/// 再往下的精度对模型没有意义。为这点收益往插件目录里塞一个 1MB 的解析器不划算 ——
/// 插件的依赖是随目录分发的,每个用户都要下载。
/// </para>
/// <para>
/// 代价是正则处理 HTML 的老问题:畸形嵌套、属性里带 <c>&gt;</c>、<c>&lt;/a&gt;</c> 缺失这些情况会得到
/// 略脏的文本。<b>这是可以接受的</b> —— 最坏情况是模型多读到几个残留符号,而不是解析失败;
/// 真正会毁掉结果的 script/style/svg 已经整块剥掉了。所有正则都带 2 秒超时兜住病态回溯。
/// </para>
/// </remarks>
internal static partial class HtmlText
{
    /// <summary>正则超时:抓到的页面是外部输入,不能让一个构造出来的页面把 UI 线程之外的任务挂死。</summary>
    private const int TimeoutMs = 2000;

    /// <summary>把一页 HTML 转成 Markdown 风格文本;<paramref name="baseUri" /> 用来把相对链接补全。</summary>
    public static string ToText(string html, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        // 一、整块剥掉不含正文的部分。放在最前面:它们里面的尖括号最容易把后面的正则带偏。
        string s = NoiseBlocks().Replace(html, "\n");
        s = Comments().Replace(s, "");

        // 二、有 <main>/<article> 就只要它。现代页面的导航栏与页脚往往比正文还长,
        //     喂给模型纯属浪费上下文;没有这两个标签时退回整页。
        if (MainContent().Match(s) is { Success: true } main)
        {
            s = main.Groups[1].Value;
        }

        // 三、结构标签翻译成 Markdown。顺序有讲究:链接要在"剥掉剩余标签"之前处理,
        //     否则 href 就跟着标签一起没了。
        s = Headings().Replace(s, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " ");
        s = HeadingEnds().Replace(s, "\n\n");
        s = Anchors().Replace(s, m =>
        {
            string href = m.Groups[1].Value.Trim();
            string text = StripTags(m.Groups[2].Value).Trim();
            if (text.Length == 0)
            {
                return "";
            }
            // 页内锚点与 javascript: 不值得占位置,只留文字
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
            return Uri.TryCreate(baseUri, href, out Uri? abs) ? $"[{text}]({abs})" : text;
        });
        s = ListItems().Replace(s, "\n- ");
        s = TableCells().Replace(s, " | ");
        s = BlockEdges().Replace(s, "\n");

        // 四、剩下的标签一律剥掉,再解实体。解实体<b>必须在剥标签之后</b> ——
        //     先解的话正文里的 &lt;script&gt; 会变成真的标签,把上面的清理全绕过去。
        s = StripTags(s);
        s = WebUtility.HtmlDecode(s);

        return Tidy(s);
    }

    /// <summary>取 <c>&lt;title&gt;</c>;没有就返回空串。</summary>
    public static string Title(string html)
        => string.IsNullOrEmpty(html) || TitleTag().Match(html) is not { Success: true } m
            ? ""
            : Tidy(WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)));

    /// <summary>把一段可能带标签的片段压成纯文本(检索结果的摘要走这条)。</summary>
    public static string Plain(string fragment)
        => string.IsNullOrEmpty(fragment) ? "" : Tidy(WebUtility.HtmlDecode(StripTags(fragment)));

    private static string StripTags(string s) => Tags().Replace(s, "");

    /// <summary>收拾空白:行内空格压成一个,行尾空白去掉,三个以上连续换行压成两个。</summary>
    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        int blankRun = 0;
        foreach (string raw in s.Split('\n'))
        {
            string line = Spaces().Replace(raw.Replace(' ', ' '), " ").Trim();
            if (line.Length == 0)
            {
                // 最多留一个空行:HTML 里成片的空 div 会翻译成十几个换行
                if (++blankRun > 1 || sb.Length == 0)
                {
                    continue;
                }
                sb.Append('\n');
                continue;
            }
            blankRun = 0;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 成对出现、且整块都不含正文的标签。
    /// </summary>
    /// <remarks>
    /// <b>刻意逐个列出,不用 <c>&lt;(a|b|c)&gt;…&lt;/\1&gt;</c> 的反向引用写法</b>:
    /// 正则源生成器不支持大小写不敏感的反向引用(SYSLIB1044),碰上就整条退回运行时解释执行,
    /// 白丢了源生成的那点好处。展开之后既能编译成源码,也省掉了反向引用的匹配开销。
    /// 加标签就在这串里照样式补一行。
    /// </remarks>
    private const string NoisePairs =
          @"<script\b[^>]*>.*?</script\s*>"
        + @"|<style\b[^>]*>.*?</style\s*>"
        + @"|<noscript\b[^>]*>.*?</noscript\s*>"
        + @"|<svg\b[^>]*>.*?</svg\s*>"
        + @"|<head\b[^>]*>.*?</head\s*>"
        + @"|<nav\b[^>]*>.*?</nav\s*>"
        + @"|<footer\b[^>]*>.*?</footer\s*>"
        + @"|<aside\b[^>]*>.*?</aside\s*>"
        + @"|<form\b[^>]*>.*?</form\s*>"
        + @"|<template\b[^>]*>.*?</template\s*>";

    /// <summary>上面那些成对标签,加上不成对的 <c>link</c> / <c>meta</c> 与自闭合写法。</summary>
    [GeneratedRegex(NoisePairs + @"|<(?:script|style|link|meta)\b[^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex NoiseBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<(?:main|article)\b[^>]*>(.*)</(?:main|article)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex MainContent();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex Headings();

    [GeneratedRegex(@"</h[1-6]\s*>", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex HeadingEnds();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex Anchors();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex TableCells();

    // 块级元素的起止都当换行:段落、div、表格行、换行符、分隔线、引用、代码块
    [GeneratedRegex(@"</?(?:p|div|section|tr|br|hr|blockquote|pre|ul|ol|dl|dt|dd|table|thead|tbody|h[1-6])\b[^>]*/?>",
        RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex BlockEdges();

    [GeneratedRegex(@"<[^>]{0,2000}>", RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex Tags();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeoutMs)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"[ \t\f\v\r]+", RegexOptions.None, TimeoutMs)]
    private static partial Regex Spaces();
}

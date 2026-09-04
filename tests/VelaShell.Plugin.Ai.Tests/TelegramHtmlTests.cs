using System.Text.RegularExpressions;
using VelaShell.Plugin.Ai.Bridge.Channels.Telegram;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// Markdown → Telegram HTML。
/// </summary>
/// <remarks>
/// <b>这一层最怕的不是转得难看,是转出一个发不出去的串。</b>Telegram 对实体的语法很严,
/// 而喂进来的东西有两个特点:模型写的 Markdown(星号、下划线、反引号成堆),
/// 以及流式那条路发的<b>半截</b>文本。所以用例里既有"转得对不对",
/// 也有一组专门撞畸形输入的。
/// </remarks>
[TestClass]
public sealed class TelegramHtmlTests
{
    [TestMethod]
    public void Bold_BecomesTheBoldTag()
        => Assert.AreEqual("<b>DeepX</b> is up", TelegramHtml.Convert("**DeepX** is up"));

    [TestMethod]
    public void InlineCode_BecomesTheCodeTag()
        => Assert.AreEqual("port <code>32601</code>", TelegramHtml.Convert("port `32601`"));

    [TestMethod]
    public void Italic_AcceptsBothMarkers()
    {
        Assert.AreEqual("<i>soon</i>", TelegramHtml.Convert("*soon*"));
        Assert.AreEqual("<i>soon</i>", TelegramHtml.Convert("_soon_"));
    }

    /// <summary>列表 Telegram 根本没有,退成排版字符而不是把 <c>-</c> 留在那儿。</summary>
    [TestMethod]
    public void Bullets_BecomeATypographicMarker()
        => Assert.AreEqual("• <b>DeepX</b>: <code>release</code>",
            TelegramHtml.Convert("- **DeepX**: `release`"));

    [TestMethod]
    public void Headings_BecomeBoldBecauseTelegramHasNone()
        => Assert.AreEqual("<b>部署清单</b>", TelegramHtml.Convert("## 部署清单"));

    [TestMethod]
    public void FencedCode_BecomesAPreBlockWithItsLanguage()
    {
        string html = TelegramHtml.Convert("```bash\nls -la\n```");

        Assert.Contains("<pre><code class=\"language-bash\">", html);
        Assert.Contains("ls -la", html);
        Assert.Contains("</code></pre>", html);
    }

    /// <summary>
    /// <b>代码块里的记号是代码,不是格式。</b>
    /// </summary>
    /// <remarks>
    /// 把 <c>*</c> 当成斜体去解释,吐出来的就是一段被改坏的命令 ——
    /// 而用户会把它复制去生产机上执行。
    /// </remarks>
    [TestMethod]
    public void FencedCode_KeepsMarkdownMarkersVerbatim()
    {
        string html = TelegramHtml.Convert("```\nrm -rf /var/log/*.log\ncat a_b_c\n```");

        Assert.Contains("rm -rf /var/log/*.log", html);
        Assert.Contains("cat a_b_c", html);
        Assert.DoesNotContain("<i>", html);
    }

    /// <summary>行内代码同理:里面的下划线不该变成斜体。</summary>
    [TestMethod]
    public void InlineCode_KeepsMarkersVerbatim()
        => Assert.AreEqual("<code>a_b_c</code>", TelegramHtml.Convert("`a_b_c`"));

    /// <summary>HTML 特殊字符先转义,否则日志里一个 <c>&lt;</c> 就能把消息弄成非法实体。</summary>
    [TestMethod]
    public void HtmlSpecialCharacters_AreEscaped()
        => Assert.AreEqual("a &amp; b &lt;tag&gt;", TelegramHtml.Convert("a & b <tag>"));

    [TestMethod]
    public void EscapingHappensBeforeTagsAreInserted()
        => Assert.AreEqual("<b>&lt;b&gt;</b>", TelegramHtml.Convert("**<b>**"));

    [TestMethod]
    public void Links_BecomeAnchors()
        => Assert.AreEqual("<a href=\"https://example.com/x\">docs</a>",
            TelegramHtml.Convert("[docs](https://example.com/x)"));

    // ---- 畸形输入 ----

    /// <summary>
    /// <b>流式发的是半截文本。</b>一个还没闭合的围栏不该让标签漏出去。
    /// </summary>
    [TestMethod]
    public void AnUnclosedFence_IsClosedForUs()
    {
        string html = TelegramHtml.Convert("看这个:\n```bash\nls -la");

        Assert.Contains("<pre><code class=\"language-bash\">", html);
        Assert.EndsWith("</code></pre>", html);
    }

    /// <summary>配不上的记号就当普通字符 —— 在 HTML 里两个星号只是两个星号。</summary>
    [TestMethod]
    public void UnpairedMarkers_StayAsLiteralText()
    {
        Assert.AreEqual("**half", TelegramHtml.Convert("**half"));
        Assert.AreEqual("2 * 3 * 4", TelegramHtml.Convert("2 * 3 * 4"));
        Assert.AreEqual("snake_case_name", TelegramHtml.Convert("snake_case_name"));
    }

    /// <summary>
    /// 每个开标签都有闭标签 —— 这条是整个转换器存在的理由。
    /// </summary>
    /// <remarks>
    /// 拿一段"什么都有"的畸形文本去撞:半截加粗、孤立的下划线、没闭合的反引号、
    /// 尖括号、以及一个开着的围栏。只要标签是配对的,Telegram 就不会 400。
    /// </remarks>
    [TestMethod]
    public void EveryTagIsBalanced_EvenOnMalformedInput()
    {
        string html = TelegramHtml.Convert(
            "## 报告 **未闭合\n- a_b `未闭合\n> 引用 <script>\n2 * 3\n```py\nprint('x')");

        foreach (string tag in (string[])["b", "i", "s", "code", "pre", "a", "blockquote"])
        {
            // 开标签有的带属性(<a href=…>、<code class=…>),所以按"名字后面跟空格或 >"数 ——
            // 直接数 "<b" 会把 <blockquote> 也算成一个加粗。
            int opened = Regex.Matches(html, $"<{tag}[ >]").Count;
            Assert.AreEqual(opened, Count(html, $"</{tag}>"), $"<{tag}> 没有配对");
        }
        Assert.DoesNotContain("<script>", html);
    }

    [TestMethod]
    public void PlainText_IsLeftAlone()
        => Assert.AreEqual("当前服务器上运行中的 .NET 程序如下:",
            TelegramHtml.Convert("当前服务器上运行中的 .NET 程序如下:"));

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}

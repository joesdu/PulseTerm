using VelaShell.Plugin.Ai.Agent.Web;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 网络检索工具:HTML 转文本、SearXNG 结果解析、以及私网闸。
/// </summary>
/// <remarks>
/// <b>一次网都不联</b>:解析部分全部喂固定报文(<see cref="StubAccess" /> 替掉 HTTP),
/// 私网闸的用例挑的是在到达 socket 之前就该被拦下的地址。SearXNG 的报文格式是外部约定,
/// 真出问题只会是"某天对方改了字段名",那不是单元测试拦得住的;这里守的是我们自己的解析。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class WebToolsTests
{
    /// <summary>把 HTTP 那一层换成固定报文。</summary>
    private sealed class StubAccess(string body, bool ok = true) : WebAccess(new WebSearchOptions())
    {
        public HttpRequestMessage? Last { get; private set; }

        public override Task<(bool Ok, string Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult((ok, body));
        }
    }

    private static WebSearchEngine Engine(string body, out StubAccess stub, bool ok = true, string baseUrl = "http://searx.example")
    {
        stub = new StubAccess(body, ok);
        return new WebSearchEngine(stub, new WebSearchOptions { SearxngBaseUrl = baseUrl });
    }

    // ---- HtmlText ----

    [TestMethod]
    public void HtmlText_DropsScriptsAndStyles()
    {
        const string html = """
            <html><head><title>Doc</title><style>body{color:red}</style></head>
            <body><script>var x = 1 < 2;</script><p>Hello</p><noscript>enable js</noscript></body></html>
            """;

        string text = HtmlText.ToText(html, new Uri("https://example.com/"));

        Assert.IsFalse(text.Contains("color:red", StringComparison.Ordinal), "样式表不该进正文");
        Assert.IsFalse(text.Contains("var x", StringComparison.Ordinal), "脚本不该进正文");
        Assert.IsFalse(text.Contains("enable js", StringComparison.Ordinal), "noscript 也是噪音");
        Assert.IsTrue(text.Contains("Hello", StringComparison.Ordinal));
        Assert.AreEqual("Doc", HtmlText.Title(html));
    }

    [TestMethod]
    public void HtmlText_PrefersMainContent_OverNavAndFooter()
    {
        const string html = """
            <body><nav><a href="/x">导航项</a></nav>
            <main><h2>标题</h2><p>正文段落</p></main>
            <footer>版权所有</footer></body>
            """;

        string text = HtmlText.ToText(html, new Uri("https://example.com/"));

        Assert.IsTrue(text.Contains("## 标题", StringComparison.Ordinal), "h2 应转成二级标题");
        Assert.IsTrue(text.Contains("正文段落", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("导航项", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("版权所有", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HtmlText_KeepsLinks_AndResolvesRelativeHrefs()
    {
        const string html = """<p>see <a href="/docs/a.html">the docs</a> and <a href="#top">top</a></p>""";

        string text = HtmlText.ToText(html, new Uri("https://example.com/guide/"));

        Assert.IsTrue(text.Contains("[the docs](https://example.com/docs/a.html)", StringComparison.Ordinal),
            "相对链接要按 base 补全,模型才能直接拿去 web_fetch");
        Assert.IsTrue(text.Contains("top", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("](#top)", StringComparison.Ordinal), "页内锚点只留文字");
    }

    [TestMethod]
    public void HtmlText_DecodesEntities_AfterStrippingTags()
    {
        // 先解实体的话,正文里被转义的标签会"活"过来绕过前面的清理
        string text = HtmlText.ToText("<p>1 &lt;script&gt;alert(1)&lt;/script&gt; 2 &amp; 3</p>", new Uri("https://e.com/"));

        Assert.AreEqual("1 <script>alert(1)</script> 2 & 3", text);
    }

    [TestMethod]
    public void HtmlText_TurnsListItemsIntoBullets_AndCollapsesBlankRuns()
    {
        string text = HtmlText.ToText("<ul><li>一</li><li>二</li></ul><div></div><div></div><p>尾</p>",
            new Uri("https://e.com/"));

        Assert.IsTrue(text.Contains("- 一", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("- 二", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("\n\n\n", StringComparison.Ordinal), "空 div 不该堆出成片空行");
    }

    // ---- SearXNG ----

    [TestMethod]
    public async Task Searxng_ParsesResults_AndAsksForJson()
    {
        const string json = """
            {"results":[
              {"title":"第一条","url":"https://a.example/1","content":"摘要 &amp; 一"},
              {"title":"第二条","url":"https://b.example/2","content":"摘要二"}]}
            """;
        WebSearchEngine engine = Engine(json, out StubAccess stub);

        (bool ok, IReadOnlyList<SearchHit> hits, string error) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsTrue(ok, error);
        Assert.HasCount(2, hits);
        Assert.AreEqual("第一条", hits[0].Title);
        Assert.AreEqual("https://a.example/1", hits[0].Url);
        Assert.AreEqual("摘要 & 一", hits[0].Snippet, "SearXNG 的摘要里带实体,要解开");
        Assert.Contains("format=json", stub.Last!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Searxng_HonoursTheRequestedCount()
    {
        const string json = """
            {"results":[{"title":"a","url":"https://a/","content":""},
                        {"title":"b","url":"https://b/","content":""},
                        {"title":"c","url":"https://c/","content":""}]}
            """;
        WebSearchEngine engine = Engine(json, out _);

        (_, IReadOnlyList<SearchHit> hits, _) = await engine.SearchAsync("q", 2, CancellationToken.None);

        Assert.HasCount(2, hits);
    }

    [TestMethod]
    public async Task Searxng_WithoutBaseUrl_TellsTheModelHowToGetOne()
    {
        var engine = new WebSearchEngine(new StubAccess("{}"), new WebSearchOptions());

        (bool ok, _, string error) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.Contains("docker run", error, StringComparison.Ordinal);
        Assert.Contains("do not keep retrying", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 403 是这套东西最常见的坑:SearXNG 默认只开 html 输出,<c>?format=json</c> 被回一个
    /// 笼统的 Forbidden。不把解法贴上去,用户会去查自己的反向代理 —— 那是错的方向。
    /// </summary>
    [TestMethod]
    public async Task Searxng_Forbidden_PointsAtSearchFormats()
    {
        WebSearchEngine engine = Engine("HTTP 403 Forbidden. <!doctype html><title>403 Forbidden</title>",
            out _, ok: false);

        (bool ok, _, string error) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.Contains("search.formats", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 引擎全挂和"这词确实搜不到"必须分开报。实测(2026-08-24)自建实例上
    /// <c>unresponsive_engines</c> 常年非空(VPS 出口 IP 会被 startpage 之类要求验证码),
    /// 全挂时若还报"没搜到",模型只会换着关键词空转好几轮。
    /// </summary>
    [TestMethod]
    public async Task Searxng_AllEnginesDown_IsNotReportedAsNoResults()
    {
        const string json = """
            {"results":[],"unresponsive_engines":[["google cse","timeout"],["startpage","CAPTCHA"]]}
            """;
        WebSearchEngine engine = Engine(json, out _);

        (bool ok, IReadOnlyList<SearchHit> hits, string note) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsFalse(ok, "引擎全挂是实例的问题,不该当成检索结果为空");
        Assert.IsEmpty(hits);
        Assert.Contains("google cse: timeout", note, StringComparison.Ordinal);
        Assert.Contains("startpage: CAPTCHA", note, StringComparison.Ordinal);
    }

    /// <summary>有结果时就别拿掉线的引擎去打扰模型 —— 自建实例上那一项基本常年非空。</summary>
    [TestMethod]
    public async Task Searxng_SomeEnginesDown_ButResultsCameBack_SaysNothingExtra()
    {
        const string json = """
            {"results":[{"title":"t","url":"https://a/","content":"c"}],
             "unresponsive_engines":[["startpage","CAPTCHA"]]}
            """;
        WebSearchEngine engine = Engine(json, out _);

        (bool ok, IReadOnlyList<SearchHit> hits, string note) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.HasCount(1, hits);
        Assert.AreEqual("", note);
    }

    /// <summary>真的没搜到时,把实例给的相关查询转达出去,比让模型自己瞎猜下一个关键词强。</summary>
    [TestMethod]
    public async Task Searxng_GenuinelyEmpty_PassesOnTheSuggestions()
    {
        const string json = """
            {"results":[],"unresponsive_engines":[],"suggestions":["systemd restartsec","systemd restart options"]}
            """;
        WebSearchEngine engine = Engine(json, out _);

        (bool ok, IReadOnlyList<SearchHit> hits, string note) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsTrue(ok, "没搜到不是失败");
        Assert.IsEmpty(hits);
        Assert.Contains("systemd restartsec", note, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Searxng_MalformedJson_IsReportedNotThrown()
    {
        WebSearchEngine engine = Engine("<html>not json</html>", out _);

        (bool ok, _, string error) = await engine.SearchAsync("q", 5, CancellationToken.None);

        Assert.IsFalse(ok, "解析失败要变成给模型的一句话,不能抛出去中断整轮");
        Assert.Contains("JSON", error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task EmptyQuery_IsRejectedWithoutCallingTheBackend()
    {
        WebSearchEngine engine = Engine("{}", out StubAccess stub);

        (bool ok, _, _) = await engine.SearchAsync("   ", 5, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.IsNull(stub.Last, "空查询不该白跑一次网络");
    }

    // ---- 私网闸 ----

    private static async Task<FetchResult> FetchAsync(WebSearchOptions options, string url)
        => await new WebAccess(options).FetchAsync(new Uri(url), CancellationToken.None);

    [TestMethod]
    [DataRow("http://127.0.0.1:8080/admin")]
    [DataRow("http://10.0.0.5/")]
    [DataRow("http://192.168.1.1/")]
    [DataRow("http://169.254.169.254/latest/meta-data/")]
    [DataRow("http://172.16.3.4/")]
    [DataRow("http://localhost:9000/")]
    [DataRow("http://gitlab.internal/")]
    public async Task Fetch_RefusesPrivateAddresses(string url)
    {
        FetchResult result = await FetchAsync(new WebSearchOptions(), url);

        Assert.IsFalse(result.Ok);
        Assert.Contains("private", result.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Fetch_RefusesNonHttpSchemes()
    {
        FetchResult result = await FetchAsync(new WebSearchOptions(), "file:///etc/passwd");

        Assert.IsFalse(result.Ok);
        Assert.Contains("http://", result.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Fetch_AllowsAnExplicitlyListedInternalHost()
    {
        // 白名单放行之后就真的去连了 —— 9 号端口不会有人听,所以必然连失败;
        // 这里要的只是"不再是那句拒绝",证明闸确实让路了。
        var options = new WebSearchOptions { AllowedPrivateHosts = "127.0.0.1:9" };

        FetchResult result = await FetchAsync(options, "http://127.0.0.1:9/");

        Assert.IsFalse(result.Ok, "端口没人听,连不上是预期的");
        Assert.IsFalse(result.Body.Contains("Refusing", StringComparison.Ordinal), result.Body);
    }

    /// <summary>
    /// 用户配的 SearXNG 实例自动过闸,不用再往白名单里抄一遍。
    /// 自建实例十有八九就在 127.0.0.1,而私网默认是拦的 —— 两个设置得彼此对上才能用,
    /// 是个一定会有人踩的坑,而且报错落在"检索失败"上,看不出是闸拦的。
    /// </summary>
    [TestMethod]
    public async Task Fetch_ConfiguredSearxngHost_PassesTheGuardWithoutBeingAllowListed()
    {
        var options = new WebSearchOptions { SearxngBaseUrl = "http://127.0.0.1:9" };

        FetchResult result = await FetchAsync(options, "http://127.0.0.1:9/search");

        Assert.IsFalse(result.Ok, "端口没人听,连不上是预期的");
        Assert.IsFalse(result.Body.Contains("Refusing", StringComparison.Ordinal), result.Body);
    }

    /// <summary>放行的只有配的那一台,不是"因为配了 SearXNG 所以整个内网都开了"。</summary>
    [TestMethod]
    public async Task Fetch_ConfiguringSearxng_DoesNotOpenTheRestOfThePrivateNetwork()
    {
        var options = new WebSearchOptions { SearxngBaseUrl = "http://127.0.0.1:9" };

        FetchResult result = await FetchAsync(options, "http://10.0.0.5/");

        Assert.IsFalse(result.Ok);
        Assert.Contains("private", result.Body, StringComparison.Ordinal);
    }

    /// <summary>端口也要对上:同一台机器上的<b>别的</b>服务不因为 SearXNG 在那儿就一并放行。</summary>
    [TestMethod]
    public async Task Fetch_SameHostDifferentPortThanSearxng_IsStillBlocked()
    {
        var options = new WebSearchOptions { SearxngBaseUrl = "http://127.0.0.1:8080" };

        FetchResult result = await FetchAsync(options, "http://127.0.0.1:9200/_cluster/health");

        Assert.IsFalse(result.Ok);
        Assert.Contains("private", result.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Fetch_AllowListEntry_MayIncludeSchemeAndTrailingSlash()
    {
        var options = new WebSearchOptions { AllowedPrivateHosts = "http://127.0.0.1:9/" };

        FetchResult result = await FetchAsync(options, "http://127.0.0.1:9/x");

        Assert.IsFalse(result.Body.Contains("Refusing", StringComparison.Ordinal),
            "用户多半是直接从地址栏抄过来的,带 scheme 和尾斜杠也得认");
    }

    // ---- 设置 / 原生检索 ----

    [TestMethod]
    public void Options_ClampsOutOfRangeNumbers()
    {
        var options = new WebSearchOptions { MaxResults = 99, MaxFetchChars = 10 };

        options.Clamp();

        Assert.AreEqual(20, options.MaxResults);
        Assert.AreEqual(2_000, options.MaxFetchChars);
    }

    [TestMethod]
    public void NativeWebSearch_OnlyClaimsTheProtocolsThatActuallySupportIt()
    {
        Assert.IsTrue(NativeWebSearch.IsSupported(ChatProtocol.AnthropicMessages));
        Assert.IsTrue(NativeWebSearch.IsSupported(ChatProtocol.OpenAiResponses));
        Assert.IsFalse(NativeWebSearch.IsSupported(ChatProtocol.OpenAiChatCompletions),
            "Chat Completions 没有服务端检索,认下来就会发出去一个对方不认的字段");
    }
}

using System.Net;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 照着 opencode 的 <c>/connect</c> 补进来的两家:xAI SuperGrok(标准设备码)与
/// DigitalOcean Gradient(隐式流)。前者复用现成流程,后者带进来一套新机制。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class OpencodeProvidersTests
{
    private static readonly LoginPrompts Prompts =
        new("Signed in", "Close this tab.", "waiting", "exchanging", "code {0}");

    // ---- xAI SuperGrok ----

    [TestMethod]
    public void Xai_IsADeviceCodeSubscriptionAndIsReadyToUse()
    {
        ProviderCatalogEntry entry = ProviderCatalog.Find("xai-grok")!;
        AiProvider provider = entry.CreateProvider();
        OAuthConfig oauth = provider.OAuth!;

        Assert.AreEqual(OAuthFlow.DeviceCode, oauth.Flow);
        Assert.AreEqual("https://auth.x.ai/oauth2/device/code", oauth.DeviceCodeUrl);
        Assert.AreEqual("https://auth.x.ai/oauth2/token", oauth.TokenUrl);
        Assert.Contains("grok-cli:access", oauth.Scopes);
        // 借的是 Grok CLI 的客户端身份,得如实标出来
        Assert.IsTrue(entry.Experimental);
        Assert.IsTrue(provider.CanSignIn, "端点和 client_id 都齐了,不该判成「登不了」");
    }

    [TestMethod]
    public async Task Xai_SignsInThroughTheDeviceCodeFlow()
    {
        var stub = new OAuthStub()
            .Json("""
                {"device_code":"dc-1","user_code":"WXYZ-1234",
                 "verification_uri":"https://x.ai/device","interval":1}
                """)
            .Json("""{"access_token":"xai-at","refresh_token":"xai-rt","expires_in":3600}""");
        using var http = new HttpClient(stub);
        var login = new ProviderLogin(
            new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask },
            (_, _) => Task.CompletedTask);

        OAuthTokens tokens = await login.SignInAsync(
            ProviderCatalog.Find("xai-grok")!.CreateProvider().OAuth!, Prompts);

        Assert.AreEqual("xai-at", tokens.AccessToken);
        Assert.AreEqual("xai-rt", tokens.RefreshToken);
        Assert.IsNotNull(tokens.ExpiresAt);
    }

    /// <summary>
    /// 设备码请求本身就是这一路的"授权请求",所以 <c>ExtraAuthorizeParams</c> 要落在它上面。
    /// 之前只有授权码那一路会带,设备码这边是空的。
    /// </summary>
    [TestMethod]
    public async Task DeviceCodeRequest_CarriesTheExtraAuthorizeParams()
    {
        var stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"AB","verification_uri":"https://x.ai/device"}""");
        using var http = new HttpClient(stub);

        await new OAuthClient(http).StartDeviceCodeAsync(
            ProviderCatalog.Find("xai-grok")!.CreateProvider().OAuth!);

        Assert.Contains("referrer=velashell", stub.Requests[0].Body);
    }

    // ---- DigitalOcean:隐式流 ----

    [TestMethod]
    public void DigitalOcean_IsAnImplicitFlowWithNoTokenEndpoint()
    {
        ProviderCatalogEntry entry = ProviderCatalog.Find("digitalocean")!;
        AiProvider provider = entry.CreateProvider();
        OAuthConfig oauth = provider.OAuth!;

        Assert.AreEqual(OAuthFlow.ImplicitFragment, oauth.Flow);
        Assert.IsEmpty(oauth.TokenUrl, "隐式流没有 token 端点 —— 令牌直接从授权页回来");
        // 上一版的 CanSignIn 拿 TokenUrl 一刀切,会把这一整路判死,而界面上只说"登不了"
        Assert.IsTrue(provider.CanSignIn, "没有 token 端点不等于登不了");
    }

    [TestMethod]
    public void ImplicitFlow_AsksForATokenNotACode()
    {
        OAuthConfig oauth = ProviderCatalog.Find("digitalocean")!.CreateProvider().OAuth!;

        string url = OAuthClient
            .BuildAuthorizationUrl(oauth, PkceCodes.Create(), "http://127.0.0.1:43920/callback")
            .ToString();

        Assert.Contains("response_type=token", url);
        // 隐式流没有"拿码去换"那一步,challenge 无处可验 —— 发出去只是噪音
        Assert.DoesNotContain("code_challenge", url);
        Assert.Contains("state=", url, "state 防的是别人往回调里塞东西,与 PKCE 是两回事,照发");
    }

    /// <summary>
    /// 片段(<c>#</c> 后面那段)<b>根本不会随请求发到服务端</b>。所以监听得先回一页
    /// 把它搬成查询串再请求一次 —— 少了这一跳,隐式流在本机永远收不到令牌。
    /// </summary>
    [TestMethod]
    public async Task Loopback_RecoversTheTokenOutOfTheUrlFragment()
    {
        using var listener = new LoopbackRedirectListener(0, "/callback");
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<Dictionary<string, string>> waiting =
            listener.WaitAsync("t", "b", fragment: true, timeout.Token);

        // 第一跳:浏览器带着 #access_token=… 打回来,服务端只看得到一个光秃秃的 /callback
        string bootstrap = await http.GetStringAsync(
            $"http://127.0.0.1:{listener.Port}/callback", timeout.Token);
        Assert.IsFalse(waiting.IsCompleted, "还没拿到令牌,不能就此收工");
        Assert.Contains("location.hash", bootstrap, "得回一页去取片段");
        Assert.Contains("location.replace", bootstrap, "别在历史记录里留下带令牌的地址");

        // 第二跳:那一页把片段原样再请求一次
        await http.GetStringAsync(
            $"http://127.0.0.1:{listener.Port}/callback?access_token=do-tok&expires_in=3600&token_type=bearer",
            timeout.Token);

        Dictionary<string, string> result = await waiting;
        Assert.AreEqual("do-tok", result["access_token"]);
    }

    /// <summary>
    /// 用户点了拒绝时,对方是拿<b>查询串</b>回的(<c>?error=…</c>)。
    /// 那一跳要当场收下 —— 再去要片段的话,页面上什么都不会发生,登录就那么挂着。
    /// </summary>
    [TestMethod]
    public async Task Loopback_InFragmentMode_StillTakesAQueryStringError()
    {
        using var listener = new LoopbackRedirectListener(0, "/callback");
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<Dictionary<string, string>> waiting =
            listener.WaitAsync("t", "b", fragment: true, timeout.Token);
        await http.GetStringAsync(
            $"http://127.0.0.1:{listener.Port}/callback?error=access_denied", timeout.Token);

        Assert.AreEqual("access_denied", (await waiting)["error"]);
    }

    [TestMethod]
    public async Task ImplicitFlow_TakesTheTokenStraightOffTheCallback()
    {
        OAuthConfig oauth = ProviderCatalog.Find("digitalocean")!.CreateProvider().OAuth!.Clone();
        oauth.RedirectPort = 0; // 测试里别占那个固定端口,并行跑会互相踩
        // 没有 token 端点可打:任何一次 HTTP 都说明走错路了
        var stub = new OAuthStub().Json("""{"should":"never be called"}""", HttpStatusCode.InternalServerError);
        using var http = new HttpClient(stub);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var browser = new HttpClient();

        // 真实顺序是"先开浏览器,再回来等回调",所以这一下必须<b>立刻返回</b> ——
        // 在这里同步去打监听的话,监听还没开始 accept,测试会把自己锁死
        var login = new ProviderLogin(new OAuthClient(http), (uri, ct) =>
        {
            Dictionary<string, string> query = OAuthClient.ParseQuery(uri.Query.TrimStart('?'));
            string redirect = query["redirect_uri"];
            // state 照原样带回来 —— 真实服务端就是这么做的,少了它这一轮会被判成"回调对不上"
            string state = query["state"];
            _ = Task.Run(async () =>
            {
                await browser.GetStringAsync(redirect, ct);
                await browser.GetStringAsync(
                    $"{redirect}?access_token=do-tok&expires_in=7200&state={Uri.EscapeDataString(state)}", ct);
            }, ct);
            return Task.CompletedTask;
        });

        OAuthTokens tokens = await login.SignInAsync(oauth, Prompts, cancellationToken: timeout.Token);

        Assert.AreEqual("do-tok", tokens.AccessToken);
        Assert.IsEmpty(tokens.RefreshToken ?? "", "隐式流不发 refresh token,过期只能重登");
        Assert.IsEmpty(stub.Requests, "这一路不该有任何 token 交换");
    }

    // ---- 目录整体 ----

    [TestMethod]
    public void Catalog_EveryEntryHasAnEndpointUnlessItAsksForOne()
    {
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            AiProvider provider = entry.CreateProvider();
            if (entry.NeedsBaseUrl)
            {
                continue; // 自定义那几条本来就等着用户填
            }
            Assert.IsNotEmpty(provider.BaseUrl, $"{entry.Id} 没有基地址");
            Assert.IsNotEmpty(provider.Models[0].Model, $"{entry.Id} 没有起手模型");
        }
    }

    [TestMethod]
    public void Catalog_IdsAreUnique()
    {
        List<string> duplicates = [.. ProviderCatalog.All
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        Assert.IsEmpty(duplicates, $"目录 id 撞了:{string.Join(", ", duplicates)}");
    }
}

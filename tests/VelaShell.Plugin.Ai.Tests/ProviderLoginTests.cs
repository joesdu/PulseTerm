using System.Net;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 整条登录链路:起环回端口 → 开浏览器 → 接住回调 → 换凭据。
/// </summary>
/// <remarks>
/// "浏览器"由一个真的 HTTP GET 扮演,打的就是插件自己起的那个环回端口 ——
/// 于是 <see cref="LoopbackRedirectListener" /> 的解析、回页、路径匹配都是真跑的,
/// 只有授权服务器那一端是打桩的(见 <see cref="OAuthStub" />)。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ProviderLoginTests
{
    private static readonly LoginPrompts Prompts =
        new("Signed in", "You can close this tab.", "waiting", "exchanging", "code {0}");

    private static OAuthConfig Standard() => new()
    {
        Flow = OAuthFlow.AuthorizationCodePkce,
        AuthorizationUrl = "https://auth.example/authorize",
        TokenUrl = "https://auth.example/token",
        ClientId = "vela-client"
    };

    /// <summary>
    /// 扮演浏览器:拿到授权地址,按 <paramref name="callback" /> 算出要打回来的查询串,发一个 GET。
    /// </summary>
    /// <remarks>
    /// <b>必须是不阻塞的</b>:真浏览器是另一个进程,而调用方开完浏览器才去 accept ——
    /// 在这里同步等响应会把两边锁在一起。
    /// </remarks>
    private static Func<Uri, CancellationToken, Task> FakeBrowser(
        Func<Dictionary<string, string>, string> callback, List<string> pages)
        => (uri, cancellationToken) =>
        {
            _ = cancellationToken;
            Dictionary<string, string> query = OAuthTestAccess.ParseQuery(uri.Query.TrimStart('?'));
            string redirect = query.GetValueOrDefault("redirect_uri")
                              ?? query.GetValueOrDefault("callback_url")
                              ?? throw new InvalidOperationException("授权地址里没有回调地址");
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                pages.Add(await http.GetStringAsync($"{redirect}?{callback(query)}"));
            });
            return Task.CompletedTask;
        };

    [TestMethod]
    public async Task Pkce_RoundTripsThroughARealLoopbackPort()
    {
        OAuthStub stub = new OAuthStub().Json("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":900}""");
        using var http = new HttpClient(stub);
        List<string> pages = [];
        List<string> progress = [];
        var login = new ProviderLogin(new OAuthClient(http),
            FakeBrowser(query => $"code=the-code&state={query["state"]}", pages));

        OAuthTokens tokens = await login.SignInAsync(Standard(), Prompts,
            new Progress<LoginProgress>(step => progress.Add(step.Message)));

        Assert.AreEqual("at-1", tokens.AccessToken);
        Assert.AreEqual("rt-1", tokens.RefreshToken);
        // 换 code 时用的回调地址必须与授权时那个逐字节一致,否则服务端直接拒
        Dictionary<string, string> form = OAuthTestAccess.ParseQuery(stub.Requests[0].Body);
        Assert.AreEqual("the-code", form["code"]);
        Assert.StartsWith("http://127.0.0.1:", form["redirect_uri"]);
        Assert.EndsWith("/callback", form["redirect_uri"]);
        // 浏览器那边要看到一页"可以关掉了",而不是连接被重置
        await WaitForAsync(() => pages.Count > 0);
        Assert.Contains("You can close this tab.", pages[0]);
        Assert.Contains("waiting", progress);
        Assert.Contains("exchanging", progress);
    }

    [TestMethod]
    public async Task Pkce_StateMismatch_DiscardsTheSignIn()
    {
        OAuthStub stub = new OAuthStub().Json("""{"access_token":"should-never-be-used"}""");
        using var http = new HttpClient(stub);
        List<string> pages = [];
        var login = new ProviderLogin(new OAuthClient(http),
            FakeBrowser(_ => "code=the-code&state=someone-elses", pages));

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            login.SignInAsync(Standard(), Prompts));

        Assert.AreEqual("invalid_state", error.Error);
        Assert.IsEmpty(stub.Requests, "state 对不上就不该拿这个 code 去换任何东西");
    }

    [TestMethod]
    public async Task Pkce_ProviderRefused_SurfacesItsErrorCode()
    {
        using var http = new HttpClient(new OAuthStub());
        List<string> pages = [];
        var login = new ProviderLogin(new OAuthClient(http),
            FakeBrowser(_ => "error=access_denied&error_description=User+said+no", pages));

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            login.SignInAsync(Standard(), Prompts));

        Assert.AreEqual("access_denied", error.Error);
        Assert.Contains("User said no", error.Message);
    }

    [TestMethod]
    public async Task Pkce_OpenRouterVariant_WorksWithoutState()
    {
        // OpenRouter 的回调不带 state —— 那一路不能因为"没有 state"就判定失败
        OAuthStub stub = new OAuthStub().Json("""{"key":"sk-or-v1-xyz"}""");
        using var http = new HttpClient(stub);
        List<string> pages = [];
        OAuthConfig config = ProviderCatalog.Find("openrouter")!.CreateProvider().OAuth!;
        var login = new ProviderLogin(new OAuthClient(http), FakeBrowser(_ => "code=or-code", pages));

        OAuthTokens tokens = await login.SignInAsync(config, Prompts);

        Assert.AreEqual("sk-or-v1-xyz", tokens.AccessToken);
    }

    [TestMethod]
    public async Task Cancelling_StopsWaitingForTheBrowser()
    {
        using var http = new HttpClient(new OAuthStub());
        using var cancel = new CancellationTokenSource();
        var login = new ProviderLogin(new OAuthClient(http), (_, _) =>
        {
            cancel.Cancel(); // 相当于用户点了"取消"
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            login.SignInAsync(Standard(), Prompts, null, cancel.Token));
    }

    [TestMethod]
    public async Task DeviceCode_ReportsTheUserCodeAndOpensTheCompleteUri()
    {
        OAuthStub stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://auth.example/device","verification_uri_complete":"https://auth.example/device?code=WXYZ-1234","interval":1}""")
            .Json("""{"access_token":"at-device"}""");
        using var http = new HttpClient(stub);
        List<Uri> opened = [];
        List<LoginProgress> progress = [];
        OAuthConfig config = Standard();
        config.Flow = OAuthFlow.DeviceCode;
        config.DeviceCodeUrl = "https://auth.example/device/code";
        var login = new ProviderLogin(
            new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask },
            (uri, _) =>
            {
                opened.Add(uri);
                return Task.CompletedTask;
            });

        OAuthTokens tokens = await login.SignInAsync(config, Prompts, new Progress<LoginProgress>(progress.Add));

        Assert.AreEqual("at-device", tokens.AccessToken);
        // 用户码必须递到界面上 —— 让人去日志里抄码是不能接受的
        await WaitForAsync(() => progress.Count > 0);
        Assert.AreEqual("code WXYZ-1234", progress[0].Message);
        Assert.AreEqual("WXYZ-1234", progress[0].Device?.UserCode);
        // 有 verification_uri_complete 就开它,省得人手抄
        Assert.AreEqual("https://auth.example/device?code=WXYZ-1234", opened[0].ToString());
    }

    /// <summary>
    /// 设备码流程里那个"去这儿输码"的地址是<b>服务端给的</b>,而我们拿到就交给系统去打开 ——
    /// 不设限的话,一条 <c>file://</c> / 自定义 scheme 就等于借本程序的手启动别的东西。
    /// </summary>
    [TestMethod]
    public async Task DeviceCode_RefusesToOpenANonWebVerificationUri()
    {
        OAuthStub stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"AB12","verification_uri":"file:///C:/Windows/System32/calc.exe"}""")
            .Json("""{"access_token":"at-device"}""");
        using var http = new HttpClient(stub);
        List<Uri> opened = [];
        OAuthConfig config = Standard();
        config.Flow = OAuthFlow.DeviceCode;
        config.DeviceCodeUrl = "https://auth.example/device/code";
        var login = new ProviderLogin(
            new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask },
            (uri, _) =>
            {
                opened.Add(uri);
                return Task.CompletedTask;
            });

        OAuthTokens tokens = await login.SignInAsync(config, Prompts);

        Assert.IsEmpty(opened, "非 http/https 的地址一律不开");
        // 但流程本身要继续:用户码已经显示出来了,人可以自己去输
        Assert.AreEqual("at-device", tokens.AccessToken);
    }

    [TestMethod]
    public async Task DeviceCode_FallsBackToTheVerificationUriWhenTheCompleteOneIsNotWeb()
    {
        OAuthStub stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"AB12","verification_uri":"https://auth.example/device","verification_uri_complete":"javascript:alert(1)"}""")
            .Json("""{"access_token":"at-device"}""");
        using var http = new HttpClient(stub);
        List<Uri> opened = [];
        OAuthConfig config = Standard();
        config.Flow = OAuthFlow.DeviceCode;
        config.DeviceCodeUrl = "https://auth.example/device/code";
        var login = new ProviderLogin(
            new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask },
            (uri, _) =>
            {
                opened.Add(uri);
                return Task.CompletedTask;
            });

        await login.SignInAsync(config, Prompts);

        Assert.HasCount(1, opened);
        Assert.AreEqual("https://auth.example/device", opened[0].ToString(), "坏的那个跳过,退回好的那个");
    }

    [TestMethod]
    public async Task Loopback_IgnoresTheFaviconRequestBrowsersTackOn()
    {
        using var listener = new LoopbackRedirectListener(0, "/callback");
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<Dictionary<string, string>> waiting = listener.WaitAsync("t", "b", cancellationToken: timeout.Token);
        // 浏览器在打开回调页之后顺手来要图标 —— 拿它当结果的话,登录会在用户点同意前就"失败"
        HttpResponseMessage favicon = await http.GetAsync($"http://127.0.0.1:{listener.Port}/favicon.ico", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, favicon.StatusCode);
        Assert.IsFalse(waiting.IsCompleted, "路径不对的请求不算数,要继续等");

        await http.GetStringAsync($"http://127.0.0.1:{listener.Port}/callback?code=abc&state=s1", timeout.Token);
        Dictionary<string, string> query = await waiting;

        Assert.AreEqual("abc", query["code"]);
        Assert.AreEqual("s1", query["state"]);
    }

    [TestMethod]
    public void Loopback_BindsLoopbackOnlyAndReportsItsPort()
    {
        using var listener = new LoopbackRedirectListener(0, "callback");

        Assert.IsGreaterThan(0, listener.Port, "端口 0 要换成系统真分配的那个");
        Assert.AreEqual($"http://127.0.0.1:{listener.Port}/callback", listener.RedirectUri);
    }

    /// <summary>等一个后台断言成立(浏览器那条 GET 是并发跑的)。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
        Assert.IsTrue(condition(), "等超时了");
    }
}

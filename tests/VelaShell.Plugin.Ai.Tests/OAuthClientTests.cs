using System.Net;
using System.Text;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 打桩的授权服务器:按"第几次请求"回预先排好的响应,并把收到的请求原样记下来。
/// </summary>
/// <remarks>
/// 授权流程里真正容易出错的就是字段名与错误码分支,而它们全在 HTTP 这一层 ——
/// 用它把请求体逐字段验一遍,比对着规范读代码可靠。
/// </remarks>
internal sealed class OAuthStub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string MediaType)> _responses = new();

    /// <summary>收到过的请求(地址 + 请求体),按顺序。</summary>
    public List<(string Url, string Body)> Requests { get; } = [];

    /// <summary>收到过的请求头(<c>名: 值</c>),按顺序累计 —— 交换端点对头有硬要求,得验。</summary>
    public List<string> RequestHeaders { get; } = [];

    /// <summary>排一个 JSON 响应。</summary>
    public OAuthStub Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, body, "application/json"));
        return this;
    }

    /// <summary>排一个表单编码的响应(GitHub 那类在没有 Accept 时的默认回法)。</summary>
    public OAuthStub Form(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, body, "application/x-www-form-urlencoded"));
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));
        RequestHeaders.AddRange(request.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        (HttpStatusCode status, string payload, string mediaType) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.InternalServerError, "{\"error\":\"stub_exhausted\"}", "application/json");
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
    }
}

/// <summary>OAuth 协议层:授权地址怎么拼、凭据怎么换、错误码怎么分。</summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class OAuthClientTests
{
    private static OAuthConfig Standard() => new()
    {
        Flow = OAuthFlow.AuthorizationCodePkce,
        AuthorizationUrl = "https://auth.example/authorize",
        TokenUrl = "https://auth.example/token",
        ClientId = "vela-client",
        Scopes = "inference offline_access"
    };

    /// <summary>把查询串解成键值对(断言用)。</summary>
    private static Dictionary<string, string> Query(string urlOrBody)
    {
        int split = urlOrBody.IndexOf('?');
        return OAuthTestAccess.ParseQuery(split < 0 ? urlOrBody : urlOrBody[(split + 1)..]);
    }

    [TestMethod]
    public void Pkce_ChallengeIsUrlSafeSha256OfVerifier()
    {
        PkceCodes codes = PkceCodes.Create();

        string expected = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codes.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(expected, codes.Challenge);
        Assert.DoesNotContain("=", codes.Challenge, "base64url 不带填充");
        Assert.DoesNotContain("+", codes.Challenge);
        Assert.DoesNotContain("/", codes.Challenge);
        Assert.IsGreaterThanOrEqualTo(43, codes.Verifier.Length, "RFC 7636 要求 verifier 至少 43 字符");
        Assert.AreNotEqual(codes.Verifier, PkceCodes.Create().Verifier, "每次都该是新的");
    }

    [TestMethod]
    public void AuthorizationUrl_Standard_CarriesPkceStateAndScope()
    {
        var codes = PkceCodes.Create();

        Uri uri = OAuthClient.BuildAuthorizationUrl(Standard(), codes, "http://127.0.0.1:5123/callback");

        Assert.AreEqual("https://auth.example/authorize", uri.GetLeftPart(UriPartial.Path));
        Dictionary<string, string> query = Query(uri.Query);
        Assert.AreEqual("code", query["response_type"]);
        Assert.AreEqual("vela-client", query["client_id"]);
        Assert.AreEqual("http://127.0.0.1:5123/callback", query["redirect_uri"]);
        Assert.AreEqual(codes.State, query["state"]);
        Assert.AreEqual(codes.Challenge, query["code_challenge"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual("inference offline_access", query["scope"]);
        Assert.DoesNotContain(codes.Verifier, uri.ToString(), "verifier 绝不能出现在地址栏里");
    }

    [TestMethod]
    public void AuthorizationUrl_OpenRouter_UsesCallbackUrlAndDropsClientId()
    {
        var codes = PkceCodes.Create();
        OAuthConfig config = ProviderCatalog.Find("openrouter")!.CreateProvider().OAuth!;

        Uri uri = OAuthClient.BuildAuthorizationUrl(config, codes, "http://127.0.0.1:6000/callback");

        Dictionary<string, string> query = Query(uri.Query);
        Assert.AreEqual("http://127.0.0.1:6000/callback", query["callback_url"]);
        Assert.AreEqual(codes.Challenge, query["code_challenge"]);
        Assert.IsFalse(query.ContainsKey("client_id"), "这一路没有 client_id");
        Assert.IsFalse(query.ContainsKey("response_type"), "这一路也没有 response_type");
    }

    [TestMethod]
    public void AuthorizationUrl_KeepsQueryAlreadyOnTheEndpoint()
    {
        OAuthConfig config = Standard();
        config.AuthorizationUrl = "https://auth.example/authorize?tenant=acme";
        config.ExtraAuthorizeParams = "prompt=consent\n bad line without equals \naudience=api";

        Uri uri = OAuthClient.BuildAuthorizationUrl(config, PkceCodes.Create(), "http://127.0.0.1:1/cb");

        Dictionary<string, string> query = Query(uri.Query);
        Assert.AreEqual("acme", query["tenant"], "端点自带的查询串不能被冲掉");
        Assert.AreEqual("consent", query["prompt"]);
        Assert.AreEqual("api", query["audience"]);
    }

    [TestMethod]
    public async Task ExchangeCode_SendsVerifierAndTurnsExpiresInIntoAnInstant()
    {
        var stub = new OAuthStub().Json(
            """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600,"scope":"inference"}""");
        using var http = new HttpClient(stub);
        var codes = PkceCodes.Create();

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(Standard(), codes, "the-code", "http://127.0.0.1:5123/callback");

        Dictionary<string, string> form = Query(stub.Requests[0].Body);
        Assert.AreEqual("https://auth.example/token", stub.Requests[0].Url);
        Assert.AreEqual("authorization_code", form["grant_type"]);
        Assert.AreEqual("the-code", form["code"]);
        Assert.AreEqual(codes.Verifier, form["code_verifier"]);
        Assert.AreEqual("http://127.0.0.1:5123/callback", form["redirect_uri"]);
        Assert.AreEqual("vela-client", form["client_id"]);
        Assert.IsFalse(form.ContainsKey("client_secret"), "公共客户端不发密钥");

        Assert.AreEqual("at-1", tokens.AccessToken);
        Assert.AreEqual("rt-1", tokens.RefreshToken);
        Assert.IsNotNull(tokens.ExpiresAt);
        // expires_in 是相对秒数,存下来的必须是绝对时刻,否则进程重启后就永远"没过期"
        double seconds = (tokens.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
        Assert.IsGreaterThan(3500, seconds);
        Assert.IsLessThan(3700, seconds);
        Assert.IsFalse(tokens.NeedsRefresh);
    }

    [TestMethod]
    public async Task ExchangeCode_FormEncodedResponse_IsParsedToo()
    {
        // 中转/代理层把 Accept 吃掉时,GitHub 之流回的就是这个形状
        var stub = new OAuthStub().Form("access_token=at-form&scope=repo&token_type=bearer");
        using var http = new HttpClient(stub);

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(Standard(), PkceCodes.Create(), "c", "http://127.0.0.1:1/cb");

        Assert.AreEqual("at-form", tokens.AccessToken);
        Assert.AreEqual("repo", tokens.Scope);
        Assert.IsNull(tokens.ExpiresAt, "没给 expires_in 就当不过期");
    }

    [TestMethod]
    public async Task ExchangeCode_ErrorPayload_ThrowsWithTheCode()
    {
        var stub = new OAuthStub().Json(
            """{"error":"invalid_grant","error_description":"code already used"}""", HttpStatusCode.BadRequest);
        using var http = new HttpClient(stub);

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            new OAuthClient(http).ExchangeCodeAsync(Standard(), PkceCodes.Create(), "c", "http://127.0.0.1:1/cb"));

        Assert.AreEqual("invalid_grant", error.Error);
        Assert.Contains("code already used", error.Message);
    }

    [TestMethod]
    public async Task ExchangeCode_HttpErrorWithoutOAuthPayload_StillReports()
    {
        var stub = new OAuthStub().Json("<html>gateway down</html>", HttpStatusCode.BadGateway);
        using var http = new HttpClient(stub);

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            new OAuthClient(http).ExchangeCodeAsync(Standard(), PkceCodes.Create(), "c", "http://127.0.0.1:1/cb"));

        Assert.Contains("502", error.Message);
        Assert.Contains("gateway down", error.Message);
    }

    [TestMethod]
    public async Task OpenRouter_ExchangesTheCodeForAPlainKey()
    {
        var stub = new OAuthStub().Json("""{"key":"sk-or-v1-abc"}""");
        using var http = new HttpClient(stub);
        OAuthConfig config = ProviderCatalog.Find("openrouter")!.CreateProvider().OAuth!;
        var codes = PkceCodes.Create();

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(config, codes, "the-code", "http://127.0.0.1:1/cb");

        Assert.AreEqual("https://openrouter.ai/api/v1/auth/keys", stub.Requests[0].Url);
        // 这一路是 JSON 请求体,不是表单
        Assert.Contains("\"code_verifier\":\"" + codes.Verifier + "\"", stub.Requests[0].Body);
        Assert.Contains("\"code_challenge_method\":\"S256\"", stub.Requests[0].Body);
        Assert.AreEqual("sk-or-v1-abc", tokens.AccessToken);
        Assert.IsNull(tokens.RefreshToken, "换来的是长期 Key,没有刷新一说");
        Assert.IsNull(tokens.ExpiresAt);
    }

    [TestMethod]
    public async Task Refresh_KeepsTheOldRefreshTokenWhenTheServerOmitsIt()
    {
        // RFC 6749 §6 允许服务端不重发 refresh token —— 丢了就再也刷不动了
        var stub = new OAuthStub().Json("""{"access_token":"at-2","expires_in":600}""");
        using var http = new HttpClient(stub);
        var current = new OAuthTokens { AccessToken = "at-1", RefreshToken = "rt-1", Account = "ops@example.com" };

        OAuthTokens fresh = await new OAuthClient(http).RefreshAsync(Standard(), current);

        Dictionary<string, string> form = Query(stub.Requests[0].Body);
        Assert.AreEqual("refresh_token", form["grant_type"]);
        Assert.AreEqual("rt-1", form["refresh_token"]);
        Assert.AreEqual("at-2", fresh.AccessToken);
        Assert.AreEqual("rt-1", fresh.RefreshToken);
        Assert.AreEqual("ops@example.com", fresh.Account, "账号也要留住,不然界面上就成了匿名");
    }

    [TestMethod]
    public async Task Refresh_WithoutARefreshToken_FailsFast()
    {
        using var http = new HttpClient(new OAuthStub());

        await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            new OAuthClient(http).RefreshAsync(Standard(), new OAuthTokens { AccessToken = "at" }));
    }

    [TestMethod]
    public async Task DeviceCode_PendingThenSlowDown_BacksOffByFiveSecondsThenSucceeds()
    {
        var stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://auth.example/device","interval":2,"expires_in":900}""")
            .Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
            .Json("""{"error":"slow_down"}""", HttpStatusCode.BadRequest)
            .Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
            .Json("""{"access_token":"at-device","expires_in":3600}""");
        using var http = new HttpClient(stub);
        List<TimeSpan> waits = [];
        var client = new OAuthClient(http) { Delay = (span, _) => { waits.Add(span); return Task.CompletedTask; } };
        OAuthConfig config = Standard();
        config.Flow = OAuthFlow.DeviceCode;
        config.DeviceCodeUrl = "https://auth.example/device/code";

        DeviceCodeGrant grant = await client.StartDeviceCodeAsync(config);
        OAuthTokens tokens = await client.PollDeviceCodeAsync(config, grant);

        Assert.AreEqual("WXYZ-1234", grant.UserCode);
        Assert.AreEqual("https://auth.example/device", grant.VerificationUri);
        Assert.AreEqual(TimeSpan.FromSeconds(2), grant.Interval);
        Assert.AreEqual("at-device", tokens.AccessToken);
        // 服务端喊 slow_down 之后,间隔必须真的加上去,否则会一直被限速
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) },
            waits);
        Dictionary<string, string> poll = Query(stub.Requests[1].Body);
        Assert.AreEqual("urn:ietf:params:oauth:grant-type:device_code", poll["grant_type"]);
        Assert.AreEqual("dc", poll["device_code"]);
    }

    [TestMethod]
    public async Task DeviceCode_AcceptsMicrosoftsVerificationUrlSpelling()
    {
        // Entra ID 用的是 RFC 定稿前的 verification_url,少一个 "i"
        var stub = new OAuthStub().Json(
            """{"device_code":"dc","user_code":"AB12","verification_url":"https://microsoft.com/devicelogin"}""");
        using var http = new HttpClient(stub);
        OAuthConfig config = Standard();
        config.DeviceCodeUrl = "https://auth.example/device/code";

        DeviceCodeGrant grant = await new OAuthClient(http).StartDeviceCodeAsync(config);

        Assert.AreEqual("https://microsoft.com/devicelogin", grant.VerificationUri);
        Assert.AreEqual(TimeSpan.FromSeconds(5), grant.Interval, "没给 interval 时按 RFC 8628 默认 5 秒");
    }

    [TestMethod]
    public async Task DeviceCode_DeniedByUser_StopsInsteadOfPollingOn()
    {
        var stub = new OAuthStub().Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest);
        using var http = new HttpClient(stub);
        var client = new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask };
        var grant = new DeviceCodeGrant("dc", "AB12", "https://auth.example/device", null,
            TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            client.PollDeviceCodeAsync(Standard(), grant));

        Assert.AreEqual("access_denied", error.Error);
        Assert.HasCount(1, stub.Requests, "被拒之后不该再轮询");
    }

    [TestMethod]
    public async Task DeviceCode_AlreadyExpired_DoesNotEvenAsk()
    {
        var stub = new OAuthStub();
        using var http = new HttpClient(stub);
        var client = new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask };
        var grant = new DeviceCodeGrant("dc", "AB12", "u", null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        OAuthException error = await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            client.PollDeviceCodeAsync(Standard(), grant));

        Assert.AreEqual("expired_token", error.Error);
        Assert.IsEmpty(stub.Requests);
    }

    [TestMethod]
    public void Tokens_NeedsRefreshLeavesAMarginBeforeExpiry()
    {
        var soon = new OAuthTokens { AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
        var later = new OAuthTokens { AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) };
        var gone = new OAuthTokens { AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };

        Assert.IsTrue(soon.NeedsRefresh, "还有 30 秒就该换了 —— 建客户端到发出请求还有间隔");
        Assert.IsFalse(soon.Expired);
        Assert.IsFalse(later.NeedsRefresh);
        Assert.IsTrue(gone.Expired);
        Assert.IsFalse(new OAuthTokens { AccessToken = "a" }.NeedsRefresh, "没有过期时刻就当不过期");
    }
}

/// <summary>测试要用到的内部解析器(<c>InternalsVisibleTo</c> 开着,但语法上得有个入口)。</summary>
internal static class OAuthTestAccess
{
    public static Dictionary<string, string> ParseQuery(string query) => OAuthClient.ParseQuery(query);
}

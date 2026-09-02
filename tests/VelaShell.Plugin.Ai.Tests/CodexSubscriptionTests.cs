using System.Text;
using System.Text.Json;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 订阅型端点那一路特有的三样东西:回调主机名、从 id_token 里取账号 id、以及请求头模板。
/// </summary>
/// <remarks>
/// 这三样单独拎出来测,是因为它们全都"错了也编译得过、跑起来只回一个看不懂的 4xx" ——
/// 回调主机名写错是 <c>invalid_redirect_uri</c>,账号 id 没取到是缺一个必填头,
/// 模板没替换就带着一个字面量 <c>{account_id}</c> 发出去。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class CodexSubscriptionTests
{
    /// <summary>拼一个只有载荷有意义的 JWT(签名段随便填,本程序本来就不验签)。</summary>
    private static string Jwt(object payload)
    {
        static string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment("""{"alg":"none"}""")}.{Segment(JsonSerializer.Serialize(payload))}.sig";
    }

    private static OAuthConfig CodexConfig() => ProviderCatalog.Find("openai-codex")!.CreateProvider().OAuth!;

    // ---- 回调地址 ----

    [TestMethod]
    public void RedirectHost_IsWrittenIntoTheUriButTheSocketStaysOnLoopback()
    {
        // localhost 与 127.0.0.1 在严格比对的服务端那里不是一回事,注册了哪个就得写哪个
        using var listener = new LoopbackRedirectListener(0, "/auth/callback", "localhost");

        Assert.AreEqual($"http://localhost:{listener.Port}/auth/callback", listener.RedirectUri);
        Assert.IsGreaterThan(0, listener.Port);
    }

    [TestMethod]
    public void RedirectHost_DefaultsToTheNumericLoopback()
    {
        using var listener = new LoopbackRedirectListener(0, "/callback");

        Assert.StartsWith("http://127.0.0.1:", listener.RedirectUri);
    }

    [TestMethod]
    public void Codex_AuthorizationUrlCarriesTheFixedCallbackAndItsExtraParams()
    {
        OAuthConfig config = CodexConfig();
        var pkce = PkceCodes.Create();

        Uri uri = OAuthClient.BuildAuthorizationUrl(config, pkce, "http://localhost:1455/auth/callback");

        Dictionary<string, string> query = OAuthTestAccess.ParseQuery(uri.Query.TrimStart('?'));
        Assert.AreEqual("https://auth.openai.com/oauth/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("http://localhost:1455/auth/callback", query["redirect_uri"]);
        Assert.AreEqual("openid profile email offline_access", query["scope"]);
        Assert.AreEqual("true", query["id_token_add_organizations"]);
        Assert.AreEqual("true", query["codex_cli_simplified_flow"]);
        Assert.AreEqual(pkce.Challenge, query["code_challenge"]);
        Assert.IsNotEmpty(query["client_id"]);
    }

    [TestMethod]
    public void Codex_StripsEveryParameterThatBackendRejects()
    {
        // 这个后端是 Responses 的受限子集,一次只告诉你一个不认的字段 ——
        // 真机上先撞的是 max_output_tokens,其余一并按 Codex 官方客户端的做法不发
        AiProvider codex = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            MaxOutputTokens = 8192,
            Temperature = 0.7f,
            TopP = 0.9f,
            StopSequences = ["STOP"],
            FrequencyPenalty = 1,
            PresencePenalty = 1,
            Seed = 42
        };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(codex, codex.Models[0]));

        Assert.IsNull(options.MaxOutputTokens);
        Assert.IsNull(options.Temperature);
        Assert.IsNull(options.TopP);
        Assert.IsNull(options.StopSequences);
        Assert.IsNull(options.FrequencyPenalty);
        Assert.IsNull(options.PresencePenalty);
        Assert.IsNull(options.Seed);
        // 不给第三方做服务端响应存储
        Assert.IsFalse(codex.StoreResponses);
        Assert.IsNotNull(options.RawRepresentationFactory, "得把 store: false 递到原生请求上");
    }

    [TestMethod]
    public void OrdinaryProviders_KeepEveryParameterTheUserSet()
    {
        // 官方 OpenAI 走的是公开 Responses API,这些都是正常能力,不该被我们摘掉
        AiProvider openai = ProviderCatalog.Find("openai")!.CreateProvider();
        var options = new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 8192, Temperature = 0.7f };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(openai, openai.Models[0]));

        Assert.AreEqual(8192, options.MaxOutputTokens);
        Assert.AreEqual(0.7f, options.Temperature);
        Assert.IsTrue(openai.StoreResponses);
        Assert.IsNull(options.RawRepresentationFactory);
    }

    [TestMethod]
    public void UnknownParameterNames_AreIgnoredRatherThanFatal()
    {
        // 目录里写错一个字,不该让整轮对话崩掉
        var provider = new AiProvider
        {
            UnsupportedParameters = "max_output_tokens\nnot_a_real_parameter\n\n  ",
            Models = [new AiModelConfig { Model = "m" }]
        };
        var options = new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 100, Temperature = 0.5f };

        AiSettingsStore.ApplyEndpointQuirks(options, new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(options.MaxOutputTokens);
        Assert.AreEqual(0.5f, options.Temperature, "没列进去的不该被顺手摘掉");
    }

    [TestMethod]
    public void Codex_EntryIsMarkedExperimental()
    {
        ProviderCatalogEntry entry = ProviderCatalog.Find("openai-codex")!;

        // 借的是别家 CLI 的客户端身份、打的是非公开端点 —— 界面上必须如实标出来
        Assert.IsTrue(entry.Experimental);
        Assert.IsTrue(entry.IsSubscription);
        AiProvider provider = entry.CreateProvider();
        Assert.AreEqual(ChatProtocol.OpenAiResponses, provider.DefaultProtocol);
        // Responses 客户端会往 {BaseUrl}/responses 发,拼出来正好是 codex 的那个端点
        Assert.AreEqual("https://chatgpt.com/backend-api/codex", provider.BaseUrl);
        Assert.IsTrue(provider.CanSignIn, "客户端 id 是写死的,这一条应当开箱即可登录");
    }

    // ---- 从 id_token 取账号 id ----

    [TestMethod]
    public async Task Exchange_PullsTheAccountIdOutOfTheNamespacedClaim()
    {
        // OpenAI 把它藏在一个<b>名字里带斜杠</b>的命名空间 claim 底下,按 / 全切就永远找不到
        string idToken = Jwt(new Dictionary<string, object>
        {
            ["email"] = "ops@example.com",
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = "acct-1234",
                ["chatgpt_plan_type"] = "plus"
            }
        });
        var stub = new OAuthStub().Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = "at-1",
            ["refresh_token"] = "rt-1",
            ["expires_in"] = 3600,
            ["id_token"] = idToken
        }));
        using var http = new HttpClient(stub);

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(CodexConfig(), PkceCodes.Create(), "code", "http://localhost:1455/auth/callback");

        Assert.AreEqual("acct-1234", tokens.AccountId);
        Assert.AreEqual("ops@example.com", tokens.Account, "顺手把邮箱也读出来,界面上好显示登的是谁");
    }

    [TestMethod]
    public async Task Refresh_KeepsTheAccountIdWhenTheServerSendsNoIdToken()
    {
        // 刷新响应通常不带 id_token —— 丢了账号 id,之后每条请求都会缺一个必填头
        var stub = new OAuthStub().Json("""{"access_token":"at-2","expires_in":3600}""");
        using var http = new HttpClient(stub);
        var current = new OAuthTokens { AccessToken = "at-1", RefreshToken = "rt-1", AccountId = "acct-1234" };

        OAuthTokens fresh = await new OAuthClient(http).RefreshAsync(CodexConfig(), current);

        Assert.AreEqual("at-2", fresh.AccessToken);
        Assert.AreEqual("acct-1234", fresh.AccountId);
        Assert.AreEqual("rt-1", fresh.RefreshToken);
    }

    [TestMethod]
    public async Task Exchange_WithoutTheClaim_LeavesTheAccountIdEmptyInsteadOfGuessing()
    {
        var stub = new OAuthStub().Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = "at-1",
            ["id_token"] = Jwt(new Dictionary<string, object> { ["sub"] = "user-1" })
        }));
        using var http = new HttpClient(stub);

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(CodexConfig(), PkceCodes.Create(), "code", "http://localhost:1455/auth/callback");

        Assert.IsNull(tokens.AccountId);
    }

    [TestMethod]
    public async Task Exchange_WithAMalformedIdToken_StillSucceeds()
    {
        // id_token 解不开不该把整次登录炸掉:access_token 才是要紧的那个
        var stub = new OAuthStub().Json("""{"access_token":"at-1","id_token":"not-a-jwt"}""");
        using var http = new HttpClient(stub);

        OAuthTokens tokens = await new OAuthClient(http)
            .ExchangeCodeAsync(CodexConfig(), PkceCodes.Create(), "code", "http://localhost:1455/auth/callback");

        Assert.AreEqual("at-1", tokens.AccessToken);
        Assert.IsNull(tokens.AccountId);
    }

    // ---- 请求头模板 ----

    [TestMethod]
    public void Headers_SubstituteTheAccountIdPlaceholder()
    {
        IReadOnlyList<KeyValuePair<string, string>> headers = ExtraHeadersPolicy.Parse(
            "chatgpt-account-id: {account_id}\nOpenAI-Beta: responses=experimental", "acct-1234");

        Assert.HasCount(2, headers);
        Assert.AreEqual("chatgpt-account-id", headers[0].Key);
        Assert.AreEqual("acct-1234", headers[0].Value);
        Assert.AreEqual("OpenAI-Beta", headers[1].Key);
        Assert.AreEqual("responses=experimental", headers[1].Value);
    }

    [TestMethod]
    public void Headers_DropTheOnesThatWouldGoOutEmpty()
    {
        // 还没拿到账号 id 时,与其发一个空头出去,不如干脆不发 —— 服务端的报错会准确得多
        IReadOnlyList<KeyValuePair<string, string>> headers = ExtraHeadersPolicy.Parse(
            "chatgpt-account-id: {account_id}\nOpenAI-Beta: responses=experimental", null);

        Assert.HasCount(1, headers);
        Assert.AreEqual("OpenAI-Beta", headers[0].Key);
    }

    [TestMethod]
    public void Headers_IgnoreBlankAndMalformedLines()
    {
        IReadOnlyList<KeyValuePair<string, string>> headers = ExtraHeadersPolicy.Parse(
            "\n  \nno-colon-here\nX-One: 1\n: novalue\n", "a");

        Assert.HasCount(1, headers);
        Assert.AreEqual("X-One", headers[0].Key);
    }

    [TestMethod]
    public void Headers_AreEmptyWhenNothingIsConfigured()
    {
        Assert.IsEmpty(ExtraHeadersPolicy.Parse(null, "a"));
        Assert.IsEmpty(ExtraHeadersPolicy.Parse("   ", "a"));
    }

    // ---- 凭据解析到头这一路 ----

    [TestMethod]
    public async Task Credential_ForCodex_CarriesTheBearerTokenAndTheAccountHeader()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("openai-codex")!.CreateProvider();
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "at-live",
            AccountId = "acct-1234",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("at-live", credential.Value);
        Assert.IsTrue(credential.IsBearerToken);
        Assert.IsNotNull(credential.Headers);
        Assert.Contains(new KeyValuePair<string, string>("chatgpt-account-id", "acct-1234"), credential.Headers);
    }

    [TestMethod]
    public async Task Credential_ForAPlainApiKeyProvider_CarriesNoHeaders()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("openai")!.CreateProvider();
        await store.SetApiKeyAsync(provider.Id, "sk-1");

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(credential.Headers);
    }
}

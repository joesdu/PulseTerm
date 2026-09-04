using System.Reflection;
using System.Text.Json;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 供应商目录、订阅令牌的存取,以及"发请求时到底拿哪一把凭据"。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SubscriptionProviderTests
{
    // ---- 目录 ----

    [TestMethod]
    public void Catalog_EntriesAreWellFormed()
    {
        Assert.IsNotEmpty(ProviderCatalog.All);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            Assert.IsTrue(ids.Add(entry.Id), $"目录 id 重了:{entry.Id}");
            Assert.IsNotEmpty(entry.Name, entry.Id);
            Assert.IsNotEmpty(entry.Models, $"{entry.Id} 少了示例模型这一行副标题");
            Assert.IsNotEmpty(entry.Monogram, entry.Id);
            Assert.IsLessThanOrEqualTo(2, entry.Monogram.Length, $"{entry.Id} 的字母牌只放得下两个字");

            AiProvider provider = entry.CreateProvider();
            Assert.AreEqual(entry.Id, provider.CatalogId, "造出来的供应商要认得自己是从哪一条来的");
            Assert.AreEqual(entry.Auth, provider.Auth);
            Assert.HasCount(1, provider.Models, $"{entry.Id} 该带一个起手模型");
            if (!entry.NeedsBaseUrl)
            {
                Assert.IsNotEmpty(provider.BaseUrl, $"{entry.Id} 不用自填地址,那就得自带一个");
                Assert.IsNotEmpty(provider.Models[0].Model, $"{entry.Id} 该带一个示例模型 id");
            }
        }
    }

    [TestMethod]
    public void Catalog_SubscriptionEntriesEitherSignInNowOrSayWhatTheyStillNeed()
    {
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All.Where(e => e.IsSubscription))
        {
            AiProvider provider = entry.CreateProvider();
            OAuthConfig oauth = provider.OAuth ?? throw new AssertFailedException($"{entry.Id} 说能登录却没有 OAuth 参数");
            if (provider.CanSignIn)
            {
                continue; // 参数齐,点一下就登
            }
            // 登不了就必须说得出还缺什么,而且缺的那样得有个去处 ——
            // 界面上"一个空框 + 无从下手"是这一页最不该出现的东西
            bool pendingClientId = oauth.Flow != OAuthFlow.OpenRouterPkce && string.IsNullOrWhiteSpace(oauth.ClientId);
            bool pendingEndpoints = entry.NeedsOAuthSetup;
            Assert.IsTrue(pendingClientId || pendingEndpoints, $"{entry.Id} 登不了,却也说不出缺什么");
            if (pendingClientId && !pendingEndpoints)
            {
                Assert.IsNotEmpty(entry.RegistrationUrl,
                    $"{entry.Id} 只差一个客户端 id,那就必须给出去哪儿注册");
            }
        }
        // 出厂即可登录的那条要真的存在 —— 否则这个功能对新用户等于不存在
        Assert.Contains(e => e.IsSubscription && e.CreateProvider().CanSignIn, ProviderCatalog.All,
            "至少要有一家开箱即登的");
    }

    [TestMethod]
    public void Catalog_EntriesThatFillInAClientIdBecomeOneClickWithNoOtherChange()
    {
        // 这条守的是"拿到 client id 就填一行"这个承诺:除了那一个字符串,别的都不用动
        ProviderCatalogEntry entry = ProviderCatalog.Find("huggingface")!;
        AiProvider provider = entry.CreateProvider();
        Assert.IsFalse(provider.CanSignIn, "还没注册下来时应当登不了");

        provider.OAuth!.ClientId = "some-registered-client";

        Assert.IsTrue(provider.CanSignIn, "填上客户端 id 就该立刻变成一键登录");
        Assert.IsNotEmpty(provider.BaseUrl, "端点、地址、模型目录里都齐了");
        Assert.IsNotEmpty(provider.OAuth.AuthorizationUrl);
        Assert.IsNotEmpty(provider.OAuth.TokenUrl);
        Assert.IsNotEmpty(provider.OAuth.Scopes);
    }

    [TestMethod]
    public void Catalog_ApiKeyEntriesDoNotCarryOAuthSettings()
    {
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All.Where(e => !e.IsSubscription))
        {
            Assert.IsNull(entry.CreateProvider().OAuth, $"{entry.Id} 是填 Key 的,不该带 OAuth 参数");
        }
    }

    [TestMethod]
    public void Catalog_StillCoversEveryProviderTheOldPresetDropdownHad()
    {
        // 老版本那个预设下拉里的六条都得在,升级上来的用户不能发现少了一家
        string[] required = ["openai", "anthropic", "xai", "ollama", "custom-openai", "custom-anthropic"];
        foreach (string id in required)
        {
            Assert.IsNotNull(ProviderCatalog.Find(id), $"目录里少了 {id}");
        }
        Assert.AreEqual("custom-openai", ProviderCatalog.Custom.Id);
        Assert.IsNull(ProviderCatalog.Find("nope"));
        Assert.IsNull(ProviderCatalog.Find(null));
    }

    [TestMethod]
    public void Loc_EveryKeyCoversAllFiveLanguages()
    {
        // 少一列不会编译报错,只会在切到那门语言时抛 IndexOutOfRange —— 上个棘轮
        FieldInfo field = typeof(Loc).GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)!;
        var table = (Dictionary<string, string[]>)field.GetValue(null)!;
        foreach ((string key, string[] values) in table)
        {
            Assert.HasCount(5, values, $"文案 {key} 少了几门语言");
            for (int i = 0; i < values.Length; i++)
            {
                Assert.IsNotEmpty(values[i], $"文案 {key} 的第 {i} 门语言是空的");
            }
        }
    }

    [TestMethod]
    public void Loc_NewSignInStringsAreTranslatedInEveryLanguage()
    {
        string[] keys = ["SetupProviders", "SetupSignIn", "StatusNotConnected", "LoginPageBody", "OAuthClientId"];
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            var loc = new Loc(locale);
            foreach (string key in keys)
            {
                Assert.AreNotEqual(key, loc[key], $"{locale} 下 {key} 没有文案(取词回退成了键名)");
            }
        }
    }

    // ---- 令牌存取 ----

    [TestMethod]
    public async Task Tokens_RoundTripThroughTheSecretStoreAndStayApartFromApiKeys()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var tokens = new OAuthTokens
        {
            AccessToken = "at-1",
            RefreshToken = "rt-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Account = "ops@example.com"
        };

        await store.SetApiKeyAsync("p1", "sk-manual");
        await store.SaveTokensAsync("p1", tokens);

        OAuthTokens? loaded = await store.GetTokensAsync("p1");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("at-1", loaded.AccessToken);
        Assert.AreEqual("rt-1", loaded.RefreshToken);
        Assert.AreEqual("ops@example.com", loaded.Account);
        Assert.AreEqual("sk-manual", await store.GetApiKeyAsync("p1"), "登录不该动手填的那把 Key");

        // 退出登录只清令牌
        await store.ClearTokensAsync("p1");
        Assert.IsNull(await store.GetTokensAsync("p1"));
        Assert.AreEqual("sk-manual", await store.GetApiKeyAsync("p1"));
    }

    [TestMethod]
    public async Task DeletingAProvider_TakesBothItsKeyAndItsSignInWithIt()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        await store.SetApiKeyAsync("p1", "sk-1");
        await store.SaveTokensAsync("p1", new OAuthTokens { AccessToken = "at-1" });

        await store.DeleteApiKeyAsync("p1");

        Assert.IsNull(await store.GetApiKeyAsync("p1"));
        Assert.IsNull(await store.GetTokensAsync("p1"), "删掉供应商却把登录留在库里,就是一份没人认领的凭据");
    }

    [TestMethod]
    public async Task Tokens_CorruptPayloadReadsAsNotSignedIn()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        await context.Secrets.SetAsync("oauth:p1", "{ this is not json");

        Assert.IsNull(await store.GetTokensAsync("p1"), "存坏了当没登录,别让每条消息都炸一次");
    }

    // ---- 凭据解析 ----

    private static (AiProvider Provider, ResolvedModel Model) Subscription(OAuthCredential credential)
    {
        var provider = new AiProvider
        {
            Name = "Sub",
            BaseUrl = "https://relay.example/v1",
            Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig
            {
                TokenUrl = "https://auth.example/token",
                AuthorizationUrl = "https://auth.example/authorize",
                Credential = credential
            },
            Models = [new AiModelConfig { Model = "m" }]
        };
        return (provider, new ResolvedModel(provider, provider.Models[0]));
    }

    [TestMethod]
    public async Task Credential_ApiKeyProvider_IsNotABearerToken()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var provider = new AiProvider { Name = "Plain", Models = [new AiModelConfig { Model = "m" }] };
        await store.SetApiKeyAsync(provider.Id, "sk-plain");

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("sk-plain", credential.Value);
        Assert.IsFalse(credential.IsBearerToken, "手填的 Key 要按各家老规矩发(Anthropic 的 x-api-key)");
    }

    [TestMethod]
    public async Task Credential_SubscriptionWithAccessToken_IsABearerToken()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        (AiProvider provider, ResolvedModel model) = Subscription(OAuthCredential.AccessToken);
        await store.SaveTokensAsync(provider.Id,
            new OAuthTokens { AccessToken = "at-live", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        ProviderCredential credential = await store.ResolveCredentialAsync(model);

        Assert.AreEqual("at-live", credential.Value);
        Assert.IsTrue(credential.IsBearerToken);
    }

    [TestMethod]
    public async Task Credential_SubscriptionThatMintedAKey_GoesDownThePlainKeyPath()
    {
        // OpenRouter 那一路:登录换回来的就是一把普通 Key,不该被当成 Bearer 令牌另眼相待
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        (AiProvider provider, ResolvedModel model) = Subscription(OAuthCredential.ApiKey);
        await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "sk-or-v1-abc" });

        ProviderCredential credential = await store.ResolveCredentialAsync(model);

        Assert.AreEqual("sk-or-v1-abc", credential.Value);
        Assert.IsFalse(credential.IsBearerToken);
    }

    [TestMethod]
    public async Task Credential_NotSignedInYet_IsEmptyRatherThanAnError()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        (AiProvider _, ResolvedModel model) = Subscription(OAuthCredential.AccessToken);

        ProviderCredential credential = await store.ResolveCredentialAsync(model);

        // 让请求带着空凭据发出去,由服务端回一个准确的 401,比在这里抛一个自造的异常有用
        Assert.IsNull(credential.Value);
        Assert.IsFalse(credential.IsBearerToken);
    }

    [TestMethod]
    public async Task Credential_ModelWithItsOwnKey_OverridesTheSubscription()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        (AiProvider provider, ResolvedModel _) = Subscription(OAuthCredential.AccessToken);
        provider.Models[0].HasOwnApiKey = true;
        await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "at-live" });
        await store.SetApiKeyAsync(provider.Models[0].Id, "sk-model");

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("sk-model", credential.Value);
        Assert.IsFalse(credential.IsBearerToken);
    }

    [TestMethod]
    public async Task Credential_NearlyExpiredToken_IsRefreshedAndWrittenBack()
    {
        using var context = new TestPluginContext();
        OAuthStub stub = new OAuthStub().Json("""{"access_token":"at-fresh","expires_in":3600}""");
        using var http = new HttpClient(stub);
        var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
        (AiProvider provider, ResolvedModel model) = Subscription(OAuthCredential.AccessToken);
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "at-stale",
            RefreshToken = "rt-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20)
        });

        ProviderCredential credential = await store.ResolveCredentialAsync(model);

        Assert.AreEqual("at-fresh", credential.Value);
        Assert.IsTrue(credential.IsBearerToken);
        // 换来的必须落盘,否则每条消息都要再刷一次
        string? stored = await context.Secrets.GetAsync($"oauth:{provider.Id}");
        Assert.IsNotNull(stored);
        OAuthTokens persisted = JsonSerializer.Deserialize<OAuthTokens>(stored)!;
        Assert.AreEqual("at-fresh", persisted.AccessToken);
        Assert.AreEqual("rt-1", persisted.RefreshToken, "服务端没重发 refresh token,得把旧的留住");
    }

    [TestMethod]
    public async Task Credential_RefreshFailure_FallsBackToTheOldTokenInsteadOfBlowingUp()
    {
        using var context = new TestPluginContext();
        OAuthStub stub = new OAuthStub().Json("""{"error":"invalid_grant"}""", System.Net.HttpStatusCode.BadRequest);
        using var http = new HttpClient(stub);
        var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
        (AiProvider provider, ResolvedModel model) = Subscription(OAuthCredential.AccessToken);
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "at-stale",
            RefreshToken = "rt-dead",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        });

        ProviderCredential credential = await store.ResolveCredentialAsync(model);

        // 拿过期令牌换一个准确的 401,好过在建客户端时抛一个用户看不懂的异常
        Assert.AreEqual("at-stale", credential.Value);
    }

    [TestMethod]
    public async Task Credential_ConcurrentResolves_RefreshOnlyOnce()
    {
        using var context = new TestPluginContext();
        OAuthStub stub = new OAuthStub().Json("""{"access_token":"at-fresh","expires_in":3600}""");
        using var http = new HttpClient(stub);
        var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
        (AiProvider provider, ResolvedModel model) = Subscription(OAuthCredential.AccessToken);
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "at-stale",
            RefreshToken = "rt-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        // 一轮对话里聊天与"后续提问"会各建一次客户端,几乎同时
        ProviderCredential[] results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => store.ResolveCredentialAsync(model)));

        Assert.IsTrue(results.All(r => r.Value == "at-fresh"));
        Assert.HasCount(1, stub.Requests, "并发解析只该刷一次令牌");
    }

    [TestMethod]
    public void Provider_LoadedFromOldSettings_StaysOnTheApiKeyPath()
    {
        // 升级上来的配置里没有 Auth / CatalogId / OAuth 这三个字段
        const string legacy = """
            {"Providers":[{"Id":"p1","Name":"OpenAI","BaseUrl":"https://api.openai.com/v1",
            "DefaultProtocol":"OpenAiResponses","Models":[{"Id":"m1","Model":"gpt-5"}]}],"ActiveModelId":"m1"}
            """;

        AiSettings settings = JsonSerializer.Deserialize<AiSettings>(legacy)!;

        Assert.AreEqual(AuthMethod.ApiKey, settings.Providers[0].Auth);
        Assert.IsNull(settings.Providers[0].OAuth);
        Assert.IsNull(settings.Providers[0].CatalogId);
        Assert.IsFalse(settings.Providers[0].CanSignIn);
    }

    [TestMethod]
    public void CanSignIn_NeedsTheEndpointsThatItsFlowActuallyUses()
    {
        var pkce = new AiProvider
        {
            Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig { TokenUrl = "https://t", AuthorizationUrl = "https://a", ClientId = "c" }
        };
        Assert.IsTrue(pkce.CanSignIn);

        pkce.OAuth!.AuthorizationUrl = "";
        Assert.IsFalse(pkce.CanSignIn, "授权码流程少了授权地址就登不了");

        pkce.OAuth.AuthorizationUrl = "https://a";
        pkce.OAuth.ClientId = "";
        Assert.IsFalse(pkce.CanSignIn, "端点填齐了但客户端 id 空着,也登不了");

        var device = new AiProvider
        {
            Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig
            {
                Flow = OAuthFlow.DeviceCode,
                TokenUrl = "https://t",
                DeviceCodeUrl = "https://d",
                ClientId = "c"
            }
        };
        Assert.IsTrue(device.CanSignIn, "设备码流程用不到授权地址");

        device.OAuth!.TokenUrl = "";
        Assert.IsFalse(device.CanSignIn);

        // OpenRouter 那一路本来就没有 client_id,不能被这条规则卡住
        var openrouter = new AiProvider
        {
            Auth = AuthMethod.Subscription,
            OAuth = new OAuthConfig
            {
                Flow = OAuthFlow.OpenRouterPkce,
                TokenUrl = "https://t",
                AuthorizationUrl = "https://a"
            }
        };
        Assert.IsTrue(openrouter.CanSignIn);
    }
}

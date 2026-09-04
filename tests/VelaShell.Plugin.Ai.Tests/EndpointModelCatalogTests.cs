using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 问端点"你供应哪些模型":地址怎么拼、鉴权怎么带、回应怎么解。
/// </summary>
/// <remarks>
/// <b>一次网都不联</b>:地址与请求头是纯函数,回应解析喂固定报文。三种回应形状都照抄自线上
/// (OpenAI / Anthropic 的 <c>{"data":[…]}</c>、部分中转站的裸数组、Ollama 的
/// <c>{"models":[…]}</c>)—— 真出问题只会是"某天对方改了字段名",那不是单元测试拦得住的;
/// 这里守的是我们自己的解析与降级。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class EndpointModelCatalogTests
{
    // ---- 地址 ----

    [TestMethod]
    public void Endpoint_OpenAiAppendsToTheBaseUrlAsTheSdkDoes()
    {
        // OpenAI 系的基地址按惯例已含 /v1,SDK 在其后接 /chat/completions —— 这里照样接 /models
        Assert.AreEqual("https://api.openai.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.openai.com/v1", ChatProtocol.OpenAiChatCompletions)!.ToString());
        Assert.AreEqual("https://relay.example/v1/models",
            EndpointModelCatalog.Endpoint("https://relay.example/v1/", ChatProtocol.OpenAiResponses)!.ToString());
    }

    [TestMethod]
    public void Endpoint_AnthropicAddsTheVersionSegmentItselfButNeverTwice()
    {
        // Anthropic 的基地址按惯例不含 /v1(SDK 自己补),这条接口得自己补
        Assert.AreEqual("https://api.anthropic.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.anthropic.com", ChatProtocol.AnthropicMessages)!.ToString());
        // 用户误带了 /v1 也不该变成 /v1/v1
        Assert.AreEqual("https://api.anthropic.com/v1/models",
            EndpointModelCatalog.Endpoint("https://api.anthropic.com/v1", ChatProtocol.AnthropicMessages)!.ToString());
    }

    [TestMethod]
    public void Endpoint_UnusableBaseUrlsAreNull()
    {
        Assert.IsNull(EndpointModelCatalog.Endpoint("", ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint("   ", ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint(null, ChatProtocol.OpenAiChatCompletions));
        Assert.IsNull(EndpointModelCatalog.Endpoint("api.openai.com/v1", ChatProtocol.OpenAiChatCompletions),
            "相对地址发不出去");
        Assert.IsNull(EndpointModelCatalog.Endpoint("file:///etc/passwd", ChatProtocol.OpenAiChatCompletions),
            "只走 http(s)");
    }

    // ---- 鉴权 ----

    [TestMethod]
    public void Request_OpenAiSendsABearerToken()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://relay.example/v1", ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key("sk-abc"))!;

        Assert.AreEqual("Bearer sk-abc", request.Headers.GetValues("Authorization").Single());
    }

    [TestMethod]
    public void Request_AnthropicSendsAnApiKeyHeaderAndTheVersion()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://api.anthropic.com", ChatProtocol.AnthropicMessages, ProviderCredential.Key("sk-ant"))!;

        Assert.AreEqual("sk-ant", request.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual(EndpointModelCatalog.AnthropicVersion,
            request.Headers.GetValues("anthropic-version").Single());
        Assert.IsFalse(request.Headers.Contains("Authorization"), "手填的 Key 走 x-api-key,不是 Bearer");
    }

    [TestMethod]
    public void Request_AnthropicWithASignInTokenSwitchesToBearer()
    {
        // 登录换回来的令牌与手填的 Key 走的不是同一个头 —— 与发对话请求时同一套判断
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://api.anthropic.com", ChatProtocol.AnthropicMessages,
            new ProviderCredential("tok", true))!;

        Assert.AreEqual("Bearer tok", request.Headers.GetValues("Authorization").Single());
        Assert.IsFalse(request.Headers.Contains("x-api-key"));
    }

    [TestMethod]
    public void Request_CarriesTheProviderSExtraHeaders()
    {
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "https://relay.example/v1", ChatProtocol.OpenAiResponses,
            new ProviderCredential("tok", true, [new KeyValuePair<string, string>("chatgpt-account-id", "acc-1")]))!;

        Assert.AreEqual("acc-1", request.Headers.GetValues("chatgpt-account-id").Single());
    }

    [TestMethod]
    public void Request_WithoutACredentialStillGoesOut()
    {
        // 本地 Ollama 就没有 Key;而 Key 漏填时,服务端回的 401 比本地猜测有用得多
        using HttpRequestMessage request = EndpointModelCatalog.Request(
            "http://localhost:11434/v1", ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key(null))!;

        Assert.IsFalse(request.Headers.Contains("Authorization"));
        Assert.AreEqual("http://localhost:11434/v1/models", request.RequestUri!.ToString());
    }

    [TestMethod]
    public void Request_WithAnUnusableBaseUrlIsNull()
        => Assert.IsNull(EndpointModelCatalog.Request("", ChatProtocol.OpenAiChatCompletions,
            ProviderCredential.Key("sk")));

    // ---- 解析 ----

    [TestMethod]
    public void Parse_ReadsTheOpenAiShape()
    {
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"object":"list","data":[
              {"id":"gpt-5","object":"model","created":1,"owned_by":"openai"},
              {"id":"gpt-4o-audio-preview","object":"model","created":2,"owned_by":"openai"}
            ]}
            """);

        Assert.AreSequenceEqual(["gpt-4o-audio-preview", "gpt-5"], ids.ToArray());
    }

    [TestMethod]
    public void Parse_ReadsTheAnthropicShape()
    {
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"data":[{"type":"model","id":"claude-sonnet-4","display_name":"Claude Sonnet 4"}],
             "has_more":false}
            """);

        Assert.AreEqual("claude-sonnet-4", ids.Single());
    }

    [TestMethod]
    public void Parse_ReadsABareArrayAsSomeRelaysReturn()
        => Assert.AreEqual("deepseek-chat",
            EndpointModelCatalog.Parse("""[{"id":"deepseek-chat"}]""").Single());

    [TestMethod]
    public void Parse_ReadsTheOllamaShape()
    {
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"models":[{"name":"llama3.1:8b","size":1},{"name":"qwen3:14b","size":2}]}
            """);

        Assert.AreSequenceEqual(["llama3.1:8b", "qwen3:14b"], ids.ToArray());
    }

    [TestMethod]
    public void Parse_DeduplicatesAndSorts()
    {
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"data":[{"id":"b"},{"id":"a"},{"id":"B"}]}
            """);

        Assert.AreSequenceEqual(["a", "b"], ids.ToArray());
    }

    [TestMethod]
    public void Parse_DropsTheModelsThatCannotChat()
    {
        // /models 把一家的全部模型都报上来,包括向量、语音、画图、审核那些
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"data":[
              {"id":"gpt-5"},
              {"id":"text-embedding-3-large"},
              {"id":"bge-embed"},
              {"id":"whisper-1"},
              {"id":"tts-1-hd"},
              {"id":"dall-e-3"},
              {"id":"omni-moderation-latest"},
              {"id":"bge-reranker-v2"}
            ]}
            """);

        Assert.AreEqual("gpt-5", ids.Single());
    }

    [TestMethod]
    public void Parse_DoesNotOverReach()
    {
        // 宁可漏筛也不误筛:误筛一个,用户在下拉里永远找不到它,而且不会有任何提示
        IReadOnlyList<string> ids = EndpointModelCatalog.Parse("""
            {"data":[{"id":"gpt-4o-audio-preview"},{"id":"gemini-2.5-pro-vision"},{"id":"pixtral-large"}]}
            """);

        Assert.HasCount(3, ids);
    }

    [TestMethod]
    public void Parse_JunkIsEmptyNotAnException()
    {
        Assert.IsEmpty(EndpointModelCatalog.Parse("<html>404</html>"), "被网关挡下来时拿到的常是 HTML");
        Assert.IsEmpty(EndpointModelCatalog.Parse("{}"));
        Assert.IsEmpty(EndpointModelCatalog.Parse("""{"error":{"message":"invalid key"}}"""));
        Assert.IsEmpty(EndpointModelCatalog.Parse("""{"data":[{"object":"model"},{"id":"  "}]}"""));
    }

    // ---- 发请求 ----

    [TestMethod]
    public async Task Fetch_AnErrorStatusYieldsAnEmptyListSoTheCallerCanFallBack()
    {
        using var http = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.NotFound));

        Assert.IsEmpty(await EndpointModelCatalog.FetchAsync(http, "https://relay.example/v1",
            ChatProtocol.OpenAiChatCompletions, ProviderCredential.Key("sk")));
    }

    [TestMethod]
    public async Task Fetch_WithoutABaseUrlNeverLeavesTheProcess()
    {
        var handler = new StatusHandler(System.Net.HttpStatusCode.OK);
        using var http = new HttpClient(handler);

        Assert.IsEmpty(await EndpointModelCatalog.FetchAsync(http, "", ChatProtocol.OpenAiChatCompletions,
            ProviderCredential.Key("sk")));
        Assert.AreEqual(0, handler.Calls);
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}

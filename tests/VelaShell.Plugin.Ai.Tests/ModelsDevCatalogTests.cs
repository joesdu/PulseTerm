using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// models.dev 模型规格库:解析、缓存、以及"拿它把用户本来要手填的那几项填满"。
/// </summary>
/// <remarks>
/// 上下文窗口和三档单价填错了<b>不会报错</b>,只会让输入框下方的占比和花费估算悄悄算歪 ——
/// 所以这条链路上每一步的降级行为都得钉死:宁可不填,也不能填成 0。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ModelsDevCatalogTests
{
    /// <summary>models.dev 原始 <c>api.json</c> 的真实形状(照抄自线上数据,只留两家两个模型)。</summary>
    private const string UpstreamShape = """
        {
          "openai": {
            "id": "openai",
            "name": "OpenAI",
            "doc": "https://platform.openai.com/docs/models",
            "models": {
              "gpt-5": {
                "id": "gpt-5",
                "name": "GPT-5",
                "reasoning": true,
                "tool_call": true,
                "limit": { "context": 400000, "input": 272000, "output": 128000 },
                "cost": { "input": 1.25, "output": 10, "cache_read": 0.125 }
              },
              "gpt-5.3-codex": {
                "id": "gpt-5.3-codex",
                "name": "GPT-5.3 Codex",
                "reasoning": true,
                "limit": { "context": 400000, "output": 128000 },
                "cost": { "input": 1.75, "output": 14, "cache_read": 0.175 }
              }
            }
          },
          "ollama": {
            "id": "ollama",
            "name": "Ollama",
            "models": {
              "llama3.1": { "id": "llama3.1", "name": "Llama 3.1" }
            }
          }
        }
        """;

    // ---- 解析 ----

    [TestMethod]
    public void Parse_ReadsTheUpstreamShape()
    {
        Dictionary<string, List<ModelSpec>> index = ModelsDevCatalog.Parse(UpstreamShape);

        Assert.HasCount(2, index);
        List<ModelSpec> openai = index["openai"];
        Assert.HasCount(2, openai);
        ModelSpec gpt5 = openai.First(m => m.Id == "gpt-5");
        Assert.AreEqual("GPT-5", gpt5.Name);
        Assert.AreEqual(400000, gpt5.ContextTokens);
        Assert.AreEqual(128000, gpt5.OutputTokens);
        Assert.AreEqual(1.25, gpt5.InputPrice);
        Assert.AreEqual(10, gpt5.OutputPrice);
        Assert.AreEqual(0.125, gpt5.CachedInputPrice);
        Assert.IsTrue(gpt5.Reasoning);
    }

    [TestMethod]
    public void Parse_ToleratesModelsWithNoLimitsOrPricing()
    {
        // 本地模型没有单价 —— 那时留 0,由 Apply 决定"不填"
        ModelSpec llama = ModelsDevCatalog.Parse(UpstreamShape)["ollama"].Single();

        Assert.AreEqual("llama3.1", llama.Id);
        Assert.AreEqual(0, llama.ContextTokens);
        Assert.AreEqual(0, llama.InputPrice);
        Assert.IsFalse(llama.Reasoning);
    }

    [TestMethod]
    public void SlimIndex_RoundTripsThroughTheSameParser()
    {
        // 刷新时落盘的是精简格式,读缓存走的是同一条解析路径 —— 两边必须完全一致
        Dictionary<string, List<ModelSpec>> upstream = ModelsDevCatalog.Parse(UpstreamShape);

        Dictionary<string, List<ModelSpec>> reloaded =
            ModelsDevCatalog.Parse(ModelsDevCatalog.Serialize(upstream));

        Assert.AreSequenceEqual(upstream["openai"], reloaded["openai"]);
        Assert.AreSequenceEqual(upstream["ollama"], reloaded["ollama"]);
    }

    [TestMethod]
    public void Parse_GarbageIsEmptyNotAnException()
    {
        Assert.IsEmpty(ModelsDevCatalog.Parse("<html>nope</html>"));
        Assert.IsEmpty(ModelsDevCatalog.Parse("[]"));
        Assert.IsEmpty(ModelsDevCatalog.Parse(""));
    }

    // ---- 填进模型配置 ----

    [TestMethod]
    public void Apply_FillsInEverythingTheUserWouldOtherwiseType()
    {
        var model = new AiModelConfig();
        ModelSpec gpt5 = ModelsDevCatalog.Parse(UpstreamShape)["openai"].First(m => m.Id == "gpt-5");

        ModelsDevCatalog.Apply(model, gpt5);

        Assert.AreEqual("gpt-5", model.Model);
        Assert.AreEqual(400000, model.MaxInputTokens);
        Assert.AreEqual(128000, model.MaxTokens);
        Assert.AreEqual(1.25, model.InputPricePerMillion);
        Assert.AreEqual(10, model.OutputPricePerMillion);
        Assert.AreEqual(0.125, model.CachedInputPricePerMillion);
    }

    [TestMethod]
    public void Apply_NeverOverwritesAKnownValueWithZero()
    {
        // 上游没有这一项时把用户填好的值抹成 0 比不填还糟:
        // 窗口 0 会让上下文占比整个消失,单价 0 会让花费估算静默停掉
        var model = new AiModelConfig
        {
            MaxInputTokens = 65536,
            MaxTokens = 4096,
            InputPricePerMillion = 3,
            OutputPricePerMillion = 15,
            CachedInputPricePerMillion = 0.3
        };
        ModelSpec bare = ModelsDevCatalog.Parse(UpstreamShape)["ollama"].Single();

        ModelsDevCatalog.Apply(model, bare);

        Assert.AreEqual("llama3.1", model.Model, "模型 id 是要换的");
        Assert.AreEqual(65536, model.MaxInputTokens);
        Assert.AreEqual(4096, model.MaxTokens);
        Assert.AreEqual(3, model.InputPricePerMillion);
        Assert.AreEqual(15, model.OutputPricePerMillion);
        Assert.AreEqual(0.3, model.CachedInputPricePerMillion);
    }

    // ---- 筛掉用不上的 ----

    /// <summary>照抄线上数据里那几种"不该进聊天下拉"的形状。</summary>
    private const string NoisyShape = """
        {
          "openai": {
            "id": "openai",
            "models": {
              "gpt-5": { "id": "gpt-5", "limit": { "context": 400000, "output": 128000 } },
              "gpt-5.6-beta": { "id": "gpt-5.6-beta", "status": "beta",
                                "limit": { "context": 400000, "output": 128000 } },
              "gpt-4-turbo": { "id": "gpt-4-turbo", "status": "deprecated",
                               "limit": { "context": 128000, "output": 4096 } },
              "gpt-image-2": { "id": "gpt-image-2", "limit": { "context": 0, "output": 0 } },
              "text-embedding-3-small": { "id": "text-embedding-3-small",
                                          "limit": { "context": 8191, "output": 1536 } }
            }
          }
        }
        """;

    [TestMethod]
    public void Parse_DropsRetiredModels()
    {
        // 线上有两百来个标了 deprecated 的;摆出来只会让人选中一个已经用不了的型号
        List<ModelSpec> models = ModelsDevCatalog.Parse(NoisyShape)["openai"];

        Assert.DoesNotContain(m => m.Id == "gpt-4-turbo", models, "已下架的不该出现");
    }

    [TestMethod]
    public void Parse_KeepsBetaModels()
    {
        // beta 是能用的,别跟着 deprecated 一起筛掉
        Assert.Contains(m => m.Id == "gpt-5.6-beta", ModelsDevCatalog.Parse(NoisyShape)["openai"]);
    }

    [TestMethod]
    public void Parse_DropsModelsThatCannotProduceChatOutput()
    {
        List<ModelSpec> models = ModelsDevCatalog.Parse(NoisyShape)["openai"];

        // 画图那类根本不产出 token
        Assert.DoesNotContain(m => m.Id == "gpt-image-2", models);
        // 向量模型:它那个 output 填的是维度不是 token 数,只能靠名字认
        Assert.DoesNotContain(m => m.Id == "text-embedding-3-small", models);
    }

    [TestMethod]
    public void Parse_KeepsTheOrdinaryChatModels()
        => Assert.Contains(m => m.Id == "gpt-5", ModelsDevCatalog.Parse(NoisyShape)["openai"]);

    [TestMethod]
    public void SlimCache_SurvivesTheFilterOnReload()
    {
        // 精简缓存里没有 status / limit 这两层,再过一遍筛子不能把好东西筛没了
        Dictionary<string, List<ModelSpec>> first = ModelsDevCatalog.Parse(NoisyShape);
        Dictionary<string, List<ModelSpec>> reloaded =
            ModelsDevCatalog.Parse(ModelsDevCatalog.Serialize(first));

        Assert.AreSequenceEqual(first["openai"], reloaded["openai"]);
    }

    // ---- 落成真正可选的模型 ----

    [TestMethod]
    public void Materialise_PutsEveryPulledModelWhereTheDropdownCanSeeIt()
    {
        // 只存进 AvailableModels 是不够的:顶栏那个下拉读的是 Models,
        // 不落进去用户一个也挑不着 —— 那就等于没拉
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "gpt-5" }] };
        IReadOnlyList<ModelSpec> specs = ModelsDevCatalog.Parse(UpstreamShape)["openai"];

        int total = ModelsDevCatalog.Materialise(provider, specs);

        Assert.AreEqual(2, total);
        Assert.HasCount(2, provider.Models);
        Assert.AreSequenceEqual(
            ["gpt-5", "gpt-5.3-codex"], provider.Models.Select(m => m.Model).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        // 顺带把规格填好,不是光加一行空模型
        AiModelConfig codex = provider.Models.First(m => m.Model == "gpt-5.3-codex");
        Assert.AreEqual(400000, codex.MaxInputTokens);
        Assert.AreEqual(1.75, codex.InputPricePerMillion);
    }

    [TestMethod]
    public void Materialise_KeepsTheActiveModelsIdentityIntact()
    {
        // ActiveModelId 指着某个 AiModelConfig.Id;重建列表会让当前选中的模型凭空消失
        var first = new AiModelConfig { Model = "gpt-5-codex", Name = "我的 Codex" };
        var provider = new AiProvider { Models = [first] };
        string id = first.Id;

        ModelsDevCatalog.Materialise(provider, ModelsDevCatalog.Parse(UpstreamShape)["openai"]);

        Assert.AreEqual(id, provider.Models[0].Id, "第一条必须还是同一个对象/同一个 Id");
        Assert.AreEqual("我的 Codex", provider.Models[0].Name, "用户改过的名字不能被覆盖");
        // 出厂示例已下架,应当对齐到同族最新的那个
        Assert.AreEqual("gpt-5.3-codex", provider.Models[0].Model);
    }

    [TestMethod]
    public void Materialise_IsIdempotent()
    {
        // 每次重连都会再跑一遍,不能越跑越长
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "gpt-5" }] };
        IReadOnlyList<ModelSpec> specs = ModelsDevCatalog.Parse(UpstreamShape)["openai"];

        ModelsDevCatalog.Materialise(provider, specs);
        ModelsDevCatalog.Materialise(provider, specs);
        ModelsDevCatalog.Materialise(provider, specs);

        Assert.HasCount(2, provider.Models);
    }

    [TestMethod]
    public void Materialise_DoesNotTouchUserTunedSettingsOnModelsTheyAlreadyHad()
    {
        var tuned = new AiModelConfig
        {
            Model = "gpt-5",
            Name = "改过名的",
            Reasoning = ReasoningLevel.High,
            SystemPrompt = "专用提示词"
        };
        var provider = new AiProvider { Models = [tuned] };

        ModelsDevCatalog.Materialise(provider, ModelsDevCatalog.Parse(UpstreamShape)["openai"]);

        Assert.AreEqual("改过名的", tuned.Name);
        Assert.AreEqual(ReasoningLevel.High, tuned.Reasoning);
        Assert.AreEqual("专用提示词", tuned.SystemPrompt);
        Assert.AreEqual(400000, tuned.MaxInputTokens, "但规格该补上");
    }

    [TestMethod]
    public void Materialise_WithNothingPulledChangesNothing()
    {
        var provider = new AiProvider { Models = [new AiModelConfig { Model = "gpt-5" }] };

        Assert.AreEqual(1, ModelsDevCatalog.Materialise(provider, []));
        Assert.HasCount(1, provider.Models);
        Assert.AreEqual("gpt-5", provider.Models[0].Model);
    }

    [TestMethod]
    public void Materialise_ResultIsVisibleThroughResolveModels()
    {
        // 这条盯的就是"用户到底看不看得见":ResolveModels 是顶栏下拉的数据源
        var provider = new AiProvider { Name = "OpenAI", Models = [new AiModelConfig { Model = "gpt-5" }] };
        var settings = new AiSettings { Providers = [provider] };

        ModelsDevCatalog.Materialise(provider, ModelsDevCatalog.Parse(UpstreamShape)["openai"]);

        List<ResolvedModel> resolved = settings.ResolveModels();
        Assert.HasCount(2, resolved);
        Assert.AreSequenceEqual(
            ["gpt-5", "gpt-5.3-codex"], resolved.Select(r => r.Model).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    // ---- 默认模型的选法 ----

    private static IReadOnlyList<ModelSpec> Specs(params string[] ids)
        => [.. ids.Select(id => new ModelSpec(id, id, 0, 0, 0, 0, 0, false))];

    [TestMethod]
    public void ChooseDefault_PrefersTheCataloguesExample()
    {
        Assert.AreEqual("gpt-5", ModelsDevCatalog.ChooseDefault("gpt-5", Specs("a", "gpt-5", "z"))!.Id);
        Assert.AreEqual("GPT-5", ModelsDevCatalog.ChooseDefault("GPT-5", Specs("GPT-5"))!.Id);
    }

    [TestMethod]
    public void ChooseDefault_MovesToTheNewestOfTheSameFamilyWhenTheExampleIsGone()
    {
        // 真实情况:目录里写死的 gpt-5-codex 已经下架,上游只剩 gpt-5.3-codex 系列。
        // 这时选 gpt-5.3-codex 显然比按字母序选 gpt-3.5 合理
        ModelSpec? chosen = ModelsDevCatalog.ChooseDefault(
            "gpt-5-codex", Specs("gpt-3.5-turbo", "gpt-5.2-codex", "gpt-5.3-codex"));

        Assert.AreEqual("gpt-5.3-codex", chosen!.Id);
    }

    [TestMethod]
    public void ChooseDefault_FallsBackToTheFirstWhenNothingIsEvenClose()
    {
        // 前缀只对上两三个字母说明根本不是一族,别硬凑
        Assert.AreEqual("aardvark", ModelsDevCatalog.ChooseDefault("zzz-9", Specs("aardvark", "beta"))!.Id);
    }

    [TestMethod]
    public void ChooseDefault_WithNothingPulled_IsNull()
        => Assert.IsNull(ModelsDevCatalog.ChooseDefault("gpt-5", []));

    // ---- 缓存 ----

    [TestMethod]
    public async Task Refresh_CachesAndIsThenServedFromDisk()
    {
        using var context = new TestPluginContext();
        var stub = new StubHandler(UpstreamShape);
        using var http = new HttpClient(stub);
        var catalog = new ModelsDevCatalog(context);
        Assert.IsTrue(catalog.IsStale, "还没缓存过就该算过期");

        Assert.IsTrue(await catalog.RefreshAsync(http));

        Assert.HasCount(2, catalog.ForProvider("openai"));
        Assert.IsFalse(catalog.IsStale);
        // 新缓存还新鲜,再刷不该真发请求
        Assert.IsFalse(await catalog.RefreshAsync(http));
        Assert.AreEqual(1, stub.Calls);

        // 换一个实例(相当于重启):从盘上读,不再联网
        Assert.HasCount(2, new ModelsDevCatalog(context).ForProvider("openai"));
        Assert.AreEqual(1, stub.Calls);
    }

    [TestMethod]
    public async Task Refresh_ANetworkFailureKeepsTheExistingCache()
    {
        using var context = new TestPluginContext();
        var good = new StubHandler(UpstreamShape);
        using (var http = new HttpClient(good))
        {
            await new ModelsDevCatalog(context).RefreshAsync(http);
        }

        var catalog = new ModelsDevCatalog(context);
        using var broken = new HttpClient(new ThrowingHandler());
        Assert.IsFalse(await catalog.RefreshAsync(broken, force: true));

        Assert.HasCount(2, catalog.ForProvider("openai"), "拉不到就用旧的,别把好缓存弄丢");
    }

    [TestMethod]
    public async Task Refresh_AnEmptyPayloadDoesNotClobberAGoodCache()
    {
        using var context = new TestPluginContext();
        var good = new StubHandler(UpstreamShape);
        using (var http = new HttpClient(good))
        {
            await new ModelsDevCatalog(context).RefreshAsync(http);
        }

        var catalog = new ModelsDevCatalog(context);
        using var empty = new HttpClient(new StubHandler("{}"));
        Assert.IsFalse(await catalog.RefreshAsync(empty, force: true));

        Assert.HasCount(2, catalog.ForProvider("openai"));
    }

    [TestMethod]
    public void ForProvider_UnknownOrEmptyIdIsEmpty()
    {
        using var context = new TestPluginContext();
        var catalog = new ModelsDevCatalog(context);

        Assert.IsEmpty(catalog.ForProvider("no-such-provider"));
        Assert.IsEmpty(catalog.ForProvider(""));
        Assert.IsEmpty(catalog.ForProvider(null));
    }

    // ---- 给端点报上来的 id 配规格 ----

    /// <summary>缓存好一份索引,供 <c>Describe</c> 的用例用。</summary>
    private static async Task<ModelsDevCatalog> CachedAsync(TestPluginContext context)
    {
        var catalog = new ModelsDevCatalog(context);
        using var http = new HttpClient(new StubHandler(UpstreamShape));
        await catalog.RefreshAsync(http);
        return catalog;
    }

    [TestMethod]
    public async Task Describe_MatchedInsideTheProvider_FillsEverything()
    {
        using var context = new TestPluginContext();
        ModelsDevCatalog catalog = await CachedAsync(context);

        ModelSpec spec = catalog.Describe("openai", ["gpt-5"]).Single();

        Assert.AreEqual(400000, spec.ContextTokens);
        Assert.AreEqual(1.25, spec.InputPrice, "自家目录里的单价就是这一家的,照填");
    }

    [TestMethod]
    public async Task Describe_MatchedAcrossProviders_KeepsCapacityButNotPrices()
    {
        using var context = new TestPluginContext();
        ModelsDevCatalog catalog = await CachedAsync(context);

        // 中转站没有 ModelsDevId,只能跨供应商按 id 找
        ModelSpec spec = catalog.Describe(null, ["gpt-5"]).Single();

        Assert.AreEqual(400000, spec.ContextTokens, "窗口是模型自身的属性,谁家跑都一样");
        Assert.AreEqual(0, spec.InputPrice, "中转站的价目是它自己定的,照抄原厂会让花费估算静默偏低");
        Assert.AreEqual(0, spec.OutputPrice);
        Assert.AreEqual(0, spec.CachedInputPrice);
    }

    [TestMethod]
    public async Task Describe_StripsTheRelayPrefixToFindTheSpec()
    {
        using var context = new TestPluginContext();
        ModelsDevCatalog catalog = await CachedAsync(context);

        ModelSpec spec = catalog.Describe(null, ["openai/gpt-5"]).Single();

        Assert.AreEqual("openai/gpt-5", spec.Id, "请求里要发的是端点报上来的那个 id,不是 models.dev 的写法");
        Assert.AreEqual(400000, spec.ContextTokens);
    }

    [TestMethod]
    public async Task Describe_UnknownIdIsCarriedThroughWithNothingFilledIn()
    {
        using var context = new TestPluginContext();
        ModelsDevCatalog catalog = await CachedAsync(context);

        ModelSpec spec = catalog.Describe("openai", ["some-private-build"]).Single();

        Assert.AreEqual("some-private-build", spec.Id, "配不上也得留在清单里 —— 它确实是端点供应的");
        Assert.AreEqual(0, spec.ContextTokens, "未知就是 0;Apply 只填非 0 的值,用户填过的不会被覆盖");
        Assert.AreEqual(0, spec.InputPrice);
    }

    [TestMethod]
    public async Task Describe_KeepsTheEndpointOrder()
    {
        using var context = new TestPluginContext();
        ModelsDevCatalog catalog = await CachedAsync(context);

        IReadOnlyList<ModelSpec> specs = catalog.Describe("openai", ["gpt-5.3-codex", "gpt-5"]);

        Assert.AreEqual("gpt-5.3-codex", specs[0].Id);
        Assert.AreEqual("gpt-5", specs[1].Id);
    }

    // ---- 目录映射 ----

    [TestMethod]
    public void EveryHostedProviderIsMappedToModelsDev()
    {
        // 本地自部署和自定义端点那边没有收录,其余都该有 —— 少一个就是"静默拉不到模型"
        string[] unmapped = ["ollama", "custom-openai", "custom-anthropic", "custom-oauth"];
        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            if (unmapped.Contains(entry.Id))
            {
                Assert.IsEmpty(entry.ModelsDevId, entry.Id);
            }
            else
            {
                Assert.IsNotEmpty(entry.ModelsDevId, $"{entry.Id} 没有映射到 models.dev,模型会静默拉不到");
            }
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual(ModelsDevCatalog.SourceUrl, request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
}

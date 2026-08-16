using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>设置存储:配置往返、机密隔离与三种协议的客户端构造。</summary>
[TestClass]
public sealed class AiSettingsStoreTests
{
    [TestMethod]
    public async Task Load_WithoutSavedSettings_ReturnsDefaults()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        AiSettings settings = await store.LoadAsync();

        Assert.IsEmpty(settings.Providers);
        Assert.IsFalse(settings.AgentMode);
        Assert.IsFalse(settings.AutoApproveCommands);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsProviders()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var settings = new AiSettings
        {
            Providers =
            [
                new AiProvider
                {
                    Name = "Anthropic",
                    DefaultProtocol = ChatProtocol.AnthropicMessages,
                    BaseUrl = "https://api.anthropic.com",
                    Models =
                    [
                        new AiModelConfig { Model = "claude-opus-5", MaxTokens = 4096 },
                        new AiModelConfig { Model = "gpt-5", Protocol = ChatProtocol.OpenAiResponses, HasOwnApiKey = true }
                    ]
                }
            ],
            AgentMode = true
        };
        settings.ActiveModelId = settings.Providers[0].Models[1].Id;

        await store.SaveAsync(settings);
        AiSettings loaded = await store.LoadAsync();

        Assert.HasCount(1, loaded.Providers);
        Assert.AreEqual("Anthropic", loaded.Providers[0].Name);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, loaded.Providers[0].DefaultProtocol);
        Assert.HasCount(2, loaded.Providers[0].Models);
        Assert.AreEqual(4096, loaded.Providers[0].Models[0].MaxTokens);
        Assert.IsNull(loaded.Providers[0].Models[0].Protocol, "没覆盖的协议要保持 null(继承)");
        Assert.AreEqual(ChatProtocol.OpenAiResponses, loaded.Providers[0].Models[1].Protocol);
        Assert.IsTrue(loaded.Providers[0].Models[1].HasOwnApiKey);
        Assert.AreEqual(settings.ActiveModelId, loaded.ActiveModelId);
        Assert.IsTrue(loaded.AgentMode);
    }

    // ---- 继承解析 ----

    [TestMethod]
    public void ResolvedModel_InheritsProtocolUrlAndKeyOwner_UnlessOverridden()
    {
        var provider = new AiProvider { Name = "Routin", BaseUrl = "https://routin.example/v1", DefaultProtocol = ChatProtocol.OpenAiChatCompletions };
        var inherit = new AiModelConfig { Model = "gpt-5" };
        var custom = new AiModelConfig
        {
            Model = "claude", Protocol = ChatProtocol.AnthropicMessages, HasOwnApiKey = true, BaseUrlOverride = "https://routin.example"
        };

        var a = new ResolvedModel(provider, inherit);
        var b = new ResolvedModel(provider, custom);

        Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, a.Protocol);
        Assert.AreEqual("https://routin.example/v1", a.BaseUrl);
        Assert.AreEqual(provider.Id, a.ApiKeyOwnerId, "没勾独立 Key 就用供应商那把");
        Assert.AreEqual("gpt-5", a.Name, "没填名称时显示模型 id");
        Assert.AreEqual(ChatProtocol.AnthropicMessages, b.Protocol);
        Assert.AreEqual("https://routin.example", b.BaseUrl);
        Assert.AreEqual(custom.Id, b.ApiKeyOwnerId);
    }

    // ---- 旧版扁平接入 → 供应商/模型两层 ----

    /// <summary>旧格式落盘时长这样:Providers 里每条自带 BaseUrl / Model / Protocol,ActiveProviderId 指向其中一条。</summary>
    private static async Task SaveLegacyAsync(TestPluginContext context, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        await context.Storage.SetAsync("settings", doc.RootElement.Clone());
    }

    [TestMethod]
    public async Task Load_LegacyFlatProviders_GroupsByBaseUrl_AndKeepsActiveSelection()
    {
        using var context = new TestPluginContext();
        await context.Secrets.SetAsync("apikey:m1", "sk-routin");
        await context.Secrets.SetAsync("apikey:m2", "sk-routin");
        await context.Secrets.SetAsync("apikey:m3", "sk-other");
        await context.Secrets.SetAsync("apikey:m4", "sk-anthropic");
        await SaveLegacyAsync(context, """
            {
              "Providers": [
                { "Id": "m1", "Name": "GPT", "Protocol": "OpenAiResponses", "BaseUrl": "https://routin.example/v1", "Model": "gpt-5", "MaxTokens": 4096, "MaxInputTokens": 400000 },
                { "Id": "m2", "Name": "Grok", "Protocol": "OpenAiChatCompletions", "BaseUrl": "https://routin.example/v1/", "Model": "grok-4", "Reasoning": "High" },
                { "Id": "m3", "Name": "Claude", "Protocol": "AnthropicMessages", "BaseUrl": "https://routin.example", "Model": "claude-opus-5" },
                { "Id": "m4", "Name": "Claude", "Protocol": "AnthropicMessages", "BaseUrl": "https://api.anthropic.com", "Model": "claude-opus-5" }
              ],
              "ActiveProviderId": "m2",
              "AgentMode": true,
              "SystemPrompt": "keep me"
            }
            """);
        var store = new AiSettingsStore(context);

        AiSettings loaded = await store.LoadAsync();

        // 结构:同一主机的三条并成一家(忽略尾斜杠与 /v1),官方 Anthropic 单独一家
        Assert.HasCount(2, loaded.Providers);
        AiProvider routin = loaded.Providers[0];
        Assert.AreEqual("routin.example", routin.Name, "名字各不相同时用主机名");
        Assert.AreEqual("https://routin.example/v1", routin.BaseUrl);
        Assert.HasCount(3, routin.Models);
        Assert.AreEqual("m1", routin.Models[0].Id, "旧接入 id 原样成为模型 id");
        Assert.AreEqual("GPT", routin.Models[0].Name);
        Assert.AreEqual(4096, routin.Models[0].MaxTokens);
        Assert.AreEqual(400000, routin.Models[0].MaxInputTokens);
        Assert.AreEqual(ReasoningLevel.High, routin.Models[1].Reasoning);
        // 协议:两条 OpenAI 系各一票,取先出现的 Responses 为默认;其它两条各自覆盖
        Assert.AreEqual(ChatProtocol.OpenAiResponses, routin.DefaultProtocol);
        Assert.IsNull(routin.Models[0].Protocol);
        Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, routin.Models[1].Protocol);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, routin.Models[2].Protocol);
        // 地址:请求打到哪儿一个字节都不变 —— 与供应商地址不完全一致的记覆盖
        Assert.IsNull(routin.Models[0].BaseUrlOverride);
        Assert.AreEqual("https://routin.example/v1/", routin.Models[1].BaseUrlOverride);
        Assert.AreEqual("https://routin.example", routin.Models[2].BaseUrlOverride);
        // Key:头一把提到供应商;同 Key 的改继承(自己那份删掉),不同的标独立
        Assert.AreEqual("sk-routin", await context.Secrets.GetAsync($"apikey:{routin.Id}"));
        Assert.IsFalse(routin.Models[0].HasOwnApiKey);
        Assert.IsFalse(routin.Models[1].HasOwnApiKey);
        Assert.IsNull(await context.Secrets.GetAsync("apikey:m2"), "改继承的模型不该留一份孤儿机密");
        Assert.IsTrue(routin.Models[2].HasOwnApiKey);
        Assert.AreEqual("sk-other", await context.Secrets.GetAsync("apikey:m3"));
        // 单条一组:名字直接用它的,模型名留空(显示模型 id)
        AiProvider anthropic = loaded.Providers[1];
        Assert.AreEqual("Claude", anthropic.Name);
        Assert.AreEqual("", anthropic.Models[0].Name);
        Assert.AreEqual("claude-opus-5", anthropic.Models[0].DisplayName);
        Assert.AreEqual("sk-anthropic", await context.Secrets.GetAsync($"apikey:{anthropic.Id}"));
        // 其余设置与当前选中不丢
        Assert.AreEqual("m2", loaded.ActiveModelId);
        Assert.IsNull(loaded.ActiveProviderId);
        Assert.AreEqual(ChatMode.Agent, loaded.Mode);
        Assert.AreEqual("keep me", loaded.SystemPrompt);
        // 解析出来的模型仍能拿到正确的 Key
        ResolvedModel resolved = loaded.FindModel("m2")!;
        Assert.AreEqual("sk-routin", await store.GetApiKeyAsync(resolved.ApiKeyOwnerId));

        // 已回写为新格式:再读一次不再走迁移,结果一致
        AiSettings again = await new AiSettingsStore(context).LoadAsync();
        Assert.HasCount(2, again.Providers);
        Assert.AreEqual(routin.Id, again.Providers[0].Id);
        Assert.AreEqual("m2", again.ActiveModelId);
    }

    [TestMethod]
    public async Task Load_LegacyWithoutAnyProviders_IsNotTreatedAsLegacy()
    {
        using var context = new TestPluginContext();
        await SaveLegacyAsync(context, """{ "Providers": [], "Mode": "Plan" }""");

        AiSettings loaded = await new AiSettingsStore(context).LoadAsync();

        Assert.IsEmpty(loaded.Providers);
        Assert.AreEqual(ChatMode.Plan, loaded.Mode);
    }

    [TestMethod]
    public async Task ApiKey_SetGetDelete_UsesSecretStore()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        await store.SetApiKeyAsync("p1", "sk-secret");
        Assert.AreEqual("sk-secret", await store.GetApiKeyAsync("p1"));

        // 空值 = 清除
        await store.SetApiKeyAsync("p1", "");
        Assert.IsNull(await store.GetApiKeyAsync("p1"));
    }

    // ---- 思考档位翻译(两家协议认的东西不一样,见 ApplyReasoning) ----

    private static ResolvedModel Provider(ChatProtocol protocol, ReasoningLevel reasoning, int maxTokens = 8192)
        => new(
            new AiProvider { Name = "t", DefaultProtocol = protocol },
            new AiModelConfig { Model = "the-real-model", MaxTokens = maxTokens, Reasoning = reasoning });

    [TestMethod]
    public void ApplyReasoning_Default_LeavesTheRequestAlone()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Default));

        Assert.IsNull(options.Reasoning, "跟随接入默认 = 请求里根本不带这个参数");
        Assert.IsNull(options.RawRepresentationFactory);
    }

    [TestMethod]
    public void ApplyReasoning_OpenAi_UsesTheStandardKnobOnly()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.OpenAiResponses, ReasoningLevel.High));

        Assert.AreEqual(ReasoningEffort.High, options.Reasoning?.Effort);
        Assert.AreEqual(ReasoningOutput.Full, options.Reasoning?.Output);
        Assert.IsNull(options.RawRepresentationFactory, "OpenAI 适配器认 ChatOptions.Reasoning,不必动请求体");
    }

    /// <summary>
    /// Anthropic 适配器不认 <c>ChatOptions.Reasoning</c>,thinking 只能经 raw 请求体下发。
    /// 同时守住实测出来的坑:raw 里的 <c>MaxTokens</c>/<c>Model</c> 会盖过适配器的值,
    /// 所以必须填真值 —— 一旦回退成占位值,线上请求就会带着错误的模型与输出上限发出去。
    /// </summary>
    [TestMethod]
    public void ApplyReasoning_Anthropic_PutsThinkingInTheRequestBody_WithRealModelAndLimit()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Medium));

        var raw = options.RawRepresentationFactory!(new StubChatClient()) as Anthropic.Models.Messages.MessageCreateParams;
        Assert.IsNotNull(raw);
        // Model 是 ApiEnum 包装,ToString 给的是 JSON 形式("the-real-model")
        Assert.Contains("the-real-model", raw.Model.ToString());
        Assert.AreEqual(8192, raw.MaxTokens);
        var thinking = raw.Thinking?.Value as Anthropic.Models.Messages.ThinkingConfigEnabled;
        Assert.IsNotNull(thinking, "中档应当开启 thinking");
        Assert.AreEqual(4096, thinking.BudgetTokens);
        Assert.IsLessThan(raw.MaxTokens, thinking.BudgetTokens, "协议要求 max_tokens > budget_tokens");
    }

    [TestMethod]
    public void ApplyReasoning_Anthropic_Off_SendsDisabledThinking()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.Off));

        var raw = (Anthropic.Models.Messages.MessageCreateParams)options.RawRepresentationFactory!(new StubChatClient())!;
        Assert.IsInstanceOfType<Anthropic.Models.Messages.ThinkingConfigDisabled>(raw.Thinking?.Value);
    }

    /// <summary>
    /// 输出上限被设得放不下思考时,抬高这一次请求的上限,而不是悄悄不思考
    /// (预算有协议下限 1024,还得给正文留余量)。
    /// </summary>
    [TestMethod]
    public void ApplyReasoning_Anthropic_TinyOutputLimit_StillLeavesRoomForTheAnswer()
    {
        var options = new ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Provider(ChatProtocol.AnthropicMessages, ReasoningLevel.High, maxTokens: 900));

        var raw = (Anthropic.Models.Messages.MessageCreateParams)options.RawRepresentationFactory!(new StubChatClient())!;
        var thinking = (Anthropic.Models.Messages.ThinkingConfigEnabled)raw.Thinking!.Value!;
        Assert.AreEqual(1024, thinking.BudgetTokens, "不得低于协议下限");
        Assert.AreEqual(2048, raw.MaxTokens, "上限抬到刚好放得下思考 + 正文");
    }

    /// <summary>raw 工厂只用到"当前客户端"这个参数的存在性,给个空壳即可。</summary>
    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task CreateClient_EachProtocol_BuildsChatClient()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        foreach (ChatProtocol protocol in Enum.GetValues<ChatProtocol>())
        {
            var provider = new ResolvedModel(
                new AiProvider { Name = "t", DefaultProtocol = protocol, BaseUrl = "https://example.com/v1" },
                new AiModelConfig { Model = "test-model" });

            IChatClient client = await store.CreateClientAsync(provider, "sk-test");

            Assert.IsNotNull(client, protocol.ToString());
        }
    }
}

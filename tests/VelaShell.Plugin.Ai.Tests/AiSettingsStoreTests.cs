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
                new AiProviderConfig
                {
                    Name = "Claude",
                    Protocol = ChatProtocol.AnthropicMessages,
                    BaseUrl = "https://api.anthropic.com",
                    Model = "claude-opus-5",
                    MaxTokens = 4096
                }
            ],
            AgentMode = true
        };
        settings.ActiveProviderId = settings.Providers[0].Id;

        await store.SaveAsync(settings);
        AiSettings loaded = await store.LoadAsync();

        Assert.HasCount(1, loaded.Providers);
        Assert.AreEqual("Claude", loaded.Providers[0].Name);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, loaded.Providers[0].Protocol);
        Assert.AreEqual(4096, loaded.Providers[0].MaxTokens);
        Assert.AreEqual(settings.ActiveProviderId, loaded.ActiveProviderId);
        Assert.IsTrue(loaded.AgentMode);
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

    private static AiProviderConfig Provider(ChatProtocol protocol, ReasoningLevel reasoning, int maxTokens = 8192)
        => new() { Name = "t", Protocol = protocol, Model = "the-real-model", MaxTokens = maxTokens, Reasoning = reasoning };

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
            var provider = new AiProviderConfig
            {
                Name = "t",
                Protocol = protocol,
                BaseUrl = protocol == ChatProtocol.AnthropicMessages ? "https://example.com/v1" : "https://example.com/v1",
                Model = "test-model"
            };

            IChatClient client = await store.CreateClientAsync(provider, "sk-test");

            Assert.IsNotNull(client, protocol.ToString());
        }
    }
}

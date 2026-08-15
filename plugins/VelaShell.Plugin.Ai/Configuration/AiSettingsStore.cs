using System.ClientModel;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 设置读写:配置走 Storage(JSON),API Key 走 Secrets(加密)。
/// 同时充当 <see cref="IChatClient" /> 工厂:三种线协议分别由
/// OpenAI 官方 SDK(Chat Completions / Responses)与 Anthropic 官方 SDK 承载,
/// 统一到 Microsoft.Extensions.AI 抽象。
/// </summary>
public sealed class AiSettingsStore(IPluginContext context)
{
    private const string SettingsKey = "settings";

    /// <summary>
    /// 已解出的 API Key。取一次要走"读库 + DPAPI 解包",而每发一条消息(以及每次要后续提问)
    /// 都会建一次客户端 —— 没必要每次都解。写入/删除时同步失效。
    /// </summary>
    private readonly Dictionary<string, string?> _keyCache = [];

    /// <summary>读取设置(不存在时返回带默认值的新实例)。</summary>
    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        => await context.Storage.GetAsync<AiSettings>(SettingsKey, cancellationToken).ConfigureAwait(false) ?? new AiSettings();

    /// <summary>持久化设置。</summary>
    public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        => context.Storage.SetAsync(SettingsKey, settings, cancellationToken);

    /// <summary>读取某接入的 API Key(未配置返回 null)。命中缓存则不碰机密存储。</summary>
    public async Task<string?> GetApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (_keyCache.TryGetValue(providerId, out string? cached))
        {
            return cached;
        }
        string? key = await context.Secrets.GetAsync(SecretName(providerId), cancellationToken).ConfigureAwait(false);
        _keyCache[providerId] = key;
        return key;
    }

    /// <summary>写入(或清除)某接入的 API Key。</summary>
    public async Task SetApiKeyAsync(string providerId, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            await context.Secrets.DeleteAsync(SecretName(providerId), cancellationToken).ConfigureAwait(false);
            _keyCache[providerId] = null;
        }
        else
        {
            await context.Secrets.SetAsync(SecretName(providerId), apiKey, cancellationToken).ConfigureAwait(false);
            _keyCache[providerId] = apiKey;
        }
    }

    /// <summary>删除接入时连带清除其机密。</summary>
    public async Task DeleteApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
    {
        _keyCache.Remove(providerId);
        await context.Secrets.DeleteAsync(SecretName(providerId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为接入构造 <see cref="IChatClient" />(每次调用读取最新 API Key)。
    /// 返回的是"裸"客户端;Agent 模式的函数调用循环由调用方经
    /// <c>AsBuilder().UseFunctionInvocation()</c> 叠加。
    /// </summary>
    public async Task<IChatClient> CreateClientAsync(AiProviderConfig provider, string? apiKeyOverride = null, CancellationToken cancellationToken = default)
    {
        string? apiKey = apiKeyOverride ?? await GetApiKeyAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        switch (provider.Protocol)
        {
            case ChatProtocol.OpenAiChatCompletions:
                return CreateOpenAiClient(provider, apiKey).GetChatClient(provider.Model).AsIChatClient();

            case ChatProtocol.OpenAiResponses:
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
                return CreateOpenAiClient(provider, apiKey).GetResponsesClient().AsIChatClient(provider.Model);
#pragma warning restore OPENAI001

            case ChatProtocol.AnthropicMessages:
                {
                    string baseUrl = provider.BaseUrl.TrimEnd('/');
                    // Anthropic SDK 自己追加 /v1 路径,用户误填时剥除
                    if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    {
                        baseUrl = baseUrl[..^3].TrimEnd('/');
                    }
                    // 属性为 init-only,按有无 Key 分别构造(无 Key 时留给 SDK 的环境变量回退)
                    AnthropicClient anthropic = string.IsNullOrWhiteSpace(apiKey)
                        ? new AnthropicClient { BaseUrl = baseUrl }
                        : new AnthropicClient { BaseUrl = baseUrl, ApiKey = apiKey };
                    return anthropic.AsIChatClient(provider.Model, provider.MaxTokens);
                }

            default:
                throw new InvalidOperationException($"Unknown protocol: {provider.Protocol}");
        }
    }

    /// <summary>Anthropic 的思考预算下限(协议要求 <c>budget_tokens ≥ 1024</c>)。</summary>
    private const int AnthropicMinThinkingBudget = 1024;

    /// <summary>
    /// 把接入的思考档位翻译进请求选项。两条路并存,因为两家的适配器认的东西不一样:
    /// <list type="bullet">
    /// <item><c>ChatOptions.Reasoning</c> —— OpenAI 系适配器认(映射成 reasoning effort / summary)。</item>
    /// <item>
    /// <c>RawRepresentationFactory</c> 返回 <see cref="MessageCreateParams" /> —— Anthropic 适配器
    /// 不认前者(12.40.0 的 <c>AsIChatClient</c> 里没有任何 reasoning 映射),只能把
    /// <c>thinking</c> 直接塞进请求体。
    /// </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <b>实测约束(2026-08-15,Anthropic 12.40.0,本地假端点抓包核对过流式与非流式两条路)</b>:
    /// <c>MessageCreateParams</c> 的 <c>required</c> 成员(<c>MaxTokens</c> / <c>Model</c> / <c>Messages</c>)
    /// 在 raw 对象里必然有值,而适配器<b>只覆盖 Messages</b> —— MaxTokens 与 Model 以 raw 里的为准,
    /// <c>ChatOptions.MaxOutputTokens</c> 和 <c>AsIChatClient(model, maxTokens)</c> 都被无视。
    /// 所以这里必须把真实的模型与输出上限一并填进去,否则请求会带着占位值发出去。
    /// 另:开思考时 Anthropic 要求 <c>max_tokens > budget_tokens</c>,且 temperature 只能是 1 或不填
    /// (本插件从不设 Temperature)。
    /// </remarks>
    public static void ApplyReasoning(ChatOptions options, AiProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Reasoning == ReasoningLevel.Default)
        {
            return;
        }

        options.Reasoning = provider.Reasoning == ReasoningLevel.Off
            ? new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.None }
            : new ReasoningOptions
            {
                Effort = provider.Reasoning switch
                {
                    ReasoningLevel.Low => ReasoningEffort.Low,
                    ReasoningLevel.High => ReasoningEffort.High,
                    _ => ReasoningEffort.Medium
                },
                // 要的就是把思考过程显示出来,能给全文就别只给摘要
                Output = ReasoningOutput.Full
            };

        if (provider.Protocol != ChatProtocol.AnthropicMessages)
        {
            return;
        }

        (int budget, int maxTokens) = AnthropicThinkingBudget(provider);
        ThinkingConfigParam thinking = provider.Reasoning == ReasoningLevel.Off
            ? new ThinkingConfigDisabled()
            : new ThinkingConfigEnabled(budget);
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            // Messages 会被适配器覆盖成真正的对话;MaxTokens/Model 不会,必须给真值(见 remarks)
            Messages = [],
            MaxTokens = maxTokens,
            Model = provider.Model,
            Thinking = thinking
        };
    }

    /// <summary>
    /// 算 Anthropic 的思考预算与配套的输出上限:预算不得低于协议下限,也必须小于 max_tokens
    /// (给正文留够 <see cref="AnthropicMinThinkingBudget" /> 的余量);
    /// 用户把输出上限设得过小时,把上限抬到刚好放得下,而不是悄悄不思考。
    /// </summary>
    private static (int Budget, int MaxTokens) AnthropicThinkingBudget(AiProviderConfig provider)
    {
        int desired = provider.Reasoning switch
        {
            ReasoningLevel.Low => 2048,
            ReasoningLevel.High => 16384,
            _ => 4096
        };
        int budget = Math.Clamp(desired, AnthropicMinThinkingBudget,
            Math.Max(AnthropicMinThinkingBudget, provider.MaxTokens - AnthropicMinThinkingBudget));
        return (budget, Math.Max(provider.MaxTokens, budget + AnthropicMinThinkingBudget));
    }

    private static OpenAIClient CreateOpenAiClient(AiProviderConfig provider, string? apiKey)
        => new(
            // OpenAI SDK 要求凭据非空;Ollama 等本地服务无鉴权,给占位值即可
            new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl.TrimEnd('/')) });

    private static string SecretName(string providerId) => $"apikey:{providerId}";
}

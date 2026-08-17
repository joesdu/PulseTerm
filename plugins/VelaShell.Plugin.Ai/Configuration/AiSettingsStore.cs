using System.ClientModel;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Chat;
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

    /// <summary>
    /// 读取设置(不存在时返回带默认值的新实例)。旧版扁平接入列表会在这里折成两层并<b>立即回写</b>
    /// (含机密整理),之后再读就是新格式了。
    /// </summary>
    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new AiSettings();
        }
        AiSettings settings = raw.Deserialize<AiSettings>() ?? new AiSettings();
        if (LegacySettingsMigration.IsLegacyShape(raw))
        {
            List<LegacyProviderConfig> legacy = raw.GetProperty(nameof(AiSettings.Providers))
                .Deserialize<List<LegacyProviderConfig>>() ?? [];
            List<(AiProvider Provider, List<LegacyProviderConfig> Members)> groups = LegacySettingsMigration.Group(legacy);
            await LegacySettingsMigration.MigrateSecretsAsync(groups, context.Secrets, cancellationToken).ConfigureAwait(false);
            _keyCache.Clear();
            settings.Providers = groups.ConvertAll(g => g.Provider);
            settings.Migrate();
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            context.Log.Info($"AI settings: migrated {legacy.Count} legacy provider entries into {settings.Providers.Count} provider(s).");
        }
        return settings;
    }

    /// <summary>持久化设置。</summary>
    public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        => context.Storage.SetAsync(SettingsKey, settings, cancellationToken);

    /// <summary>读取某 Key 归属者(供应商 id,或带独立 Key 的模型 id)的 API Key(未配置返回 null)。命中缓存则不碰机密存储。按模型解析继承链请传 <see cref="ResolvedModel.ApiKeyOwnerId" />。</summary>
    public async Task<string?> GetApiKeyAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        if (_keyCache.TryGetValue(ownerId, out string? cached))
        {
            return cached;
        }
        string? key = await context.Secrets.GetAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
        _keyCache[ownerId] = key;
        return key;
    }

    /// <summary>写入(或清除)某归属者的 API Key。</summary>
    public async Task SetApiKeyAsync(string ownerId, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            await context.Secrets.DeleteAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
            _keyCache[ownerId] = null;
        }
        else
        {
            await context.Secrets.SetAsync(SecretName(ownerId), apiKey, cancellationToken).ConfigureAwait(false);
            _keyCache[ownerId] = apiKey;
        }
    }

    /// <summary>删除供应商 / 模型时连带清除其机密。</summary>
    public async Task DeleteApiKeyAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        _keyCache.Remove(ownerId);
        await context.Secrets.DeleteAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为模型构造 <see cref="IChatClient" />(每次调用按继承链读取最新 API Key)。
    /// 返回的是"裸"客户端;Agent 模式的函数调用循环由调用方经
    /// <c>AsBuilder().UseFunctionInvocation()</c> 叠加。
    /// </summary>
    public async Task<IChatClient> CreateClientAsync(ResolvedModel provider, string? apiKeyOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string? apiKey = apiKeyOverride ?? await GetApiKeyAsync(provider.ApiKeyOwnerId, cancellationToken).ConfigureAwait(false);
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
                    // 中转站常在 Anthropic 流末尾补一行 OpenAI 习惯的 data: DONE,
                    // 而 SDK 对每个 data: 行无条件反序列化 —— 整轮回复会在最后一刻炸掉(见 SseRepairHandler)
                    DelegatingHandler[] handlers = [new SseRepairHandler(ReportSseDrop)];
                    // 属性为 init-only,按有无 Key 分别构造(无 Key 时留给 SDK 的环境变量回退)
                    AnthropicClient anthropic = string.IsNullOrWhiteSpace(apiKey)
                        ? new AnthropicClient { BaseUrl = baseUrl, Handlers = handlers }
                        : new AnthropicClient { BaseUrl = baseUrl, ApiKey = apiKey, Handlers = handlers };
                    return anthropic.AsIChatClient(provider.Model, provider.MaxTokens);
                }

            default:
                throw new InvalidOperationException($"Unknown protocol: {provider.Protocol}");
        }
    }

    /// <summary>已经报告过的收尾哨兵(见 <see cref="ReportSseDrop" />)。</summary>
    private readonly HashSet<string> _reportedSentinels = [];

    /// <summary>
    /// SSE 清洗丢了一行时怎么记。
    /// </summary>
    /// <remarks>
    /// 哨兵(<c>[DONE]</c> 之类)每轮对话都会来一次,报成每轮一条 Warning 就是纯噪音;
    /// 但完全不报又会丢掉"清洗生效了"这个凭据 —— 折中成<b>每种哨兵只报头一次</b>,而且降到 Info。
    /// 认不出来的载荷照旧每次都警告:那可能是中转站塞进来的错误信息,漏一条就变成无声的截断。
    /// </remarks>
    private void ReportSseDrop(string payload, bool sentinel)
    {
        if (!sentinel)
        {
            context.Log.Warn($"SSE repair: dropped an unparsable data line — {payload}");
            return;
        }
        // 清洗跑在后台的搬运任务上,这个集合会被多个流并发碰到
        lock (_reportedSentinels)
        {
            if (!_reportedSentinels.Add(payload))
            {
                return;
            }
        }
        context.Log.Info(
            $"SSE repair: this endpoint ends its stream with a non-Anthropic sentinel ({payload}); dropping it from here on.");
    }

    /// <summary>Anthropic 的思考预算下限(协议要求 <c>budget_tokens ≥ 1024</c>)。</summary>
    private const int AnthropicMinThinkingBudget = 1024;

    /// <summary>
    /// 把模型的思考档位翻译进请求选项。两条路并存,因为两家的适配器认的东西不一样:
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
    public static void ApplyReasoning(ChatOptions options, ResolvedModel provider)
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
    private static (int Budget, int MaxTokens) AnthropicThinkingBudget(ResolvedModel provider)
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

    private static OpenAIClient CreateOpenAiClient(ResolvedModel provider, string? apiKey)
        => new(
            // OpenAI SDK 要求凭据非空;Ollama 等本地服务无鉴权,给占位值即可
            new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl.TrimEnd('/')) });

    private static string SecretName(string ownerId) => LegacySettingsMigration.SecretName(ownerId);
}

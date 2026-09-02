using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OpenAI;
using VelaShell.Plugin.Ai.Auth;
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

    /// <summary>删除供应商 / 模型时连带清除其机密(API Key 与订阅令牌一并清)。</summary>
    public async Task DeleteApiKeyAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        _keyCache.Remove(ownerId);
        _tokenCache.Remove(ownerId);
        await context.Secrets.DeleteAsync(SecretName(ownerId), cancellationToken).ConfigureAwait(false);
        await context.Secrets.DeleteAsync(TokenSecretName(ownerId), cancellationToken).ConfigureAwait(false);
    }

    // ---- 订阅登录的令牌 ----

    /// <summary>
    /// 已解出的登录令牌。与 <see cref="_keyCache" /> 同一个理由:每发一条消息都要取一次,
    /// 而取一次是"读库 + DPAPI 解包 + 反序列化"。
    /// </summary>
    private readonly Dictionary<string, OAuthTokens?> _tokenCache = [];

    /// <summary>刷新令牌时的单飞闸:一轮对话会并发建好几个客户端,没有它就会同时刷好几次。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// 刷令牌用的 HTTP 客户端。<b>静态共享</b>:每次刷新新建一个会攒下一堆处于 TIME_WAIT 的连接,
    /// 而这条路上不需要任何自定义 handler(令牌端点不是 SSE,也没有中转站要清洗)。
    /// </summary>
    private static readonly HttpClient TokenHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>刷令牌走谁。留成可替换的,测试里换成打桩的 handler(生产从不改它)。</summary>
    internal OAuthClient TokenClient { get; set; } = new(TokenHttp);

    /// <summary>读取某供应商的订阅登录令牌;没登录过返回 null。</summary>
    public async Task<OAuthTokens?> GetTokensAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (_tokenCache.TryGetValue(providerId, out OAuthTokens? cached))
        {
            return cached;
        }
        string? json = await context.Secrets.GetAsync(TokenSecretName(providerId), cancellationToken).ConfigureAwait(false);
        OAuthTokens? tokens = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                tokens = JsonSerializer.Deserialize<OAuthTokens>(json);
            }
            catch (JsonException ex)
            {
                // 存坏了就当没登录 —— 让用户重登一次,好过每条消息都炸一次
                context.Log.Warn($"Stored sign-in for provider {providerId} could not be read: {ex.Message}");
            }
        }
        _tokenCache[providerId] = tokens;
        return tokens;
    }

    /// <summary>写入登录令牌(整组 JSON 加密落盘)。</summary>
    public async Task SaveTokensAsync(string providerId, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await context.Secrets.SetAsync(TokenSecretName(providerId), JsonSerializer.Serialize(tokens), cancellationToken)
                     .ConfigureAwait(false);
        _tokenCache[providerId] = tokens;
    }

    /// <summary>退出登录:清掉令牌。</summary>
    public async Task ClearTokensAsync(string providerId, CancellationToken cancellationToken = default)
    {
        _tokenCache[providerId] = null;
        await context.Secrets.DeleteAsync(TokenSecretName(providerId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解出这个模型发请求时该用的凭据:模型自带 Key > 供应商订阅登录 > 供应商 API Key。
    /// </summary>
    /// <remarks>
    /// 订阅登录且换回来的是短期 access token 时,这里会<b>顺手把快过期的刷掉</b> ——
    /// 客户端是每发一条消息现建的,在建之前刷新,就等于每条消息都拿着一把新鲜的令牌上路,
    /// 不必再往请求管道里塞一层"401 就重试"。
    /// </remarks>
    /// <param name="model">已解出继承链的模型。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<ProviderCredential> ResolveCredentialAsync(ResolvedModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        AiProvider provider = model.Provider;
        if (provider.Auth != AuthMethod.Subscription || model.Config.HasOwnApiKey)
        {
            return ProviderCredential.Key(await GetApiKeyAsync(model.ApiKeyOwnerId, cancellationToken).ConfigureAwait(false));
        }
        OAuthTokens? tokens = await GetTokensAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (tokens is null)
        {
            return ProviderCredential.Key(null); // 还没登录:让请求带着空凭据发出去,由服务端给出准确的 401
        }
        // 登录换回来的是一把长期 API Key(OpenRouter 那类):与手填的 Key 走同一条路
        if (provider.OAuth?.Credential == OAuthCredential.ApiKey)
        {
            return ProviderCredential.Key(tokens.AccessToken);
        }
        if (tokens.NeedsRefresh && !string.IsNullOrEmpty(tokens.RefreshToken) && provider.OAuth is { } oauth)
        {
            tokens = await RefreshAsync(provider, oauth, tokens, cancellationToken).ConfigureAwait(false);
        }
        return new ProviderCredential(tokens.AccessToken, true,
            ExtraHeadersPolicy.Parse(provider.OAuth?.ExtraHeaders, tokens.AccountId),
            tokens.BaseUrl);
    }

    /// <summary>
    /// 取<b>供应商这一层</b>的凭据(拉模型列表用,那一步还没有选定模型)。
    /// </summary>
    /// <remarks>
    /// 拿一个空白模型去解继承链,而不是拿 <c>Models[0]</c>:后者可能勾着"用自己的 Key",
    /// 那把 Key 是给那个模型的,拿它去问整家的模型列表就问错了对象 ——
    /// 空白模型什么都不覆盖,解出来的正好是供应商自己的地址与 Key。
    /// </remarks>
    /// <param name="provider">供应商。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<ProviderCredential> ResolveProviderCredentialAsync(AiProvider provider,
        CancellationToken cancellationToken = default)
        => ResolveCredentialAsync(new ResolvedModel(provider, new AiModelConfig()), cancellationToken);

    /// <summary>换一组新令牌并落盘;换不动就沿用旧的(过期的令牌至少能换来一个准确的 401)。</summary>
    private async Task<OAuthTokens> RefreshAsync(AiProvider provider, OAuthConfig oauth, OAuthTokens tokens,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等闸期间别人可能已经刷过了
            OAuthTokens? latest = await GetTokensAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            if (latest is not null && !latest.NeedsRefresh)
            {
                return latest;
            }
            OAuthTokens fresh = await TokenClient.RefreshAsync(oauth, latest ?? tokens, cancellationToken)
                                                 .ConfigureAwait(false);
            await SaveTokensAsync(provider.Id, fresh, cancellationToken).ConfigureAwait(false);
            context.Log.Info($"Refreshed the sign-in for '{provider.Name}'.");
            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"Refreshing the sign-in for '{provider.Name}' failed: {ex.Message}");
            return tokens;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// 为模型构造 <see cref="IChatClient" />(每次调用按继承链取最新凭据:API Key 或订阅令牌)。
    /// 返回的是"裸"客户端;Agent 模式的函数调用循环由调用方经
    /// <c>AsBuilder().UseFunctionInvocation()</c> 叠加。
    /// </summary>
    /// <param name="provider">已解出继承链的模型。</param>
    /// <param name="apiKeyOverride">设置页"测试"用:拿表单里还没保存的那把 Key 试一下。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<IChatClient> CreateClientAsync(ResolvedModel provider, string? apiKeyOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ProviderCredential credential = apiKeyOverride is null
            ? await ResolveCredentialAsync(provider, cancellationToken).ConfigureAwait(false)
            : ProviderCredential.Key(apiKeyOverride);
        // OpenAI 系协议无论 Key 还是 access token 都走 Authorization: Bearer,一条路即可;
        // Anthropic 分岔(Key 是 x-api-key,令牌是 Bearer),见下。
        string? secret = credential.Value;
        switch (provider.Protocol)
        {
            case ChatProtocol.OpenAiChatCompletions:
                return CreateOpenAiClient(provider, credential).GetChatClient(provider.Model).AsIChatClient();

            case ChatProtocol.OpenAiResponses:
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
                return CreateOpenAiClient(provider, credential).GetResponsesClient().AsIChatClient(provider.Model);
#pragma warning restore OPENAI001

            case ChatProtocol.AnthropicMessages:
                {
                    // 少数供应商按账户下发端点(Copilot 的企业账户),那时以登录带回来的为准
                    string baseUrl = EndpointOf(provider, credential).TrimEnd('/');
                    // Anthropic SDK 自己追加 /v1 路径,用户误填时剥除
                    if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    {
                        baseUrl = baseUrl[..^3].TrimEnd('/');
                    }
                    // 中转站常在 Anthropic 流末尾补一行 OpenAI 习惯的 data: DONE,
                    // 而 SDK 对每个 data: 行无条件反序列化 —— 整轮回复会在最后一刻炸掉(见 SseRepairHandler)
                    DelegatingHandler[] handlers = credential.Headers is { Count: > 0 } extra
                        ? [new SseRepairHandler(ReportSseDrop), new ExtraHeadersHandler(extra)]
                        : [new SseRepairHandler(ReportSseDrop)];
                    // 属性为 init-only,按凭据形态分别构造:
                    // 无凭据时留给 SDK 的环境变量回退;登录换来的短期令牌要走 AuthToken
                    // (它发的是 Authorization: Bearer,而 ApiKey 发的是 x-api-key —— 两者不能混)。
                    AnthropicClient anthropic = string.IsNullOrWhiteSpace(secret)
                        ? new AnthropicClient { BaseUrl = baseUrl, Handlers = handlers }
                        : credential.IsBearerToken
                            ? new AnthropicClient { BaseUrl = baseUrl, AuthToken = secret, Handlers = handlers }
                            : new AnthropicClient { BaseUrl = baseUrl, ApiKey = secret, Handlers = handlers };
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
    /// 把这一家端点的"脾气"应用到请求上:不收的参数摘掉、该关的开关关掉。
    /// </summary>
    /// <remarks>
    /// 订阅型的私有后端常常只是标准协议的<b>受限子集</b>,多发一个字段就整轮 400,
    /// 而且一次只告诉你一个。所以这些差异全部收在<b>目录数据</b>里
    /// (<see cref="AiProvider.UnsupportedParameters" /> / <see cref="AiProvider.StoreResponses" />),
    /// 由这里统一施加 —— 再发现一条只要加一行数据,不必改代码。
    /// </remarks>
    /// <param name="options">这一轮的请求选项;<b>就地修改</b>。</param>
    /// <param name="provider">已解出继承链的模型。</param>
    public static void ApplyEndpointQuirks(ChatOptions options, ResolvedModel provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        // 按目录现读,不用供应商身上那份快照 —— 否则每加一条新规则,
        // 已经连上的用户都得重新登录一次才拿得到(见 EndpointQuirks)
        EndpointQuirks quirks = EndpointQuirks.Of(provider.Provider);
        ApplyResponseStore(options, provider, quirks);
        foreach (string raw in quirks.UnsupportedParameters
                                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // 认不出来的名字直接跳过:目录里写错一个字,不该让整轮对话崩掉
            switch (raw.Trim().ToLowerInvariant())
            {
                case "max_output_tokens" or "max_tokens":
                    options.MaxOutputTokens = null;
                    break;
                case "temperature":
                    options.Temperature = null;
                    break;
                case "top_p":
                    options.TopP = null;
                    break;
                case "stop" or "stop_sequences":
                    options.StopSequences = null;
                    break;
                case "frequency_penalty":
                    options.FrequencyPenalty = null;
                    break;
                case "presence_penalty":
                    options.PresencePenalty = null;
                    break;
                case "seed":
                    options.Seed = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 明确要求"别存这一轮"时,往 Responses 请求里塞 <c>store: false</c>。
    /// </summary>
    /// <remarks>
    /// 只对 <see cref="ChatProtocol.OpenAiResponses" /> 有意义(<c>store</c> 是那套协议的字段)。
    /// 走的是 <c>RawRepresentationFactory</c>,与 Anthropic 那条思考预算的路子同一个道理:
    /// <c>ChatOptions</c> 上没有对应的抽象属性,只能把原生请求对象递下去。
    /// </remarks>
    private static void ApplyResponseStore(ChatOptions options, ResolvedModel provider, EndpointQuirks quirks)
    {
        if (provider.Protocol != ChatProtocol.OpenAiResponses || quirks.StoreResponses)
        {
            return;
        }
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
        options.RawRepresentationFactory = _ => new OpenAI.Responses.CreateResponseOptions
        {
            StoredOutputEnabled = false
        };
#pragma warning restore OPENAI001
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

    /// <summary>
    /// 这次请求该打到哪儿:登录带回来的端点优先,没有才用供应商配置里的。
    /// </summary>
    /// <remarks>
    /// 少数供应商按账户下发地址(Copilot 的企业账户与个人账户不是同一个),而那个地址
    /// <b>只有登录之后才知道</b> —— 没有这一层,就只能让用户手填一个他根本无从知道的值。
    /// </remarks>
    private static string EndpointOf(ResolvedModel provider, ProviderCredential credential)
        => string.IsNullOrWhiteSpace(credential.BaseUrl) ? provider.BaseUrl : credential.BaseUrl;

    private static OpenAIClient CreateOpenAiClient(ResolvedModel provider, ProviderCredential credential)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(EndpointOf(provider, credential).TrimEnd('/')) };
        if (credential.Headers is { Count: > 0 } headers)
        {
            options.AddPolicy(new ExtraHeadersPolicy(headers), PipelinePosition.PerCall);
        }
        return new OpenAIClient(
            // OpenAI SDK 要求凭据非空;Ollama 等本地服务无鉴权,给占位值即可。
            // access token 与 API Key 在这条路上是一回事 —— SDK 都发成 Authorization: Bearer。
            new ApiKeyCredential(string.IsNullOrWhiteSpace(credential.Value) ? "not-needed" : credential.Value),
            options);
    }

    private static string SecretName(string ownerId) => LegacySettingsMigration.SecretName(ownerId);

    /// <summary>订阅登录的令牌组存在哪个机密键下。与 API Key 分开,退出登录时不误伤手填的 Key。</summary>
    private static string TokenSecretName(string providerId) => $"oauth:{providerId}";
}

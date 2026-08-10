using System.ClientModel;
using Anthropic;
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

    /// <summary>读取设置(不存在时返回带默认值的新实例)。</summary>
    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        => await context.Storage.GetAsync<AiSettings>(SettingsKey, cancellationToken).ConfigureAwait(false) ?? new AiSettings();

    /// <summary>持久化设置。</summary>
    public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        => context.Storage.SetAsync(SettingsKey, settings, cancellationToken);

    /// <summary>读取某接入的 API Key(未配置返回 null)。</summary>
    public Task<string?> GetApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
        => context.Secrets.GetAsync(SecretName(providerId), cancellationToken);

    /// <summary>写入(或清除)某接入的 API Key。</summary>
    public async Task SetApiKeyAsync(string providerId, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            await context.Secrets.DeleteAsync(SecretName(providerId), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await context.Secrets.SetAsync(SecretName(providerId), apiKey, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>删除接入时连带清除其机密。</summary>
    public Task DeleteApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
        => context.Secrets.DeleteAsync(SecretName(providerId), cancellationToken);

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

    private static OpenAIClient CreateOpenAiClient(AiProviderConfig provider, string? apiKey)
        => new(
            // OpenAI SDK 要求凭据非空;Ollama 等本地服务无鉴权,给占位值即可
            new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl.TrimEnd('/')) });

    private static string SecretName(string providerId) => $"apikey:{providerId}";
}

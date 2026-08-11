using System.Text.Json.Serialization;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>模型接入使用的线协议。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatProtocol
{
    /// <summary>OpenAI Chat Completions 兼容协议(OpenAI / Grok / Ollama / 绝大多数中转站)。</summary>
    OpenAiChatCompletions,

    /// <summary>OpenAI Responses 流式协议(OpenAI 官方新 API 及兼容中转站)。</summary>
    OpenAiResponses,

    /// <summary>Anthropic Messages 协议(Claude 官方及 Anthropic 兼容中转站)。</summary>
    AnthropicMessages
}

/// <summary>
/// 一个模型接入配置。API Key 不在此结构里 —— 单独经
/// <c>ISecretsApi</c> 加密存储(键 <c>apikey:&lt;Id&gt;</c>)。
/// </summary>
public sealed class AiProviderConfig
{
    /// <summary>稳定 id(创建时生成,作为机密键的一部分)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>线协议。</summary>
    public ChatProtocol Protocol { get; set; } = ChatProtocol.OpenAiChatCompletions;

    /// <summary>
    /// 基地址。OpenAI 系协议习惯含 <c>/v1</c>(如 https://api.openai.com/v1);
    /// Anthropic 协议写根地址即可(如 https://api.anthropic.com),误带 /v1 会被自动剥除。
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>模型 id。</summary>
    public string Model { get; set; } = "";

    /// <summary>单轮最大输出 token(Anthropic 协议必需;OpenAI 系协议不发送)。</summary>
    public int MaxTokens { get; set; } = 8192;
}

/// <summary>插件持久化设置(经 Storage 存 JSON)。</summary>
public sealed class AiSettings
{
    /// <summary>全部接入配置。</summary>
    public List<AiProviderConfig> Providers { get; set; } = [];

    /// <summary>当前选中的接入 id。</summary>
    public string? ActiveProviderId { get; set; }

    /// <summary>Agent 模式(带工具循环)。</summary>
    public bool AgentMode { get; set; }

    /// <summary>Agent 模式下 run_command / write_terminal 免审批(有风险,默认关)。</summary>
    public bool AutoApproveCommands { get; set; }

    /// <summary>自定义系统提示词(空 = 用内置默认)。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>用户自定义的 MCP 服务器(Agent 模式下启用项的工具并入工具箱)。</summary>
    public List<McpServerConfig> McpServers { get; set; } = [];
}

/// <summary>内置的接入预设(设置页"新增"下拉)。</summary>
public static class ProviderPresets
{
    /// <summary>全部预设:显示标签 + 出厂配置工厂。</summary>
    public static IReadOnlyList<(string Label, Func<AiProviderConfig> Create)> All { get; } =
    [
        ("OpenAI (Responses)", () => new AiProviderConfig
        {
            Name = "OpenAI",
            Protocol = ChatProtocol.OpenAiResponses,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5"
        }),
        ("OpenAI (Chat Completions)", () => new AiProviderConfig
        {
            Name = "OpenAI (CC)",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5"
        }),
        ("Anthropic Claude", () => new AiProviderConfig
        {
            Name = "Claude",
            Protocol = ChatProtocol.AnthropicMessages,
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-opus-5"
        }),
        ("xAI Grok", () => new AiProviderConfig
        {
            Name = "Grok",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "https://api.x.ai/v1",
            Model = "grok-4"
        }),
        ("Ollama (local)", () => new AiProviderConfig
        {
            Name = "Ollama",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3.1"
        }),
        ("Custom (OpenAI compatible)", () => new AiProviderConfig
        {
            Name = "Custom",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "https://example.com/v1",
            Model = ""
        }),
        ("Custom (Anthropic compatible)", () => new AiProviderConfig
        {
            Name = "Custom (Anthropic)",
            Protocol = ChatProtocol.AnthropicMessages,
            BaseUrl = "https://example.com",
            Model = ""
        })
    ];
}

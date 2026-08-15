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

/// <summary>对话模式:决定这一轮给不给模型工具、给哪些。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatMode
{
    /// <summary>纯对话:不给任何工具,模型只能凭对话内容作答。</summary>
    Chat,

    /// <summary>计划:只给<b>只读</b>工具,并要求模型先给方案而不是动手。</summary>
    Plan,

    /// <summary>Agent:全部工具,可读可写,危险操作按审批模式处理。</summary>
    Agent
}

/// <summary>危险操作的审批方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalMode
{
    /// <summary>默认审批:每个可能改变状态的操作都问一次。</summary>
    Ask,

    /// <summary>
    /// 只读放行:一望即知无副作用的命令(ls / df / cat 之类)自动执行,
    /// 写文件、往终端敲字、以及任何看不准的命令仍旧逐条问。
    /// </summary>
    ReadOnlyAuto,

    /// <summary>绕过审批:所有工具调用一律自动批准(有风险)。</summary>
    Bypass
}

/// <summary>模型的思考(reasoning)档位。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningLevel
{
    /// <summary>不指定 —— 请求里不带 reasoning 参数,由模型/服务端自行决定(默认)。</summary>
    Default,

    /// <summary>显式关闭思考。</summary>
    Off,

    /// <summary>低。</summary>
    Low,

    /// <summary>中。</summary>
    Medium,

    /// <summary>高。</summary>
    High
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

    /// <summary>
    /// 单轮最大输出 token(Anthropic 协议构造客户端时必需,三种协议都会作为
    /// <c>ChatOptions.MaxOutputTokens</c> 随请求下发)。
    /// </summary>
    /// <remarks>属性名保持 <c>MaxTokens</c> 不改:已落盘的设置就是按这个名字序列化的。</remarks>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>
    /// 模型的上下文窗口(最大输入 token)。不参与请求 —— 只用来把"这一轮吃掉了多少上下文"
    /// 换算成输入框下方那个占比;填 0 表示未知,那时只显示累计用量。
    /// </summary>
    public int MaxInputTokens { get; set; } = 128000;

    /// <summary>
    /// 自动打提示词缓存断点(仅 Anthropic 协议有效,见 <c>PromptCache</c>)。
    /// 默认开:短于最小可缓存长度的前缀服务端直接忽略标记,不缓存也不加价,所以开着没有下行风险。
    /// </summary>
    public bool PromptCaching { get; set; } = true;

    /// <summary>采样温度;null = 不发这个参数,用服务端默认。</summary>
    /// <remarks>开思考的 Anthropic 模型只接受 temperature=1 或不填,那种情况下这里应留空。</remarks>
    public float? Temperature { get; set; }

    /// <summary>核采样 top_p;null = 不发。与 <see cref="Temperature" /> 一般只调一个。</summary>
    public float? TopP { get; set; }

    /// <summary>停止序列(每行一条);为空则不发。</summary>
    public string StopSequences { get; set; } = "";

    /// <summary>
    /// 这个接入专用的系统提示词。非空时盖过全局那份 ——
    /// 不同模型吃的提示词风格不一样,一份全局的按不住。
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>每百万输入 token 的价格(货币由用户自己心里有数);0 = 不估算成本。</summary>
    public double InputPricePerMillion { get; set; }

    /// <summary>每百万输出 token 的价格;0 = 不估算。</summary>
    public double OutputPricePerMillion { get; set; }

    /// <summary>每百万<b>缓存命中</b>输入 token 的价格;0 = 按普通输入价算。</summary>
    public double CachedInputPricePerMillion { get; set; }

    /// <summary>思考档位;<see cref="ReasoningLevel.Default" /> 时请求里不带该参数。</summary>
    /// <remarks>
    /// 经 <c>ChatOptions.Reasoning</c> 下发,由各家适配器翻译成自己的字段
    /// (OpenAI 系是 reasoning effort / summary)。适配器不认的协议会忽略它,
    /// 此时模型是否吐思考内容仍由服务端决定。
    /// </remarks>
    public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Default;
}

/// <summary>插件持久化设置(经 Storage 存 JSON)。</summary>
public sealed class AiSettings
{
    /// <summary>全部接入配置。</summary>
    public List<AiProviderConfig> Providers { get; set; } = [];

    /// <summary>当前选中的接入 id。</summary>
    public string? ActiveProviderId { get; set; }

    /// <summary>对话模式(纯对话 / 计划 / Agent)。</summary>
    public ChatMode Mode { get; set; } = ChatMode.Chat;

    /// <summary>危险操作的审批方式。</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;

    /// <summary>不想暴露给模型的内置工具名,每行一条(见"配置工具"窗口)。</summary>
    public string DisabledBuiltinTools { get; set; } = "";

    /// <summary>旧版的 Agent 开关。仅用于迁移 —— 读到 true 就折算成 <see cref="ChatMode.Agent" />。</summary>
    /// <remarks>不要在新代码里用它;<see cref="Migrate" /> 跑完之后它就没有意义了。</remarks>
    public bool AgentMode { get; set; }

    /// <summary>旧版的免审批开关。仅用于迁移 —— 读到 true 就折算成 <see cref="ApprovalMode.Bypass" />。</summary>
    public bool AutoApproveCommands { get; set; }

    /// <summary>
    /// 把旧版的两个布尔开关折算成新的枚举。读设置时调用一次即可 ——
    /// 老用户升级上来不该发现自己的 Agent 模式被悄悄关掉了。
    /// </summary>
    public void Migrate()
    {
        if (AgentMode && Mode == ChatMode.Chat)
        {
            Mode = ChatMode.Agent;
        }
        if (AutoApproveCommands && Approval == ApprovalMode.Ask)
        {
            Approval = ApprovalMode.Bypass;
        }
        AgentMode = false;
        AutoApproveCommands = false;
    }

    /// <summary>自定义系统提示词(空 = 用内置默认)。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// 一轮回答结束后,额外问一次模型要几条"后续提问"显示在输入框上方。
    /// 关掉只是不显示后续提问,空会话的起手提示是本地文案,不受影响。
    /// </summary>
    /// <remarks>这会多发一次(很小的)请求,所以单独给开关 —— 见 <c>ChatPanelView.Suggestions.cs</c>。</remarks>
    public bool SuggestFollowUps { get; set; } = true;

    /// <summary>
    /// 上下文快撑满窗口时,把早期对话折成一段摘要继续聊(而不是直接丢掉最早的几轮)。
    /// 关掉则退回"按窗口丢最早几条"。需要接入里填了"最大输入 tokens"才会生效。
    /// </summary>
    public bool CompactContext { get; set; } = true;

    /// <summary>
    /// 以标签页打开时,右侧那一栏占标签区的百分比(15–85,越界夹取)。
    /// </summary>
    /// <remarks>
    /// <b>没有对应的设置项,由用户拖分割条决定</b>:拖完宿主通知一次
    /// (<c>IPluginPanel.PlacementRatioChanged</c>),这里记下来,下次打开还是那个宽度。
    /// 让人去填一个百分比,不如直接把他拖出来的结果记住。
    /// </remarks>
    public int PanelWidthPercent { get; set; } = 30;

    /// <summary>用户自定义的 MCP 服务器(Agent 模式下启用项的工具并入工具箱)。</summary>
    public List<McpServerConfig> McpServers { get; set; } = [];
}

/// <summary>内置的接入预设(设置页"新增"下拉)。</summary>
/// <remarks>
/// 预设里的 <see cref="AiProviderConfig.MaxInputTokens" /> 只是各家常见档位的出厂值,
/// 换模型后请在设置页按实际上下文窗口改 —— 它只影响输入框下方那个占比的分母。
/// </remarks>
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
            Model = "gpt-5",
            MaxInputTokens = 400000
        }),
        ("OpenAI (Chat Completions)", () => new AiProviderConfig
        {
            Name = "OpenAI (CC)",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5",
            MaxInputTokens = 400000
        }),
        ("Anthropic Claude", () => new AiProviderConfig
        {
            Name = "Claude",
            Protocol = ChatProtocol.AnthropicMessages,
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-opus-5",
            MaxInputTokens = 200000
        }),
        ("xAI Grok", () => new AiProviderConfig
        {
            Name = "Grok",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "https://api.x.ai/v1",
            Model = "grok-4",
            MaxInputTokens = 256000
        }),
        ("Ollama (local)", () => new AiProviderConfig
        {
            Name = "Ollama",
            Protocol = ChatProtocol.OpenAiChatCompletions,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3.1",
            MaxInputTokens = 128000
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

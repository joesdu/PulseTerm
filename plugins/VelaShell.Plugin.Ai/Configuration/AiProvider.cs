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
/// 供应商:一个 endpoint + 一把默认 API Key,下面挂若干模型。
/// API Key 不在此结构里 —— 单独经 <c>ISecretsApi</c> 加密存储(键 <c>apikey:&lt;Id&gt;</c>)。
/// </summary>
/// <remarks>
/// 之所以分两层:中转站场景下一堆模型共用同一个地址与 Key,只是模型名和协议不同,
/// 把地址/Key 复制 N 份既难改又难看。模型级仍可覆盖协议、Key、地址(见 <see cref="AiModelConfig" />)。
/// </remarks>
public sealed class AiProvider
{
    /// <summary>稳定 id(创建时生成,作为机密键的一部分)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 基地址。OpenAI 系协议习惯含 <c>/v1</c>(如 https://api.openai.com/v1);
    /// Anthropic 协议写根地址即可(如 https://api.anthropic.com),误带 /v1 会被自动剥除。
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>默认线协议;模型没有另填时沿用这个。</summary>
    public ChatProtocol DefaultProtocol { get; set; } = ChatProtocol.OpenAiChatCompletions;

    /// <summary>
    /// 拿什么鉴权:填 Key(默认,老配置读上来就是它)还是订阅登录。
    /// </summary>
    /// <remarks>
    /// 老设置里没有这个字段,反序列化后为默认值 <see cref="AuthMethod.ApiKey" /> ——
    /// 也就是升级上来的用户什么都不会变,这正是想要的。
    /// </remarks>
    public AuthMethod Auth { get; set; } = AuthMethod.ApiKey;

    /// <summary>
    /// 从供应商目录哪一条建来的(<see cref="ProviderCatalog" /> 的 id);
    /// 手工新建的为 null。界面用它认出"这一家已经加过了"并显示对应的徽标。
    /// </summary>
    public string? CatalogId { get; set; }

    /// <summary>
    /// 订阅登录的参数;<see cref="Auth" /> 不是 <see cref="AuthMethod.Subscription" /> 时为 null。
    /// 令牌本身不在这儿 —— 那是机密,单独存(键 <c>oauth:&lt;Id&gt;</c>)。
    /// </summary>
    public OAuthConfig? OAuth { get; set; }

    /// <summary>该供应商下的模型。</summary>
    public List<AiModelConfig> Models { get; set; } = [];

    /// <summary>
    /// 这一家的可用模型 id(见 <see cref="ModelsDevCatalog" />);空 = 还没拉过或那边没收录。
    /// </summary>
    /// <remarks>
    /// 只是<b>候选清单</b>,不是"已启用的模型" —— 真正在用的仍然是 <see cref="Models" />。
    /// 有了它,设置页那个模型框就能从"照着文档一个字一个字敲"变成"从下拉里挑"。
    /// </remarks>
    public List<string> AvailableModels { get; set; } = [];

    /// <summary>
    /// 允许服务端存下这一轮的响应(OpenAI Responses 协议的 <c>store</c> 字段)。
    /// </summary>
    /// <remarks>
    /// 默认 true(不发这个字段,由服务端定)。<b>订阅型的私有后端一般不接受</b> ——
    /// 它们不给第三方做服务端响应存储,请求里带着 <c>store</c> 会被直接 400 掉。
    /// 做成配置而不是按供应商 id 写死:哪家不收是端点的性质,不是代码分支。
    /// </remarks>
    public bool StoreResponses { get; set; } = true;

    /// <summary>
    /// 这一家收不收 <c>system</c> 角色的消息。
    /// </summary>
    /// <remarks>
    /// 默认收。ChatGPT 的 Codex 后端<b>不收</b> —— 照常发会得到
    /// <c>400 {"detail":"System messages are not allowed"}</c>,整轮对话直接失败。
    /// 关掉之后,系统提示词改走 Responses 协议自己的 <c>instructions</c> 字段
    /// (抓包确认过:<c>ChatOptions.Instructions</c> 就落在那儿,且不产生 system 角色,
    /// 见 <c>ResponsesWireTests</c>)。
    /// </remarks>
    public bool AllowSystemMessages { get; set; } = true;

    /// <summary>
    /// 这一家<b>不认</b>的请求参数,每行一个(线上的字段名,如 <c>max_output_tokens</c>)。
    /// </summary>
    /// <remarks>
    /// 订阅型的私有后端往往只是标准协议的一个<b>受限子集</b>:多发一个它不认的字段就整轮 400,
    /// 而且一次只告诉你一个(<c>Unsupported parameter: …</c>)。做成清单而不是一个参数一个开关 ——
    /// 再发现一个只要往目录里加一行,不必改代码、不必再来一轮试错。
    /// <para>认得的名字见 <c>AiSettingsStore.ApplyEndpointQuirks</c>;不认识的行会被忽略。</para>
    /// </remarks>
    public string UnsupportedParameters { get; set; } = "";

    /// <summary>订阅登录且参数齐全(能真的发起一次登录)。</summary>
    /// <remarks>
    /// 目录里带占位端点、等着用户填客户端 id 的那几条(Azure / 自定义)在这里必须判 false ——
    /// 不然点「登录」会发出一个 <c>client_id=</c> 空着的请求,换回来一句服务端的天书,
    /// 而用户真正该看到的是"先把客户端 id 填上"。
    /// </remarks>
    [JsonIgnore]
    public bool CanSignIn => Auth == AuthMethod.Subscription && OAuth is { } oauth
                             && !string.IsNullOrWhiteSpace(oauth.TokenUrl)
                             // OpenRouter 那一路本来就没有 client_id,别把它一起卡掉
                             && (oauth.Flow == OAuthFlow.OpenRouterPkce
                                 || !string.IsNullOrWhiteSpace(oauth.ClientId))
                             // 设备码类流程用不到授权页地址,要的是设备码地址(别只判 DeviceCode
                             // 一个值 —— 两段式的 Copilot 也是设备码起手,漏了它就永远"登不了")
                             && (oauth.Flow is OAuthFlow.DeviceCode or OAuthFlow.GitHubCopilotDevice
                                 ? !string.IsNullOrWhiteSpace(oauth.DeviceCodeUrl)
                                 : !string.IsNullOrWhiteSpace(oauth.AuthorizationUrl));
}

/// <summary>
/// 一个模型配置,挂在 <see cref="AiProvider" /> 下。地址 / 协议 / Key 默认继承供应商,
/// 三者都可单独覆盖;其余(容量、思考、采样、单价、专用提示词)是模型自己的。
/// </summary>
public sealed class AiModelConfig
{
    /// <summary>稳定 id。<see cref="AiSettings.ActiveModelId" /> 指向它;独立 Key 也以它为机密键。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称;留空则界面上显示 <see cref="Model" />。</summary>
    public string Name { get; set; } = "";

    /// <summary>模型 id。</summary>
    public string Model { get; set; } = "";

    /// <summary>线协议;null = 继承供应商的 <see cref="AiProvider.DefaultProtocol" />。</summary>
    public ChatProtocol? Protocol { get; set; }

    /// <summary>
    /// 用自己的 API Key(机密键 <c>apikey:&lt;模型 Id&gt;</c>)而不是供应商那把。
    /// 同一家中转站按模型发不同 Key 的情况不少见,所以留这个口子。
    /// </summary>
    public bool HasOwnApiKey { get; set; }

    /// <summary>覆盖供应商基地址;null/空 = 继承。偶尔某个模型走的路径跟同家其它模型不一样时用。</summary>
    public string? BaseUrlOverride { get; set; }

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
    /// 这个模型专用的系统提示词。非空时盖过全局那份 ——
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

    /// <summary>界面上显示的名字:名称,没填就退回模型 id。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
}

/// <summary>
/// 供应商 + 模型合并后的<b>扁平视图</b>:地址 / 协议 / Key 归属都已按继承规则解出。
/// 发请求、算成本、建客户端的代码只认这个,不用再关心两层结构。
/// </summary>
public sealed class ResolvedModel
{
    /// <summary>按继承规则把供应商与模型合并。</summary>
    public ResolvedModel(AiProvider provider, AiModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        Provider = provider;
        Config = model;
        Protocol = model.Protocol ?? provider.DefaultProtocol;
        BaseUrl = string.IsNullOrWhiteSpace(model.BaseUrlOverride) ? provider.BaseUrl : model.BaseUrlOverride;
        ApiKeyOwnerId = model.HasOwnApiKey ? model.Id : provider.Id;
    }

    /// <summary>所属供应商(原对象,不是拷贝)。</summary>
    public AiProvider Provider { get; }

    /// <summary>模型配置(原对象,不是拷贝)。</summary>
    public AiModelConfig Config { get; }

    /// <summary>模型配置 id(即 <see cref="AiSettings.ActiveModelId" /> 指向的那个)。</summary>
    public string Id => Config.Id;

    /// <summary>模型显示名(名称或模型 id)。</summary>
    public string Name => Config.DisplayName;

    /// <summary>供应商显示名。</summary>
    public string ProviderName => Provider.Name;

    /// <summary>解出的线协议。</summary>
    public ChatProtocol Protocol { get; }

    /// <summary>解出的基地址。</summary>
    public string BaseUrl { get; }

    /// <summary>模型 id。</summary>
    public string Model => Config.Model;

    /// <summary>该模型的 API Key 挂在谁名下(模型自己的 id 或供应商 id)—— 机密键就是 <c>apikey:&lt;这个&gt;</c>。</summary>
    public string ApiKeyOwnerId { get; }

    /// <inheritdoc cref="AiModelConfig.MaxTokens" />
    public int MaxTokens => Config.MaxTokens;

    /// <inheritdoc cref="AiModelConfig.MaxInputTokens" />
    public int MaxInputTokens => Config.MaxInputTokens;

    /// <inheritdoc cref="AiModelConfig.PromptCaching" />
    public bool PromptCaching => Config.PromptCaching;

    /// <inheritdoc cref="AiModelConfig.Temperature" />
    public float? Temperature => Config.Temperature;

    /// <inheritdoc cref="AiModelConfig.TopP" />
    public float? TopP => Config.TopP;

    /// <inheritdoc cref="AiModelConfig.StopSequences" />
    public string StopSequences => Config.StopSequences;

    /// <inheritdoc cref="AiModelConfig.SystemPrompt" />
    public string? SystemPrompt => Config.SystemPrompt;

    /// <inheritdoc cref="AiModelConfig.InputPricePerMillion" />
    public double InputPricePerMillion => Config.InputPricePerMillion;

    /// <inheritdoc cref="AiModelConfig.OutputPricePerMillion" />
    public double OutputPricePerMillion => Config.OutputPricePerMillion;

    /// <inheritdoc cref="AiModelConfig.CachedInputPricePerMillion" />
    public double CachedInputPricePerMillion => Config.CachedInputPricePerMillion;

    /// <inheritdoc cref="AiModelConfig.Reasoning" />
    public ReasoningLevel Reasoning => Config.Reasoning;
}

/// <summary>插件持久化设置(经 Storage 存 JSON)。</summary>
public sealed class AiSettings
{
    /// <summary>全部供应商(各自带着模型)。</summary>
    public List<AiProvider> Providers { get; set; } = [];

    /// <summary>当前选中的模型 id(<see cref="AiModelConfig.Id" />)。</summary>
    public string? ActiveModelId { get; set; }

    /// <summary>
    /// 旧版(供应商/模型不分层时代)的"当前接入 id"。仅用于迁移 —— 旧接入 id 原样成为模型 id,
    /// 所以直接搬到 <see cref="ActiveModelId" /> 即可。见 <c>LegacySettingsMigration</c>。
    /// </summary>
    /// <remarks>不要在新代码里用它;<see cref="Migrate" /> 跑完之后它就没有意义了。</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    /// 把旧版字段折算成新的。读设置时调用一次即可 ——
    /// 老用户升级上来不该发现自己的 Agent 模式(或选中的模型)被悄悄弄丢了。
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
        if (ActiveModelId is null && ActiveProviderId is not null)
        {
            ActiveModelId = ActiveProviderId;
        }
        ActiveProviderId = null;
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
    /// 关掉则退回"按窗口丢最早几条"。需要模型里填了"最大输入 tokens"才会生效。
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

    /// <summary>网络检索(web_search / web_fetch)。</summary>
    public WebSearchOptions WebSearch { get; set; } = new();

    /// <summary>把两层结构摊平:按供应商顺序、供应商内按模型顺序。</summary>
    public List<ResolvedModel> ResolveModels()
    {
        var list = new List<ResolvedModel>();
        foreach (AiProvider provider in Providers)
        {
            foreach (AiModelConfig model in provider.Models)
            {
                list.Add(new ResolvedModel(provider, model));
            }
        }
        return list;
    }

    /// <summary>按模型 id 找;找不到返回 null。</summary>
    public ResolvedModel? FindModel(string? modelId)
    {
        if (modelId is null)
        {
            return null;
        }
        foreach (AiProvider provider in Providers)
        {
            AiModelConfig? model = provider.Models.Find(m => m.Id == modelId);
            if (model is not null)
            {
                return new ResolvedModel(provider, model);
            }
        }
        return null;
    }

    /// <summary>找某模型所属的供应商;找不到返回 null。</summary>
    public AiProvider? FindProviderOfModel(string modelId)
        => Providers.Find(p => p.Models.Exists(m => m.Id == modelId));
}

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 一家端点的"脾气":它不收哪些东西。
/// </summary>
/// <remarks>
/// <para>
/// <b>这份数据的权威副本在目录里,不在用户的配置里。</b>订阅型的私有后端是标准协议的受限子集,
/// 多发一个它不认的字段就整轮 400,而且一次只告诉你一个 —— 于是这个清单会随着实测不断变长。
/// </para>
/// <para>
/// 一开始它是跟着供应商一起落盘的,结果是:<b>每加一条新规则,已经连上的用户都得重新登录一次
/// 才能拿到</b>(字段是后加的,老配置反序列化出来是默认值)。真机验收时就栽在这儿 ——
/// 同一个 400 修了两轮还在。现在改成发请求时按 <see cref="AiProvider.CatalogId" />
/// 从目录现读,新规则对所有人立即生效。
/// </para>
/// </remarks>
/// <param name="StoreResponses">允许服务端存下这一轮(OpenAI Responses 的 <c>store</c>)。</param>
/// <param name="AllowSystemMessages">收不收 <c>system</c> 角色的消息。</param>
/// <param name="UnsupportedParameters">不认的请求参数,每行一个线上字段名。</param>
public sealed record EndpointQuirks(
    bool StoreResponses = true,
    bool AllowSystemMessages = true,
    string UnsupportedParameters = "")
{
    /// <summary>标准端点:什么都收。</summary>
    public static readonly EndpointQuirks None = new();

    /// <summary>
    /// 这个供应商此刻该按哪份脾气发请求。
    /// </summary>
    /// <remarks>
    /// 目录里有就用目录的(<b>永远是最新的</b>);用户手工建的自定义供应商没有目录 id,
    /// 那就用它自己身上那份。
    /// </remarks>
    /// <param name="provider">供应商。</param>
    public static EndpointQuirks Of(AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ProviderCatalog.Find(provider.CatalogId) is { } entry
            ? entry.Quirks
            : new EndpointQuirks(provider.StoreResponses, provider.AllowSystemMessages,
                provider.UnsupportedParameters);
    }
}

/// <summary>
/// 目录里的一条:一家供应商的出厂配置 + 它支持哪种接入方式。
/// </summary>
/// <param name="Id">目录 id;落在 <see cref="AiProvider.CatalogId" /> 上,用来认出"这一家已经加过了"。</param>
/// <param name="Name">显示名。</param>
/// <param name="Models">副标题:示例模型。<b>只是出厂示例</b>,各家的型号更新得比本程序快,加完请按实际改。</param>
/// <param name="Monogram">
/// 列表左侧那枚方牌上的字。<b>刻意用字母而不是各家的商标</b>:商标是别人的资产,
/// 随程序分发要看授权脸色,而一枚字母牌在这套等宽、克制的界面里也并不吃亏。
/// </param>
/// <param name="Auth">出厂的接入方式(订阅登录 / 填 Key)。</param>
/// <remarks>见 <see cref="EndpointQuirks" />。</remarks>
/// <param name="Create">造一份出厂配置(含模型、协议、地址,订阅项还含 <see cref="AiProvider.OAuth" />)。</param>
public sealed record ProviderCatalogEntry(
    string Id,
    string Name,
    string Models,
    string Monogram,
    AuthMethod Auth,
    Func<AiProvider> Create)
{
    /// <summary>要用户自己填地址才用得起来(自建 / 自定义 / 按资源分配地址的云服务)。</summary>
    public bool NeedsBaseUrl { get; init; }

    /// <summary>要用户自己填 OAuth 参数(客户端 id、端点)才登得上。</summary>
    public bool NeedsOAuthSetup { get; init; }

    /// <summary>
    /// 去哪儿给 VelaShell 注册一个 OAuth 应用、拿到 <see cref="OAuthConfig.ClientId" />。
    /// 目录里客户端 id 还空着的条目,界面上就把这个地址显出来 ——
    /// 让人对着一个空框猜"这玩意儿哪儿来的"是最糟的设计。
    /// </summary>
    public string RegistrationUrl { get; init; } = "";

    /// <summary>
    /// 这一家在 <b>models.dev</b> 里的供应商 id;空 = 那边没有收录(本地自部署、自定义端点)。
    /// </summary>
    /// <remarks>
    /// 两边的 id 不总是一样(我们叫 <c>moonshot</c>,那边叫 <c>moonshotai</c>),所以单列一个字段
    /// 而不是拿目录 id 硬套 —— 套不上时是<b>静默</b>拉不到模型,最难查的那种。
    /// </remarks>
    public string ModelsDevId { get; init; } = "";

    /// <summary>
    /// 这一条依赖的是<b>没有公开承诺稳定</b>的接口。界面上会明确标出来。
    /// </summary>
    /// <remarks>
    /// 用户有权知道自己接的是哪一类东西:官方公开 API 坏了是事故,
    /// 而这类接口本来就可能在任何一天换掉请求形状 —— 到时候"AI 突然不能用了"
    /// 不该让人以为是本程序的 bug。
    /// </remarks>
    public bool Experimental { get; init; }

    /// <summary>这一家不收哪些东西(权威副本在这儿,不在用户配置里)。</summary>
    public EndpointQuirks Quirks { get; init; } = EndpointQuirks.None;

    /// <summary>这一条走的是订阅登录。</summary>
    public bool IsSubscription => Auth == AuthMethod.Subscription;

    /// <summary>
    /// 按这一条造一个供应商,并把目录 id 打上去 —— 界面据此知道"这一家已经加过了",
    /// 以及该显示登录状态还是 API Key 输入框。
    /// </summary>
    public AiProvider CreateProvider()
    {
        AiProvider provider = Create();
        provider.CatalogId = Id;
        // 也拷一份到供应商身上:设置页要显示、自定义供应商要能改。
        // 但发请求时以目录那份为准(见 EndpointQuirks.Of)—— 落盘的这份只会越来越旧。
        provider.StoreResponses = Quirks.StoreResponses;
        provider.AllowSystemMessages = Quirks.AllowSystemMessages;
        provider.UnsupportedParameters = Quirks.UnsupportedParameters;
        return provider;
    }
}

/// <summary>
/// 内置的供应商目录 —— 设置页「新增供应商」打开的那一页就是它。
/// </summary>
/// <remarks>
/// <para>
/// <b>两类接入并列</b>:多数家是"自己去后台开一把 API Key 填进来";少数家把
/// <b>第三方应用的 OAuth 登录</b>作为公开能力(OpenRouter 就是),那种直接点「登录」,
/// 浏览器里认证一次,凭据自动落进机密存储,用户从头到尾不用见到 Key。
/// </para>
/// <para>
/// <b>目录是数据,不是代码</b>。想接一家这里没有的,不必改任何逻辑:
/// 走「自定义(OAuth 登录)」把授权/令牌端点、客户端 id、scope 填进去即可 ——
/// 授权码 + PKCE 与设备码两套标准流程本程序都实现了。
/// 也正因为如此,本目录<b>只收各家公开面向第三方应用的登录方式</b>:
/// 冒用别家官方客户端的 client id 去蹭订阅,既不稳(对方一改就全断)也不该由本程序代做决定。
/// </para>
/// </remarks>
public static class ProviderCatalog
{
    /// <summary>
    /// VelaShell 在各家注册的 OAuth 应用的客户端 id。
    /// </summary>
    /// <remarks>
    /// <b>拿到一个就填一个,填完那一行当场变成"点一下即登"</b>,不需要改任何逻辑。
    /// 各家的申请入口写在对应条目的 <see cref="ProviderCatalogEntry.RegistrationUrl" /> 上,
    /// 界面也会把它显出来。
    /// <para>
    /// 申请时按<b>公共客户端</b>(native / desktop,不要密钥)登记,回调地址填
    /// <c>http://127.0.0.1/callback</c> 与 <c>http://localhost/callback</c> 两条 ——
    /// 遵循 RFC 8252 §7.3 的服务端(Hugging Face 明确支持)只比对 scheme/host/path,
    /// 端口在请求时任意,正好对上本程序"每次随机取一个空闲环回端口"的做法。
    /// </para>
    /// </remarks>
    private static class ClientIds
    {
        /// <summary>https://huggingface.co/settings/applications/new(公共应用,免密钥)。</summary>
        public const string HuggingFace = "";

        /// <summary>
        /// Codex CLI 的公共客户端 id。
        /// </summary>
        /// <remarks>
        /// <b>这一条与上面几条性质不同</b>:它不是 VelaShell 自己注册来的,而是 Codex 命令行工具
        /// 公开使用的那个标识。用它意味着本程序以 Codex 客户端的身份去换取用户的 ChatGPT 订阅权益 ——
        /// 对方一旦更换 id 或加一道客户端校验,这条路就整体失效,且其适用条款需使用者自行确认。
        /// 目录条目因此标了 <see cref="ProviderCatalogEntry.Experimental" />,界面上也如实标出。
        /// </remarks>
        public const string OpenAiCodex = "app_EMoamEEZ73f0CkXaXp7hrann";

        /// <summary>Claude Code 的公共客户端 id。性质同 <see cref="OpenAiCodex" />(借的是别家官方客户端的身份)。</summary>
        public const string AnthropicClaude = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

        /// <summary>GitHub 官方 Copilot 编辑器插件的客户端 id。性质同上。</summary>
        public const string GitHubCopilot = "Iv1.b507a08c87ecfe98";

        /// <summary>Grok CLI 的公共客户端 id。性质同 <see cref="OpenAiCodex" />。</summary>
        /// <remarks>
        /// xAI 在 <c>auth.x.ai/.well-known/openid-configuration</c> 里公开声明了
        /// <c>device_authorization_endpoint</c> 与对应的 <c>device_code</c> 授权类型 ——
        /// 也就是说设备码这条路是它自己宣告支持的,不是猜出来的。
        /// </remarks>
        public const string XaiGrok = "b1a00492-073a-47ea-816f-4c329264a828";

        /// <summary>
        /// DigitalOcean Gradient 的公共 OAuth 客户端 id(隐式流,见 <see cref="OAuthFlow.ImplicitFragment" />)。
        /// </summary>
        public const string DigitalOcean =
            "b1a6c5158156caac821fd1b30253ca8acb52454a48fa744420e41889cb589f82";

        /// <summary>Google Cloud Console → API 和服务 → 凭据 → OAuth 客户端 ID(类型选"桌面应用")。</summary>
        public const string Google = "";
    }

    /// <summary>
    /// 本目录的 id → <b>models.dev</b> 里的供应商 id。
    /// </summary>
    /// <remarks>
    /// 集中放一处,而不是在每条记录里各插一行:两边命名不一致的有好几家
    /// (<c>moonshot</c>→<c>moonshotai</c>、<c>zhipu</c>→<c>zhipuai</c>、<c>qwen</c>→<c>alibaba</c>、
    /// <c>together</c>→<c>togetherai</c>、<c>fireworks</c>→<c>fireworks-ai</c>),
    /// 散在各处对不上时是<b>静默</b>拉不到模型,最难查的那一类。
    /// <para>
    /// 没列进来的是那边确实没有的:本地自部署(Ollama)与两条自定义端点。
    /// <c>openai-codex</c> 映到 <c>openai</c> —— ChatGPT 订阅能用的就是 OpenAI 那批模型。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> ModelsDevIds = new(StringComparer.Ordinal)
    {
        ["openai"] = "openai",
        ["openai-codex"] = "openai",
        ["anthropic"] = "anthropic",
        ["anthropic-claude"] = "anthropic",
        ["github-copilot"] = "github-copilot",
        ["xai"] = "xai",
        ["xai-grok"] = "xai",
        ["digitalocean"] = "digitalocean",
        ["google"] = "google",
        ["deepseek"] = "deepseek",
        ["moonshot"] = "moonshotai",
        ["zhipu"] = "zhipuai",
        ["qwen"] = "alibaba",
        ["together"] = "togetherai",
        ["fireworks"] = "fireworks-ai",
        ["groq"] = "groq",
        ["mistral"] = "mistral",
        ["cerebras"] = "cerebras",
        ["deepinfra"] = "deepinfra",
        ["nvidia"] = "nvidia",
        ["perplexity"] = "perplexity",
        ["cohere"] = "cohere",
        ["baseten"] = "baseten",
        ["upstage"] = "upstage",
        ["minimax"] = "minimax",
        ["zenmux"] = "zenmux",
        ["llmgateway"] = "llmgateway",
        ["openrouter"] = "openrouter",
        ["huggingface"] = "huggingface",
        ["azure-openai"] = "azure"
    };

    /// <summary>出厂条目本身;对外的 <see cref="All" /> 是它贴上 models.dev id 之后的样子。</summary>
    private static IReadOnlyList<ProviderCatalogEntry> Entries { get; } =
    [
        // ── 订阅登录 ────────────────────────────────────────────────
        new("openrouter", "OpenRouter", "Multiple models · e.g. Claude · GPT · Gemini · Llama", "OR",
            AuthMethod.Subscription, () => Subscription(
                "OpenRouter", "https://openrouter.ai/api/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "openai/gpt-5", MaxInputTokens = 200000 },
                new OAuthConfig
                {
                    // OpenRouter 公开支持第三方应用用 PKCE 接自家账号,换回来的是一把普通 Key
                    Flow = OAuthFlow.OpenRouterPkce,
                    AuthorizationUrl = "https://openrouter.ai/auth",
                    TokenUrl = "https://openrouter.ai/api/v1/auth/keys",
                    Credential = OAuthCredential.ApiKey
                })),

        new("openai-codex", "OpenAI Codex (ChatGPT 订阅)", "e.g. gpt-5.3-codex · gpt-5.3-codex-spark", "CX",
            AuthMethod.Subscription, () => Subscription(
                "OpenAI Codex", "https://chatgpt.com/backend-api/codex", ChatProtocol.OpenAiResponses,
                new AiModelConfig { Model = "gpt-5.3-codex", MaxInputTokens = 400000, MaxTokens = 128000 },
                new OAuthConfig
                {
                    Flow = OAuthFlow.AuthorizationCodePkce,
                    AuthorizationUrl = "https://auth.openai.com/oauth/authorize",
                    TokenUrl = "https://auth.openai.com/oauth/token",
                    ClientId = ClientIds.OpenAiCodex,
                    Scopes = "openid profile email offline_access",
                    // 回调地址必须与 Codex 客户端注册的那条<b>逐字节一致</b>:
                    // 固定端口 1455,而且主机名是 localhost 不是 127.0.0.1(两者不通用)
                    RedirectPort = 1455,
                    RedirectPath = "/auth/callback",
                    RedirectHost = "localhost",
                    ExtraAuthorizeParams = "id_token_add_organizations=true\ncodex_cli_simplified_flow=true",
                    // 订阅端点光有 Bearer 还不够,得同时说清楚算在哪个 ChatGPT 账号头上
                    AccountIdClaim = "https://api.openai.com/auth/chatgpt_account_id",
                    ExtraHeaders = "chatgpt-account-id: {account_id}\nOpenAI-Beta: responses=experimental",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            Experimental = true,
            // ChatGPT 那个后端是 Responses 的受限子集,多发一个字段就整轮 400,一次只报一个。
            // 实测撞过的顺序:store → system 角色 → max_output_tokens。
            // 其余几个采样参数本插件默认就不发,一并列上是为了别再多来几轮试错
            // (Codex 官方客户端同样不发它们)。
            Quirks = new EndpointQuirks(
                StoreResponses: false,
                AllowSystemMessages: false,
                UnsupportedParameters:
                "max_output_tokens\ntemperature\ntop_p\nstop\nfrequency_penalty\npresence_penalty\nseed")
        },

        new("anthropic-claude", "Anthropic Claude (Pro/Max 订阅)", "e.g. claude-opus-5 · claude-sonnet-5", "CL",
            AuthMethod.Subscription, () => Subscription(
                "Claude 订阅", "https://api.anthropic.com", ChatProtocol.AnthropicMessages,
                new AiModelConfig { Model = "claude-opus-5", MaxInputTokens = 200000 },
                new OAuthConfig
                {
                    Flow = OAuthFlow.AuthorizationCodePkce,
                    AuthorizationUrl = "https://claude.ai/oauth/authorize",
                    TokenUrl = "https://platform.claude.com/v1/oauth/token",
                    ClientId = ClientIds.AnthropicClaude,
                    Scopes = "org:create_api_key user:profile user:inference",
                    // 回调要与 Claude Code 注册的那条逐字节一致:端口固定 53692,主机名是 localhost
                    RedirectPort = 53692,
                    RedirectPath = "/callback",
                    RedirectHost = "localhost",
                    // 订阅令牌走 Authorization: Bearer(不是 x-api-key),另需这个 beta 标记
                    ExtraHeaders = "anthropic-beta: oauth-2025-04-20",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            Experimental = true
        },

        new("github-copilot", "GitHub Copilot", "e.g. gpt-5.3-codex · claude-sonnet-5", "GH",
            AuthMethod.Subscription, () => Subscription(
                "GitHub Copilot", "https://api.individual.githubcopilot.com",
                ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "gpt-5.3-codex", MaxInputTokens = 128000 },
                new OAuthConfig
                {
                    // 两段式:先按 RFC 8628 拿 GitHub 的长期 token,再换一枚短命的 Copilot 会话令牌
                    Flow = OAuthFlow.GitHubCopilotDevice,
                    DeviceCodeUrl = "https://github.com/login/device/code",
                    TokenUrl = "https://github.com/login/oauth/access_token",
                    ExchangeUrl = "https://api.github.com/copilot_internal/v2/token",
                    // 这个交换端点会校验调用方是不是一个编辑器:只带 Authorization 会被 403,
                    // 而报错正文里一个字都不提缺了什么(实测)。
                    ExchangeHeaders =
                        "Editor-Version: VelaShell/1.0\n"
                        + "Editor-Plugin-Version: velashell-ai/0.4.0\n"
                        + "User-Agent: VelaShell/1.0\n"
                        + "Copilot-Integration-Id: vscode-chat",
                    ClientId = ClientIds.GitHubCopilot,
                    Scopes = "read:user",
                    // 交换响应里会带 endpoints.api —— 企业账户与个人账户不是同一个地址,
                    // 那时以它为准(见 ProviderCredential.BaseUrl)
                    ExtraHeaders = "Copilot-Integration-Id: vscode-chat\nEditor-Version: VelaShell/1.0",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            Experimental = true
        },

        new("xai-grok", "xAI Grok (SuperGrok 订阅)", "e.g. grok-4.6 · grok-code-fast", "XG",
            AuthMethod.Subscription, () => Subscription(
                "xAI Grok", "https://api.x.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "grok-4.6", MaxInputTokens = 256000 },
                new OAuthConfig
                {
                    // 只走设备码:xAI 在自己的 openid-configuration 里公开声明了
                    // device_authorization_endpoint 与 device_code 授权类型,
                    // 而授权码那一路它没有登记任何第三方可用的回调地址
                    Flow = OAuthFlow.DeviceCode,
                    DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code",
                    TokenUrl = "https://auth.x.ai/oauth2/token",
                    ClientId = ClientIds.XaiGrok,
                    Scopes = "openid profile email offline_access grok-cli:access api:access",
                    // 设备码请求里带上来路,便于对方区分是哪个客户端在用
                    ExtraAuthorizeParams = "referrer=velashell",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            Experimental = true
        },

        new("digitalocean", "DigitalOcean Gradient", "e.g. Llama · Qwen · DeepSeek(托管推理)", "DO",
            AuthMethod.Subscription, () => Subscription(
                "DigitalOcean", "https://inference.do-ai.run/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "llama3.3-70b-instruct", MaxInputTokens = 128000 },
                new OAuthConfig
                {
                    // 隐式流:令牌落在回调地址的 #fragment 里,得靠一页 HTML 把它回传
                    Flow = OAuthFlow.ImplicitFragment,
                    AuthorizationUrl = "https://cloud.digitalocean.com/v1/oauth/authorize",
                    ClientId = ClientIds.DigitalOcean,
                    Scopes = "genai:read inference:query",
                    // 回调必须与登记的那条完全一致,所以端口固定
                    RedirectPort = 43920,
                    RedirectPath = "/callback",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            Experimental = true
        },

        new("huggingface", "Hugging Face", "Inference Providers · e.g. DeepSeek · Qwen · Llama", "HF",
            AuthMethod.Subscription, () => Subscription(
                "Hugging Face", "https://router.huggingface.co/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 },
                new OAuthConfig
                {
                    // 官方文档明确支持"公共应用(无密钥)+ PKCE + 环回任意端口",正好是本程序的形态
                    Flow = OAuthFlow.AuthorizationCodePkce,
                    AuthorizationUrl = "https://huggingface.co/oauth/authorize",
                    TokenUrl = "https://huggingface.co/oauth/token",
                    DeviceCodeUrl = "https://huggingface.co/oauth/device",
                    ClientId = ClientIds.HuggingFace,
                    // inference-api = 代表用户向 Inference Providers 发推理请求
                    Scopes = "openid profile inference-api",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            RegistrationUrl = "https://huggingface.co/settings/applications/new"
        },

        new("azure-openai", "Azure OpenAI", "Your deployments · e.g. gpt-4o · o4-mini", "AZ",
            AuthMethod.Subscription, () => Subscription(
                "Azure OpenAI", "https://<resource>.openai.azure.com/openai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "gpt-4o", MaxInputTokens = 128000 },
                new OAuthConfig
                {
                    // Entra ID 的设备码流程:把 common 换成自己的租户 id,client id 填自己注册的应用。
                    // 用设备码而不是环回:企业环境里回调地址往往要走审批,而设备码只要一个公共客户端。
                    Flow = OAuthFlow.DeviceCode,
                    DeviceCodeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode",
                    TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                    Scopes = "https://cognitiveservices.azure.com/.default offline_access",
                    Credential = OAuthCredential.AccessToken
                }))
        {
            NeedsBaseUrl = true,
            NeedsOAuthSetup = true,
            RegistrationUrl = "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"
        },

        new("custom-oauth", "Custom (OAuth sign-in)", "Any OpenAI/Anthropic-compatible endpoint behind OAuth", "OA",
            AuthMethod.Subscription, () => Subscription(
                "Custom (OAuth)", "", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig(),
                new OAuthConfig { Flow = OAuthFlow.AuthorizationCodePkce, Credential = OAuthCredential.AccessToken }))
        {
            NeedsBaseUrl = true,
            NeedsOAuthSetup = true
        },

        // ── 填 API Key ──────────────────────────────────────────────
        new("openai", "OpenAI", "e.g. gpt-5 · gpt-5-mini · o4-mini", "AI", AuthMethod.ApiKey,
            () => Key("OpenAI", "https://api.openai.com/v1", ChatProtocol.OpenAiResponses,
                new AiModelConfig { Model = "gpt-5", MaxInputTokens = 400000 })),

        new("anthropic", "Anthropic Claude", "e.g. claude-opus-5 · claude-sonnet-5", "AN", AuthMethod.ApiKey,
            () => Key("Anthropic", "https://api.anthropic.com", ChatProtocol.AnthropicMessages,
                new AiModelConfig { Model = "claude-opus-5", MaxInputTokens = 200000 })),

        new("xai", "xAI Grok", "e.g. grok-4.6 · grok-4.5 · grok-4.3", "XA", AuthMethod.ApiKey,
            () => Key("xAI", "https://api.x.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "grok-4.6", MaxInputTokens = 256000 })),

        new("google", "Google Gemini", "e.g. gemini-2.5-pro · gemini-2.5-flash", "GE", AuthMethod.ApiKey,
            () => Key("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai",
                ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "gemini-2.5-pro", MaxInputTokens = 1000000 })),

        new("deepseek", "DeepSeek", "e.g. deepseek-v4-pro · deepseek-v4-flash", "DS", AuthMethod.ApiKey,
            () => Key("DeepSeek", "https://api.deepseek.com/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-v4-pro", MaxInputTokens = 64000 })),

        new("moonshot", "Moonshot Kimi", "e.g. kimi-k3 · kimi-k2.7-code", "KI", AuthMethod.ApiKey,
            () => Key("Moonshot", "https://api.moonshot.cn/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "kimi-k3", MaxInputTokens = 128000 })),

        new("zhipu", "Z.AI / 智谱 GLM", "e.g. glm-5.3 · glm-4.7", "GL", AuthMethod.ApiKey,
            () => Key("Z.AI", "https://open.bigmodel.cn/api/paas/v4", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "glm-5.3", MaxInputTokens = 128000 })),

        new("qwen", "阿里云百炼 Qwen", "e.g. qwen-max · qwen-plus", "QW", AuthMethod.ApiKey,
            () => Key("Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1",
                ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "qwen-max", MaxInputTokens = 128000 })),

        new("together", "Together AI", "e.g. deepseek-ai/DeepSeek-V3 · Qwen/Qwen2.5-72B-Instruct", "TG",
            AuthMethod.ApiKey,
            () => Key("Together AI", "https://api.together.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("fireworks", "Fireworks AI", "e.g. deepseek-v4-pro · kimi-k3 · glm-5.3", "FW", AuthMethod.ApiKey,
            () => Key("Fireworks AI", "https://api.fireworks.ai/inference/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig
                {
                    Model = "accounts/fireworks/models/deepseek-v4-pro-0813",
                    MaxInputTokens = 128000
                })),

        new("groq", "Groq", "e.g. llama-3.3-70b-versatile", "GQ", AuthMethod.ApiKey,
            () => Key("Groq", "https://api.groq.com/openai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "llama-3.3-70b-versatile", MaxInputTokens = 128000 })),

        new("mistral", "Mistral AI", "e.g. mistral-large-latest", "MI", AuthMethod.ApiKey,
            () => Key("Mistral AI", "https://api.mistral.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "mistral-large-latest", MaxInputTokens = 128000 })),

        new("cerebras", "Cerebras", "e.g. qwen-3-coder-480b · gpt-oss-120b", "CB", AuthMethod.ApiKey,
            () => Key("Cerebras", "https://api.cerebras.ai/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "qwen-3-coder-480b", MaxInputTokens = 128000 })),

        new("deepinfra", "Deep Infra", "e.g. deepseek-ai/DeepSeek-V3 · Qwen/Qwen3-235B", "DI", AuthMethod.ApiKey,
            () => Key("Deep Infra", "https://api.deepinfra.com/v1/openai", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("nvidia", "NVIDIA NIM", "e.g. deepseek-ai/deepseek-r1 · qwen/qwen3-coder", "NV", AuthMethod.ApiKey,
            () => Key("NVIDIA NIM", "https://integrate.api.nvidia.com/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/deepseek-r1", MaxInputTokens = 128000 })),

        new("perplexity", "Perplexity", "e.g. sonar-pro · sonar-reasoning-pro", "PX", AuthMethod.ApiKey,
            () => Key("Perplexity", "https://api.perplexity.ai", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "sonar-pro", MaxInputTokens = 200000 })),

        new("cohere", "Cohere", "e.g. command-a-03-2025 · command-r-plus", "CO", AuthMethod.ApiKey,
            () => Key("Cohere", "https://api.cohere.com/compatibility/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "command-a-03-2025", MaxInputTokens = 256000 })),

        new("baseten", "Baseten", "e.g. deepseek-ai/DeepSeek-V3 · moonshotai/Kimi-K2", "BT", AuthMethod.ApiKey,
            () => Key("Baseten", "https://inference.baseten.co/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "deepseek-ai/DeepSeek-V3", MaxInputTokens = 128000 })),

        new("upstage", "Upstage Solar", "e.g. solar-pro2", "UP", AuthMethod.ApiKey,
            () => Key("Upstage", "https://api.upstage.ai/v1/solar", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "solar-pro2", MaxInputTokens = 64000 })),

        new("minimax", "MiniMax", "e.g. MiniMax-M2", "MM", AuthMethod.ApiKey,
            // 这一家给的是 Anthropic 兼容端点,不是 OpenAI 的 —— 协议别跟着邻居抄
            () => Key("MiniMax", "https://api.minimax.io/anthropic/v1", ChatProtocol.AnthropicMessages,
                new AiModelConfig { Model = "MiniMax-M2", MaxInputTokens = 200000 })),

        new("zenmux", "ZenMux", "Multiple models behind one key", "ZM", AuthMethod.ApiKey,
            () => Key("ZenMux", "https://zenmux.ai/api/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "anthropic/claude-sonnet-4.5", MaxInputTokens = 200000 })),

        new("llmgateway", "LLM Gateway", "Multiple models behind one key", "LG", AuthMethod.ApiKey,
            () => Key("LLM Gateway", "https://api.llmgateway.io/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "openai/gpt-5", MaxInputTokens = 200000 })),

        new("ollama", "Ollama (local)", "Locally served models · no key needed", "OL", AuthMethod.ApiKey,
            () => Key("Ollama", "http://localhost:11434/v1", ChatProtocol.OpenAiChatCompletions,
                new AiModelConfig { Model = "llama3.1" })),

        new("custom-openai", "Custom (OpenAI compatible)", "Any relay or self-hosted OpenAI-compatible endpoint", "CU",
            AuthMethod.ApiKey,
            () => Key("Custom", "", ChatProtocol.OpenAiChatCompletions, new AiModelConfig()))
        {
            NeedsBaseUrl = true
        },

        new("custom-anthropic", "Custom (Anthropic compatible)", "Any relay speaking the Anthropic Messages API", "CA",
            AuthMethod.ApiKey,
            () => Key("Custom (Anthropic)", "", ChatProtocol.AnthropicMessages, new AiModelConfig()))
        {
            NeedsBaseUrl = true
        }
    ];

    /// <summary>
    /// 全部条目,按"能登录的在前"排;models.dev 的 id 由 <see cref="ModelsDevIds" /> 统一贴上。
    /// </summary>
    /// <remarks>
    /// <b>必须声明在 <see cref="Entries" /> 之后</b>:静态初始化按声明顺序跑,
    /// 摆在前面的话这里读到的是 null,首次访问目录就是一个 <c>TypeInitializationException</c>。
    /// </remarks>
    public static IReadOnlyList<ProviderCatalogEntry> All { get; } =
        [.. Entries.Select(entry => ModelsDevIds.TryGetValue(entry.Id, out string? id)
            ? entry with { ModelsDevId = id }
            : entry)];

    /// <summary>按目录 id 找;没有返回 null(用户手工建的供应商没有目录 id)。</summary>
    public static ProviderCatalogEntry? Find(string? catalogId)
        => catalogId is null ? null : All.FirstOrDefault(e => e.Id == catalogId);

    /// <summary>「自定义(OpenAI 兼容)」那一条 —— 手工新建供应商时的兜底出厂值。</summary>
    public static ProviderCatalogEntry Custom => All.First(e => e.Id == "custom-openai");

    private static AiProvider Key(string name, string baseUrl, ChatProtocol protocol, AiModelConfig model)
        => new()
        {
            Name = name,
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Auth = AuthMethod.ApiKey,
            Models = [model]
        };

    private static AiProvider Subscription(string name, string baseUrl, ChatProtocol protocol,
        AiModelConfig model, OAuthConfig oauth)
        => new()
        {
            Name = name,
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Auth = AuthMethod.Subscription,
            OAuth = oauth,
            Models = [model]
        };
}

using System.Text.Json.Serialization;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>供应商拿什么去鉴权。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthMethod
{
    /// <summary>用户自己填一把 API Key(本插件一直以来唯一的方式)。</summary>
    ApiKey,

    /// <summary>
    /// 订阅登录:在浏览器里登供应商的账号,换回凭据存进机密,不让用户碰 Key。
    /// 细节见 <see cref="AiProvider.OAuth" />。
    /// </summary>
    Subscription
}

/// <summary>订阅登录走哪套授权流程。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuthFlow
{
    /// <summary>
    /// 标准授权码 + PKCE(RFC 7636):本机起一个环回监听端口收 <c>code</c>,再拿它换 token。
    /// 桌面应用的标准做法 —— 没有客户端密钥可藏,PKCE 就是防截获的那道锁。
    /// </summary>
    AuthorizationCodePkce,

    /// <summary>
    /// 设备授权码(RFC 8628):显示一段用户码,让人在浏览器里输,本机轮询换 token。
    /// 不需要环回端口,因此在"浏览器和程序不在同一台机器上"(远程桌面 / SSH 转发)时也能用。
    /// </summary>
    DeviceCode,

    /// <summary>
    /// OpenRouter 的 PKCE 变体。三处与标准不同,所以单列一档而不是堆布尔开关:
    /// 回调参数叫 <c>callback_url</c> 不是 <c>redirect_uri</c>、请求里没有 <c>client_id</c> 与
    /// <c>response_type</c>、换回来的是一把普通 API Key(响应字段 <c>key</c>)而不是 access_token。
    /// </summary>
    OpenRouterPkce,

    /// <summary>
    /// GitHub Copilot 的<b>两段式</b>设备码。先按 RFC 8628 拿 GitHub 的长期 token,
    /// 再拿它去换一枚短命的 Copilot 令牌(还会顺带告诉你该打哪个地址)。
    /// </summary>
    /// <remarks>
    /// 之所以单列:标准流程里"刷新"是拿 refresh token 走 <c>refresh_token</c> 授权,
    /// 而这一路的刷新是<b>重新做一次交换</b> —— GitHub token 本身不过期,过期的是换来的那枚。
    /// </remarks>
    GitHubCopilotDevice,

    /// <summary>
    /// 隐式流(<c>response_type=token</c>):令牌直接放在回调地址的 <b>#fragment</b> 里。
    /// </summary>
    /// <remarks>
    /// 片段<b>不会随请求发到服务端</b>,所以本机监听根本读不到它 —— 必须先回一页 HTML,
    /// 让浏览器自己把 <c>location.hash</c> 再回传一次。DigitalOcean Gradient 走的就是这一路。
    /// <para>
    /// 隐式流在 OAuth 2.1 里已被劝退,这里实现它<b>只为接上还在用它的服务</b>,
    /// 不作为新接入的推荐:令牌进过浏览器地址栏,而且没有 refresh token,过期只能重登。
    /// </para>
    /// </remarks>
    ImplicitFragment
}

/// <summary>登录换回来的东西是什么 —— 决定它存到哪、请求头怎么带。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuthCredential
{
    /// <summary>
    /// 短期 access token:存 <c>oauth:&lt;供应商 id&gt;</c>,临近过期用 refresh token 换新的,
    /// 请求走 <c>Authorization: Bearer</c>。
    /// </summary>
    AccessToken,

    /// <summary>
    /// 长期 API Key(OpenRouter 这类"登录一次给你一把 Key"的):存成普通 API Key
    /// (<c>apikey:&lt;供应商 id&gt;</c>),之后与手填的 Key 走同一条路,不需要刷新。
    /// </summary>
    ApiKey
}

/// <summary>
/// 一个供应商的订阅登录参数。目录里的内置项自带这份配置;"自定义 OAuth"由用户自己填。
/// </summary>
/// <remarks>
/// <b>刻意做成数据而不是代码</b>:各家的端点、client id、scope 只是配置,
/// 写死在 switch 里的话每加一家都要改 <see cref="Auth.OAuthClient" />。
/// 现在加一家 = 往 <see cref="ProviderCatalog" /> 里加一条记录,或者用户在界面上填一份。
/// </remarks>
public sealed class OAuthConfig
{
    /// <summary>走哪套流程。</summary>
    public OAuthFlow Flow { get; set; } = OAuthFlow.AuthorizationCodePkce;

    /// <summary>授权页地址(用户在浏览器里看到的那一页)。设备码流程忽略。</summary>
    public string AuthorizationUrl { get; set; } = "";

    /// <summary>换 / 刷新 token 的地址。<see cref="OAuthFlow.OpenRouterPkce" /> 下是"换 Key"的地址。</summary>
    public string TokenUrl { get; set; } = "";

    /// <summary>设备码申请地址;仅设备码类流程用。</summary>
    public string DeviceCodeUrl { get; set; } = "";

    /// <summary>
    /// 第二段交换的地址(仅 <see cref="OAuthFlow.GitHubCopilotDevice" /> 用):
    /// 拿第一段换来的长期 token 去换真正能发推理请求的那枚短命令牌。
    /// </summary>
    public string ExchangeUrl { get; set; } = "";

    /// <summary>
    /// 第二段交换要带的头,每行一条 <c>名: 值</c>。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="ExtraHeaders" /> 不是一回事</b>:那份是发推理请求时带的。
    /// 交换端点往往另有要求 —— Copilot 那个会<b>校验调用方是不是一个编辑器</b>,
    /// 不带版本标识就直接 403(实测),而那时的报错里一个字都不会提到缺了什么头。
    /// </remarks>
    public string ExchangeHeaders { get; set; } = "";

    /// <summary>客户端 id。<see cref="OAuthFlow.OpenRouterPkce" /> 不需要,留空即可。</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// 客户端密钥。桌面程序是"公共客户端",<b>通常留空</b> —— 装在用户机器上的密钥不是密钥。
    /// 只为少数强制要求它的自建服务留这个口子。
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>申请的权限范围,空格分隔。</summary>
    public string Scopes { get; set; } = "";

    /// <summary>
    /// 环回回调固定端口;0 = 每次随机取一个空闲端口。
    /// 有些供应商要求回调地址与注册时<b>逐字节一致</b>,那种情况必须固定端口。
    /// </summary>
    public int RedirectPort { get; set; }

    /// <summary>环回回调路径(要与注册的回调地址一致)。</summary>
    public string RedirectPath { get; set; } = "/callback";

    /// <summary>
    /// 环回回调用哪个主机名写进 <c>redirect_uri</c>。
    /// </summary>
    /// <remarks>
    /// <b><c>localhost</c> 与 <c>127.0.0.1</c> 在回调地址校验里不是一回事</b> ——
    /// 严格比对的服务端(注册了哪个就只认哪个)会把另一个直接判为 <c>invalid_redirect_uri</c>。
    /// 监听端始终只绑环回网卡,这里改的只是写进请求里的那个名字。
    /// </remarks>
    public string RedirectHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// 每次请求都要额外带上的头,每行一条 <c>名: 值</c>。
    /// 值里可以写 <c>{account_id}</c>,发请求时用登录换回来的账号 id 替换
    /// (见 <see cref="OAuthTokens.AccountId" />)。
    /// </summary>
    public string ExtraHeaders { get; set; } = "";

    /// <summary>
    /// 从 <c>id_token</c> 里取账号 id 的 claim 路径,<c>/</c> 分隔;空 = 不取。
    /// 例:<c>https://api.openai.com/auth/chatgpt_account_id</c>。
    /// </summary>
    /// <remarks>
    /// 只做 base64url 解码读一个字段,<b>不验签</b> —— 这枚令牌是我们自己刚从 TLS 通道上
    /// 换回来的,读它只为知道把请求路由到哪个账号,不作任何信任判断。
    /// </remarks>
    public string AccountIdClaim { get; set; } = "";

    /// <summary>授权地址上额外挂的查询参数,每行一条 <c>名=值</c>(如 <c>prompt=consent</c>)。</summary>
    public string ExtraAuthorizeParams { get; set; } = "";

    /// <summary>登录换回来的凭据种类。</summary>
    public OAuthCredential Credential { get; set; } = OAuthCredential.AccessToken;

    /// <summary>逐字段拷贝(编辑表单要改副本,取消时不能污染已保存的那份)。</summary>
    public OAuthConfig Clone() => new()
    {
        Flow = Flow,
        AuthorizationUrl = AuthorizationUrl,
        TokenUrl = TokenUrl,
        DeviceCodeUrl = DeviceCodeUrl,
        ExchangeUrl = ExchangeUrl,
        ExchangeHeaders = ExchangeHeaders,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Scopes = Scopes,
        RedirectPort = RedirectPort,
        RedirectPath = RedirectPath,
        RedirectHost = RedirectHost,
        ExtraHeaders = ExtraHeaders,
        AccountIdClaim = AccountIdClaim,
        ExtraAuthorizeParams = ExtraAuthorizeParams,
        Credential = Credential
    };
}

/// <summary>
/// 一次登录换回来的令牌组。整体序列化成 JSON 存进机密存储(键 <c>oauth:&lt;供应商 id&gt;</c>)——
/// <c>ISecretsApi</c> 只收字符串,而这几样东西必须一起加密、一起失效。
/// </summary>
public sealed class OAuthTokens
{
    /// <summary>访问令牌。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>刷新令牌;为空表示这家不给刷新(过期只能重登)。</summary>
    public string? RefreshToken { get; set; }

    /// <summary>过期时刻(UTC);null = 服务端没说,当作不过期。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>服务端实际批下来的权限范围(可能比申请的少)。</summary>
    public string? Scope { get; set; }

    /// <summary>界面上显示"以谁的身份登录的";拿不到就留空,不猜。</summary>
    public string? Account { get; set; }

    /// <summary>
    /// 账号 id(从 <c>id_token</c> 的 claim 里取,见 <see cref="OAuthConfig.AccountIdClaim" />)。
    /// 有些订阅型端点光有令牌还不够,得同时告诉它"算在哪个账号头上"。
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// 这一家要求把请求打到哪儿(仅少数供应商按账户下发,如 Copilot 的企业端点);
    /// 空 = 用供应商配置里的基地址。
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>拿到这组令牌的时刻(UTC),只用于界面展示。</summary>
    public DateTimeOffset ObtainedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 该换新的了吗。留 <b>2 分钟</b>余量:一次请求从建客户端到真正发出去有间隔,
    /// 卡着秒数用一个 1 秒后过期的 token 只会换来一个 401。
    /// </summary>
    [JsonIgnore]
    public bool NeedsRefresh => ExpiresAt is { } expiry && expiry - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(2);

    /// <summary>已经过期(拿它发请求必定 401)。</summary>
    [JsonIgnore]
    public bool Expired => ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;
}

/// <summary>
/// 发请求时用的凭据:值 + 它该以什么身份出现在请求里。
/// </summary>
/// <param name="Value">Key 或 access token;null / 空 = 这家不需要鉴权(如本地 Ollama)。</param>
/// <param name="IsBearerToken">
/// true = 短期 access token,走 <c>Authorization: Bearer</c>;
/// false = API Key,按各家 SDK 的老规矩(Anthropic 的 <c>x-api-key</c>、OpenAI 系的 Bearer)。
/// </param>
/// <param name="Headers">
/// 这一家还要求每次请求额外带的头(占位符已经替换过);没有则为空。
/// </param>
/// <param name="BaseUrl">
/// 覆盖供应商配置里的基地址;null = 不覆盖。少数供应商按账户下发端点(Copilot 的企业账户
/// 与个人账户不是同一个),而那个地址<b>只有登录之后才知道</b> —— 没有这一项,
/// 就只能让用户手填一个他根本无从知道的值。
/// </param>
public readonly record struct ProviderCredential(
    string? Value,
    bool IsBearerToken,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    string? BaseUrl = null)
{
    /// <summary>一把普通 API Key(可能为空)。</summary>
    public static ProviderCredential Key(string? value) => new(value, false);
}

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>网络检索(<c>web_search</c> / <c>web_fetch</c>)的设置。</summary>
/// <remarks>
/// <para>
/// 不挂在某个模型下面:检索后端是"这台机器怎么上网",跟用哪个模型无关 ——
/// 所以它在全局设置里,不在模型配置页。
/// </para>
/// <para>
/// <b>检索只有 SearXNG 一条路</b>(外加供应商自带的服务端检索,见 <c>NativeWebSearch</c>)。
/// 曾经还并排放过 DuckDuckGo / Tavily / Brave 三个后端,后来删了:
/// DDG 实测直接返回人机验证页,不是"可选后端"而是个坑;Tavily / Brave 要填 Key,
/// 而想接第三方检索服务的人在这个插件里本来就有更合适的去处 —— 「配置工具」里加一台
/// MCP 服务器。留着它们等于把同一件事做两遍,还要多养一套 Key 的存取与界面。
/// </para>
/// </remarks>
public sealed class WebSearchOptions
{
    /// <summary>关掉就等于两个工具都不注册(与在"配置工具"里逐个取消勾选等价,只是一把总闸)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SearXNG 实例地址(形如 <c>http://127.0.0.1:8080</c>)。
    /// 实例的 <c>settings.yml</c> 必须把 <c>json</c> 列进 <c>search.formats</c> ——
    /// 默认只有 <c>html</c>,这时 <c>?format=json</c> 会被回 403。
    /// </summary>
    public string SearxngBaseUrl { get; set; } = "";

    /// <summary>一次检索最多返回几条(1–20)。条数是给模型省 token 的,不是越多越好。</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// 当前模型的协议自带服务端检索工具时优先用它(Anthropic Messages / OpenAI Responses)。
    /// 解不出原生能力的模型(OpenAI Chat Completions、Ollama、多数中转站)自动回落到 SearXNG。
    /// </summary>
    public bool PreferProviderNative { get; set; } = true;

    /// <summary>
    /// 放行私网地址。<b>默认关</b>:插件跑在用户自己的机器上,让模型任意 GET
    /// <c>127.0.0.1</c> / <c>10.x</c> / <c>192.168.x</c> / <c>169.254.x</c> 是实打实的风险
    /// (云环境里 169.254.169.254 就是元数据服务)。
    /// </summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>
    /// 私网白名单,每行一个主机名或 <c>host:port</c>。即使 <see cref="AllowPrivateNetwork" /> 关着,
    /// 这里列出的也放行 —— 跑在本机或内网的 SearXNG、内网 wiki 属于这一类。
    /// </summary>
    public string AllowedPrivateHosts { get; set; } = "";

    /// <summary><c>web_fetch</c> 单页最多返回多少字符(超出截断并注明)。</summary>
    public int MaxFetchChars { get; set; } = 60_000;

    /// <summary>取值夹到合法区间。设置页允许用户填任意数字,这里兜住。</summary>
    public void Clamp()
    {
        MaxResults = Math.Clamp(MaxResults, 1, 20);
        MaxFetchChars = Math.Clamp(MaxFetchChars, 2_000, 400_000);
    }
}

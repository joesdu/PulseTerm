using System.Net.Http.Headers;
using System.Text.Json;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 去端点<b>自己</b>那儿问"你供应哪些模型"(OpenAI 系的 <c>/models</c>、Anthropic 的 <c>/v1/models</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>它与 <see cref="ModelsDevCatalog" /> 是互补,不是替代。</b> 两者各知道一半:
/// </para>
/// <list type="bullet">
/// <item>端点知道<b>这个地址实际供应哪些型号</b> —— 中转站只转发其中一部分,自建的
/// Ollama 装了哪几个权重,models.dev 一概不知道,也没法知道。</item>
/// <item>models.dev 知道<b>规格</b>(上下文窗口、三档单价)—— 而 <c>/models</c> 只给一串 id。
/// 那两项恰恰是本插件里最难填、填错了又<b>不报错</b>的东西(窗口错则输入框下方的占比错,
/// 单价错则花费估算错)。</item>
/// </list>
/// <para>
/// 所以拉取的顺序是:先问端点拿真实清单,再按 id 去 models.dev 配规格
/// (见 <see cref="ModelsDevCatalog.Describe" />);端点没有这条接口(订阅型私有后端就没有)
/// 或请求失败时,整条退回 models.dev 的清单 —— 少一份准确,不至于一无所有。
/// </para>
/// <para>
/// <b>不发请求就不知道结果。</b> 这里只负责"把请求拼对 + 把回应解开",不判断某家有没有这条接口:
/// 兼容端点的花样太多,与其维护一张必然过期的名单,不如发一次 GET,失败就回落。
/// </para>
/// </remarks>
internal static class EndpointModelCatalog
{
    /// <summary>Anthropic 要求每条请求都带的 API 版本头。</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// 拼出模型列表接口的地址;地址不可用时返回 null。
    /// </summary>
    /// <remarks>
    /// <b>与建聊天客户端时的处理保持一致</b>(见 <c>AiSettingsStore.CreateClientAsync</c>):
    /// OpenAI 系的基地址按惯例已含 <c>/v1</c>,SDK 在其后接 <c>/chat/completions</c>,
    /// 这里照样在其后接 <c>/models</c>;Anthropic 的基地址按惯例<b>不含</b> <c>/v1</c>
    /// (SDK 自己补),所以这里得自己补上。两边都不猜、不改写用户填的东西。
    /// </remarks>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    public static Uri? Endpoint(string? baseUrl, ChatProtocol protocol)
    {
        string root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (root.Length == 0)
        {
            return null;
        }
        if (protocol == ChatProtocol.AnthropicMessages && !root.EndsWith("/v1", StringComparison.Ordinal))
        {
            root += "/v1";
        }
        return Uri.TryCreate($"{root}/models", UriKind.Absolute, out Uri? url)
               && url.Scheme is "http" or "https"
            ? url
            : null;
    }

    /// <summary>
    /// 拼一条带好鉴权的请求;地址不可用时返回 null。
    /// </summary>
    /// <remarks>
    /// 鉴权方式跟着协议走,与发对话请求时同一套:OpenAI 系无论 Key 还是订阅令牌都是
    /// <c>Authorization: Bearer</c>;Anthropic 分岔 —— 手填的 Key 是 <c>x-api-key</c>,
    /// 登录换回来的令牌才是 Bearer。凭据为空也照发:让服务端回一个准确的 401,
    /// 比本地猜"大概是没填 Key"要有用。
    /// </remarks>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    /// <param name="credential">凭据(含订阅端点要求的额外头)。</param>
    public static HttpRequestMessage? Request(string? baseUrl, ChatProtocol protocol, ProviderCredential credential)
    {
        if (Endpoint(baseUrl, protocol) is not { } url)
        {
            return null;
        }
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        string? secret = credential.Value;
        if (protocol == ChatProtocol.AnthropicMessages)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers.TryAddWithoutValidation(
                    credential.IsBearerToken ? "Authorization" : "x-api-key",
                    credential.IsBearerToken ? $"Bearer {secret}" : secret);
            }
        }
        else if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        }
        foreach ((string name, string value) in credential.Headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    /// <summary>
    /// 发一次请求,拿这个端点供应的模型 id。
    /// </summary>
    /// <remarks>拿不到不抛异常 —— 调用方要的是"回落到 models.dev",不是一个要处理的异常。</remarks>
    /// <param name="http">发请求用的客户端。</param>
    /// <param name="baseUrl">供应商基地址。</param>
    /// <param name="protocol">线协议。</param>
    /// <param name="credential">凭据。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>模型 id(按字典序,已去重、已滤掉非聊天模型);拿不到时为空。</returns>
    public static async Task<IReadOnlyList<string>> FetchAsync(HttpClient http, string? baseUrl,
        ChatProtocol protocol, ProviderCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        using HttpRequestMessage? request = Request(baseUrl, protocol, credential);
        if (request is null)
        {
            return [];
        }
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>
    /// 从回应里解出模型 id。
    /// </summary>
    /// <remarks>
    /// 认三种形状,因为线上确实就这三种:OpenAI 与 Anthropic 都是
    /// <c>{"data":[{"id":…}]}</c>;有的中转站直接回一个裸数组;Ollama 的原生接口是
    /// <c>{"models":[{"name":…}]}</c>。差别只在外面那层壳和 id 的字段名,解一次就够。
    /// </remarks>
    /// <param name="json">回应正文。</param>
    public static IReadOnlyList<string> Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            JsonElement list = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object => Array(root, "data") ?? Array(root, "models") ?? default,
                _ => default
            };
            if (list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (IdOf(item) is { } id && IsUsableForChat(id))
                {
                    ids.Add(id);
                }
            }
            return [.. ids];
        }
    }

    /// <summary>
    /// 这一条值不值得摆进模型下拉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/models</c> 把一家的<b>全部</b>模型都报上来,包括向量、语音、画图、审核那些
    /// 根本进不了对话的。它们不像 models.dev 那样带着能力字段可判,只能按名字认 ——
    /// 所以这里是<b>启发式</b>,宁可漏筛也不误筛:漏一个,列表里多一条用不上的;
    /// 误筛一个,用户在下拉里<b>永远找不到</b>自己要的模型,而且不会有任何提示。
    /// </para>
    /// <para>
    /// 长词按子串认(<c>embedding</c> 不会出现在聊天模型的名字里);短词按<b>整段</b>认
    /// (<c>tts</c> 当子串会误伤,比如某天出现一个叫 <c>…-ttsx-…</c> 的东西)。
    /// </para>
    /// </remarks>
    private static bool IsUsableForChat(string id)
    {
        foreach (string word in NonChatWords)
        {
            if (id.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        foreach (string segment in id.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string word in NonChatSegments)
            {
                if (string.Equals(segment, word, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>按子串认的非聊天模型(足够长、不会误伤)。</summary>
    private static readonly string[] NonChatWords =
        ["embedding", "rerank", "whisper", "moderation", "dall-e", "gpt-image", "stable-diffusion", "midjourney"];

    /// <summary>按整段认的非聊天模型(太短,当子串会误伤)。</summary>
    private static readonly string[] NonChatSegments = ["embed", "tts", "stt", "ocr", "asr"];

    private static readonly char[] SegmentSeparators = ['-', '_', '.', '/', ':'];

    /// <summary>一条记录的 id:两套字段名都试,裸字符串也认。</summary>
    private static string? IdOf(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return Trimmed(item.GetString());
        }
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Trimmed(Text(item, "id")) ?? Trimmed(Text(item, "name")) ?? Trimmed(Text(item, "model"));
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Array(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;
}

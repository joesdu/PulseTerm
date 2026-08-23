using System.Net.Http.Headers;
using System.Text.Json;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Agent.Web;

/// <summary>一条检索结果。</summary>
/// <param name="Title">标题。</param>
/// <param name="Url">地址。</param>
/// <param name="Snippet">摘要(可能为空)。</param>
internal sealed record SearchHit(string Title, string Url, string Snippet);

    /// <summary>
/// <c>web_search</c> 的检索后端:SearXNG。
/// </summary>
/// <remarks>
/// <para>
/// 全网索引没法在本地自建 —— 那正是搜索引擎在做的事。SearXNG 是绕过这一点的办法:
/// 它自己是<b>元搜索引擎</b>,替你去问 Google/Bing/DDG/Brave 再合并去重,
/// <c>?format=json</c> 直接给结构化结果。不用办任何 Key,查询也不经第三方检索服务的手。
/// </para>
/// <para>
/// 这里只负责"发请求 + 解出 (标题, URL, 摘要)"。正文一律不在这一步取:
/// 检索返回的是<b>一张清单</b>,让模型自己挑哪条值得 <c>web_fetch</c> ——
/// 一次把五个页面的正文全塞进上下文,读的是钱,浪费的也是钱。
/// </para>
/// </remarks>
internal sealed class WebSearchEngine(WebAccess access, WebSearchOptions options)
{
    /// <summary>
    /// 跑一次检索。失败返回 <c>Ok = false</c> 与一句给模型看的原因(不抛异常);
    /// 成功时 <c>Note</c> 通常是空的,只有"确实没搜到"时才带上实例给的相关查询。
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<SearchHit> Hits, string Note)> SearchAsync(
        string query, int count, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return (false, [], "Empty query.");
        }
        count = Math.Clamp(count, 1, 20);

        string root = options.SearxngBaseUrl.Trim().TrimEnd('/');
        if (root.Length == 0)
        {
            return (false, [], NotConfigured);
        }
        if (!Uri.TryCreate($"{root}/search?q={Uri.EscapeDataString(query)}&format=json&safesearch=1",
                UriKind.Absolute, out Uri? url))
        {
            return (false, [], $"'{options.SearxngBaseUrl}' is not a valid SearXNG base URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        (bool ok, string body) = await access.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return (false, [], Explain(body));
        }

        try
        {
            var hits = new List<SearchHit>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("results", out JsonElement results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in results.EnumerateArray())
                {
                    if (Text(item, "url") is not { Length: > 0 } link)
                    {
                        continue;
                    }
                    hits.Add(new SearchHit(Text(item, "title") ?? link, link, HtmlText.Plain(Text(item, "content") ?? "")));
                    if (hits.Count >= count)
                    {
                        break;
                    }
                }
            }
            if (hits.Count > 0)
            {
                return (true, hits, "");
            }
            // 一条都没有时才去追问为什么:引擎全挂和"这词确实搜不到"是两回事,
            // 报同一句"没搜到"会让模型换着关键词空转好几轮。
            (int down, string names) = Unresponsive(doc.RootElement);
            return down > 0
                ? (false, [], $"The SearXNG instance answered, but every engine it tried failed ({names}). "
                              + "This is the instance's own engine configuration, not the query — "
                              + "tell the user to check it instead of rephrasing the search.")
                : (true, [], Suggestions(doc.RootElement));
        }
        catch (JsonException ex)
        {
            return (false, [], $"The SearXNG instance returned something that isn't valid JSON: {ex.Message}");
        }
    }

    /// <remarks>
    /// 出厂是带默认实例的,所以走到这里说明用户<b>主动清空</b>了地址 —— 那是"我不要网络检索"
    /// 的意思,不该再劝他去配一台,说清现状就行。
    /// </remarks>
    private const string NotConfigured =
        "Web search is turned off: the user cleared the SearXNG instance address in the AI plugin's global "
        + "settings. Say so and answer from what you already know; do not keep retrying. "
        + "(web_fetch still works if you have a specific URL to read.)";

    /// <summary>
    /// 把 HTTP 失败翻成用户能照着做的一句话。
    /// </summary>
    /// <remarks>
    /// <b>403 几乎总是同一个原因</b>:SearXNG 默认只开 <c>html</c> 输出,<c>?format=json</c>
    /// 会被 Flask 直接回一个笼统的 Forbidden(实测 2026-08-24,searxng 2026.8.22:
    /// 同一台实例的 HTML 检索与 <c>/config</c> 都正常,只有 <c>format=json</c> 403)。
    /// 不把解法写进去,用户只会以为是自己的反向代理配错了 —— 那正是最容易白查半天的方向。
    /// </remarks>
    private static string Explain(string body)
        => body.Contains("403", StringComparison.Ordinal)
            ? body + " — a SearXNG instance answers 403 to ?format=json unless its settings.yml lists 'json' "
                   + "under search.formats (the default is html only). Ask the user to add it and restart the instance."
            : body;

    /// <summary>
    /// <c>unresponsive_engines</c>:形如 <c>[["google cse","timeout"],["startpage","CAPTCHA"]]</c>。
    /// </summary>
    /// <remarks>
    /// 自建实例上这一项几乎总是非空(VPS 出口 IP 容易被 startpage 之类要求验证码),
    /// 有结果时不值得拿它去打扰模型;只有<b>一条结果都没有</b>时它才是答案本身。
    /// </remarks>
    private static (int Count, string Names) Unresponsive(JsonElement root)
    {
        if (!root.TryGetProperty("unresponsive_engines", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return (0, "");
        }
        var parts = new List<string>();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0)
            {
                continue;
            }
            string name = entry[0].ValueKind == JsonValueKind.String ? entry[0].GetString() ?? "?" : "?";
            string reason = entry.GetArrayLength() > 1 && entry[1].ValueKind == JsonValueKind.String
                ? entry[1].GetString() ?? ""
                : "";
            parts.Add(reason.Length > 0 ? $"{name}: {reason}" : name);
        }
        return (parts.Count, string.Join("; ", parts));
    }

    /// <summary>真的没搜到时,把实例给的相关查询转达出去 —— 比让模型自己瞎猜下一个关键词强。</summary>
    private static string Suggestions(JsonElement root)
    {
        if (!root.TryGetProperty("suggestions", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        string[] items = [.. list.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .Take(6)];
        return items.Length == 0 ? "" : "The instance suggests these related queries: " + string.Join(" / ", items);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

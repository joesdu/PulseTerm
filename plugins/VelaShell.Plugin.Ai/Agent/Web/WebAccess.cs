using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Agent.Web;

/// <summary>抓一页的结果。失败也是一个值,不抛异常 —— 工具的返回值要交给模型自己读。</summary>
/// <param name="Ok">成功与否。</param>
/// <param name="Body">成功时的正文(已转文本);失败时是给模型看的说明。</param>
/// <param name="FinalUrl">跟完同站跳转之后的最终地址。</param>
/// <param name="Redirect">跨站跳转时的目标地址(此时 <paramref name="Ok" /> 为 false)。</param>
/// <param name="ContentType">响应的 Content-Type(便于工具决定怎么呈现)。</param>
internal sealed record FetchResult(bool Ok, string Body, Uri FinalUrl, Uri? Redirect = null, string ContentType = "");

/// <summary>
/// 网页抓取:HTTP 取回 → 转文本 → 截断,外加三道闸(私网、体积、跳转)。
/// </summary>
/// <remarks>
/// <para>
/// <b>HttpClient 是静态单例</b>:每次 new 一个会耗尽端口,而这里的请求是模型驱动的、频次不低。
/// <c>AllowAutoRedirect = false</c> 是刻意的 —— 跳转要自己看,见 <see cref="FetchAsync" />。
/// </para>
/// <para>
/// <b>私网默认不放行。</b> 这个插件跑在用户自己的机器上,模型能任意 GET 就等于拿到了一个
/// 内网探测器;云主机上 <c>169.254.169.254</c> 更是直接对着元数据服务。
/// 校验按 DNS 解析出来的<b>地址</b>做而不是按主机名,不然 <c>foo.example.com → 127.0.0.1</c>
/// 这种指向就绕过去了。(严格说校验与真正建连之间仍有一个 DNS 重绑定的窗口;
/// 桌面工具这个量级不值得为它自己做连接池,故只记在这里。)
/// </para>
/// </remarks>
internal class WebAccess(WebSearchOptions options)
{
    /// <summary>缓存有效期。Agent 反复读同一页是常态(列清单 → 逐条读 → 回头再读)。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>同站跳转最多跟几跳。</summary>
    private const int MaxRedirects = 5;

    /// <summary>一次抓取遇到瞬时网络故障最多试几次(含首次)。</summary>
    private const int MaxSendAttempts = 3;

    /// <summary>是不是"再试一次多半就好"的瞬时网络故障(断连 / 超时 / DNS 抖动)。</summary>
    private static bool IsTransient(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // HttpClient 超时抛的是 TaskCanceledException(OCE 子类);用户按停止那条已在上面按 token 拦下。
            if (e is HttpRequestException or IOException or SocketException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>响应体最多读多少字节(转文本前的原始上限,防止一个大文件把内存吃了)。</summary>
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>
    /// UA 写成普通浏览器:相当一部分站点(含 DuckDuckGo 的 HTML 端点)对空 UA 或
    /// 一眼是脚本的 UA 直接返回 403 / 空页。
    /// </summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 VelaShell/1.0";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>URL → (取回时刻, 文本)。进程内、不落盘。</summary>
    private static readonly Dictionary<string, (DateTime At, FetchResult Result)> Cache = [with(StringComparer.Ordinal)];

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en,zh-CN;q=0.9,zh;q=0.8");
        return client;
    }

    /// <summary>发一个请求并读回文本,不做转换、不进缓存(检索后端调 API 用这条)。</summary>
    /// <remarks>virtual 是给测试留的口子:检索后端的解析逻辑得能在不联网的前提下验。</remarks>
    public virtual async Task<(bool Ok, string Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { } uri)
        {
            return (false, "Request has no URI.");
        }
        if (await GuardAsync(uri, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            return (false, blocked);
        }
        try
        {
            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            string body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, body)
                : (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {Clip(body, 400)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 取一页并转成文本。
    /// </summary>
    /// <remarks>
    /// <b>跨站跳转不自动跟</b>,而是把目标地址报回去让模型自己决定要不要再发一次。
    /// 两个理由:短链接/追踪链接会把"我以为在读 A"变成"实际读了 B",用户在工具卡里得看得见;
    /// 同时跳转也是绕过私网闸最省事的路子,自动跟等于把闸拆了。同站跳转(http→https、
    /// 加尾斜杠、换 www)没有这个问题,照跟。
    /// </remarks>
    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (TryCache(url) is { } hit)
        {
            return hit;
        }

        Uri current = url;
        for (int hop = 0; ; hop++)
        {
            if (await GuardAsync(current, cancellationToken).ConfigureAwait(false) is { } blocked)
            {
                return new FetchResult(false, blocked, current);
            }
            HttpResponseMessage response;
            // 墙内抓外网常常链路一抖就断:瞬时故障退避后重试几次(web_fetch 只读,重放安全),
            // 都不行才把原因转成文本返回 —— 全程不抛,免得中断整轮对话。
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.5");
                    response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                         .ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxSendAttempts && IsTransient(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new FetchResult(false, $"Could not fetch {current}: {ex.Message}", current);
                }
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        return new FetchResult(false,
                            $"{current} redirects to a different host: {next}\n"
                            + "Cross-host redirects are not followed automatically. "
                            + "If that destination is what you want, call web_fetch again with the new URL.",
                            current, next);
                    }
                    if (hop >= MaxRedirects)
                    {
                        return new FetchResult(false, $"Too many redirects starting at {url}.", current);
                    }
                    current = next;
                    continue;
                }

                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!response.IsSuccessStatusCode)
                {
                    return new FetchResult(false,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {current}.", current, null, contentType);
                }
                if (IsBinary(contentType))
                {
                    return new FetchResult(false,
                        $"{current} is {contentType}, not a text document — web_fetch only reads text/HTML pages.",
                        current, null, contentType);
                }

                string raw = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
                string text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? Render(raw, current)
                    : raw;
                var result = new FetchResult(true, Truncate(text), current, null, contentType);
                Remember(url, result);
                if (current != url)
                {
                    Remember(current, result);
                }
                return result;
            }
        }
    }

    /// <summary>HTML → 标题 + 正文文本。</summary>
    private static string Render(string html, Uri url)
    {
        string title = HtmlText.Title(html);
        string body = HtmlText.ToText(html, url);
        return string.IsNullOrEmpty(title) ? body : $"# {title}\n\n{body}";
    }

    private string Truncate(string text)
    {
        int cap = Math.Clamp(options.MaxFetchChars, 2_000, 400_000);
        return text.Length <= cap
            ? text
            : text[..cap] + $"\n\n[... truncated at {cap} characters of {text.Length}. "
                          + "Fetch a more specific URL (an anchor, a print view, a raw file) if you need the rest.]";
    }

    // ---- 缓存 ----

    private static FetchResult? TryCache(Uri url)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(url.AbsoluteUri, out (DateTime At, FetchResult Result) entry))
            {
                return null;
            }
            if (DateTime.UtcNow - entry.At <= CacheTtl)
            {
                return entry.Result;
            }
            Cache.Remove(url.AbsoluteUri);
            return null;
        }
    }

    private static void Remember(Uri url, FetchResult result)
    {
        lock (Cache)
        {
            // 顺手清一遍过期项:这个字典没有别的清理时机,不扫就只会长
            if (Cache.Count > 64)
            {
                DateTime now = DateTime.UtcNow;
                foreach (string stale in Cache.Where(e => now - e.Value.At > CacheTtl).Select(e => e.Key).ToList())
                {
                    Cache.Remove(stale);
                }
            }
            Cache[url.AbsoluteUri] = (DateTime.UtcNow, result);
        }
    }

    // ---- 闸门 ----

    /// <summary>过闸;放行返回 null,拦下返回给模型看的原因。</summary>
    private async Task<string?> GuardAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return $"Only http:// and https:// URLs are supported (got {url.Scheme}://).";
        }
        if (options.AllowPrivateNetwork || IsAllowListed(url))
        {
            return null;
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            if (url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                || !url.Host.Contains('.', StringComparison.Ordinal))
            {
                return Refusal(url);
            }
            try
            {
                addresses = await Dns.GetHostAddressesAsync(url.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"Could not resolve {url.Host}: {ex.Message}";
            }
        }
        return addresses.Length > 0 && Array.TrueForAll(addresses, a => !IsPrivate(a)) ? null : Refusal(url);
    }

    private static string Refusal(Uri url)
        => $"Refusing to fetch {url}: it points at a private/loopback address. "
           + "The user can allow specific internal hosts (or private networks in general) "
           + "in the AI plugin's global settings, under web search.";

    /// <summary>
    /// 这个地址是不是被明确放行的。
    /// </summary>
    /// <remarks>
    /// <b>用户配的 SearXNG 实例自动算数</b>,不用再往白名单里抄一遍:自建实例十有八九就在
    /// <c>127.0.0.1:8080</c>,而私网闸默认拦私网 —— 两个设置必须彼此对上才能用,
    /// 是个一定会有人踩的坑(而且报错还落在检索上,看不出是闸拦的)。
    /// 用户把地址填进"SearXNG 实例"这个框,本身就是最明确的授权。
    /// </remarks>
    private bool IsAllowListed(Uri url)
    {
        if (Uri.TryCreate(options.SearxngBaseUrl.Trim(), UriKind.Absolute, out Uri? searx)
            && string.Equals(searx.Host, url.Host, StringComparison.OrdinalIgnoreCase)
            && searx.Port == url.Port)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(options.AllowedPrivateHosts))
        {
            return false;
        }
        foreach (string raw in options.AllowedPrivateHosts.Split('\n'))
        {
            string entry = raw.Trim().Trim('/');
            if (entry.Length == 0)
            {
                continue;
            }
            // 允许写 host 或 host:port;写了 scheme 也剥掉,免得用户照抄地址栏不生效
            int scheme = entry.IndexOf("//", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                entry = entry[(scheme + 2)..];
            }
            string host = entry.Split('/')[0];
            if (host.Equals(url.Host, StringComparison.OrdinalIgnoreCase)
                || host.Equals($"{url.Host}:{url.Port}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这个地址算不算"不该让模型碰"的。</summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                   || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 唯一本地地址
        }
        byte[] b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and <= 127,                    // 100.64/10 运营商级 NAT
            169 => b[1] == 254,                                  // 169.254/16 链路本地(云元数据服务在这里)
            172 => b[1] is >= 16 and <= 31,                      // 172.16/12
            192 => (b[1] == 168) || (b[1] == 0 && b[2] == 0),      // 192.168/16、192.0.0/24
            198 => b[1] is 18 or 19,                              // 198.18/15 基准测试网段
            >= 224 => true,                                       // 组播与保留
            _ => false
        };
    }

    // ---- 读取 ----

    private static bool IsBinary(string contentType)
        => contentType.Length > 0
           && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
           && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
           && !contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);

    /// <summary>读响应体,超过 <see cref="MaxResponseBytes" /> 就地截断(不是先读完再截)。</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, Math.Min(read, MaxResponseBytes - (int)buffer.Length));
            if (buffer.Length >= MaxResponseBytes)
            {
                break;
            }
        }
        Encoding encoding = ResolveEncoding(response);
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>响应声明了字符集就按它解,认不出来一律 UTF-8。</summary>
    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        string? name = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(name))
        {
            return Encoding.UTF8;
        }
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

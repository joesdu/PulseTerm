using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Ai.Bridge.Channels.Feishu;

/// <summary>
/// 飞书开放平台的 REST 调用:租户令牌、发消息、改消息、取机器人自己的 open_id。
/// </summary>
/// <remarks>
/// 只用到 <c>im.v1</c> 的三个接口,所以不引任何飞书 SDK。
/// <see cref="HttpClient" /> 走默认代理 —— 宿主启动时把
/// <c>HttpClient.DefaultProxy</c> 换成了自己的 <c>VelaWebProxy</c>(设置 → 代理),
/// 不显式设 Proxy 才能跟着全局那份走。
/// </remarks>
internal sealed class FeishuApi(string appId, string appSecret, bool international, IPluginLogger log) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private string? _botOpenId;

    /// <summary>开放平台域名(国内 / 国际两套)。</summary>
    public string Domain { get; } = international ? "https://open.larksuite.com" : "https://open.feishu.cn";

    /// <summary>取租户令牌(带缓存;快到期就提前换)。</summary>
    public async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } cached && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return cached;
            }
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"{Domain}/open-apis/auth/v3/tenant_access_token/internal",
                new { app_id = appId, app_secret = appSecret }, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            int code = root.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : -1;
            if (code != 0 || !root.TryGetProperty("tenant_access_token", out JsonElement tokenElement))
            {
                throw new InvalidOperationException(
                    $"Feishu token request failed: {Message(root)} (code {code}).");
            }
            _token = tokenElement.GetString();
            int expire = root.TryGetProperty("expire", out JsonElement e) ? e.GetInt32() : 7200;
            // 提前 5 分钟换,免得刚好卡在过期那一秒发消息
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expire - 300));
            return _token!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// 往一个会话发一张<b>能渲染 Markdown 的卡片</b>,返回消息 id。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是 <c>msg_type: text</c>。</b>模型的回答天然是 Markdown ——
    /// 列表、行内代码、代码块、加粗。发成纯文本的话,飞书原样显示 <c>- **DeepX**:</c>
    /// 和三个反引号,读起来比没有格式更糟:符号本身成了噪音。
    /// <para>
    /// 飞书这边能渲染 Markdown 的只有<b>卡片</b>。卡片 JSON 2.0 的 <c>markdown</c> 元素
    /// 覆盖标题、列表、代码块、表格、链接,是这四家里最完整的一档。
    /// <c>update_multi</c> 必须开,否则流式那几次改会被拒。
    /// </para>
    /// </remarks>
    public async Task<string?> SendCardAsync(string chatId, string markdown, CancellationToken cancellationToken)
    {
        using JsonDocument document = await CallAsync(HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            new
            {
                receive_id = chatId,
                msg_type = "interactive",
                content = Card(markdown)
            }, cancellationToken).ConfigureAwait(false);
        return MessageId(document);
    }

    /// <summary>更新一张已经发出的卡片。</summary>
    /// <remarks>
    /// 卡片走 <c>PATCH</c>,而文本走 <c>PUT</c> —— 这是两个不同的接口,用错那个会直接报错。
    /// </remarks>
    public async Task UpdateCardAsync(string messageId, string markdown, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await CallAsync(HttpMethod.Patch,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}",
            new { content = Card(markdown) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 传一个文件上去换 <c>file_key</c>,再发一条文件消息。
    /// </summary>
    /// <remarks>
    /// 飞书把"传文件"和"发消息"分成两步,中间靠 <c>file_key</c> 串起来。
    /// <c>file_type</c> 用 <c>stream</c> 这一档通吃 —— 另外那几档(opus/mp4/pdf…)
    /// 会按类型做转码或预览,而日志包既不是音视频也不一定是 pdf,
    /// 报错了反而说不清是哪一步的问题。
    /// </remarks>
    public async Task SendFileAsync(string chatId, string localPath, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(localPath);
        string token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        string fileKey;
        await using (FileStream stream = File.OpenRead(localPath))
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent("stream"), "file_type" },
                { new StringContent(name), "file_name" }
            };
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", name);
            using var request = new HttpRequestMessage(HttpMethod.Post, Domain + "/open-apis/im/v1/files")
            {
                Content = form
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using JsonDocument uploaded = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = uploaded.RootElement;
            if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() != 0)
            {
                throw new InvalidOperationException($"Feishu file upload failed: {Message(root)} (code {code.GetInt32()}).");
            }
            fileKey = root.GetProperty("data").GetProperty("file_key").GetString()
                      ?? throw new InvalidOperationException("Feishu file upload returned no file_key.");
        }
        using JsonDocument _ = await CallAsync(HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            new
            {
                receive_id = chatId,
                msg_type = "file",
                content = JsonSerializer.Serialize(new { file_key = fileKey })
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一张只有一个 Markdown 元素的卡片(序列化成字符串,接口要的就是字符串)。</summary>
    private static string Card(string markdown) => JsonSerializer.Serialize(new
    {
        schema = "2.0",
        // 不开这个,同一张卡片改第二次就会被拒 —— 而流式进度天生要改很多次
        config = new { update_multi = true },
        body = new { elements = new object[] { new { tag = "markdown", content = markdown } } }
    });

    private static string? MessageId(JsonDocument document)
        => document.RootElement.TryGetProperty("data", out JsonElement data)
           && data.TryGetProperty("message_id", out JsonElement id)
            ? id.GetString()
            : null;

    /// <summary>往一个会话发文本,返回消息 id。</summary>
    public async Task<string?> SendTextAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        using JsonDocument document = await CallAsync(HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            new
            {
                receive_id = chatId,
                msg_type = "text",
                content = JsonSerializer.Serialize(new { text })
            }, cancellationToken).ConfigureAwait(false);
        return MessageId(document);
    }

    /// <summary>
    /// 改一条已发出的文本消息。
    /// </summary>
    /// <remarks>
    /// 平台对<b>同一条消息</b>的编辑次数有上限(实测量级为个位到二十次),所以流式进度
    /// 必须限流 —— 见 <c>ConversationRouter</c> 里的编辑间隔与次数预算。
    /// </remarks>
    public async Task EditTextAsync(string messageId, string text, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await CallAsync(HttpMethod.Put,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}",
            new { msg_type = "text", content = JsonSerializer.Serialize(new { text }) },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 机器人自己的 open_id。群里判断"这条消息 @ 的是不是我"要用它。
    /// </summary>
    public async Task<string?> BotOpenIdAsync(CancellationToken cancellationToken)
    {
        if (_botOpenId is { } cached)
        {
            return cached;
        }
        try
        {
            using JsonDocument document = await CallAsync(HttpMethod.Get, "/open-apis/bot/v3/info", null, cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("bot", out JsonElement bot)
                && bot.TryGetProperty("open_id", out JsonElement id))
            {
                _botOpenId = id.GetString();
            }
        }
        catch (Exception ex)
        {
            // 拿不到就退化成"群里有 @ 就算 @ 我" —— 比整个渠道起不来强
            log.Warn($"Feishu: could not read the bot's own open_id ({ex.Message}); @-mention matching will be loose.");
        }
        return _botOpenId;
    }

    /// <summary>
    /// 长连接接入点的请求体。
    /// </summary>
    /// <remarks>
    /// <b>字段名必须逐字是 <c>AppID</c> / <c>AppSecret</c>,所以用特性钉死,不靠序列化选项。</b>
    /// <para>
    /// 这里踩过一次:<see cref="HttpClientJsonExtensions.PostAsJsonAsync{TValue}(HttpClient,string,TValue,CancellationToken)" />
    /// 用的是 <see cref="JsonSerializerDefaults.Web" />,它会把属性名转成 camelCase ——
    /// 匿名对象写 <c>AppID</c> 发出去却是 <c>appID</c>,平台回
    /// <c>{"code":9499,"msg":"Bad Request"}</c>。而同一个类里换令牌那一条用的是
    /// <c>app_id</c>(本来就小写开头),camelCase 动不了它,于是症状是"凭证明明是对的,
    /// 只有接入点这一步失败" —— 最难往序列化上想的那种组合。
    /// </para>
    /// <para>
    /// 用 <see cref="JsonPropertyNameAttribute" /> 而不是"调用时记得传对 options":
    /// 前者跟着类型走,后者跟着每一个调用点走,下次谁再加一个调用点就又中一次。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> 而不是 <c>private</c>:字段名是这条协议的一部分,由
    /// <c>FeishuApiTests</c> 按 <see cref="JsonSerializerDefaults.Web" /> 序列化一次钉住。
    /// </remarks>
    internal sealed record EndpointRequest(
        [property: JsonPropertyName("AppID")] string AppId,
        [property: JsonPropertyName("AppSecret")] string AppSecret);

    /// <summary>
    /// 取长连接的接入点。<b>这一条不带租户令牌</b> —— 它是用应用凭证换连接地址,
    /// 与业务接口的鉴权方式不同。
    /// </summary>
    public async Task<(string Url, int PingIntervalSeconds)> EndpointAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"{Domain}/callback/ws/endpoint",
            new EndpointRequest(appId, appSecret), cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        int code = root.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : -1;
        if (code != 0)
        {
            // 1000040350 = 连接数超限(一个应用最多 50 条),这个错因值得原样报给用户
            throw new InvalidOperationException($"Feishu endpoint request failed: {Message(root)} (code {code}).");
        }
        JsonElement data = root.GetProperty("data");
        string url = data.GetProperty("URL").GetString()
                     ?? throw new InvalidOperationException("Feishu endpoint response has no URL.");
        int ping = 120;
        if (data.TryGetProperty("ClientConfig", out JsonElement config)
            && config.TryGetProperty("PingInterval", out JsonElement interval))
        {
            ping = interval.GetInt32();
        }
        return (url, ping);
    }

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        string token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, Domain + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        JsonDocument document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() != 0)
        {
            string detail = $"{Message(root)} (code {code.GetInt32()})";
            document.Dispose();
            throw new InvalidOperationException($"Feishu {method} {path} failed: {detail}");
        }
        return document;
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            throw new InvalidOperationException($"Feishu returned an empty body ({(int)response.StatusCode}).");
        }
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Feishu returned a non-JSON body ({(int)response.StatusCode}): {Truncate(body)}", ex);
        }
    }

    private static string Message(JsonElement root)
        => root.TryGetProperty("msg", out JsonElement msg) ? msg.GetString() ?? "" : "";

    private static string Truncate(string text) => text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "…");

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}

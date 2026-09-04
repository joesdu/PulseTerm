using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge.Channels.WeCom;

/// <summary>
/// 企业微信渠道:自建应用的<b>接收消息回调</b>。
/// </summary>
/// <remarks>
/// <b>这一家和另外三家不一样。</b>飞书、钉钉、Telegram 都能从内网主动连出去,企业微信只有
/// "平台把消息 POST 到你配的地址"这一条路 —— 它需要一个公网可达的入口。
///
/// <para>本实现<b>只监听 127.0.0.1</b>,不提供绑 0.0.0.0 的选项:把一个能在生产机上敲命令的
/// 回调口直接开到公网,不该是一个勾选框能决定的事。要让企业微信够得着,用一条反向隧道把
/// 公网上某台机器的端口转到这里即可 —— VelaShell 自己就有远程端口转发
/// (会话 → 隧道 → 远程转发),转发目标填 <c>127.0.0.1:&lt;这里的端口&gt;</c>,
/// 再在那台机器上用 nginx 之类把 HTTPS 落到该端口。</para>
///
/// <para>签名与解密见 <see cref="WeComCrypto" />。回调必须<b>先验签再解密</b>,
/// 而且解出来的 receiveid 要等于自己的 corpid —— 少一步都等于让任何人都能给机器人下指令。</para>
/// </remarks>
internal sealed class WeComChannel(
    ChannelConfig config,
    string corpSecret,
    string token,
    string encodingAesKey,
    IPluginContext context) : IMessageChannel
{
    private const string ApiBase = "https://qyapi.weixin.qq.com";

    /// <summary>会话 id → 是不是群聊(发消息要挑不同的接口)。</summary>
    private readonly ConcurrentDictionary<string, bool> _groups = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly byte[] _aesKey = WeComCrypto.ParseKey(encodingAesKey);
    private HttpListener? _listener;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public string Id => config.Id;

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.WeCom;

    /// <inheritdoc />
    public string Label => config.Label;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => new(false, 2000, 20 * 1024 * 1024);

    /// <inheritdoc />
    public event Action? Connected;

    /// <inheritdoc />
    public async Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{config.WebhookPort}/");
        listener.Start();
        _listener = listener;
        Connected?.Invoke();
        context.Log.Info(
            $"Bridge: {Label} listening for WeCom callbacks on http://127.0.0.1:{config.WebhookPort}{config.WebhookPath} " +
            "(put a tunnel or reverse proxy in front of it).");
        // 停机靠关监听来唤醒 accept,而不是 WaitAsync(token)。后者只是**放弃等待**:
        // GetContextAsync 那个任务还挂在监听上,随后 finally 里的 Close 会让它以
        // HttpListenerException 收场 —— 一个没人观察的任务异常;而 WaitAsync 自己抛出的
        // TaskCanceledException 又不在下面的 catch 里,得一路穿到 ChannelHub 才被吃掉。
        // 关监听则让 accept 自己以 HttpListenerException 返回,正好落进已有的分支。
        await using CancellationTokenRegistration stopping =
            cancellationToken.Register(static state => ((HttpListener)state!).Close(), listener);
        try
        {
            while (listener.IsListening && !cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext http;
                try
                {
                    http = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                try
                {
                    await HandleAsync(http, onMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    context.Log.Warn($"Bridge: {Label} failed to handle a callback: {ex.Message}");
                    http.Response.StatusCode = 500;
                }
                finally
                {
                    try
                    {
                        http.Response.Close();
                    }
                    catch (Exception)
                    {
                        // 对端先断了
                    }
                }
            }
        }
        finally
        {
            listener.Close();
            _listener = null;
        }
    }

    private async Task HandleAsync(HttpListenerContext http, Func<InboundMessage, Task> onMessage,
        CancellationToken cancellationToken)
    {
        HttpListenerRequest request = http.Request;
        if (!string.Equals(request.Url?.AbsolutePath.TrimEnd('/'), config.WebhookPath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = 404;
            return;
        }
        string signature = request.QueryString["msg_signature"] ?? "";
        string timestamp = request.QueryString["timestamp"] ?? "";
        string nonce = request.QueryString["nonce"] ?? "";

        if (request.HttpMethod == "GET")
        {
            // 配置回调地址时企业微信先来这一趟:把 echostr 解开原样回去就算验证通过
            string echo = request.QueryString["echostr"] ?? "";
            if (!WeComCrypto.Verify(token, timestamp, nonce, echo, signature))
            {
                context.Log.Warn($"Bridge: {Label} rejected a callback verification with a bad signature.");
                http.Response.StatusCode = 401;
                return;
            }
            (string plain, string receiveId) = WeComCrypto.Decrypt(_aesKey, echo);
            if (!string.Equals(receiveId, config.AppId, StringComparison.Ordinal))
            {
                http.Response.StatusCode = 401;
                return;
            }
            await WriteAsync(http.Response, plain, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod != "POST")
        {
            http.Response.StatusCode = 405;
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        string encrypted = XDocument.Parse(body).Root?.Element("Encrypt")?.Value ?? "";
        if (encrypted.Length == 0 || !WeComCrypto.Verify(token, timestamp, nonce, encrypted, signature))
        {
            context.Log.Warn($"Bridge: {Label} rejected a callback with a bad signature.");
            http.Response.StatusCode = 401;
            return;
        }
        (string message, string corpId) = WeComCrypto.Decrypt(_aesKey, encrypted);
        if (!string.Equals(corpId, config.AppId, StringComparison.Ordinal))
        {
            context.Log.Warn($"Bridge: {Label} got a callback for another corp ({corpId}); ignoring it.");
            http.Response.StatusCode = 401;
            return;
        }
        // 平台只等一个 200,回复走主动发消息的接口 —— 一轮 agent 要跑几十秒,回包等不了
        http.Response.StatusCode = 200;
        if (Parse(message) is { } inbound)
        {
            await onMessage(inbound).ConfigureAwait(false);
        }
    }

    private InboundMessage? Parse(string xml)
    {
        XElement? root = XDocument.Parse(xml).Root;
        if (root is null || root.Element("MsgType")?.Value != "text")
        {
            return null;
        }
        string text = root.Element("Content")?.Value.Trim() ?? "";
        string user = root.Element("FromUserName")?.Value ?? "";
        string chat = root.Element("ChatId")?.Value ?? "";
        bool isGroup = chat.Length > 0;
        string chatId = isGroup ? chat : user;
        if (chatId.Length == 0)
        {
            return null;
        }
        _groups[chatId] = isGroup;
        string messageId = root.Element("MsgId")?.Value ?? "";
        // 应用收到的群消息本来就只有 @ 了它的那些,单聊更不必说
        return new InboundMessage(config.Id, chatId, isGroup, user, user, text, messageId, true);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } cached && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return cached;
            }
            string url = $"{ApiBase}/cgi-bin/gettoken?corpid={Uri.EscapeDataString(config.AppId)}"
                         + $"&corpsecret={Uri.EscapeDataString(corpSecret)}";
            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("access_token", out JsonElement value))
            {
                throw new InvalidOperationException($"WeCom token request failed: {Describe(document.RootElement)}");
            }
            _accessToken = value.GetString();
            int expires = document.RootElement.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() : 7200;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expires - 300));
            return _accessToken!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken)
    {
        string accessToken = await TokenAsync(cancellationToken).ConfigureAwait(false);
        bool isGroup = _groups.TryGetValue(target.ChatId, out bool group) && group;
        string path = isGroup ? "/cgi-bin/appchat/send" : "/cgi-bin/message/send";
        // Markdown 而不是纯文本:理由同其它三家 —— 模型的回答天然带列表与行内代码。
        // 企微的 Markdown 是个子集(没有表格、没有代码块高亮),但列表、加粗、
        // 行内代码、引用都认,已经比原样显示那些符号强得多。
        object body = isGroup
            ? new { chatid = target.ChatId, msgtype = "markdown", markdown = new { content = text } }
            : new
            {
                touser = target.ChatId,
                msgtype = "markdown",
                agentid = int.TryParse(config.AgentId, out int agent) ? agent : 0,
                markdown = new { content = text }
            };
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"{ApiBase}{path}?access_token={Uri.EscapeDataString(accessToken)}", body, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (document.RootElement.TryGetProperty("errcode", out JsonElement code) && code.GetInt32() != 0)
        {
            throw new InvalidOperationException($"WeCom {path} failed: {Describe(document.RootElement)}");
        }
        return null; // 企业微信不给可再编辑的消息 id
    }

    /// <inheritdoc />
    public Task EditAsync(OutboundTarget target, string messageId, string text, CancellationToken cancellationToken)
        => Task.CompletedTask; // Capabilities.CanEdit = false

    /// <inheritdoc />
    /// <remarks>
    /// 临时素材换 <c>media_id</c> 再发。素材三天过期,而我们发完就不再引用它,所以不必留着。
    /// </remarks>
    public async Task SendFileAsync(OutboundTarget target, string localPath, CancellationToken cancellationToken)
    {
        string accessToken = await TokenAsync(cancellationToken).ConfigureAwait(false);
        string name = Path.GetFileName(localPath);
        string mediaId;
        await using (FileStream stream = File.OpenRead(localPath))
        {
            using var form = new MultipartFormDataContent();
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "media", name);
            using HttpResponseMessage upload = await _http.PostAsync(
                $"{ApiBase}/cgi-bin/media/upload?access_token={Uri.EscapeDataString(accessToken)}&type=file",
                form, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await upload.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("errcode", out JsonElement code) && code.GetInt32() != 0)
            {
                throw new InvalidOperationException($"WeCom media upload failed: {Describe(document.RootElement)}");
            }
            mediaId = document.RootElement.GetProperty("media_id").GetString()
                      ?? throw new InvalidOperationException("WeCom media upload returned no media_id.");
        }
        bool isGroup = _groups.TryGetValue(target.ChatId, out bool group) && group;
        string path = isGroup ? "/cgi-bin/appchat/send" : "/cgi-bin/message/send";
        object body = isGroup
            ? new { chatid = target.ChatId, msgtype = "file", file = new { media_id = mediaId } }
            : new
            {
                touser = target.ChatId,
                msgtype = "file",
                agentid = int.TryParse(config.AgentId, out int agent) ? agent : 0,
                file = new { media_id = mediaId }
            };
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"{ApiBase}{path}?access_token={Uri.EscapeDataString(accessToken)}", body, cancellationToken)
            .ConfigureAwait(false);
        using var sent = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (sent.RootElement.TryGetProperty("errcode", out JsonElement sendCode) && sendCode.GetInt32() != 0)
        {
            throw new InvalidOperationException($"WeCom {path} failed: {Describe(sent.RootElement)}");
        }
    }

    private static string Describe(JsonElement root)
    {
        int code = root.TryGetProperty("errcode", out JsonElement c) ? c.GetInt32() : -1;
        string message = root.TryGetProperty("errmsg", out JsonElement m) ? m.GetString() ?? "" : "";
        return $"{message} (errcode {code})";
    }

    private static async Task WriteAsync(HttpListenerResponse response, string payload,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        response.ContentType = "text/plain";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listener?.Close();
        _http.Dispose();
        _tokenGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

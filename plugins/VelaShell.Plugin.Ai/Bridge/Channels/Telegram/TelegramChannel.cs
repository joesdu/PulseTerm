using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge.Channels.Telegram;

/// <summary>
/// Telegram 渠道:Bot API 长轮询(<c>getUpdates</c>)。
/// </summary>
/// <remarks>
/// 四个渠道里最简单的一个 —— 没有握手、没有帧格式、没有应答,一次 HTTP 挂 50 秒等消息。
/// 它在这里的价值是<b>把抽象压到位</b>:飞书是长连接、钉钉是 Stream、企微是回调,
/// 只有再摆一条"长轮询"进来,<see cref="IMessageChannel.RunAsync" /> 那句"跑到断为止"
/// 才算真的经得起四种传输。
///
/// <para>国内网络通常连不上 api.telegram.org,靠宿主的全局代理(设置 → 代理)解决:
/// <see cref="HttpClient" /> 不显式设 Proxy 就会走 <c>HttpClient.DefaultProxy</c>,
/// 而宿主启动时已经把那里换成了自己的实现。</para>
/// </remarks>
internal sealed class TelegramChannel(ChannelConfig config, string token, IPluginContext context) : IMessageChannel
{
    /// <summary>一次长轮询挂多久(秒)。Telegram 允许最长 50。</summary>
    private const int PollSeconds = 50;

    /// <summary>只订阅普通消息:其它更新(编辑、频道帖、成员变动)对桥接没有意义,少收一点少解析一点。</summary>
    private static readonly string[] AllowedUpdates = ["message"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(PollSeconds + 20) };
    private long _offset;
    private string? _botUsername;

    /// <inheritdoc />
    public string Id => config.Id;

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Telegram;

    /// <inheritdoc />
    public string Label => config.Label;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => new(true, 4000, 50 * 1024 * 1024);

    /// <inheritdoc />
    public event Action? Connected;

    /// <inheritdoc />
    public async Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        _botUsername = await BotUsernameAsync(cancellationToken).ConfigureAwait(false);
        Connected?.Invoke();
        context.Log.Info($"Bridge: {Label} polling Telegram as @{_botUsername}.");
        while (!cancellationToken.IsCancellationRequested)
        {
            using JsonDocument document = await CallAsync("getUpdates", new
            {
                offset = _offset,
                timeout = PollSeconds,
                allowed_updates = AllowedUpdates
            }, cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("result", out JsonElement updates))
            {
                continue;
            }
            foreach (JsonElement update in updates.EnumerateArray())
            {
                if (update.TryGetProperty("update_id", out JsonElement id))
                {
                    // offset 一旦推过去,平台就当这条已消费。所以先解析再推 ——
                    // 崩在中间时下次还能重来一遍,顶多重复一条,总好过静默丢掉。
                    _offset = Math.Max(_offset, id.GetInt64() + 1);
                }
                if (Parse(update) is { } inbound)
                {
                    await onMessage(inbound).ConfigureAwait(false);
                }
            }
        }
    }

    private InboundMessage? Parse(JsonElement update)
    {
        if (!update.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("text", out JsonElement textElement))
        {
            return null;
        }
        string text = textElement.GetString() ?? "";
        if (!message.TryGetProperty("chat", out JsonElement chat))
        {
            return null;
        }
        string chatId = chat.GetProperty("id").GetRawText();
        bool isGroup = chat.TryGetProperty("type", out JsonElement chatType)
                       && chatType.GetString() is "group" or "supergroup";
        string userId = "", userName = "";
        if (message.TryGetProperty("from", out JsonElement from))
        {
            userId = from.GetProperty("id").GetRawText();
            userName = from.TryGetProperty("username", out JsonElement u) ? u.GetString() ?? "" : "";
            if (userName.Length == 0 && from.TryGetProperty("first_name", out JsonElement f))
            {
                userName = f.GetString() ?? userId;
            }
        }
        string messageId = message.TryGetProperty("message_id", out JsonElement mid) ? mid.GetRawText() : "";
        (bool mentioned, string cleaned) = StripMention(text, message);
        return new InboundMessage(config.Id, chatId, isGroup, userId, userName, cleaned, messageId,
            !isGroup || mentioned);
    }

    /// <summary>
    /// 群里认三种"在跟我说话":@我、回复我发的消息、以 <c>/命令@我</c> 起头。
    /// 认出来之后把 @ 从正文里抹掉,免得模型把它当内容的一部分。
    /// </summary>
    private (bool Mentioned, string Text) StripMention(string text, JsonElement message)
    {
        bool mentioned = false;
        if (_botUsername is { Length: > 0 } name)
        {
            string handle = "@" + name;
            if (text.Contains(handle, StringComparison.OrdinalIgnoreCase))
            {
                mentioned = true;
                text = text.Replace(handle, "", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (!mentioned
            && message.TryGetProperty("reply_to_message", out JsonElement replied)
            && replied.TryGetProperty("from", out JsonElement repliedFrom)
            && repliedFrom.TryGetProperty("is_bot", out JsonElement isBot)
            && isBot.GetBoolean())
        {
            mentioned = true;
        }
        return (mentioned, text.Trim());
    }

    private async Task<string> BotUsernameAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await CallAsync("getMe", null, cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("result", out JsonElement result)
               && result.TryGetProperty("username", out JsonElement username)
            ? username.GetString() ?? ""
            : "";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>先按 HTML 发,被拒了退回纯文本。</b>转换器(<see cref="TelegramHtml" />)自己生成标签、
    /// 结构上不会不闭合,但 Telegram 那边的实体规则还有别的讲究(比如嵌套限制),
    /// 真撞上了该让用户看到一条没格式的回答,而不是什么都看不到。
    /// </remarks>
    public async Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken)
    {
        long chat = long.Parse(target.ChatId);
        try
        {
            return MessageId(await CallAsync("sendMessage",
                new { chat_id = chat, text = TelegramHtml.Convert(text), parse_mode = "HTML" },
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"Bridge: {Label} could not send HTML ({ex.Message}); falling back to plain text.");
            return MessageId(await CallAsync("sendMessage", new { chat_id = chat, text }, cancellationToken)
                .ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public async Task EditAsync(OutboundTarget target, string messageId, string text,
        CancellationToken cancellationToken)
    {
        long chat = long.Parse(target.ChatId);
        long message = long.Parse(messageId);
        try
        {
            using JsonDocument _ = await CallAsync("editMessageText",
                new { chat_id = chat, message_id = message, text = TelegramHtml.Convert(text), parse_mode = "HTML" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using JsonDocument _ = await CallAsync("editMessageText",
                new { chat_id = chat, message_id = message, text }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Telegram 这一家最省事:一步 multipart,不用先换 key。
    /// </remarks>
    public async Task SendFileAsync(OutboundTarget target, string localPath, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(localPath);
        await using FileStream stream = File.OpenRead(localPath);
        using var form = new MultipartFormDataContent { { new StringContent(target.ChatId), "chat_id" } };
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "document", name);
        using HttpResponseMessage response = await _http
            .PostAsync($"https://api.telegram.org/bot{token}/sendDocument", form, cancellationToken)
            .ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            throw new InvalidOperationException($"Telegram sendDocument failed: {payload}");
        }
    }

    private static string? MessageId(JsonDocument document)
    {
        using (document)
        {
            return document.RootElement.TryGetProperty("result", out JsonElement result)
                   && result.TryGetProperty("message_id", out JsonElement id)
                ? id.GetRawText()
                : null;
        }
    }

    private async Task<JsonDocument> CallAsync(string method, object? body, CancellationToken cancellationToken)
    {
        string url = $"https://api.telegram.org/bot{token}/{method}";
        using HttpResponseMessage response = body is null
            ? await _http.GetAsync(url, cancellationToken).ConfigureAwait(false)
            : await _http.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("ok", out JsonElement ok) && !ok.GetBoolean())
        {
            string description = document.RootElement.TryGetProperty("description", out JsonElement d)
                ? d.GetString() ?? ""
                : "";
            document.Dispose();
            throw new InvalidOperationException($"Telegram {method} failed: {description}");
        }
        return document;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

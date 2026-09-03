using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge.Channels.DingTalk;

/// <summary>
/// 钉钉渠道:Stream 模式(WebSocket + JSON 帧)。
/// </summary>
/// <remarks>
/// 与飞书同样是"从内网连出去",但帧是明文 JSON 而不是 protobuf,协议本身官方有公开文档,
/// 所以这一份比飞书那份短得多。
///
/// <para><b>发消息不能走 sessionWebhook。</b>事件里带的那个回调地址只能发 5 条、有效期
/// 一个半小时,而一轮对话光是"占位 + 审批 + 结果"就可能超。所以统一走带令牌的
/// <c>robot/groupMessages/send</c> 与 <c>robot/oToMessages/batchSend</c>;代价是发消息时
/// 需要知道这个会话是群还是单聊,所以收到消息时把这点记下来(见 <see cref="_routes" />)。</para>
/// </remarks>
internal sealed class DingTalkChannel(ChannelConfig config, string clientSecret, IPluginContext context)
    : IMessageChannel
{
    private const string ApiBase = "https://api.dingtalk.com";
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>会话 id → 发消息需要的那点信息(收到消息时记下)。</summary>
    private readonly ConcurrentDictionary<string, (bool IsGroup, string UserId, string RobotCode)> _routes = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private ClientWebSocket? _socket;

    /// <inheritdoc />
    public string Id => config.Id;

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.DingTalk;

    /// <inheritdoc />
    public string Label => config.Label;

    /// <inheritdoc />
    /// <remarks>钉钉的普通机器人消息发出去就改不了,所以进度只能另发一条 —— 由桥接决定别刷屏。</remarks>
    public ChannelCapabilities Capabilities => new(false, 3000);

    /// <inheritdoc />
    public event Action? Connected;

    /// <inheritdoc />
    public async Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        (string endpoint, string ticket) = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = HttpClient.DefaultProxy;
        await socket.ConnectAsync(new Uri($"{endpoint}?ticket={Uri.EscapeDataString(ticket)}"), cancellationToken)
            .ConfigureAwait(false);
        _socket = socket;
        Connected?.Invoke();
        context.Log.Info($"Bridge: {Label} connected to DingTalk stream.");

        // 读循环刻意**不**接宿主的取消令牌:取消一次挂起的 ReceiveAsync,ClientWebSocket
        // 走的是 Abort,整条 TLS/TCP 栈会连锁抛异常,平台侧看到的也是一条莫名断掉的长连接。
        // 停机改走 Close 帧(见 ChannelShutdown.CloseAsync),读循环从对端的 Close 应答里
        // 正常返回;只有对端不应答时才由那里 Abort 兜底。
        using var stop = new CancellationTokenSource();
        Task receive = ReceiveLoopAsync(socket, onMessage, stop.Token);
        try
        {
            await ChannelShutdown.WhenCompletedOrCancelledAsync(receive, cancellationToken).ConfigureAwait(false);
            if (!receive.IsCompleted)
            {
                await ChannelShutdown.CloseAsync(socket, receive).ConfigureAwait(false);
            }
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // 优雅关闭超时后被 Abort 掐断的读取。停机路径,不算故障。
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _socket = null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, Func<InboundMessage, Task> onMessage,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxFrameBytes)
                    {
                        throw new InvalidDataException("DingTalk sent an oversized websocket message.");
                    }
                }
                while (!result.EndOfMessage);

                if (!await DispatchAsync(socket, message.ToArray(), onMessage, cancellationToken).ConfigureAwait(false))
                {
                    return; // 平台要求断开
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>处理一帧;返回 false 表示平台让我们断开。</summary>
    private async Task<bool> DispatchAsync(ClientWebSocket socket, byte[] raw,
        Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;
        string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
        JsonElement headers = root.TryGetProperty("headers", out JsonElement h) ? h : default;
        string topic = headers.ValueKind == JsonValueKind.Object && headers.TryGetProperty("topic", out JsonElement tp)
            ? tp.GetString() ?? ""
            : "";
        string messageId = headers.ValueKind == JsonValueKind.Object
                           && headers.TryGetProperty("messageId", out JsonElement mid)
            ? mid.GetString() ?? ""
            : "";
        string data = root.TryGetProperty("data", out JsonElement d) ? d.GetString() ?? "" : "";

        switch (type)
        {
            case "SYSTEM" when topic == "ping":
                // ping 的 data 里带一个 opaque,原样回去即可
                await RespondAsync(socket, messageId, data, cancellationToken).ConfigureAwait(false);
                return true;

            case "SYSTEM" when topic == "disconnect":
                context.Log.Info($"Bridge: {Label} was asked to disconnect by DingTalk; reconnecting.");
                return false;

            case "CALLBACK" when topic == "/v1.0/im/bot/messages/get":
                // 先应答再干活:平台等的是"收到了",不是"处理完了"
                await RespondAsync(socket, messageId, "{}", cancellationToken).ConfigureAwait(false);
                if (Parse(data) is { } inbound)
                {
                    await onMessage(inbound).ConfigureAwait(false);
                }
                return true;

            default:
                if (messageId.Length > 0)
                {
                    await RespondAsync(socket, messageId, "{}", cancellationToken).ConfigureAwait(false);
                }
                return true;
        }
    }

    private static async Task RespondAsync(ClientWebSocket socket, string messageId, string data,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        string payload = JsonSerializer.Serialize(new
        {
            code = 200,
            headers = new { contentType = "application/json", messageId },
            message = "OK",
            data
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private InboundMessage? Parse(string data)
    {
        if (data.Length == 0)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;
        string messageType = root.TryGetProperty("msgtype", out JsonElement mt) ? mt.GetString() ?? "" : "";
        if (messageType != "text")
        {
            return null;
        }
        string text = root.TryGetProperty("text", out JsonElement textNode)
                      && textNode.TryGetProperty("content", out JsonElement content)
            ? content.GetString()?.Trim() ?? ""
            : "";
        string chatId = root.TryGetProperty("conversationId", out JsonElement cid) ? cid.GetString() ?? "" : "";
        // conversationType: "1" = 单聊, "2" = 群聊
        bool isGroup = root.TryGetProperty("conversationType", out JsonElement ct) && ct.GetString() == "2";
        string userId = root.TryGetProperty("senderStaffId", out JsonElement staff) ? staff.GetString() ?? "" : "";
        string userName = root.TryGetProperty("senderNick", out JsonElement nick) ? nick.GetString() ?? "" : userId;
        string messageId = root.TryGetProperty("msgId", out JsonElement mid) ? mid.GetString() ?? "" : "";
        string robotCode = root.TryGetProperty("robotCode", out JsonElement rc) ? rc.GetString() ?? "" : config.AppId;
        if (chatId.Length == 0)
        {
            return null;
        }
        // 群聊发消息要 openConversationId(就是这个 conversationId),单聊要 userId ——
        // 两者只在这条事件里出现一次,所以收到时就记下来
        _routes[chatId] = (isGroup, userId, robotCode.Length > 0 ? robotCode : config.AppId);
        // 钉钉只会把 @ 了机器人的群消息推过来,所以群里收到就等于被 @ 了
        return new InboundMessage(config.Id, chatId, isGroup, userId, userName, text, messageId, true);
    }

    private async Task<(string Endpoint, string Ticket)> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"{ApiBase}/v1.0/gateway/connections/open",
            new
            {
                clientId = config.AppId,
                clientSecret,
                ua = "velashell-bridge/1.0",
                subscriptions = new[]
                {
                    new { type = "CALLBACK", topic = "/v1.0/im/bot/messages/get" }
                }
            }, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("endpoint", out JsonElement endpoint) || !root.TryGetProperty("ticket", out JsonElement ticket))
        {
            throw new InvalidOperationException($"DingTalk refused the stream connection: {Truncate(body)}");
        }
        return (endpoint.GetString()!, ticket.GetString()!);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } cached && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return cached;
            }
            using HttpResponseMessage response = await _http.PostAsJsonAsync($"{ApiBase}/v1.0/oauth2/accessToken",
                new { appKey = config.AppId, appSecret = clientSecret }, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("accessToken", out JsonElement token))
            {
                throw new InvalidOperationException($"DingTalk token request failed: {Truncate(body)}");
            }
            _token = token.GetString();
            int expire = document.RootElement.TryGetProperty("expireIn", out JsonElement e) ? e.GetInt32() : 7200;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expire - 300));
            return _token!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken)
    {
        if (!_routes.TryGetValue(target.ChatId, out (bool IsGroup, string UserId, string RobotCode) route))
        {
            context.Log.Warn($"Bridge: {Label} has no route for conversation {target.ChatId}; dropping the reply.");
            return null;
        }
        string token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        string parameters = JsonSerializer.Serialize(new { content = text });
        object body = route.IsGroup
            ? new
            {
                msgKey = "sampleText",
                msgParam = parameters,
                openConversationId = target.ChatId,
                robotCode = route.RobotCode
            }
            : new
            {
                msgKey = "sampleText",
                msgParam = parameters,
                robotCode = route.RobotCode,
                userIds = new[] { route.UserId }
            };
        string path = route.IsGroup ? "/v1.0/robot/groupMessages/send" : "/v1.0/robot/oToMessages/batchSend";
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("x-acs-dingtalk-access-token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DingTalk {path} failed ({(int)response.StatusCode}): {Truncate(payload)}");
        }
        // 群消息返回 processQueryKey,单聊返回 processQueryKey/flowControlledStaffIdList;
        // 两者都不是"可以再改的消息 id",所以不往上报。
        return null;
    }

    /// <inheritdoc />
    public Task EditAsync(OutboundTarget target, string messageId, string text, CancellationToken cancellationToken)
        => Task.CompletedTask; // Capabilities.CanEdit = false,不会走到这里

    private static string Truncate(string text) => text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "…");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket?.Abort();
        _http.Dispose();
        _tokenGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Web;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge.Channels.Feishu;

/// <summary>
/// 飞书 / Lark 渠道:官方长连接(WebSocket + pbbp2 帧)。
/// </summary>
/// <remarks>
/// <b>为什么是长连接而不是 Webhook。</b>VelaShell 是桌面程序,绝大多数时候躲在 NAT 后面 ——
/// 回调模式要求平台能主动连上你,那就得有公网地址加内网穿透,配置成本比整个功能本身还高。
/// 长连接是从本机连出去的,开箱即用。代价是<b>一个应用最多 50 条连接</b>,而且同一个应用
/// 起多个客户端时平台按<b>集群</b>投递(随机挑一个),不是广播 —— 所以同一套应用凭证
/// 不要在两台机器上同时跑,否则消息会随机落到另一台上。
///
/// <para>协议细节(端点、帧字段号、控制帧、应答与分片)照官方 Go SDK 的 <c>ws/</c> 实现,
/// 见 <see cref="Pbbp2" /> 的注释。</para>
/// </remarks>
internal sealed class FeishuChannel(ChannelConfig config, string appSecret, IPluginContext context) : IMessageChannel
{
    /// <summary>单个 WebSocket 消息的接收上限(飞书事件远小于此,超了说明对端出问题了)。</summary>
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>分片缓存的寿命。官方实现也是 5 秒 —— 收不齐就当这条消息丢了。</summary>
    private static readonly TimeSpan FragmentTtl = TimeSpan.FromSeconds(5);

    private readonly FeishuApi _api = new(config.AppId, appSecret, config.International, context.Log);
    private readonly Dictionary<string, (byte[]?[] Parts, DateTimeOffset At)> _fragments = [];
    private readonly Queue<string> _seenEvents = new();
    private readonly HashSet<string> _seenEventSet = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    private ClientWebSocket? _socket;
    private int _serviceId;
    private string? _botOpenId;

    /// <inheritdoc />
    public string Id => config.Id;

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Feishu;

    /// <inheritdoc />
    public string Label => config.Label;

    /// <inheritdoc />
    /// <remarks>飞书能改已发出的文本消息,所以进度可以就地刷新,不必刷屏。</remarks>
    public ChannelCapabilities Capabilities => new(true, 3000);

    /// <inheritdoc />
    public event Action? Connected;

    /// <inheritdoc />
    public async Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        (string url, int pingSeconds) = await _api.EndpointAsync(cancellationToken).ConfigureAwait(false);
        _serviceId = ReadServiceId(url);
        _botOpenId = await _api.BotOpenIdAsync(cancellationToken).ConfigureAwait(false);

        using var socket = new ClientWebSocket();
        // 全局代理:宿主把 HttpClient.DefaultProxy 换成了自己的实现(设置 → 代理),
        // 而 ClientWebSocket 不会自动跟着走,得显式交给它。
        socket.Options.Proxy = HttpClient.DefaultProxy;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // 心跳由协议自己的 ping 帧负责
        try
        {
            await socket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new InvalidOperationException($"Feishu handshake failed: {HandshakeDetail(socket) ?? ex.Message}", ex);
        }
        _socket = socket;
        Connected?.Invoke();
        context.Log.Info($"Bridge: {Label} connected to Feishu (service {_serviceId}, ping every {pingSeconds}s).");

        // 读循环刻意**不**接宿主的取消令牌:取消一次挂起的 ReceiveAsync,ClientWebSocket
        // 走的是 Abort,整条 TLS/TCP 栈会连锁抛异常,平台侧看到的也是一条莫名断掉的长连接。
        // 停机改走 Close 帧(见 ChannelShutdown.CloseAsync),读循环从对端的 Close 应答里
        // 正常返回;只有对端不应答时才由那里 Abort 兜底。
        using var stop = new CancellationTokenSource();
        Task receive = ReceiveLoopAsync(socket, onMessage, stop.Token);
        Task pingLoop = PingLoopAsync(socket, pingSeconds, stop.Token);
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
            try
            {
                await pingLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                           or WebSocketException
                                           or ObjectDisposedException)
            {
                // 收摊时撞上正在关闭的连接
            }
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
                        return; // 对端主动关 —— 交给 ChannelHub 重连
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxFrameBytes)
                    {
                        throw new InvalidDataException("Feishu sent an oversized websocket message.");
                    }
                }
                while (!result.EndOfMessage);

                Pbbp2.Frame frame = Pbbp2.Decode(message.GetBuffer().AsSpan(0, (int)message.Length));
                await DispatchAsync(socket, frame, onMessage, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DispatchAsync(ClientWebSocket socket, Pbbp2.Frame frame,
        Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        if (frame.Method == Pbbp2.FrameTypeControl)
        {
            // pong 里可能捎回新的 ClientConfig;目前只有心跳间隔有用,而它变了也不影响正确性
            return;
        }
        if (frame.Method != Pbbp2.FrameTypeData
            || !string.Equals(frame.Header(Pbbp2.HeaderNames.Type), "event", StringComparison.Ordinal))
        {
            // card 类型(卡片按钮回传)先不处理:审批走文本回复,见 ImApprovalBroker 的注释
            return;
        }
        byte[]? payload = Reassemble(frame);
        if (payload is null)
        {
            return; // 分片还没收齐
        }
        long startedAt = Environment.TickCount64;
        try
        {
            if (Parse(payload) is { } inbound)
            {
                await onMessage(inbound).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            context.Log.Error($"Bridge: {Label} failed to handle an event: {ex.Message}");
        }
        finally
        {
            // 平台要求 3 秒内应答,否则会重投。所以应答一定要走在业务处理之后**但不等业务结果** ——
            // 这里业务已经是"排队 + 后台跑",Parse/入队本身是毫秒级的。
            await AckAsync(socket, frame, Environment.TickCount64 - startedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>应答一帧:原样回去 + 补一个耗时头 + 一段 200 的 JSON。</summary>
    private static async Task AckAsync(ClientWebSocket socket, Pbbp2.Frame frame, long elapsedMs,
        CancellationToken cancellationToken)
    {
        frame.SetHeader(Pbbp2.HeaderNames.BizRt, elapsedMs.ToString());
        frame.Payload = Encoding.UTF8.GetBytes("""{"code":200,"headers":null,"data":null}""");
        await SendFrameAsync(socket, frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task PingLoopAsync(ClientWebSocket socket, int pingSeconds, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(pingSeconds, 10, 600));
        // 等待走不抛的那条路:停机时这里被取消是常态,不该每次都在调试输出里留一条异常。
        while (socket.State == WebSocketState.Open
               && await ChannelShutdown.DelayAsync(interval, cancellationToken).ConfigureAwait(false))
        {
            var ping = new Pbbp2.Frame { Method = Pbbp2.FrameTypeControl, Service = _serviceId };
            ping.SetHeader(Pbbp2.HeaderNames.Type, "ping");
            await SendFrameAsync(socket, ping, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendFrameAsync(ClientWebSocket socket, Pbbp2.Frame frame,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        await socket.SendAsync(Pbbp2.Encode(frame), WebSocketMessageType.Binary, true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>分片重组。<c>sum</c> ≤ 1 就是完整的一帧。</summary>
    private byte[]? Reassemble(Pbbp2.Frame frame)
    {
        int sum = frame.HeaderInt(Pbbp2.HeaderNames.Sum, 1);
        if (sum <= 1)
        {
            return frame.Payload;
        }
        string messageId = frame.Header(Pbbp2.HeaderNames.MessageId);
        int seq = frame.HeaderInt(Pbbp2.HeaderNames.Seq);
        if (messageId.Length == 0 || seq < 0 || seq >= sum)
        {
            return frame.Payload;
        }
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string stale in _fragments.Where(kv => now - kv.Value.At > FragmentTtl).Select(kv => kv.Key).ToArray())
            {
                _fragments.Remove(stale);
            }
            if (!_fragments.TryGetValue(messageId, out (byte[]?[] Parts, DateTimeOffset At) entry))
            {
                entry = (new byte[]?[sum], now);
                _fragments[messageId] = entry;
            }
            entry.Parts[seq] = frame.Payload;
            if (entry.Parts.Any(p => p is null))
            {
                return null;
            }
            _fragments.Remove(messageId);
            return [.. entry.Parts.SelectMany(p => p!)];
        }
    }

    /// <summary>把一条 <c>im.message.receive_v1</c> 事件翻成桥接认识的样子(其它事件返回 null)。</summary>
    private InboundMessage? Parse(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("header", out JsonElement header)
            || !root.TryGetProperty("event", out JsonElement body))
        {
            return null;
        }
        if (header.TryGetProperty("event_type", out JsonElement type)
            && type.GetString() != "im.message.receive_v1")
        {
            return null;
        }
        if (header.TryGetProperty("event_id", out JsonElement eventId) && !FirstTime(eventId.GetString()))
        {
            return null; // 平台重投过来的同一条,别答两遍
        }
        if (!body.TryGetProperty("message", out JsonElement message))
        {
            return null;
        }
        string messageType = message.TryGetProperty("message_type", out JsonElement mt) ? mt.GetString() ?? "" : "";
        if (messageType != "text")
        {
            return null; // 图片 / 文件 / 富文本先不接
        }
        string chatId = message.TryGetProperty("chat_id", out JsonElement chat) ? chat.GetString() ?? "" : "";
        bool isGroup = message.TryGetProperty("chat_type", out JsonElement chatType)
                       && chatType.GetString() == "group";
        string messageId = message.TryGetProperty("message_id", out JsonElement mid) ? mid.GetString() ?? "" : "";
        string senderId = body.TryGetProperty("sender", out JsonElement sender)
                          && sender.TryGetProperty("sender_id", out JsonElement senderIds)
                          && senderIds.TryGetProperty("open_id", out JsonElement openId)
            ? openId.GetString() ?? ""
            : "";
        string text = message.TryGetProperty("content", out JsonElement content)
            ? ExtractText(content.GetString())
            : "";
        (bool mentionsBot, string cleaned) = StripMentions(text, message);
        return chatId.Length == 0
            ? null
            : new InboundMessage(config.Id, chatId, isGroup, senderId, senderId, cleaned, messageId,
                !isGroup || mentionsBot);
    }

    /// <summary><c>content</c> 是一段 JSON 文本,文本消息里就一个 <c>text</c> 字段。</summary>
    private static string ExtractText(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// 把 <c>@_user_N</c> 这类占位符从正文里去掉,顺带判断这条消息 @ 的是不是本机器人。
    /// </summary>
    /// <remarks>
    /// 拿不到自己的 open_id 时(权限不够),退化成"有任何 @ 就算 @ 了我"。
    /// 宁可多应一次,也好过在群里被 @ 了却装死 —— 后者用户会以为坏了。
    /// </remarks>
    private (bool MentionsBot, string Text) StripMentions(string text, JsonElement message)
    {
        bool mentionsBot = false;
        if (message.TryGetProperty("mentions", out JsonElement mentions) && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement mention in mentions.EnumerateArray())
            {
                string key = mention.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "" : "";
                string open = mention.TryGetProperty("id", out JsonElement id)
                              && id.TryGetProperty("open_id", out JsonElement o)
                    ? o.GetString() ?? ""
                    : "";
                if (_botOpenId is null || string.Equals(open, _botOpenId, StringComparison.Ordinal))
                {
                    mentionsBot = true;
                }
                if (key.Length > 0)
                {
                    text = text.Replace(key, "", StringComparison.Ordinal);
                }
            }
        }
        return (mentionsBot, text.Trim());
    }

    /// <summary>事件去重(平台在 3 秒内没收到应答会重投)。只记最近若干条,够用且不涨内存。</summary>
    private bool FirstTime(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return true;
        }
        lock (_sync)
        {
            if (!_seenEventSet.Add(eventId))
            {
                return false;
            }
            _seenEvents.Enqueue(eventId);
            while (_seenEvents.Count > 512)
            {
                _seenEventSet.Remove(_seenEvents.Dequeue());
            }
            return true;
        }
    }

    /// <summary>ws 地址的 query 里带着 <c>service_id</c>,发帧时要原样填回去。</summary>
    private static int ReadServiceId(string url)
    {
        string query = new Uri(url).Query;
        string? value = HttpUtility.ParseQueryString(query)["service_id"];
        return int.TryParse(value, out int id) ? id : 0;
    }

    /// <summary>握手失败时,平台把原因放在响应头里。</summary>
    private static string? HandshakeDetail(ClientWebSocket socket)
    {
        if (socket.HttpResponseHeaders is not { } headers)
        {
            return null;
        }
        string? status = Get("Handshake-Status");
        string? message = Get("Handshake-Msg");
        string? code = Get("Handshake-Autherrcode");
        return status is null && message is null && code is null
            ? null
            : $"status={status}, code={code}, msg={message}";

        string? Get(string name) => headers.TryGetValue(name, out IEnumerable<string>? values)
            ? string.Join(";", values)
            : null;
    }

    /// <inheritdoc />
    public async Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken)
        => await _api.SendTextAsync(target.ChatId, text, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task EditAsync(OutboundTarget target, string messageId, string text, CancellationToken cancellationToken)
        => await _api.EditTextAsync(messageId, text, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket?.Abort();
        _api.Dispose();
        return ValueTask.CompletedTask;
    }
}

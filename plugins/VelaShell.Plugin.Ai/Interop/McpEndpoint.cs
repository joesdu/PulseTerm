using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary>
/// 把 VelaShell 的工具对外开成一个 MCP 服务端,让 Claude Code / Codex / Cursor
/// 这类外部 agent 能连进来用。
/// </summary>
/// <remarks>
/// <b>传输选的是 Streamable HTTP,不是 stdio。</b>stdio 的前提是"客户端能把服务端拉起来",
/// 而 VelaShell 是一个已经开着的桌面程序 —— 外部 agent 拉不起它,也不该拉起第二个。
/// HTTP 只需要用户在自己的 agent 配置里填一行地址。协议允许对一次 POST 直接回一个 JSON
/// 响应(不必开 SSE 流),而 MCP 的请求-响应本来就都是一问一答,所以这里只实现 JSON 那条路。
///
/// <para><b>只绑 127.0.0.1,且必须带令牌。</b>本机监听不等于安全:同机任何进程,包括浏览器里的
/// 网页,都能往本地端口发请求。令牌是这条路上唯一的门,见
/// <see cref="McpServerSettingsStore.TokenAsync" />。</para>
///
/// <para>配置方法(Claude Code):
/// <c>claude mcp add --transport http velashell http://127.0.0.1:8391/mcp --header "Authorization: Bearer &lt;令牌&gt;"</c>
/// </para>
/// </remarks>
public sealed class McpEndpoint(IPluginContext context, McpServerSettingsStore store) : IAsyncDisposable
{
    /// <summary>本实现对齐的协议版本。客户端要更老的版本时按它要的回。</summary>
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>一个外部会话闲置多久后丢掉。</summary>
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromHours(2);

    // ———————————————————— 资源预算 ————————————————————
    //
    // 这几条以前一条都没有。合法的本机客户端也会误用(脚本跑飞、把整个仓库塞进一个
    // tools/call、并发压测),而这里跑在用户的桌面程序进程里 —— 撑爆的是他正在用的终端。

    /// <summary>请求正文上限。超过直接 413,<b>不先整份读进内存再判断</b>。</summary>
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>同时处理的请求数上限。超出回 503,让客户端自己退避。</summary>
    private const int MaxConcurrentRequests = 8;

    /// <summary>单个请求的处理时限。tools/call 可能是一条远程命令,给得宽,但不能没有。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>同时保有的外部会话数上限。</summary>
    private const int MaxSessions = 32;

    /// <summary>闲置会话的清扫间隔(以前只在 initialize 时顺带扫一次)。</summary>
    private static readonly TimeSpan SessionSweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, McpToolHost> _sessions = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>并发请求闸。</summary>
    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);

    /// <summary>在跑的请求处理任务;停端点时要等它们收completed,不能把监听一关就走人。</summary>
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Timer? _sweeper;
    private McpServerSettings _settings = new();
    private string _token = "";

    /// <summary>此刻在不在监听。</summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>给设置页显示的接入地址(没开时为 null)。</summary>
    public string? Url => IsRunning ? $"http://127.0.0.1:{_settings.Port}/mcp" : null;

    /// <summary>读设置并把服务端调到该有的样子。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            McpServerSettings settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            await StopCoreAsync().ConfigureAwait(false);
            if (!settings.Enabled)
            {
                return;
            }
            _settings = settings;
            _token = await store.TokenAsync(cancellationToken).ConfigureAwait(false);
            var listener = new HttpListener();
            // 只在回环上开。这里刻意不提供"绑 0.0.0.0"的选项 —— 把一个能在生产机上敲命令的
            // 接口开到局域网上,不是一个应该由勾选框决定的事。
            listener.Prefixes.Add($"http://127.0.0.1:{settings.Port}/");
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _loop = AcceptLoopAsync(listener, _cts.Token);
            // 闲置会话以前只在 initialize 里顺带扫 —— 一个再也不来的客户端留下的会话
            // 因此永远不会被清掉。改成定时扫。
            _sweeper = new Timer(_ => EvictIdleSessions(), null, SessionSweepInterval, SessionSweepInterval);
            context.Log.Info($"MCP server listening on {Url} (mode {settings.Mode}, approval {settings.Approval}).");
        }
        catch (HttpListenerException ex)
        {
            context.Log.Error($"MCP server could not listen on port {_settings.Port}: {ex.Message}");
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>停掉服务端。</summary>
    public async Task StopAsync()
    {
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_sweeper is { } sweeper)
        {
            await sweeper.DisposeAsync().ConfigureAwait(false);
            _sweeper = null;
        }
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        _listener?.Close();
        _listener = null;
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                // 关监听时的正常收摊
            }
        }
        _loop = null;

        // 在跑的请求要等完。以前只等 accept 循环 —— 于是"停掉 MCP 服务"之后,
        // 已经进来的 tools/call 还在后台继续在用户的机器上执行命令。
        Task[] pending = [.. _inFlight.Keys];
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                context.Log.Warn("MCP server: some in-flight requests did not finish within the stop budget.");
            }
        }
        _inFlight.Clear();

        _cts?.Dispose();
        _cts = null;
        _sessions.Clear();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext http;
            try
            {
                http = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // 监听被关掉了
            }
            // 一次请求一个任务:tools/call 可能跑几十秒(一条远程命令),不能占着 accept 循环。
            // 任务要**登记**下来:停端点时得等它们收完,不能把监听一关就当结束了。
            Task worker = Task.Run(() => ServeAsync(http, cancellationToken), cancellationToken);
            _inFlight[worker] = 0;
            _ = worker.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 处理一个请求:先抢并发名额,再加单请求时限,最后一定关掉响应。
    /// </summary>
    private async Task ServeAsync(HttpListenerContext http, CancellationToken cancellationToken)
    {
        bool slotHeld = false;
        try
        {
            // 名额满了就当场回绝,而不是把请求攒在进程里 —— 攒着的每一个都占着一个
            // HttpListenerContext 和一份正文缓冲。
            slotHeld = await _requestSlots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (!slotHeld)
            {
                http.Response.StatusCode = 503;
                http.Response.AddHeader("Retry-After", "1");
                await WriteAsync(http.Response, """{"error":"server busy"}""", cancellationToken).ConfigureAwait(false);
                return;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);
            await HandleAsync(http, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停端点或请求超时:连接关掉即可,客户端会自己看到断开。
        }
        catch (Exception ex)
        {
            context.Log.Warn($"MCP server: request failed: {ex.Message}");
        }
        finally
        {
            if (slotHeld)
            {
                _requestSlots.Release();
            }
            try
            {
                http.Response.Close();
            }
            catch (Exception)
            {
                // 客户端先断了
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext http, CancellationToken cancellationToken)
    {
        HttpListenerRequest request = http.Request;
        HttpListenerResponse response = http.Response;
        if (!Authorized(request))
        {
            response.StatusCode = 401;
            response.AddHeader("WWW-Authenticate", "Bearer");
            await WriteAsync(response, """{"error":"unauthorized"}""", cancellationToken).ConfigureAwait(false);
            return;
        }
        switch (request.HttpMethod)
        {
            case "DELETE":
                if (SessionId(request) is { Length: > 0 } ending)
                {
                    _sessions.TryRemove(ending, out _);
                }
                response.StatusCode = 200;
                return;

            case "GET":
                // 服务端不主动推消息,所以不开 SSE 流。协议允许这么回。
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST, DELETE");
                return;

            case "POST":
                break;

            default:
                response.StatusCode = 405;
                return;
        }

        // 正文有上限,而且**先看声明的长度再读**:整份读进来之后才判断,等于让任何本机进程
        // 都能凭一个请求把桌面程序的内存顶上去。没有 Content-Length 的分块请求由下面的
        // 限长读兜底。
        if (request.ContentLength64 > MaxRequestBytes)
        {
            response.StatusCode = 413;
            await WriteAsync(response, """{"error":"request body too large"}""", cancellationToken).ConfigureAwait(false);
            return;
        }
        string? body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            response.StatusCode = 413;
            await WriteAsync(response, """{"error":"request body too large"}""", cancellationToken).ConfigureAwait(false);
            return;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            response.StatusCode = 400;
            await WriteAsync(response, Error(null, -32700, $"Parse error: {ex.Message}"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                // 批量请求:MCP 客户端极少用,不支持也要说清楚而不是静默失败
                response.StatusCode = 400;
                await WriteAsync(response, Error(null, -32600, "Batched requests are not supported."), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            await DispatchAsync(http, root, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(HttpListenerContext http, JsonElement request, CancellationToken cancellationToken)
    {
        HttpListenerResponse response = http.Response;
        string method = request.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
        JsonElement? id = request.TryGetProperty("id", out JsonElement idElement) ? idElement : null;
        JsonElement parameters = request.TryGetProperty("params", out JsonElement p) ? p : default;

        // 通知(没有 id)一律 202,没有响应体
        if (id is null)
        {
            response.StatusCode = 202;
            return;
        }

        switch (method)
        {
            case "initialize":
                {
                    // 先扫一遍闲置的,再判上限:满了就明确回绝,而不是让会话字典一路涨下去。
                    EvictIdleSessions();
                    if (_sessions.Count >= MaxSessions)
                    {
                        response.StatusCode = 503;
                        await WriteAsync(response, Error(id, -32000, "Too many MCP sessions are open. Close some and retry."),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    string sessionId = Guid.NewGuid().ToString("n");
                    var host = new McpToolHost(context, _settings);
                    await host.AutoSelectAsync(cancellationToken).ConfigureAwait(false);
                    _sessions[sessionId] = host;
                    response.AddHeader("Mcp-Session-Id", sessionId);
                    string version = parameters.ValueKind == JsonValueKind.Object
                                     && parameters.TryGetProperty("protocolVersion", out JsonElement requested)
                        ? requested.GetString() ?? ProtocolVersion
                        : ProtocolVersion;
                    await WriteAsync(response, Result(id.Value, new
                    {
                        protocolVersion = version,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "velashell", title = "VelaShell", version = context.PluginVersion },
                        instructions = "Tools act on the SSH sessions the user has open in VelaShell. "
                                       + "Call list_sessions first, then use_session to pick one. "
                                       + host.Describe()
                    }), cancellationToken).ConfigureAwait(false);
                    return;
                }

            case "ping":
                await WriteAsync(response, Result(id.Value, new { }), cancellationToken).ConfigureAwait(false);
                return;

            case "tools/list":
                {
                    if (Resolve(http) is not { } host)
                    {
                        await WriteAsync(response, Error(id, -32001, "Unknown or expired session; call initialize first."),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    object[] tools =
                    [
                        .. host.Tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.JsonSchema
                    })
                    ];
                    await WriteAsync(response, Result(id.Value, new { tools }), cancellationToken).ConfigureAwait(false);
                    return;
                }

            case "tools/call":
                {
                    if (Resolve(http) is not { } host)
                    {
                        await WriteAsync(response, Error(id, -32001, "Unknown or expired session; call initialize first."),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    string name = parameters.ValueKind == JsonValueKind.Object
                                  && parameters.TryGetProperty("name", out JsonElement n)
                        ? n.GetString() ?? ""
                        : "";
                    var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("arguments", out JsonElement args)
                        && args.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in args.EnumerateObject())
                        {
                            arguments[property.Name] = property.Value;
                        }
                    }
                    try
                    {
                        string text = await host.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                        await WriteAsync(response, Result(id.Value, new
                        {
                            content = new[] { new { type = "text", text } },
                            isError = false
                        }), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 工具出错走 isError,不走 JSON-RPC 错误 —— 前者模型看得到并能改正,
                        // 后者多数客户端会直接把整轮判失败。
                        await WriteAsync(response, Result(id.Value, new
                        {
                            content = new[] { new { type = "text", text = ex.Message } },
                            isError = true
                        }), cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }

            default:
                await WriteAsync(response, Error(id, -32601, $"Method not found: {method}"), cancellationToken)
                    .ConfigureAwait(false);
                return;
        }
    }

    private McpToolHost? Resolve(HttpListenerContext http)
        => SessionId(http.Request) is { Length: > 0 } id && _sessions.TryGetValue(id, out McpToolHost? host)
            ? host
            : null;

    /// <summary>
    /// 限长读取请求正文;超过 <see cref="MaxRequestBytes" /> 返回 <see langword="null" />。
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxRequestBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private void EvictIdleSessions()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - SessionIdleTimeout;
        foreach (KeyValuePair<string, McpToolHost> entry in _sessions)
        {
            if (entry.Value.LastActivity < cutoff)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string? SessionId(HttpListenerRequest request) => request.Headers["Mcp-Session-Id"];

    /// <summary>令牌比对走定时安全比较 —— 本地端口谁都能敲,不给逐字节试探留缝。</summary>
    private bool Authorized(HttpListenerRequest request)
    {
        string? header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        byte[] presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        byte[] expected = Encoding.UTF8.GetBytes(_token);
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static string Result(JsonElement id, object result)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static string Error(JsonElement? id, int code, string message)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    private static async Task WriteAsync(HttpListenerResponse response, string payload,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _reloadGate.Dispose();
    }
}

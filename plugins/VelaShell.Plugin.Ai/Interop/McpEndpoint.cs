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

    private readonly ConcurrentDictionary<string, McpToolHost> _sessions = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
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
            // 一次请求一个任务:tools/call 可能跑几十秒(一条远程命令),不能占着 accept 循环
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(http, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    context.Log.Warn($"MCP server: request failed: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        http.Response.Close();
                    }
                    catch (Exception)
                    {
                        // 客户端先断了
                    }
                }
            }, cancellationToken);
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

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
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
                    string sessionId = Guid.NewGuid().ToString("n");
                    var host = new McpToolHost(context, _settings);
                    await host.AutoSelectAsync(cancellationToken).ConfigureAwait(false);
                    _sessions[sessionId] = host;
                    EvictIdleSessions();
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

using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// MCP 服务器连接管理:按配置连接启用的服务器,把其工具
/// (<see cref="McpClientTool" /> 本身就是 M.E.AI 的 AIFunction)并入 Agent 工具箱。
/// 连接按配置指纹缓存复用;连接失败短暂退避,避免每轮对话都重复拉起失败进程。
/// 非只读工具(MCP 注解 readOnlyHint != true)执行前走与 run_command 相同的审批。
/// </summary>
public sealed class McpManager(IPluginContext context) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(30);

    private sealed class Connection
    {
        public string Fingerprint = "";
        public McpClient? Client;
        public IList<McpClientTool> Tools = [];
        public string? Error;
        public DateTimeOffset FailedAt;
    }

    private readonly Dictionary<string, Connection> _connections = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>危险操作审批(与 AgentToolbox 共用同一交互)。未设置视为拒绝。</summary>
    public Func<ApprovalRequest, Task<bool>>? ApprovalHandler { get; set; }

    /// <summary>审批方式(与内置工具共用同一设置)。</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;

    /// <summary>把一条工具执行告警写进插件日志(供嵌套的工具包装器用,它拿不到 <c>context</c>)。</summary>
    internal void LogToolWarning(string message) => context.Log.Warn(message);

    /// <summary>
    /// 是不是那种"再试一次多半就好"的瞬时故障:网络层的断连 / 超时 / DNS 抖动。
    /// 用来决定 MCP 工具调用要不要自动重试 —— 语义类错误(参数不对、服务端 4xx)重试没意义。
    /// </summary>
    internal static bool IsTransientToolFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // HttpClient 超时抛的是 TaskCanceledException(OperationCanceledException 的子类);
            // 真正的"用户按停止"在调用点已按 cancellationToken 单独拦下,到这儿的都是内部超时。
            if (e is HttpRequestException or IOException or SocketException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 汇集全部启用服务器的工具。逐服务器容错:单个失败不影响其余,
    /// 错误以 "<c>名称: 原因</c>" 文本返回给调用方展示。
    /// </summary>
    public async Task<(List<AITool> Tools, List<string> Errors)> GetToolsAsync(
        IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();
        var errors = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = servers.Where(s => s.Enabled).ToList();

            // 被删除/停用的服务器:断开并移除
            foreach (string staleId in _connections.Keys.Except(wanted.Select(s => s.Id)).ToList())
            {
                await CloseAsync(_connections[staleId]).ConfigureAwait(false);
                _connections.Remove(staleId);
            }

            foreach (McpServerConfig server in wanted)
            {
                string fingerprint = JsonSerializer.Serialize(server);
                if (_connections.TryGetValue(server.Id, out Connection? existing) && existing.Fingerprint == fingerprint)
                {
                    if (existing.Client is not null)
                    {
                        CollectTools(tools, usedNames, server, existing.Tools);
                        continue;
                    }
                    if (DateTimeOffset.UtcNow - existing.FailedAt < FailureRetryDelay)
                    {
                        errors.Add($"{DisplayName(server)}: {existing.Error}");
                        continue;
                    }
                }
                if (existing is not null)
                {
                    await CloseAsync(existing).ConfigureAwait(false);
                    _connections.Remove(server.Id);
                }

                var connection = new Connection { Fingerprint = fingerprint };
                _connections[server.Id] = connection;
                try
                {
                    // 进程拉起/网络握手放线程池,避免占用 UI 线程
                    (connection.Client, connection.Tools) = await Task.Run(
                        () => ConnectAsync(server, cancellationToken), cancellationToken).ConfigureAwait(false);
                    CollectTools(tools, usedNames, server, connection.Tools);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    connection.Error = ex.Message;
                    connection.FailedAt = DateTimeOffset.UtcNow;
                    context.Log.Error($"MCP server '{DisplayName(server)}' connect failed.", ex);
                    errors.Add($"{DisplayName(server)}: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return (tools, errors);
    }

    /// <summary>设置页"测试":临时连接,返回工具名列表后即断开。</summary>
    public static async Task<IReadOnlyList<string>> ProbeAsync(McpServerConfig server, CancellationToken cancellationToken)
        => [.. (await RefreshToolsAsync(server, cancellationToken).ConfigureAwait(false)).Select(t => t.Name)];

    /// <summary>
    /// "更新工具库":连上去把这台服务器现在提供的工具列回来(名称 + 说明 + 只读注解)。
    /// 调用方负责写回 <see cref="McpServerConfig.KnownTools" /> —— 缓存下来,
    /// "配置工具"窗口才能在不连网的情况下列出勾选项。
    /// </summary>
    public static async Task<IReadOnlyList<McpToolInfo>> RefreshToolsAsync(McpServerConfig server,
        CancellationToken cancellationToken)
    {
        (McpClient client, IList<McpClientTool> tools) = await Task.Run(
            () => ConnectAsync(server, cancellationToken), cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return
            [
                .. tools.Select(t => new McpToolInfo
                {
                    Name = t.Name,
                    Description = Shorten(t.Description),
                    ReadOnly = t.ProtocolTool.Annotations?.ReadOnlyHint == true
                })
            ];
        }
    }

    /// <summary>工具说明只用于界面上那一行,过长没有意义。</summary>
    private static string Shorten(string? description)
    {
        string text = (description ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    /// <summary>断开全部连接(面板关闭时调用)。</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (Connection connection in _connections.Values)
            {
                await CloseAsync(connection).ConfigureAwait(false);
            }
            _connections.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 连接与工具包装 ----------

    private static async Task<(McpClient Client, IList<McpClientTool> Tools)> ConnectAsync(
        McpServerConfig server, CancellationToken outerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        timeout.CancelAfter(ConnectTimeout);
        McpClient client = await McpClient.CreateAsync(CreateTransport(server), cancellationToken: timeout.Token).ConfigureAwait(false);
        try
        {
            IList<McpClientTool> tools = await client
                .ListToolsAsync((ModelContextProtocol.RequestOptions?)null, timeout.Token)
                .ConfigureAwait(false);
            return (client, tools);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig server)
    {
        if (server.Transport == McpTransportType.Http)
        {
            Uri endpoint = ValidateHttpEndpoint(server.Url);
            Dictionary<string, string> headers = McpConfigParser.ParseHeaderLines(server.Headers);
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = DisplayName(server),
                AdditionalHeaders = headers.Count > 0 ? headers : null
                // TransportMode 默认 AutoDetect:先试 Streamable HTTP,再回落 SSE
            });
        }
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException("MCP command is empty.");
        }
        Dictionary<string, string?> env = McpConfigParser.ParseEnvironmentLines(server.EnvironmentVariables);
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            // npx/uvx 等脚本命令的 Windows cmd 包装由 SDK 处理
            Command = server.Command.Trim(),
            Arguments = McpConfigParser.SplitArguments(server.Arguments),
            // 空 = ~/.velashell;~ 要在这儿展开,Process.Start 不认它(见 McpWorkspace)
            WorkingDirectory = McpWorkspace.ResolveAndEnsure(server.WorkingDirectory),
            EnvironmentVariables = env.Count > 0 ? env : null,
            Name = DisplayName(server)
        });
    }

    /// <summary>
    /// 工具改名(前缀 = 服务器名,防多服务器同名冲突)并按只读注解决定是否包审批;
    /// 截断/清洗后仍撞名时追加序号(模型侧要求工具名唯一)。
    /// </summary>
    private void CollectTools(List<AITool> sink, HashSet<string> usedNames, McpServerConfig server, IList<McpClientTool> tools)
    {
        string prefix = McpConfigParser.SanitizeToolPrefix(server.Name);
        HashSet<string> disabled = new(
            server.DisabledTools.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        foreach (McpClientTool tool in tools)
        {
            // 用户屏蔽掉的工具直接不给模型看见:工具太多既占上下文又容易被误调
            if (disabled.Contains(tool.Name))
            {
                continue;
            }
            string name = $"{prefix}_{tool.Name}";
            if (name.Length > 64)
            {
                name = name[..64]; // OpenAI 函数名长度上限
            }
            for (int i = 2; !usedNames.Add(name); i++)
            {
                string suffix = $"_{i}";
                name = name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] + suffix : $"{name}{suffix}";
            }
            McpClientTool named = tool.WithName(name);
            // readOnlyHint 是远端服务器自己给出的建议性元数据,不能作为安全边界。恶意服务器
            // 完全可以把删除/上传工具伪装成只读;所有 MCP 工具统一经过本地审批策略。
            sink.Add(new ApprovalGatedFunction(named, DisplayName(server), this));
        }
    }

    /// <summary>
    /// HTTP MCP 端点只允许 HTTPS。为本机开发保留 loopback HTTP;鉴权请求头绝不能经普通
    /// 局域网 HTTP 明文发送。其它 URI scheme 也拒绝,避免 SDK 对非网络 URI 的意外解释。
    /// </summary>
    internal static Uri ValidateHttpEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Invalid MCP server URL. Use an absolute HTTPS URL.");
        }
        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "Insecure MCP server URL. HTTP is allowed only for localhost; use HTTPS for remote servers.");
        }
        return endpoint;
    }

    private static string DisplayName(McpServerConfig server)
        => string.IsNullOrWhiteSpace(server.Name) ? "(unnamed MCP)" : server.Name.Trim();

    private async Task CloseAsync(Connection connection)
    {
        if (connection.Client is { } client)
        {
            connection.Client = null;
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Log.Warn($"MCP client dispose failed: {ex.Message}");
            }
        }
    }

    /// <summary>非只读 MCP 工具的审批闸:批准后转调原工具,拒绝以文本告知模型。</summary>
    private sealed class ApprovalGatedFunction(McpClientTool inner, string serverName, McpManager owner)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // MCP 的 readOnlyHint 来自远端,只能用于展示,不能据此跳过本地审批。
            if (owner.Approval != ApprovalMode.Bypass)
            {
                if (owner.ApprovalHandler is not { } handler)
                {
                    return "No approval channel available; the call was not executed.";
                }
                // 记忆键到"服务器 + 工具名"为止:同一个工具会被反复调用,但换工具仍要点头
                var request = new ApprovalRequest($"mcp:{serverName} · {Name}", SerializeArguments(arguments),
                    $"mcp:{serverName}:{Name}");
                // 用户点停止时不再傻等审批
                if (!await handler(request).WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    return "The user DENIED this MCP tool call. Do not retry it; ask the user how to proceed.";
                }
            }

            // MCP 工具常常连着一台远端服务器,链路一抖(尤其在墙内)整条调用就抛。
            // 以前这里直接把异常放出去:这一轮里挂着的 tool_use 收不到结果,下一次请求就被服务端
            // 以 "'<tool>' cannot be absent" 挡回来 —— 整轮作废,还没法重试。
            // 现在:①瞬时网络故障自动重试几次(仅对服务器自称只读的工具,免得把有副作用的调用重放两遍);
            //       ②最终仍失败就把原因转成给模型看的文本(而不是抛)—— 工具调用始终有个结果,
            //         tool_use 不再悬空,模型可以自己决定再试一次还是如实告诉用户。
            // 只有用户按了停止(cancellationToken)才照旧抛出,交给外层统一收尾。
            bool retrySafe = inner.ProtocolTool.Annotations?.ReadOnlyHint == true;
            int maxAttempts = retrySafe ? 3 : 1;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientToolFailure(ex))
                {
                    owner.LogToolWarning(
                        $"MCP tool '{Name}' failed (attempt {attempt}/{maxAttempts}), retrying: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    owner.LogToolWarning($"MCP tool '{Name}' failed: {ex.Message}");
                    string hint = IsTransientToolFailure(ex)
                        ? " This looks like a transient network issue (the MCP server may be unreachable or blocked);"
                          + " you may retry the call."
                        : "";
                    return $"The MCP tool '{Name}' failed: {ex.Message}.{hint}"
                           + " Do not treat this as your own mistake — report what happened to the user if it keeps failing.";
                }
            }
        }

        private static string SerializeArguments(AIFunctionArguments arguments)
        {
            try
            {
                return arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments.ToDictionary(p => p.Key, p => p.Value));
            }
            catch
            {
                return string.Join(", ", arguments.Keys);
            }
        }
    }
}

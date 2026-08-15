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

    /// <summary>免审批开关(跟随用户的 Auto-approve 设置)。</summary>
    public bool AutoApprove { get; set; }

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
    {
        (McpClient client, IList<McpClientTool> tools) = await Task.Run(
            () => ConnectAsync(server, cancellationToken), cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return tools.Select(t => t.Name).ToList();
        }
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
            if (!Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out Uri? endpoint))
            {
                throw new InvalidOperationException("Invalid MCP server URL.");
            }
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
            WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory.Trim(),
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
            bool readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true;
            sink.Add(readOnly ? named : new ApprovalGatedFunction(named, DisplayName(server), this));
        }
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
            if (!owner.AutoApprove)
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
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
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

using System.ComponentModel;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary>
/// 一个外部 agent 连进来之后的会话状态:它选中了哪台服务器、能调哪些工具。
/// </summary>
/// <remarks>
/// <b>工具直接复用 <see cref="AgentToolbox" />。</b>VelaShell 自己的 agent 用的就是那一套,
/// 对外再抄一份只会让两边慢慢长歪。代价是工具签名里没有"服务器"这个参数
/// (工具箱靠 <see cref="AgentToolbox.SessionIdProvider" /> 拿会话),所以这里补一个
/// <c>use_session</c>:外部 agent 先 <c>list_sessions</c> 看有哪些,再 <c>use_session</c> 选一台。
///
/// <para><b>审批在这条路上没有界面。</b>外部 agent 跑在别的进程里,弹不出 VelaShell 的审批卡。
/// 所以 <see cref="ApprovalMode.Ask" /> 在这里等于"一律拒绝"(工具箱在
/// <c>ApprovalHandler</c> 为 null 时就是这个行为),用户要放开就得显式选
/// 只读放行或绕过审批 —— 这是一个明摆着的选择,而不是一个悄悄的默认。</para>
/// </remarks>
internal sealed class McpToolHost
{
    private readonly IPluginContext _context;
    private readonly McpServerSettings _settings;
    private readonly AgentToolbox _toolbox;
    private readonly List<string> _allowedTargets;
    private string? _sessionId;
    private string _target = "";

    /// <summary>暴露给外部 agent 的工具(建好就不变)。</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>最后一次活动时刻(空闲会话回收看它)。</summary>
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    public McpToolHost(IPluginContext context, McpServerSettings settings)
    {
        _context = context;
        _settings = settings;
        _allowedTargets =
        [
            .. settings.AllowedTargets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
        _toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => _sessionId,
            Approval = settings.Approval,
            DisabledTools = new HashSet<string>(
                settings.DisabledTools.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase)
            // ApprovalHandler 刻意不设:见类型注释
        };
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(UseSessionAsync, "use_session",
                "Select which of the user's VelaShell SSH sessions the other tools act on. "
                + "Call list_sessions first to see what is connected. "
                + "Accepts user@host:port, host:port or host.")
        };
        tools.AddRange(_toolbox.CreateTools(settings.Mode).OfType<AIFunction>());
        Tools = tools;
    }

    /// <summary>按名字调一个工具,返回给模型看的文本。</summary>
    public async Task<string> CallAsync(string name, IDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        LastActivity = DateTimeOffset.UtcNow;
        if (Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) is not { } tool)
        {
            throw new KeyNotFoundException($"Unknown tool: {name}");
        }
        object? result = await tool.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken)
            .ConfigureAwait(false);
        return result?.ToString() ?? "";
    }

    /// <summary>
    /// 选一台服务器。绑定的是<b>当下那条连接</b> —— 与桥接不同,MCP 会话本来就是短命的,
    /// 没必要为它做"过夜后重新解析"。
    /// </summary>
    private async Task<string> UseSessionAsync(
        [Description("Which session to use: user@host:port, host:port or just host.")] string target,
        CancellationToken cancellationToken = default)
    {
        if (_allowedTargets.Count > 0
            && !_allowedTargets.Any(a => string.Equals(a, target, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{target}' is not on the allowlist configured in VelaShell. "
                   + $"Allowed: {string.Join(", ", _allowedTargets)}";
        }
        if (await SessionTargets.ResolveAsync(_context, target, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            string list = await SessionTargets.DescribeAsync(_context, cancellationToken).ConfigureAwait(false);
            return list.Length == 0
                ? "No sessions are connected in VelaShell right now."
                : $"No connected session matches '{target}'. Connected sessions:\n{list}";
        }
        _sessionId = session.SessionId;
        _target = SessionTargets.Format(session);
        return $"Now acting on {_target}.";
    }

    /// <summary>
    /// 只有一台连着时,免掉 <c>use_session</c> 这一步。
    /// </summary>
    /// <remarks>
    /// 纯粹是省事:外部 agent 一上来就 <c>run_command</c> 是很自然的写法,
    /// 而"只有一台"时并不存在选错的风险。多于一台就老老实实让它自己选。
    /// </remarks>
    public async Task AutoSelectAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not null)
        {
            return;
        }
        IReadOnlyList<SessionInfo> sessions = await _context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        List<SessionInfo> connected = [.. sessions.Where(s => s.State == SessionState.Connected)];
        if (connected.Count != 1)
        {
            return;
        }
        string only = SessionTargets.Format(connected[0]);
        if (_allowedTargets.Count > 0
            && !_allowedTargets.Any(a => string.Equals(a, only, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _sessionId = connected[0].SessionId;
        _target = only;
    }

    /// <summary>给 <c>initialize</c> 的服务端说明里带一句"现在对着哪台机器"。</summary>
    public string Describe()
        => _target.Length > 0
            ? $"VelaShell is acting on {_target}. Mode: {_settings.Mode}, approval: {_settings.Approval}."
            : $"No session selected yet — call list_sessions, then use_session. Mode: {_settings.Mode}, approval: {_settings.Approval}.";
}

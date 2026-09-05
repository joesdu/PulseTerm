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
///
/// <para><b><see cref="McpServerSettings.Scope" /> 挡在每一次工具调用上。</b>
/// 从前那份 <c>user@host:port</c> 清单只挡 <c>use_session</c>,而工具箱里九个工具都收可选的
/// <c>session_id</c> —— 外部 agent <c>list_sessions</c> 拿到 id 直接传就绕过去了。现在它是一个
/// <see cref="ISessionScope" />,挂在 <see cref="AgentToolbox.Scope" /> 上,每一次调用都要过。
/// <b>默认值仍是不限范围</b> —— 这条路的边界是回环地址、令牌与只读挡位,把用户自己机器上的
/// agent 一起收紧挡不住任何攻击者,只挡得住用户自己。</para>
/// </remarks>
internal sealed class McpToolHost
{
    private readonly IPluginContext _context;
    private readonly McpServerSettings _settings;
    private readonly AgentToolbox _toolbox;
    private readonly ISessionScope? _scope;
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
        _scope = settings.ResolveScope(context);
        _toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => _sessionId,
            Scope = _scope,
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
        object? result = await tool.InvokeAsync([with(arguments)], cancellationToken)
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
        // 范围外的会话在 ResolveAsync 里就当作不存在,这里不再单独比一遍字符串:
        // 一句"它不在名单上,名单是 A、B、C"会把拒绝消息本身变成一个探测接口。
        if (await SessionTargets.ResolveAsync(_context, target, cancellationToken, _scope).ConfigureAwait(false) is not { } session)
        {
            string list = await SessionTargets.DescribeAsync(_context, cancellationToken, _scope).ConfigureAwait(false);
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
        var connected = new List<SessionInfo>();
        foreach (SessionInfo session in sessions.Where(s => s.State == SessionState.Connected))
        {
            // 数的是"允许操作的里面有几台",不是"一共连着几台" —— 否则用户连着两台、
            // 清单里只写了一台时,本该没有歧义的那一台反而选不上。
            if (_scope is not null && !await _scope.AllowsLiveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            connected.Add(session);
        }
        if (connected.Count != 1)
        {
            return;
        }
        _sessionId = connected[0].SessionId;
        _target = SessionTargets.Format(connected[0]);
    }

    /// <summary>给 <c>initialize</c> 的服务端说明里带一句"现在对着哪台机器"。</summary>
    public string Describe()
        => _target.Length > 0
            ? $"VelaShell is acting on {_target}. Mode: {_settings.Mode}, approval: {_settings.Approval}.{CanOpen}"
            : $"No session selected yet — call list_sessions, then use_session. Mode: {_settings.Mode}, approval: {_settings.Approval}.{CanOpen}";

    /// <summary>
    /// 一台都没连着时还有没有别的路走。
    /// </summary>
    /// <remarks>
    /// 只在 <c>open_session</c> 真的走得通时才说,而这条路上"走得通"只有<b>绕过审批</b>一种:
    /// 没有审批界面,<see cref="ApprovalMode.Ask" /> 等于一律拒绝(见类型注释);
    /// <see cref="ApprovalMode.ReadOnlyAuto" /> 只放行确定无副作用的命令,开连接不在其列。
    /// 把"你可以自己连一台"写进说明却让它每次都撞墙,比不写更糟。
    /// </remarks>
    private string CanOpen
        => _settings.Approval == ApprovalMode.Bypass
           && Tools.Any(t => string.Equals(t.Name, "open_session", StringComparison.Ordinal))
            ? " A machine that is saved in VelaShell but not connected can be connected with"
              + " list_saved_sessions + open_session; the user is asked to confirm on their desktop."
            : "";
}

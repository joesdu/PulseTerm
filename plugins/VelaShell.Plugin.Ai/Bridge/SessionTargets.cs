using System.Text;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 在"绑定的服务器"与宿主的会话 id 之间来回换。
/// </summary>
/// <remarks>
/// <b>绑定存的是 <c>user@host:port</c>,不是 SessionId。</b>
/// SessionId 是一次连接的不透明 id —— 用户断线重连、或者早上重开一次 VelaShell,它就换了;
/// 存 id 的话群里的绑定第二天全部失效,而且失效得毫无提示。存"哪台机器",
/// 每轮临时解析成当下那条活着的会话,才是能过夜的做法。
/// </remarks>
public static class SessionTargets
{
    /// <summary>把一条会话写成绑定用的目标串。</summary>
    public static string Format(SessionInfo session) => $"{session.Username}@{session.Host}:{session.Port}";

    /// <summary>
    /// 找一条与 <paramref name="target" /> 对得上、且<b>已连上</b>的会话。
    /// 目标串可以省略用户名与端口(<c>host</c> / <c>host:port</c> / <c>user@host</c> 都认)。
    /// </summary>
    /// <param name="context">插件上下文。</param>
    /// <param name="target"><c>[user@]host[:port]</c>。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="scope">
    /// 范围闸门(<see langword="null" /> = 不限制)。范围外的会话在这里就当作<b>不存在</b>,
    /// 而不是"找到了但不给" —— 后者会让 <c>/use 某台</c> 的回复变成一个探测接口:
    /// 试一个主机名就能问出它在不在用户的机器列表里。
    /// </param>
    public static async Task<SessionInfo?> ResolveAsync(
        IPluginContext context, string target, CancellationToken cancellationToken, ISessionScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        (string? user, string host, int? port) = Parse(target);
        foreach (SessionInfo session in sessions)
        {
            if (session.State != SessionState.Connected)
            {
                continue;
            }
            if (!string.Equals(session.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (port is { } p && session.Port != p)
            {
                continue;
            }
            if (user is { } u && !string.Equals(session.Username, u, StringComparison.Ordinal))
            {
                continue;
            }
            if (scope is not null && !await scope.AllowsLiveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            return session;
        }
        return null;
    }

    /// <summary>列出当前连上的会话,给 IM 里的 <c>/sessions</c> 用。</summary>
    /// <param name="context">插件上下文。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="scope">范围闸门(<see langword="null" /> = 不限制);范围外的不列出来。</param>
    public static async Task<string> DescribeAsync(
        IPluginContext context, CancellationToken cancellationToken, ISessionScope? scope = null)
    {
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (SessionInfo session in sessions.Where(s => s.State == SessionState.Connected))
        {
            if (scope is not null && !await scope.AllowsLiveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            sb.Append("• ").AppendLine(Format(session));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>拆 <c>[user@]host[:port]</c>。拆不出端口就返回 null(表示"不挑端口")。</summary>
    private static (string? User, string Host, int? Port) Parse(string target)
    {
        string rest = target.Trim();
        string? user = null;
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = rest[..at];
            rest = rest[(at + 1)..];
        }
        int colon = rest.LastIndexOf(':');
        // IPv6 字面量里冒号成堆,只有带方括号时才认最后那个冒号是端口分隔符
        bool portish = colon > 0 && (rest.IndexOf(':') == colon || rest.StartsWith('['));
        if (portish && int.TryParse(rest[(colon + 1)..], out int port))
        {
            return (user, rest[..colon].Trim('[', ']'), port);
        }
        return (user, rest.Trim('[', ']'), null);
    }
}

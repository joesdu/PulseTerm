using System.Collections.Concurrent;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="ISessionsApi" /> 的桥接实现:宿主会话列表的脱敏投影,不含任何凭据;
/// 以及「请求宿主打开一条已保存会话」的宿主侧闸门。
/// </summary>
/// <remarks>
/// 开会话是一次实打实的权限扩张,闸门全部焊在这一侧(契约见 <see cref="ISessionsApi" />):
/// <list type="bullet">
/// <item>只能开**已保存的配置** —— 连哪些机器由用户先在宿主里定下来,插件给不出主机名端口。</item>
/// <item>凭据一个字节不经过插件:这里拿到的是 <see cref="SessionProfile" />,插件那边只有一个不透明 id。</item>
/// <item>宿主可以拒绝,且拒绝是契约的一部分(<see cref="PluginPermissionDeniedException" />)。</item>
/// <item>只关得掉**本插件自己开的**会话 —— 一个能挂断别人正在用的终端的接口,不该存在。</item>
/// </list>
/// 每插件一个实例(<see cref="_openedByThisPlugin" /> 是按插件计的归属账本)。
/// </remarks>
internal sealed class SessionsCapability(
    string pluginId,
    ISshConnectionService connections,
    ISessionRepository? profiles = null,
    IPluginSessionOpener? opener = null) : ISessionsApi
{
    /// <summary>本插件经 <see cref="OpenAsync" /> 真正开出来的会话 id(复用到的**不算**)。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _openedByThisPlugin = new();

    /// <summary>把宿主会话对象投影为对插件安全的 DTO(仅连接元数据)。</summary>
    internal static SessionInfo Map(SshSession session) => new(
        session.SessionId.ToString(),
        session.ConnectionInfo.Host,
        session.ConnectionInfo.Port,
        session.ConnectionInfo.Username,
        session.Status switch
        {
            SessionStatus.Connecting => SessionState.Connecting,
            SessionStatus.Connected => SessionState.Connected,
            SessionStatus.Error => SessionState.Error,
            _ => SessionState.Disconnected
        },
        new DateTimeOffset(session.CreatedAt, TimeSpan.Zero),
        session.ConnectedAt is { } connectedAt ? new DateTimeOffset(connectedAt, TimeSpan.Zero) : null);

    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionInfo> sessions = [.. connections.Sessions.Select(Map)];
        return Task.FromResult(sessions);
    }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        SessionInfo? result = Guid.TryParse(sessionId, out Guid id) && connections.GetSession(id) is { } session
            ? Map(session)
            : null;
        return Task.FromResult(result);
    }

    /// <summary>
    /// 已保存配置的脱敏快照。只报 SSH 配置:其余协议(SFTP / FTP / 插件协议)
    /// <see cref="OpenAsync" /> 根本开不出 <see cref="SessionInfo" /> 来,
    /// 列出去只会让插件拿到一个注定失败的 id。
    /// </summary>
    public async Task<IReadOnlyList<SavedSessionInfo>> ListSavedAsync(CancellationToken cancellationToken = default)
    {
        if (profiles is null)
        {
            return [];
        }
        cancellationToken.ThrowIfCancellationRequested();
        List<SessionProfile> saved = await profiles.GetAllSessionsAsync().ConfigureAwait(false);
        List<ServerGroup> groups = await profiles.GetAllGroupsAsync().ConfigureAwait(false);
        var groupNames = groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
        return
        [
            .. saved.Where(p => p.ConnectionType == ConnectionType.SSH)
                    .Select(p => new SavedSessionInfo(
                        p.Id.ToString(),
                        string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name,
                        p.Host,
                        p.Port,
                        p.Username,
                        p.GroupId is { } groupId && groupNames.TryGetValue(groupId, out string? name) ? name : null))
        ];
    }

    public async Task<SessionInfo> OpenAsync(string savedSessionId, SessionOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // 理由是给用户看的,不是给日志看的:空理由等于把确认框变成一个只能盲点的按钮。
        // 与其让用户面对一句空白,不如在这里就把这次调用判成插件的编码错误。
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Reason);
        if (profiles is null || !Guid.TryParse(savedSessionId, out Guid profileId)
            || await profiles.GetSessionAsync(profileId).ConfigureAwait(false) is not { } profile)
        {
            throw new PluginSessionNotFoundException(savedSessionId,
                $"No saved session configuration with id '{savedSessionId}'.");
        }
        if (profile.ConnectionType != ConnectionType.SSH)
        {
            throw new PluginSessionOpenException(savedSessionId,
                $"Saved session '{savedSessionId}' is not an SSH configuration.");
        }
        // 复用:这条配置已经有连着的会话时默认直接给回去。它**不进**归属账本 ——
        // 用户自己开的那个标签页不归插件管,CloseAsync 关不掉它(契约如此)。
        if (options.ReuseConnected && FindConnected(profile) is { } existing)
        {
            return Map(existing);
        }
        if (opener is null)
        {
            // 没有界面可问 = 不放行。绝不静默授权(与终端回写授权闸同一条纪律)。
            throw new PluginPermissionDeniedException(
                $"This host cannot ask the user to confirm opening sessions; plugin '{pluginId}' was denied.");
        }
        PluginSessionOpenResult result;
        try
        {
            result = await opener.OpenAsync(pluginId, profile, options.Reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginSessionOpenException(savedSessionId, ex.Message, ex);
        }
        switch (result.Outcome)
        {
            case PluginSessionOpenOutcome.Denied:
                throw new PluginPermissionDeniedException(result.Error ?? "The user denied the request.");
            case PluginSessionOpenOutcome.Opened when connections.GetSession(result.SessionId) is { } opened:
                _openedByThisPlugin[result.SessionId] = 0;
                return Map(opened);
            default:
                throw new PluginSessionOpenException(savedSessionId,
                    result.Error ?? "The host could not open the session.");
        }
    }

    public async Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out Guid id) || !_openedByThisPlugin.ContainsKey(id))
        {
            throw new PluginPermissionDeniedException(
                $"Session '{sessionId}' was not opened by plugin '{pluginId}'.");
        }
        // 幂等:用户先手动关了不算错。归属登记随之撤销 —— 会话 id 不复用,留着只是让账本长胖。
        _openedByThisPlugin.TryRemove(id, out _);
        if (connections.GetSession(id) is null)
        {
            return;
        }
        if (opener is not null)
        {
            await opener.CloseAsync(id, cancellationToken).ConfigureAwait(false);
            return;
        }
        await connections.DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>找一条与该配置对得上的、已连上的会话。</summary>
    /// <remarks>
    /// 用户名为空的配置(留到连接时再问)只按主机 + 端口配对:拿空串去比对
    /// 一条都配不上,于是每次都新开一条,“复用”形同虚设。
    /// </remarks>
    private SshSession? FindConnected(SessionProfile profile) =>
        connections.Sessions.FirstOrDefault(s =>
            s.Status == SessionStatus.Connected
            && string.Equals(s.ConnectionInfo.Host, profile.Host, StringComparison.OrdinalIgnoreCase)
            && s.ConnectionInfo.Port == profile.Port
            && (string.IsNullOrWhiteSpace(profile.Username)
                || string.Equals(s.ConnectionInfo.Username, profile.Username, StringComparison.Ordinal)));
}

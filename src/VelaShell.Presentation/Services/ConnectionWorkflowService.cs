using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 连接工作流服务:统筹会话配置的加载、保存、连接测试与实际连接,
/// 并在连接前后处理密码持久化策略、跳板链构建与历史/审计记录。
/// </summary>
public sealed class ConnectionWorkflowService(
    ISessionRepository sessionRepository,
    ISshConnectionService sshConnectionService,
    IRecentConnectionService? recentConnections = null,
    IAuditLogService? auditLog = null,
    ISettingsService? settingsService = null)
    : IConnectionWorkflowService
{
    private readonly ISessionRepository _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
    private readonly ISshConnectionService _sshConnectionService = sshConnectionService ?? throw new ArgumentNullException(nameof(sshConnectionService));

    /// <summary>获取全部已保存的会话配置,按最近连接时间倒序、再按名称升序排列。</summary>
    public async Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SessionProfile> sessions = await _sessionRepository.GetAllSessionsAsync().ConfigureAwait(false);
        return [.. sessions
               .OrderByDescending(profile => profile.LastConnectedAt)
               .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>校验并保存会话配置(保存不强制凭据),按记住密码策略决定是否落盘密码。</summary>
    public async Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        // 保存不要求凭据 —— 未勾选“记住密码”的配置在连接时再询问。
        ValidateProfile(profile, false);
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(profile).ConfigureAwait(false)).ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc />
    public PluginConnectionProbe? PluginProbe { get; set; }

    /// <summary>
    /// 测试连接:建立后立即断开,返回成功与否及失败原因,不抛出连接异常。
    /// <para>
    /// 插件连接类型走**另一条**路。它们的握手不是 SSH:拿 SSH 去连 Redis 的 6379
    /// 或 S3 的 443,只会在 TCP 连上之后卡在版本交换里,最后报一个与真实原因毫无
    /// 关系的"连接超时" —— 用户会以为端口不通,于是去查防火墙。没有探针可用时
    /// 宁可明说"这种连接类型测不了",也不给一个假的失败原因。
    /// </para>
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ConnectionType == ConnectionType.Plugin)
        {
            if (PluginProbe is not { } probe)
            {
                return new(false, Strings.Get("Plugin_TestUnavailable"));
            }
            try
            {
                await probe(profile, cancellationToken).ConfigureAwait(false);
                return new(true);
            }
            catch (Exception ex)
            {
                return new(false, ex.Message);
            }
        }
        try
        {
            SshSession session = await _sshConnectionService
                                       .ConnectAsync(await BuildConnectionInfoAsync(profile).ConfigureAwait(false), cancellationToken)
                                       .ConfigureAwait(false);
            await _sshConnectionService.DisconnectAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>连接指定配置:成功后更新最近连接时间并持久化,同时记录连接历史与审计。</summary>
    public async Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        SshSession session;
        try
        {
            session = await _sshConnectionService
                            .ConnectAsync(await BuildConnectionInfoAsync(profile).ConfigureAwait(false), cancellationToken)
                            .ConfigureAwait(false);
        }
        catch
        {
            await RecordHistoryAsync(profile, startedAt, false).ConfigureAwait(false);
            throw;
        }
        profile.LastConnectedAt = DateTime.UtcNow;
        await _sessionRepository.SaveSessionAsync(await WithPersistablePasswordAsync(profile).ConfigureAwait(false)).ConfigureAwait(false);
        await RecordHistoryAsync(profile, startedAt, true).ConfigureAwait(false);
        return session;
    }

    /// <summary>断开指定会话 Id 对应的 SSH 连接。</summary>
    public Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) => _sshConnectionService.DisconnectAsync(sessionId, cancellationToken);

    /// <summary>
    /// “记住密码”未勾选、或全局“记住密码”(设置 → 常规 → 隐私与安全)关闭时,
    /// 持久化副本不包含密码/口令(仅本次连接使用)。
    /// </summary>
    private async Task<SessionProfile> WithPersistablePasswordAsync(SessionProfile profile)
    {
        bool remember = profile.RememberPassword;
        if (remember && settingsService is not null)
        {
            try
            {
                AppSettings settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                remember = settings.General.RememberPasswords;
            }
            catch
            {
                // 设置不可读时沿用配置自身的勾选。
            }
        }
        if (remember)
        {
            return profile;
        }
        return new()
        {
            Id = profile.Id,
            ConnectionType = profile.ConnectionType,
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = null,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            GroupId = profile.GroupId,
            LastConnectedAt = profile.LastConnectedAt,
            Tags = [.. profile.Tags],
            // 保留配置自身的勾选:全局开关只影响是否落盘,不改写每条配置的选择。
            RememberPassword = profile.RememberPassword,
            JumpHostProfileId = profile.JumpHostProfileId,
            Ftp = profile.Ftp?.Clone(),
            PluginProtocolId = profile.PluginProtocolId,
            PluginSettings = SessionProfile.CloneSettings(profile.PluginSettings),
            PluginSecrets = SessionProfile.CloneSettings(profile.PluginSecrets)
        };
    }

    /// <summary>连接结果写入连接历史与审计日志(SonnetDB 时序),失败不影响主流程。</summary>
    private async Task RecordHistoryAsync(SessionProfile profile, DateTimeOffset startedAt, bool success)
    {
        if (auditLog is not null)
        {
            try
            {
                await auditLog.WriteAsync(new()
                {
                    Timestamp = startedAt,
                    Category = "connection",
                    Action = success ? "connect" : "connect-failed",
                    ProfileId = profile.Id,
                    Detail = $"{profile.Username}@{profile.Host}:{profile.Port}"
                }).ConfigureAwait(false);
            }
            catch
            {
                // 审计写入失败不阻塞连接。
            }
        }
        if (recentConnections is null)
        {
            return;
        }
        try
        {
            string groupName = string.Empty;
            if (profile.GroupId is { } groupId)
            {
                List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync().ConfigureAwait(false);
                groupName = groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? string.Empty;
            }
            await recentConnections.RecordAsync(new()
            {
                ProfileId = profile.Id,
                ConnectionType = profile.ConnectionType,
                Name = string.IsNullOrWhiteSpace(profile.Name)
                           ? $"{profile.Username}@{profile.Host}"
                           : profile.Name,
                GroupName = groupName,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                ConnectedAt = startedAt,
                Success = success,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            }).ConfigureAwait(false);
        }
        catch
        {
            // 历史记录失败不阻塞连接。
        }
    }

    /// <summary>
    /// 把配置解析成连接信息;JumpHostProfileId 链递归展开为嵌套 JumpHost
    /// (最多 5 跳,带环检测)。跳板配置必须已保存凭据,否则该跳认证会失败。
    /// </summary>
    private async Task<ConnectionInfo> BuildConnectionInfoAsync(SessionProfile profile)
    {
        var visited = new HashSet<Guid> { profile.Id };
        return await BuildChainAsync(profile, visited, 0).ConfigureAwait(false);
    }

    private async Task<ConnectionInfo> BuildChainAsync(SessionProfile profile, HashSet<Guid> visited, int depth)
    {
        ConnectionInfo? jump = null;
        if (profile.JumpHostProfileId is not { } jumpId)
        {
            return new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password,
                PrivateKeyPath = profile.PrivateKeyPath,
                PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
                JumpHost = jump
            };
        }
        if (depth >= 5)
        {
            throw new InvalidOperationException(Strings.Get("Svc_JumpChainTooLong"));
        }
        if (!visited.Add(jumpId))
        {
            throw new InvalidOperationException(Strings.Get("Svc_JumpChainLoop"));
        }
        SessionProfile jumpProfile = await _sessionRepository.GetSessionAsync(jumpId).ConfigureAwait(false) ?? throw new InvalidOperationException(Strings.Get("Svc_JumpHostMissing"));
        jump = await BuildChainAsync(jumpProfile, visited, depth + 1).ConfigureAwait(false);
        return new()
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = profile.Password,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            JumpHost = jump
        };
    }

    private static void ValidateProfile(SessionProfile profile, bool requireCredentials = true)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException(Strings.Get("Svc_ProfileNameRequired"), nameof(profile));
        }
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            throw new ArgumentException(Strings.Get("Svc_HostRequired"), nameof(profile));
        }
        // 「必须填用户名」这条并非对所有协议成立:FTP 匿名登录与插件协议的匿名访问
        // (S3 的公开只读桶)都不需要。连接配置页的保存按钮已按同一口径放行,
        // 这里再无条件拦一道,结果就是按钮亮着、一保存就抛。
        bool anonymousAllowed = profile.ConnectionType == ConnectionType.Plugin
                                || (profile.ConnectionType == ConnectionType.FTP && profile.Ftp?.Anonymous == true);
        if (!anonymousAllowed && string.IsNullOrWhiteSpace(profile.Username))
        {
            throw new ArgumentException(Strings.Get("Svc_UsernameRequired"), nameof(profile));
        }
        if (profile.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), Strings.Get("Svc_PortRange"));
        }
        if (!requireCredentials)
        {
            return;
        }
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (profile.AuthMethod == AuthMethod.Password && string.IsNullOrWhiteSpace(profile.Password))
        {
            throw new ArgumentException(Strings.Get("Svc_PasswordRequired"), nameof(profile));
        }
        if (profile.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new ArgumentException(Strings.Get("Svc_PrivateKeyRequired"), nameof(profile));
        }
    }
}

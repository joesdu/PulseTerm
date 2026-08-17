using System.Collections.Concurrent;
using System.Diagnostics;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 插件协议的宿主适配器:对内把 <see cref="IProtocolFileSystem" /> 翻译成宿主的
/// <see cref="ISftpService" />,对外再补上会话生命周期(<see cref="IPluginProtocolSessionService" />)。
/// <para>
/// 这一层是整个插件化的关键接缝。<see cref="ISftpService" /> 的每个成员都以
/// <c>Guid sessionId</c> 为键、返回协议无关的 <see cref="RemoteFileInfo" />,
/// 因此双栏浏览器、传输队列、限速、拖放、冲突策略对"协议来自插件"这件事完全无感 ——
/// 这正是当年把 FTP 接进来时立下的接缝,插件协议只是又走了一遍。
/// </para>
/// <para>
/// 三件事刻意放在宿主侧而不是让每个插件各写一遍:
/// 进度节流(<see cref="TransferProgressThrottle" />,含并发乱序下的单调收敛)、
/// SDK 异常到 Core 异常族的翻译、以及协议被注销时的会话收尾。
/// </para>
/// </summary>
/// <param name="registry">协议注册表。</param>
public sealed class PluginProtocolFileService(PluginProtocolRegistry registry) : ISftpService, IPluginProtocolSessionService
{
    /// <summary>一条已建立的插件协议会话。</summary>
    /// <param name="Descriptor">协议描述(异常翻译要用到它的证书字段声明)。</param>
    /// <param name="FileSystem">协议实现。</param>
    /// <param name="Key">交给插件的会话键(不透明字符串)。</param>
    private sealed record Session(ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem, string Key);

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private int _subscribed;

    /// <inheritdoc />
    public event EventHandler<PluginProtocolSessionStateChange>? SessionStateChanged;

    /// <inheritdoc />
    public bool OwnsSession(Guid sessionId) => _sessions.ContainsKey(sessionId);

    /// <inheritdoc />
    public async Task<Guid> OpenSessionAsync(SessionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureSubscribed();
        string? protocolId = profile.PluginProtocolId;
        PluginProtocolRegistration? registration = await registry.ResolveAsync(protocolId).ConfigureAwait(false) ?? throw new PluginProtocolUnavailableException(protocolId,
                string.IsNullOrWhiteSpace(protocolId)
                    ? "This session profile does not name a plugin protocol."
                    : $"Protocol '{protocolId}' is not available. Install or enable the plugin that provides it.");
        // 终端协议(Telnet / 串口…)没有文件系统:这条会话该由终端服务打开,
        // 走到这里说明分派点漏判了 —— 明说,别以 NullReference 收场。
        if (registration.FileSystem is not { } fileSystem)
        {
            throw new PluginProtocolUnavailableException(protocolId,
                $"Protocol '{protocolId}' is a terminal protocol and has no file system.");
        }

        var sessionId = Guid.NewGuid();
        string key = sessionId.ToString("N");
        var request = new ProtocolConnectRequest
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            Password = profile.Password ?? string.Empty,
            Settings = BuildSettings(registration.Descriptor, profile),
            DisplayName = profile.Name
        };
        try
        {
            await fileSystem.ConnectAsync(key, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, registration.Descriptor);
        }
        _sessions[sessionId] = new(registration.Descriptor, fileSystem, key);
        return sessionId;
    }

    /// <inheritdoc />
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out Session? session))
        {
            return;
        }
        try
        {
            await session.FileSystem.DisconnectAsync(session.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 关闭路径不该再抛:会话已经从表里摘掉,收不干净由插件自负。
            Trace.WriteLine($"[PluginProtocols] Disconnect of '{session.Descriptor.Id}' threw: {ex.Message}");
        }
        RaiseSessionState(sessionId, PluginProtocolSessionState.Closed);
    }

    /// <inheritdoc />
    public Task InvokeActionAsync(Guid sessionId, string actionId, string path, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.InvokeActionAsync(session.Key, actionId, path, cancellationToken));
    }

    /// <summary>
    /// 某种协议被注销(插件停用/卸载)时,关掉它名下还开着的会话。
    /// 不做这件事的话,文件面板会一直握着一个再也不会应答的实现,
    /// 用户看到的是"点什么都没反应"而不是"连接已断开"。
    /// </summary>
    /// <param name="protocolId">被注销的协议 id。</param>
    public void OnProtocolUnregistered(string protocolId)
    {
        foreach (KeyValuePair<Guid, Session> pair in _sessions.ToArray())
        {
            if (!pair.Value.Descriptor.Id.Equals(protocolId, StringComparison.Ordinal)
                || !_sessions.TryRemove(pair.Key, out _))
            {
                continue;
            }
            RaiseSessionState(pair.Key, PluginProtocolSessionState.Closed);
        }
    }

    // ---- ISftpService ----

    /// <inheritdoc />
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        IReadOnlyList<RemoteFileEntry> entries = await GuardValue(session,
            () => session.FileSystem.ListDirectoryAsync(session.Key, path, cancellationToken)).ConfigureAwait(false);
        return [.. entries.Select(ToRemoteFileInfo)];
    }

    /// <inheritdoc />
    public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath,
        IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        var bridge = ProgressBridge.For(progress, Path.GetFileName(localPath));
        return Guard(session, () => session.FileSystem.UploadFileAsync(session.Key, localPath, remotePath,
            bridge, resumeOffset, cancellationToken));
    }

    /// <inheritdoc />
    public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath,
        IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        var bridge = ProgressBridge.For(progress, GetFileName(remotePath));
        return Guard(session, () => session.FileSystem.DownloadFileAsync(session.Key, remotePath, localPath,
            bridge, resumeOffset, cancellationToken));
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        IProgress<ProtocolDeleteProgress>? bridge = progress is null ? null : new DeleteProgressBridge(progress);
        return Guard(session, () => session.FileSystem.DeleteAsync(session.Key, remotePath, bridge, cancellationToken));
    }

    /// <inheritdoc />
    public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.CreateDirectoryAsync(session.Key, remotePath, cancellationToken));
    }

    /// <inheritdoc />
    public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.CreateFileAsync(session.Key, remotePath, cancellationToken));
    }

    /// <inheritdoc />
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.EnsureDirectoryAsync(session.Key, remotePath, cancellationToken));
    }

    /// <inheritdoc />
    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.RenameAsync(session.Key, oldPath, newPath, cancellationToken));
    }

    /// <inheritdoc />
    public Task CopyAsync(Guid sessionId, string sourcePath, string destPath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        var bridge = ProgressBridge.For(progress, GetFileName(sourcePath));
        return Guard(session, () => session.FileSystem.CopyAsync(session.Key, sourcePath, destPath, bridge, cancellationToken));
    }

    /// <inheritdoc />
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return Guard(session, () => session.FileSystem.SetPermissionsAsync(session.Key, remotePath, octalMode, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        RemoteFileEntry? entry = await GuardValue(session,
            () => session.FileSystem.StatAsync(session.Key, remotePath, cancellationToken)).ConfigureAwait(false);
        // 宿主契约与 SDK 在这一点上不同:StatAsync 用 null 表示不存在,GetFileInfoAsync 抛。
        // 翻译放在这里,而不是让每个插件各自去抛一种异常。
        return entry is null
            ? throw new FileNotFoundException($"Remote path not found: {remotePath}", remotePath)
            : ToRemoteFileInfo(entry);
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return GuardValue(session, () => session.FileSystem.OpenReadAsync(session.Key, remotePath, cancellationToken));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return GuardValue(session, () => session.FileSystem.ExistsAsync(session.Key, remotePath, cancellationToken));
    }

    /// <inheritdoc />
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Session session = Require(sessionId);
        return GuardValue(session, () => session.FileSystem.GetHomePathAsync(session.Key, cancellationToken));
    }

    /// <summary>关闭全部会话。插件实例本身由插件运行时释放,这里只负责断开会话。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid sessionId in _sessions.Keys.ToArray())
        {
            await CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    // ---- 内部 ----

    /// <summary>首次用到时挂上注册表事件(从不开会话的宿主因此完全不订阅)。</summary>
    private void EnsureSubscribed()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 1)
        {
            return;
        }
        registry.SessionStateChanged += OnRegistrySessionStateChanged;
        registry.Unregistered += OnProtocolUnregistered;
    }

    private void OnRegistrySessionStateChanged(object? sender, PluginProtocolSessionEvent payload)
    {
        // 插件用它自己拿到的会话键上报,这里翻回宿主的 Guid。
        foreach (KeyValuePair<Guid, Session> pair in _sessions)
        {
            if (!pair.Value.Key.Equals(payload.Change.SessionId, StringComparison.Ordinal)
                || !pair.Value.Descriptor.Id.Equals(payload.ProtocolId, StringComparison.Ordinal))
            {
                continue;
            }
            PluginProtocolSessionState state = payload.Change.State switch
            {
                ProtocolSessionState.Connected => PluginProtocolSessionState.Connected,
                ProtocolSessionState.Faulted => PluginProtocolSessionState.Faulted,
                _ => PluginProtocolSessionState.Closed
            };
            if (state == PluginProtocolSessionState.Closed)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
            RaiseSessionState(pair.Key, state);
            return;
        }
    }

    private void RaiseSessionState(Guid sessionId, PluginProtocolSessionState state)
    {
        try
        {
            SessionStateChanged?.Invoke(this, new(sessionId, state));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginProtocols] Session state handler threw: {ex.Message}");
        }
    }

    private Session Require(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out Session? session)
            ? session
            : throw new PluginProtocolConnectionException($"Plugin protocol session {sessionId} is not open.");

    /// <summary>
    /// 组装交给插件的设置字典:先铺字段默认值,再盖上用户存的非机密设置,最后盖上机密。
    /// <para>
    /// 铺默认值这一步不能省:老配置是在插件加字段之前存的,少了那个键,
    /// 插件读到的就是调用处的兜底值而不是它自己声明的默认值。
    /// </para>
    /// </summary>
    /// <summary>
    /// 把「协议声明的默认值 → 用户填的设置 → 机密」三层合并成交给插件的设置字典。
    /// 终端协议(<see cref="PluginProtocolTerminalConnector" />)走同一份合并规则:
    /// 两处各写一遍必然分叉,而分叉的表现是"同一个字段在文件面板生效、在终端标签不生效"。
    /// </summary>
    internal static Dictionary<string, string> BuildSettings(ProtocolDescriptor descriptor, SessionProfile profile)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ProtocolSettingField field in descriptor.Fields)
        {
            if (field.DefaultValue is { } value)
            {
                settings[field.Key] = value;
            }
        }
        if (profile.PluginSettings is { } stored)
        {
            foreach (KeyValuePair<string, string> entry in stored)
            {
                settings[entry.Key] = entry.Value;
            }
        }
        if (profile.PluginSecrets is { } secrets)
        {
            foreach (KeyValuePair<string, string> entry in secrets)
            {
                settings[entry.Key] = entry.Value;
            }
        }
        return settings;
    }

    private static RemoteFileInfo ToRemoteFileInfo(RemoteFileEntry entry) =>
        new()
        {
            Name = entry.Name,
            FullPath = entry.FullPath,
            Size = entry.Size,
            Permissions = entry.Permissions,
            IsDirectory = entry.IsDirectory,
            // SDK 侧是带时区的绝对时刻,宿主模型是「本地墙钟时间」(SFTP/FTP 后端存的也是这个)。
            // 取 LocalDateTime 而不是 UtcDateTime:后者会让文件列表里的时间整体偏掉一个时区。
            // default 是协议说的「不知道」(S3 的虚构目录就没有修改时间),原样传成 MinValue
            // 让界面留空 —— 换算它会在 UTC+n 时区上下溢。
            LastModified = entry.LastModified == default ? DateTime.MinValue : entry.LastModified.LocalDateTime,
            Owner = entry.Owner,
            Group = entry.Group
        };

    /// <summary>远端路径的最后一段(协议路径一律用 <c>/</c>,不能拿本地路径规则去切)。</summary>
    private static string GetFileName(string remotePath)
    {
        string trimmed = remotePath.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static async Task Guard(Session session, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, session.Descriptor);
        }
    }

    private static async Task<T> GuardValue<T>(Session session, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, session.Descriptor);
        }
    }

    /// <summary>
    /// SDK 异常 → Core 中立异常族。与 AWSSDK / FluentFTP / Tmds.Ssh 同一条分层硬规则:
    /// 插件 SDK 的类型不越过 Infrastructure 边界,界面只认 <c>VelaShell.Core.Protocols</c> 那几个。
    /// 认不出的异常原样放行 —— 包装成"未知错误"只会把插件给出的可读信息埋掉。
    /// </summary>
    /// <summary>SDK 异常族 → 宿主中立异常族(终端协议共用,理由同 <see cref="BuildSettings" />)。</summary>
    internal static Exception Translate(Exception ex, ProtocolDescriptor descriptor) =>
        ex switch
        {
            OperationCanceledException => ex,
            ProtocolAuthenticationException auth => new PluginProtocolAuthenticationException(auth.Message, auth),
            ProtocolCertificateTrustException cert => new PluginProtocolCertificateException(
                cert.Message, cert.Subject, cert.Issuer, cert.ExpiresOn, cert.Thumbprint, cert.PolicyErrors,
                descriptor.TrustedThumbprintSettingKey),
            ProtocolConnectionException conn => new PluginProtocolConnectionException(conn.Message, conn),
            ProtocolUnsupportedException unsupported => new NotSupportedException(unsupported.Message, unsupported),
            PluginSessionNotFoundException missing => new PluginProtocolConnectionException(
                $"Protocol '{descriptor.Id}' no longer knows this session: {missing.Message}", missing),
            _ => ex
        };

    /// <summary>
    /// 传输进度桥:把 SDK 的原始字节数接进宿主的节流器。
    /// <para>
    /// 节流(≥100ms)与并发乱序下的单调收敛放在宿主做,插件因此可以放心地每读一块就上报一次
    /// —— 否则每个协议插件都要重新踩一遍"7.7GB 文件二十多万次回调把 UI 线程压死"那个坑。
    /// 总字节数要等第一次上报才知道,所以节流器是懒建的。
    /// </para>
    /// </summary>
    private sealed class ProgressBridge(IProgress<TransferProgress> sink, string fileName) : IProgress<RemoteTransferProgress>
    {
        private TransferProgressThrottle? _throttle;
        private long _knownTotal = -1;
        private int _finalEmitted;

        /// <summary>没有接收方时返回 null:整条桥连同对象分配一起省掉。</summary>
        public static ProgressBridge? For(IProgress<TransferProgress>? sink, string fileName) =>
            sink is null ? null : new(sink, fileName);

        public void Report(RemoteTransferProgress value)
        {
            // SDK 约定 TotalBytes 未知时为 -1(见 IRemoteFsApi 的 RemoteTransferProgress 文档)。
            // 未知不等于不上报:节流器在 totalBytes<=0 时会安全退化(百分比走 0 分支、ETA 归零),
            // 已传字节数照样有意义。真正不能做的是"拿 0 把 totalBytes 永久焊死",
            // 所以后续某次回调带来真实总量时要换一个节流器。
            long total = Math.Max(value.TotalBytes, 0);
            TransferProgressThrottle? throttle = _throttle;
            if (throttle is null || (total > 0 && Volatile.Read(ref _knownTotal) <= 0))
            {
                var created = new TransferProgressThrottle(sink, fileName, total);
                _throttle = created;
                Volatile.Write(ref _knownTotal, total);
                throttle = created;
            }
            // 收尾那一次必须绕过节流。SFTP 直连走的是 throttle.ReportFinal,而 IProgress 这层桥
            // 表达不了"这是最后一次" —— 不特判的话,20ms 传完的小文件会把满格那次丢进 100ms 节流窗,
            // 进度条永远停在个位数百分比上(状态却已是"已完成")。
            // 只放行一次:目录树复制会在循环里反复满足 Transferred>=Total(大文件在前、
            // 后面全是 0 字节占位符),那会让每一条都绕过节流。
            if (total > 0 && value.TransferredBytes >= total && Interlocked.Exchange(ref _finalEmitted, 1) == 0)
            {
                throttle.ReportFinal(value.TransferredBytes);
                return;
            }
            throttle.Report(value.TransferredBytes);
        }
    }

    /// <summary>删除进度桥(纯字段搬运)。</summary>
    private sealed class DeleteProgressBridge(IProgress<SftpDeleteProgress> sink) : IProgress<ProtocolDeleteProgress>
    {
        public void Report(ProtocolDeleteProgress value) =>
            sink.Report(new(value.DeletedCount, value.TotalCount, value.CurrentPath));
    }
}

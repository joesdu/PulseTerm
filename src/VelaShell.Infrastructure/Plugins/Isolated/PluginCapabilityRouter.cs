using System.Collections.Concurrent;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 隔离插件的宿主侧能力路由:把 RPC 请求分发到该插件的 <see cref="PluginContext" />
/// 能力实现(与进程内插件同一套 —— 权限/节流/纪律单点生效),并把宿主事件、
/// 命令触发与面板交互作为通知推回插件进程。
/// 握手完成前除 <see cref="PluginRpc.Handshake" /> 外一切调用拒绝。
/// </summary>
internal sealed class PluginCapabilityRouter : IDisposable
{
    private readonly PluginContext _context;
    private readonly RpcConnection _rpc;
    private readonly string _expectedToken;
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, IDisposable> _commandRegistrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Stream> _openStreams = new(StringComparer.Ordinal);
    private readonly string _hostVersion;
    private volatile bool _handshakeDone;
    private bool _disposed;

    private readonly Func<Task<IReadOnlyList<ThemeTokenDto>>>? _themeTokens;
    private readonly IPluginEmbedHost? _embedHost;
    private readonly ConcurrentDictionary<string, PluginSdk.Ui.IPluginPanel> _embeddedPanels = new(StringComparer.Ordinal);

    /// <summary>装配路由并接线事件转发。</summary>
    public PluginCapabilityRouter(PluginContext context, RpcConnection rpc, string expectedToken, string hostVersion,
        Func<Task<IReadOnlyList<ThemeTokenDto>>>? themeTokens = null, IPluginEmbedHost? embedHost = null)
    {
        _context = context;
        _rpc = rpc;
        _expectedToken = expectedToken;
        _hostVersion = hostVersion;
        _themeTokens = themeTokens;
        _embedHost = embedHost;
        // 宿主事件 → 插件进程通知。订阅挂在 context.Events(PluginEventHub)上,
        // context.Dispose 时统一拆除。
        context.Events.SessionConnected += session =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("sessionConnected", session, null));
        context.Events.SessionDisconnected += session =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("sessionDisconnected", session, null));
        context.Events.ThemeChanged += theme =>
        {
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("themeChanged", null, theme));
            // 主题切换重推令牌快照。稍等一拍:宿主应用新明暗变体与令牌重解析都在 UI
            // 线程队列里,等它落定再取值,插件拿到的才是新主题的颜色。
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                await PushThemeTokensAsync().ConfigureAwait(false);
            });
        };
        context.Events.LocaleChanged += locale =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("localeChanged", null, locale));
    }

    /// <summary>下发主题令牌快照(尽力而为:无提供者或采集失败则跳过)。</summary>
    public async Task PushThemeTokensAsync()
    {
        if (_themeTokens is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<ThemeTokenDto> tokens = await _themeTokens().ConfigureAwait(false);
            if (tokens.Count > 0)
            {
                await _rpc.NotifyAsync(PluginRpc.ThemeTokens, new ThemeTokensNotification([.. tokens])).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Theme token push failed: {ex.Message}");
        }
    }

    /// <summary>握手完成(令牌校验通过)时完成;超时/失败由等待方裁决。</summary>
    public Task HandshakeCompleted => _handshake.Task;

    /// <summary>插件发起了一次 RPC 往来(请求或通知)—— 空闲回收的活跃信号。</summary>
    public event Action? Activity;

    /// <summary>插件进程的打开面板数变化 —— 非零时不做空闲回收。</summary>
    public event Action<int>? SurfacesChanged;

    /// <summary>RPC 请求入口。</summary>
    public async Task<object?> HandleRequestAsync(string method, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (method == PluginRpc.Handshake)
        {
            return Handshake(Get<HandshakeRequest>(payload));
        }
        Activity?.Invoke();
        if (!_handshakeDone)
        {
            throw new InvalidOperationException("Handshake not completed.");
        }
        switch (method)
        {
            case PluginRpc.SessionsList:
                return await _context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.SessionsGet:
                return await _context.Sessions.GetAsync(Get<SessionRef>(payload).SessionId, cancellationToken).ConfigureAwait(false);
            case PluginRpc.ExecRun:
                {
                    ExecRunRequest request = Get<ExecRunRequest>(payload);
                    return await _context.RemoteExec.RunAsync(request.SessionId, request.Command,
                        new() { Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds) }, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsList:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.ListDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsStat:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.StatAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsExists:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.ExistsAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsWorkingDirectory:
                return await _context.RemoteFs.GetWorkingDirectoryAsync(Get<SessionRef>(payload).SessionId, cancellationToken).ConfigureAwait(false);
            case PluginRpc.FsDownload:
                {
                    FsTransferRequest request = Get<FsTransferRequest>(payload);
                    await _context.RemoteFs.DownloadFileAsync(request.SessionId, request.RemotePath, request.LocalPath,
                        ProgressFor(request.ProgressToken), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsUpload:
                {
                    FsTransferRequest request = Get<FsTransferRequest>(payload);
                    await _context.RemoteFs.UploadFileAsync(request.SessionId, request.LocalPath, request.RemotePath,
                        ProgressFor(request.ProgressToken), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsOpenRead:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    Stream stream = await _context.RemoteFs.OpenReadAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    string streamId = Guid.NewGuid().ToString("N");
                    _openStreams[streamId] = stream;
                    long length = stream.CanSeek ? stream.Length : -1;
                    return new FsOpenReadResponse(streamId, length);
                }
            case PluginRpc.FsStreamRead:
                {
                    FsStreamReadRequest request = Get<FsStreamReadRequest>(payload);
                    if (!_openStreams.TryGetValue(request.StreamId, out Stream? stream))
                    {
                        throw new InvalidOperationException("Stream is not open (already closed or never opened).");
                    }
                    // 单块上限 512KB:base64 后 ~700KB,远低于帧上限,又不至于块太碎。
                    int count = Math.Clamp(request.MaxBytes, 1, 512 * 1024);
                    byte[] buffer = new byte[count];
                    int read = 0;
                    while (read < count)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }
                        read += n;
                    }
                    bool eof = read == 0;
                    if (eof && _openStreams.TryRemove(request.StreamId, out Stream? finished))
                    {
                        await finished.DisposeAsync().ConfigureAwait(false); // 流尽即释放,插件忘关也不泄漏
                    }
                    return new FsStreamReadResponse(Convert.ToBase64String(buffer, 0, read), eof);
                }
            case PluginRpc.FsStreamClose:
                {
                    if (_openStreams.TryRemove(Get<FsStreamRef>(payload).StreamId, out Stream? stream))
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    return null;
                }
            case PluginRpc.FsReadAll:
                {
                    FsReadAllRequest request = Get<FsReadAllRequest>(payload);
                    byte[] content = await _context.RemoteFs.ReadAllBytesAsync(request.SessionId, request.Path, request.MaxBytes,
                        cancellationToken).ConfigureAwait(false);
                    return Convert.ToBase64String(content);
                }
            case PluginRpc.FsWriteAll:
                {
                    FsWriteAllRequest request = Get<FsWriteAllRequest>(payload);
                    await _context.RemoteFs.WriteAllBytesAsync(request.SessionId, request.Path,
                        Convert.FromBase64String(request.ContentBase64), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsDelete:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.DeleteAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsCreateDirectory:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.CreateDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsEnsureDirectory:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.EnsureDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsRename:
                {
                    FsRenameRequest request = Get<FsRenameRequest>(payload);
                    await _context.RemoteFs.RenameAsync(request.SessionId, request.OldPath, request.NewPath, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.CommandsRegister:
                {
                    CommandRegistration registration = Get<CommandRegistration>(payload);
                    // 命令体留在插件进程:宿主触发时发通知过去。
                    IDisposable handle = _context.Commands.Register(new(registration.Id, registration.Title, registration.Category,
                        executeToken =>
                        {
                            _ = executeToken;
                            _ = _rpc.NotifyAsync(PluginRpc.CommandExecute, new CommandRef(registration.Id));
                            return Task.CompletedTask;
                        }));
                    if (_commandRegistrations.TryRemove(registration.Id, out IDisposable? replaced))
                    {
                        replaced.Dispose();
                    }
                    _commandRegistrations[registration.Id] = handle;
                    return null;
                }
            case PluginRpc.CommandsTryExecute:
                return _context.Commands.TryExecute(Get<CommandRef>(payload).Id);
            case PluginRpc.StorageGet:
                return await _context.Storage.GetAsync<JsonElement?>(Get<StorageKeyRef>(payload).Key, cancellationToken).ConfigureAwait(false);
            case PluginRpc.StorageSet:
                {
                    StorageSetRequest request = Get<StorageSetRequest>(payload);
                    await _context.Storage.SetAsync(request.Key, request.Value, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.StorageRemove:
                return await _context.Storage.RemoveAsync(Get<StorageKeyRef>(payload).Key, cancellationToken).ConfigureAwait(false);
            case PluginRpc.StorageKeys:
                return await _context.Storage.GetKeysAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.SecretsGet:
                return await _context.Secrets.GetAsync(Get<SecretRef>(payload).Name, cancellationToken).ConfigureAwait(false);
            case PluginRpc.SecretsSet:
                {
                    SecretSetRequest request = Get<SecretSetRequest>(payload);
                    await _context.Secrets.SetAsync(request.Name, request.Value, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.SecretsDelete:
                return await _context.Secrets.DeleteAsync(Get<SecretRef>(payload).Name, cancellationToken).ConfigureAwait(false);
            case PluginRpc.ClipboardGetText:
                return await _context.Clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.ClipboardSetText:
                await _context.Clipboard.SetTextAsync(Get<ClipboardSetRequest>(payload).Text, cancellationToken).ConfigureAwait(false);
                return null;
            case PluginRpc.TerminalGetOutput:
                {
                    TerminalGetOutputRequest request = Get<TerminalGetOutputRequest>(payload);
                    return await _context.Terminal.GetOutputAsync(request.SessionId, request.MaxLines, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.TerminalSearch:
                {
                    TerminalSearchRequest request = Get<TerminalSearchRequest>(payload);
                    IReadOnlyList<PluginSdk.Terminal.TerminalMatch> matches = await _context.Terminal
                        .SearchOutputAsync(request.SessionId, request.Pattern, request.IsRegex, request.MaxMatches, cancellationToken)
                        .ConfigureAwait(false);
                    return matches.Select(m => new TerminalMatchDto(m.Line, m.Text)).ToArray();
                }
            case PluginRpc.TerminalWrite:
                {
                    TerminalWriteRequest request = Get<TerminalWriteRequest>(payload);
                    await _context.Terminal.WriteAsync(request.SessionId, request.Input, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.UiEmbedPanel:
                {
                    if (_embedHost is not { IsSupported: true } embedHost)
                    {
                        throw new InvalidOperationException("Dock embedding is not supported by this host.");
                    }
                    UiEmbedRequest request = Get<UiEmbedRequest>(payload);
                    PluginSdk.Ui.IPluginPanel panel = await embedHost.EmbedAsync(_context.PluginId, _context.Log,
                        request.Title, (nint)request.Hwnd, cancellationToken).ConfigureAwait(false);
                    _embeddedPanels[panel.PanelId] = panel;
                    panel.Closed += () =>
                    {
                        // 用户关标签/插件停用:摘出记录并通知插件进程关掉它的窗口。
                        _embeddedPanels.TryRemove(panel.PanelId, out _);
                        _ = _rpc.NotifyAsync(PluginRpc.UiPanelClosed, new UiPanelRef(panel.PanelId));
                    };
                    return new UiEmbedResponse(panel.PanelId);
                }
            case PluginRpc.UiClosePanel:
                {
                    if (_embeddedPanels.TryRemove(Get<UiPanelRef>(payload).PanelId, out PluginSdk.Ui.IPluginPanel? panel))
                    {
                        await panel.CloseAsync().ConfigureAwait(false);
                    }
                    return null;
                }
            // 注:界面不走 RPC —— 隔离插件的窗口由插件进程自带的 Avalonia 直接呈现。
            default:
                throw new InvalidOperationException($"Unknown method '{method}'.");
        }
    }

    /// <summary>RPC 通知入口(日志、命令注销、面板数)。</summary>
    public void HandleNotification(string method, JsonElement? payload)
    {
        if (!_handshakeDone)
        {
            return;
        }
        Activity?.Invoke();
        switch (method)
        {
            case PluginRpc.UiSurfaces:
                {
                    SurfacesChanged?.Invoke(Get<UiSurfacesNotification>(payload).Count);
                    break;
                }
            case PluginRpc.LogWrite:
                {
                    LogNotification log = Get<LogNotification>(payload);
                    _context.Log.Write(log.Level, log.Exception is null ? log.Message : $"{log.Message} — {log.Exception}");
                    break;
                }
            case PluginRpc.CommandsUnregister:
                {
                    if (_commandRegistrations.TryRemove(Get<CommandRef>(payload).Id, out IDisposable? handle))
                    {
                        handle.Dispose();
                    }
                    break;
                }
        }
    }

    private HandshakeResponse Handshake(HandshakeRequest request)
    {
        if (!string.Equals(request.Token, _expectedToken, StringComparison.Ordinal)
            || !string.Equals(request.PluginId, _context.PluginId, StringComparison.Ordinal))
        {
            var rejected = new InvalidOperationException("Handshake rejected: token or plugin id mismatch.");
            _handshake.TrySetException(rejected);
            throw rejected;
        }
        if (!request.ApiLevels.Contains(VelaPluginApi.Level))
        {
            var incompatible = new InvalidOperationException(
                $"Handshake rejected: plugin host supports apiLevels [{string.Join(",", request.ApiLevels)}], " +
                $"this host requires {VelaPluginApi.Level}.");
            _handshake.TrySetException(incompatible);
            throw incompatible;
        }
        _handshakeDone = true;
        _handshake.TrySetResult();
        return new(VelaPluginApi.Level, _hostVersion, _context.Host.Locale, _context.Host.Theme,
            SupportsEmbedding: _embedHost is { IsSupported: true });
    }

    private Progress<RemoteTransferProgress>? ProgressFor(string? progressToken)
        => progressToken is null
            ? null
            : new Progress<RemoteTransferProgress>(p =>
                _ = _rpc.NotifyAsync(PluginRpc.FsProgress,
                    new FsProgressNotification(progressToken, p.TransferredBytes, p.TotalBytes)));

    private static T Get<T>(JsonElement? payload) where T : class
        => payload is { } element
            ? element.Deserialize<T>() ?? throw new ArgumentException("Malformed payload.")
            : throw new ArgumentException("Missing payload.");

    /// <summary>拆除命令/面板注册(context 本体由 PluginManager 统一释放)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (IDisposable handle in _commandRegistrations.Values)
        {
            handle.Dispose();
        }
        _commandRegistrations.Clear();
        // 在途的流式读取随插件停用释放。
        foreach (Stream stream in _openStreams.Values.ToArray())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // 释放尽力而为。
            }
        }
        _openStreams.Clear();
        // 插件停用/崩溃:嵌入的停靠标签一并撤下(面板 Closed 会尝试通知插件进程,断连时静默)。
        foreach (PluginSdk.Ui.IPluginPanel panel in _embeddedPanels.Values.ToArray())
        {
            _ = panel.CloseAsync();
        }
        _embeddedPanels.Clear();
        _handshake.TrySetException(new RpcDisconnectedException("Router disposed before handshake."));
    }
}

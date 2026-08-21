using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.PluginHost;

/// <summary>PluginHost 侧的日志:转发到宿主日志管道,同时落本进程 Trace 兜底。</summary>
internal sealed class RpcLogger(RpcConnection rpc, string pluginId) : IPluginLogger
{
    public void Write(PluginLogLevel level, string message, Exception? exception = null)
    {
        Trace.WriteLine(exception is null
            ? $"[Plugin:{pluginId}] [{level}] {message}"
            : $"[Plugin:{pluginId}] [{level}] {message} — {exception}");
        _ = rpc.NotifyAsync(PluginRpc.LogWrite, new LogNotification(level, message, exception?.ToString()));
    }
}

/// <summary>宿主信息:握手快照 + 事件驱动的语言/主题热更新。</summary>
internal sealed class RemoteHostInfo(HandshakeResponse hello) : IHostInfo
{
    public string AppVersion => hello.HostVersion;
    public int ApiLevel => hello.ApiLevel;
    public string Locale { get; internal set; } = hello.Locale;
    public string Theme { get; internal set; } = hello.Theme;
}

/// <summary>会话能力的 RPC 代理。</summary>
internal sealed class RpcSessions(RpcConnection rpc) : ISessionsApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<SessionInfo[]>(PluginRpc.SessionsList, null, Timeout, cancellationToken)
               .ConfigureAwait(false) ?? [];

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<SessionInfo?>(PluginRpc.SessionsGet, new SessionRef(sessionId), Timeout, cancellationToken);
}

/// <summary>远程执行能力的 RPC 代理(超时随选项放宽,留 15s 往返余量)。</summary>
internal sealed class RpcRemoteExec(RpcConnection rpc) : IRemoteExecApi
{
    /// <summary>隔离模式下长驻命令的兜底死线。见 <see cref="StreamAsync" /> 的说明。</summary>
    private static readonly TimeSpan DefaultStreamTimeout = TimeSpan.FromHours(2);

    /// <summary>在途流式执行的输出接收器(outputToken → 回调)。</summary>
    internal ConcurrentDictionary<string, IProgress<ExecOutput>> OutputSinks { get; } = new();

    /// <summary>宿主输出通知入口。</summary>
    internal void OnOutput(ExecOutputNotification notification)
    {
        if (OutputSinks.TryGetValue(notification.OutputToken, out IProgress<ExecOutput>? sink))
        {
            sink.Report(new(notification.IsStandardError ? ExecStream.StandardError : ExecStream.StandardOutput, notification.Line));
        }
    }

    public async Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan execTimeout = options?.Timeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(30);
        return await rpc.RequestAsync<ExecResult>(PluginRpc.ExecRun,
                   new ExecRunRequest(sessionId, command, execTimeout.TotalSeconds),
                   execTimeout + TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false)
               ?? new("");
    }

    /// <summary>
    /// 流式执行的 RPC 代理。
    /// <para>
    /// **隔离模式下"不限时"是做不到的**:取消令牌不跨进程传播(dev-guide §6),
    /// 插件这边取消了,宿主那边的 <c>docker logs -f</c> 不会知道。所以这里给未指定超时的
    /// 流补上一个两小时的死线 —— 让"忘了取消"最坏也只是浪费两小时一个通道,
    /// 而不是把它泄漏到宿主退出为止。要真正即时的取消,请用 <c>inProcess</c>。
    /// </para>
    /// </summary>
    public async Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
        IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        TimeSpan deadline = options?.Timeout is { } t && t > TimeSpan.Zero ? t : DefaultStreamTimeout;
        string token = Guid.NewGuid().ToString("n");
        OutputSinks[token] = output;
        try
        {
            return await rpc.RequestAsync<ExecStreamResult>(PluginRpc.ExecStream,
                       new ExecStreamRequest(sessionId, command, token, deadline.TotalSeconds,
                           options?.IncludeStandardError ?? true),
                       deadline + TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false)
                   ?? new(0, 0);
        }
        finally
        {
            OutputSinks.TryRemove(token, out _);
        }
    }
}

/// <summary>远程文件能力的 RPC 代理。传输走同机文件路径,进度经通知回流。</summary>
internal sealed class RpcRemoteFs(RpcConnection rpc) : IRemoteFsApi
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Transfer = TimeSpan.FromHours(2);

    /// <summary>在途传输的进度接收器(progressToken → 回调)。</summary>
    internal ConcurrentDictionary<string, IProgress<RemoteTransferProgress>> ProgressSinks { get; } = new();

    /// <summary>宿主进度通知入口。</summary>
    internal void OnProgress(FsProgressNotification notification)
    {
        if (ProgressSinks.TryGetValue(notification.ProgressToken, out IProgress<RemoteTransferProgress>? sink))
        {
            sink.Report(new(notification.TransferredBytes, notification.TotalBytes));
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path,
        CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<RemoteFileEntry[]>(PluginRpc.FsList, new FsPathRequest(sessionId, path), Short, cancellationToken)
               .ConfigureAwait(false) ?? [];

    public Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<RemoteFileEntry?>(PluginRpc.FsStat, new FsPathRequest(sessionId, path), Short, cancellationToken);

    public async Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<bool>(PluginRpc.FsExists, new FsPathRequest(sessionId, path), Short, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<string>(PluginRpc.FsWorkingDirectory, new SessionRef(sessionId), Short, cancellationToken)
               .ConfigureAwait(false) ?? "/";

    public Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => TransferAsync(PluginRpc.FsDownload, sessionId, remotePath, localPath, progress, cancellationToken);

    public Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => TransferAsync(PluginRpc.FsUpload, sessionId, remotePath, localPath, progress, cancellationToken);

    private async Task TransferAsync(string method, string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress, CancellationToken cancellationToken)
    {
        string? token = null;
        if (progress is not null)
        {
            token = Guid.NewGuid().ToString("N");
            ProgressSinks[token] = progress;
        }
        try
        {
            await rpc.RequestAsync<object>(method, new FsTransferRequest(sessionId, remotePath, localPath, token),
                Transfer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (token is not null)
            {
                ProgressSinks.TryRemove(token, out _);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        FsOpenReadResponse response = await rpc.RequestAsync<FsOpenReadResponse>(PluginRpc.FsOpenRead,
                                          new FsPathRequest(sessionId, remotePath), Short, cancellationToken).ConfigureAwait(false)
                                      ?? throw new InvalidOperationException("Host did not return a stream id.");
        return new RemoteReadStream(rpc, response.StreamId, response.Length);
    }

    public async Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        string base64 = await rpc.RequestAsync<string>(PluginRpc.FsReadAll,
                            new FsReadAllRequest(sessionId, remotePath, maxBytes),
                            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false) ?? "";
        return Convert.FromBase64String(base64);
    }

    public Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.FsWriteAll,
            new FsWriteAllRequest(sessionId, remotePath, Convert.ToBase64String(content.Span)),
            TimeSpan.FromMinutes(10), cancellationToken);

    public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.FsDelete, new FsPathRequest(sessionId, remotePath), Transfer, cancellationToken);

    public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.FsCreateDirectory, new FsPathRequest(sessionId, remotePath), Short, cancellationToken);

    public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.FsEnsureDirectory, new FsPathRequest(sessionId, remotePath), Short, cancellationToken);

    public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.FsRename, new FsRenameRequest(sessionId, oldPath, newPath), Short, cancellationToken);
}

/// <summary>命令能力的 RPC 代理:注册表在宿主,命令体留在本进程,触发经通知回流。</summary>
internal sealed class RpcCommands(RpcConnection rpc, IPluginLogger log) : ICommandsApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _callbacks = new();

    public IDisposable Register(PluginCommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _callbacks[command.Id] = command.ExecuteAsync;
        try
        {
            rpc.RequestAsync<object>(PluginRpc.CommandsRegister,
                new CommandRegistration(command.Id, command.Title, command.Category), Timeout).GetAwaiter().GetResult();
        }
        catch
        {
            _callbacks.TryRemove(command.Id, out _);
            throw;
        }
        return new Registration(this, command.Id);
    }

    public bool TryExecute(string commandId)
    {
        try
        {
            return rpc.RequestAsync<bool>(PluginRpc.CommandsTryExecute, new CommandRef(commandId), Timeout)
                      .GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>宿主的命令触发通知入口。</summary>
    internal void OnExecute(string commandId)
    {
        if (!_callbacks.TryGetValue(commandId, out Func<CancellationToken, Task>? callback))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await callback(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error($"Command '{commandId}' threw.", ex);
            }
        });
    }

    private void Unregister(string commandId)
    {
        if (_callbacks.TryRemove(commandId, out _))
        {
            _ = rpc.NotifyAsync(PluginRpc.CommandsUnregister, new CommandRef(commandId));
        }
    }

    private sealed class Registration(RpcCommands owner, string id) : IDisposable
    {
        public void Dispose() => owner.Unregister(id);
    }
}

/// <summary>宿主事件的接收枢纽:通知 → 本地事件,逐处理器守卫。</summary>
internal sealed class RemoteEventHub(IPluginLogger log) : IHostEvents
{
    public event Action<SessionInfo>? SessionConnected;
    public event Action<SessionInfo>? SessionDisconnected;
    public event Action<string>? ThemeChanged;
    public event Action<string>? LocaleChanged;

    /// <summary>宿主事件通知入口(已在线程池上)。</summary>
    internal void OnHostEvent(HostEventNotification notification)
    {
        switch (notification.Kind)
        {
            case "sessionConnected" when notification.Session is { } connected:
                Forward(SessionConnected, connected);
                break;
            case "sessionDisconnected" when notification.Session is { } disconnected:
                Forward(SessionDisconnected, disconnected);
                break;
            case "themeChanged" when notification.Value is { } theme:
                Forward(ThemeChanged, theme);
                break;
            case "localeChanged" when notification.Value is { } locale:
                Forward(LocaleChanged, locale);
                break;
        }
    }

    private void Forward<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                log.Error("Event handler threw.", ex);
            }
        }
    }
}

/// <summary>机密能力的 RPC 代理:机密只存宿主侧,值仅在本机管道上瞬时传输。</summary>
internal sealed class RpcSecrets(RpcConnection rpc) : VelaShell.PluginSdk.Secrets.ISecretsApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<string?>(PluginRpc.SecretsGet, new SecretRef(name), Timeout, cancellationToken);

    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.SecretsSet, new SecretSetRequest(name, value), Timeout, cancellationToken);

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<bool>(PluginRpc.SecretsDelete, new SecretRef(name), Timeout, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>剪贴板能力的 RPC 代理:经宿主主窗口执行,与进程内语义一致。</summary>
internal sealed class RpcClipboard(RpcConnection rpc) : VelaShell.PluginSdk.Clipboard.IClipboardApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
        => rpc.RequestAsync<string?>(PluginRpc.ClipboardGetText, null, Timeout, cancellationToken);

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.ClipboardSetText, new ClipboardSetRequest(text), Timeout, cancellationToken);
}

/// <summary>
/// KV 存储的 RPC 代理:数据落宿主 SonnetDB(按插件 id 命名空间隔离,卸载整体清除),
/// 隔离进程不落本地文件。
/// </summary>
internal sealed class RpcStorage(RpcConnection rpc) : VelaShell.PluginSdk.Storage.IPluginStorage
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        System.Text.Json.JsonElement? value = await rpc.RequestAsync<System.Text.Json.JsonElement?>(
            PluginRpc.StorageGet, new StorageKeyRef(key), Timeout, cancellationToken).ConfigureAwait(false);
        return value is { ValueKind: not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.Undefined } element
            ? element.Deserialize<T>()
            : default;
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return rpc.RequestAsync<object>(PluginRpc.StorageSet,
            new StorageSetRequest(key, System.Text.Json.JsonSerializer.SerializeToElement(value)), Timeout, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return await rpc.RequestAsync<bool>(PluginRpc.StorageRemove, new StorageKeyRef(key), Timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<string[]>(PluginRpc.StorageKeys, null, Timeout, cancellationToken)
               .ConfigureAwait(false) ?? [];
}

/// <summary>
/// 时序能力的 RPC 代理:句柄按 measurement 短名寻址(宿主侧记住已打开的实例),
/// 数据落宿主 SonnetDB,隔离进程不落本地文件。
/// </summary>
internal sealed class RpcTimeSeries(RpcConnection rpc) : VelaShell.PluginSdk.TimeSeries.ITimeSeriesApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<VelaShell.PluginSdk.TimeSeries.ITimeSeries> OpenAsync(
        VelaShell.PluginSdk.TimeSeries.TimeSeriesDefinition definition, CancellationToken cancellationToken = default)
    {
        VelaShell.PluginSdk.TimeSeries.TimeSeriesValidation.RequireDefinition(definition);
        TimeSeriesNameRef? response = await rpc.RequestAsync<TimeSeriesNameRef>(PluginRpc.TimeSeriesOpen,
            new TimeSeriesOpenRequest(definition), Timeout, cancellationToken).ConfigureAwait(false);
        return new RpcSeries(rpc, response?.Name ?? definition.Name);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<string[]>(PluginRpc.TimeSeriesList, null, Timeout, cancellationToken)
               .ConfigureAwait(false) ?? [];

    public async Task<bool> DropAsync(string name, CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<bool>(PluginRpc.TimeSeriesDrop, new TimeSeriesNameRef(name), Timeout, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>单个 measurement 的 RPC 句柄。</summary>
    private sealed class RpcSeries(RpcConnection rpc, string name) : VelaShell.PluginSdk.TimeSeries.ITimeSeries
    {
        public string Name => name;

        public Task WriteAsync(VelaShell.PluginSdk.TimeSeries.TimeSeriesPoint point, CancellationToken cancellationToken = default)
            => WriteManyAsync([point], cancellationToken);

        public Task WriteManyAsync(IEnumerable<VelaShell.PluginSdk.TimeSeries.TimeSeriesPoint> points,
            CancellationToken cancellationToken = default)
            => rpc.RequestAsync<object>(PluginRpc.TimeSeriesWrite,
                new TimeSeriesWriteRequest(name, [.. points]), Timeout, cancellationToken);

        public async Task<IReadOnlyList<VelaShell.PluginSdk.TimeSeries.TimeSeriesPoint>> QueryAsync(
            VelaShell.PluginSdk.TimeSeries.TimeSeriesQuery query, CancellationToken cancellationToken = default)
            => await rpc.RequestAsync<VelaShell.PluginSdk.TimeSeries.TimeSeriesPoint[]>(PluginRpc.TimeSeriesQuery,
                   new TimeSeriesQueryRequest(name, query), Timeout, cancellationToken).ConfigureAwait(false) ?? [];

        public async Task<long> CountAsync(string field, VelaShell.PluginSdk.TimeSeries.TimeSeriesQuery query,
            CancellationToken cancellationToken = default)
            => await rpc.RequestAsync<long>(PluginRpc.TimeSeriesCount,
                new TimeSeriesCountRequest(name, field, query), Timeout, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<string>> DistinctTagValuesAsync(string tag, CancellationToken cancellationToken = default)
            => await rpc.RequestAsync<string[]>(PluginRpc.TimeSeriesDistinct,
                   new TimeSeriesDistinctRequest(name, tag), Timeout, cancellationToken).ConfigureAwait(false) ?? [];

        public async Task<int> DeleteAsync(IReadOnlyDictionary<string, string>? tags = null,
            CancellationToken cancellationToken = default)
            => await rpc.RequestAsync<int>(PluginRpc.TimeSeriesDelete,
                new TimeSeriesDeleteRequest(name, tags?.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal)),
                Timeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 隔离模式的远端文件只读流:经 RPC 顺序分块拉取(不支持 Seek),
/// 读尽或释放时通知宿主关闭底层 SFTP 流。
/// </summary>
internal sealed class RemoteReadStream(RpcConnection rpc, string streamId, long length) : Stream
{
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(2);
    private const int ChunkSize = 256 * 1024;

    private byte[] _buffer = [];
    private int _bufferOffset;
    private long _position;
    private bool _eof;
    private bool _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length >= 0 ? length : throw new NotSupportedException("Stream length is unknown.");
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bufferOffset >= _buffer.Length && !_eof)
        {
            FsStreamReadResponse? chunk = await rpc.RequestAsync<FsStreamReadResponse>(PluginRpc.FsStreamRead,
                new FsStreamReadRequest(streamId, ChunkSize), ChunkTimeout, cancellationToken).ConfigureAwait(false);
            _buffer = chunk is null ? [] : Convert.FromBase64String(chunk.DataBase64);
            _bufferOffset = 0;
            _eof = chunk?.Eof ?? true;
        }
        int available = _buffer.Length - _bufferOffset;
        if (available <= 0)
        {
            return 0;
        }
        int count = Math.Min(available, destination.Length);
        _buffer.AsSpan(_bufferOffset, count).CopyTo(destination.Span);
        _bufferOffset += count;
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_eof)
            {
                // 提前弃读:通知宿主释放底层流(读尽的流宿主已自动释放)。
                _ = rpc.NotifyAsync(PluginRpc.FsStreamClose, new FsStreamRef(streamId));
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>终端能力的 RPC 代理:读取/搜索/回写全部路由到宿主(授权对话框在宿主弹)。</summary>
internal sealed class RpcTerminal(RpcConnection rpc) : VelaShell.PluginSdk.Terminal.ITerminalApi
{
    // 回写可能等用户在对话框上做选择,给足时间。
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> GetOutputAsync(string sessionId, int maxLines = 1000, CancellationToken cancellationToken = default)
        => await rpc.RequestAsync<string>(PluginRpc.TerminalGetOutput,
               new TerminalGetOutputRequest(sessionId, maxLines), ReadTimeout, cancellationToken).ConfigureAwait(false) ?? "";

    public async Task<IReadOnlyList<VelaShell.PluginSdk.Terminal.TerminalMatch>> SearchOutputAsync(string sessionId,
        string pattern, bool isRegex = false, int maxMatches = 100, CancellationToken cancellationToken = default)
    {
        TerminalMatchDto[] hits = await rpc.RequestAsync<TerminalMatchDto[]>(PluginRpc.TerminalSearch,
            new TerminalSearchRequest(sessionId, pattern, isRegex, maxMatches), ReadTimeout, cancellationToken)
            .ConfigureAwait(false) ?? [];
        return [.. hits.Select(h => new VelaShell.PluginSdk.Terminal.TerminalMatch(h.Line, h.Text))];
    }

    public Task WriteAsync(string sessionId, string input, CancellationToken cancellationToken = default)
        => rpc.RequestAsync<object>(PluginRpc.TerminalWrite,
            new TerminalWriteRequest(sessionId, input), WriteTimeout, cancellationToken);
}

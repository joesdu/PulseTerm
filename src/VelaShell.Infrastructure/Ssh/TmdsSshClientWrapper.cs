using Tmds.Ssh;
using VelaShell.Core.Net;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Net;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshClientWrapper" /> 的 Tmds.Ssh 实现。
/// </summary>
public sealed class TmdsSshClientWrapper : ISshClientWrapper
{
    private readonly SshClientSettings _settings;
    private readonly SshClientSettings _firstHopSettings;
    private readonly string _firstHopHost;
    private readonly int _firstHopPort;
    private readonly IProxyResolver? _proxyResolver;
    private LoopbackProxyRelay? _relay;
    private SshClient? _client;
    private bool _disposed;

    /// <summary>不带网络代理支持的构造(测试与无代理场景)。</summary>
    public TmdsSshClientWrapper(SshClientSettings settings)
        : this(settings, settings, settings?.HostName ?? "", settings?.Port ?? 22, null)
    {
    }

    /// <summary>
    /// 带网络代理支持的构造。<paramref name="firstHopSettings" /> 是发起真实 TCP 出站的
    /// 那份设置(有跳板链时为最内层跳板,否则即主设置),连接时若代理生效,
    /// 其 HostName/Port 会被改写到环回中继;<paramref name="firstHopHost" />/<paramref name="firstHopPort" />
    /// 保存原始目标,供代理解析与无代理时还原。
    /// </summary>
    public TmdsSshClientWrapper(SshClientSettings settings, SshClientSettings firstHopSettings,
        string firstHopHost, int firstHopPort, IProxyResolver? proxyResolver)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _firstHopSettings = firstHopSettings ?? settings;
        _firstHopHost = firstHopHost;
        _firstHopPort = firstHopPort;
        _proxyResolver = proxyResolver;
        ConnectionTimeout = settings.ConnectTimeout;
    }

    internal SshClient? InnerClient => _client;

    /// <summary>
    /// Tmds.Ssh 的 SshClient 无 IsConnected 属性:_client 仅在连接成功后被赋值,
    /// 再结合 Disconnected 令牌(底层连接丢失时取消)即可如实反映断线。
    /// </summary>
    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client is { } client && !client.Disconnected.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Tmds.Ssh 的 SshClientSettings.ConnectTimeout 对应 SSH 连接超时,默认 10 秒。
    /// </summary>
    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return field;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            field = value;
            _settings.ConnectTimeout = value;
        }
    }

    /// <summary>
    /// Tmds.Ssh 的 SshClient.Disconnected 令牌,底层连接丢失时取消。
    /// </summary>
    public CancellationToken Disconnected => _client?.Disconnected ?? CancellationToken.None;

    // ---- Connection methods ----
    /// <summary>
    /// 连接到远程 SSH 服务器,成功后 _client 被赋值,失败时抛出 SshConnectionException。
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="VelaSshConnectionException"></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return;
        PrepareProxyRelay();
        SshClient? client;
        try
        {
            client = new SshClient(_settings);
        }
        catch (ArgumentException argEx)
        {
            DisposeRelay();
            throw new VelaSshConnectionException(
                $"SSH client configuration is invalid: {argEx.Message}", argEx);
        }
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            SafeDisposeClient(client);
            DisposeRelay();
            throw new VelaSshConnectionException(
                $"SSH connection rejected by Tmds.Ssh with invalid argument: {argEx.Message}", argEx);
        }
        catch (Exception ex)
        {
            SafeDisposeClient(client);
            // 代理拨号/握手失败时,Tmds 只看到连接被断;用中继记录的真实原因报错。
            Exception? proxyError = _relay?.Error;
            // 算法协商失败时回探一次对端的 KEXINIT,把"对端提供什么、我们支持什么"补进消息。
            // 必须赶在 DisposeRelay 之前:走代理时 _settings.HostName 指的正是那条环回中继。
            string? mismatch = proxyError is null
                ? await TryDescribeAlgorithmMismatchAsync(ex, cancellationToken).ConfigureAwait(false)
                : null;
            DisposeRelay();
            if (proxyError is not null)
                throw new VelaSshConnectionException(proxyError.Message, proxyError);
            if (mismatch is not null)
                throw new VelaSshConnectionException($"{ex.Message}\n{mismatch}", ex);
            if (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated) throw translated;
            throw;
        }
        _client = client;
    }

    /// <summary>
    /// 只在算法协商失败(<c>KeyExchangeFailed</c>)时回探对端算法名单。其余失败原因 —— 认证不过、
    /// 超时、根本连不上 —— 本身已经说清楚了,再去开一条连接只是白白打扰对端。
    /// </summary>
    /// <remarks>
    /// 诊断绝不能改变失败本身:探不到就返回 null,原来的异常照常抛出。
    /// </remarks>
    private async Task<string?> TryDescribeAlgorithmMismatchAsync(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || TmdsSshInterop.ExtractConnectFailedReason(ex.Message) is not "KeyExchangeFailed")
        {
            return null;
        }
        try
        {
            return await SshAlgorithmDiagnostics.TryDescribeAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception probeError)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] SSH algorithm probe failed: {probeError}");
            return null;
        }
    }

    /// <summary>
    /// 连接前按当前设置决定代理路由:走代理时开一个环回中继并把首跳设置改写过去,
    /// 不走代理时把首跳设置还原为原始目标(代理设置可能在两次连接之间被改动)。
    /// </summary>
    private void PrepareProxyRelay()
    {
        DisposeRelay();
        if (_proxyResolver is null) return;
        ProxyRoute route;
        try
        {
            route = _proxyResolver.Resolve(_firstHopHost, _firstHopPort);
        }
        catch (InvalidOperationException ex)
        {
            throw new VelaSshConnectionException(ex.Message, ex);
        }
        if (route.Kind == ProxyKind.None)
        {
            _firstHopSettings.HostName = _firstHopHost;
            _firstHopSettings.Port = _firstHopPort;
            return;
        }
        _relay = LoopbackProxyRelay.Start(route, _firstHopHost, _firstHopPort);
        _firstHopSettings.HostName = "127.0.0.1";
        _firstHopSettings.Port = _relay.Port;
    }

    private void DisposeRelay()
    {
        _relay?.Dispose();
        _relay = null;
    }

    /// <summary>
    /// 断开当前连接,释放 _client,不抛出异常。
    /// </summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SafeDisposeClient(ref _client);
        DisposeRelay();
    }

    /// <summary>
    /// 在当前连接上异步打开交互式 shell。Tmds.Ssh 的 pty-req 只接受字符行列数:
    /// 像素尺寸(width/height)、bufferSize 与 terminalModeValues 无对应 API,被忽略。
    /// </summary>
    public async Task<IShellStreamWrapper> CreateShellStreamAsync(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IReadOnlyDictionary<TerminalMode, uint>? terminalModeValues = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            var options = new ExecuteOptions
            {
                AllocateTerminal = true,
                TerminalType = terminalName,
                TerminalWidth = (int)columns,
                TerminalHeight = (int)rows,
            };
            RemoteProcess process = await _client
                .ExecuteShellAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return new ShellStreamWrapper(process);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 在当前连接上异步执行命令,返回标准输出。Tmds.Ssh 的 ExecuteAsync 只返回标准输出,标准错误被忽略。
    /// </summary>
    /// <param name="commandText"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    public async Task<string> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            using RemoteProcess process = await _client
                .ExecuteAsync(commandText, cancellationToken)
                .ConfigureAwait(false);

            using var reader = new StreamReader(
                process.ReadAsStream(StderrHandler.Ignore));
            string result = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is SshConnectionException && IsTornDown())
        {
            throw new ObjectDisposedException(nameof(TmdsSshClientWrapper), ex);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<RemoteCommandResult> RunCommandDetailedAsync(string commandText, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            using RemoteProcess process = await _client
                .ExecuteAsync(commandText, cancellationToken)
                .ConfigureAwait(false);

            // 两条流分别读进内存。Tmds 的 ReadToEndAsStringAsync(readStdout, readStderr, ct)
            // 一次调用同时收两条并各自返回,不会因为一条读空了就把另一条堵住 ——
            // 自己分两次读才是那个经典的死锁:对端在 stderr 上写满了缓冲区等你读,
            // 而你在 stdout 上等它写完。
            (string? standardOutput, string? standardError) = await process
                .ReadToEndAsStringAsync(readStdout: true, readStderr: true, cancellationToken)
                .ConfigureAwait(false);
            int exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            return new(standardOutput ?? string.Empty, standardError ?? string.Empty, exitCode);
        }
        catch (Exception ex) when (ex is SshConnectionException && IsTornDown())
        {
            throw new ObjectDisposedException(nameof(TmdsSshClientWrapper), ex);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public async Task<RemoteCommandStreamResult> StreamCommandAsync(
        string commandText,
        bool includeStandardError,
        Action<bool, string> onLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        RemoteProcess? process = null;
        try
        {
            process = await _client.ExecuteAsync(commandText, cancellationToken).ConfigureAwait(false);
            long lines = 0;
            while (true)
            {
                (bool isError, string? line) = await process
                    .ReadLineAsync(readStdout: true, readStderr: includeStandardError, cancellationToken)
                    .ConfigureAwait(false);
                // line == null 表示进程已退出且输出读完 —— 这是**唯一**的正常收尾条件。
                if (line is null)
                {
                    break;
                }
                lines++;
                onLine(isError, line);
            }
            int exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            return new(exitCode, lines);
        }
        catch (OperationCanceledException)
        {
            // 取消长驻命令时先给远端进程一个 TERM。只 Dispose 通道的话,`docker logs -f`
            // 那一端要等到写管道被拒才知道该退出 —— 在没有新日志的空闲期,那可能是"永远"。
            TrySendTerm(process);
            throw;
        }
        catch (Exception ex) when (ex is SshConnectionException && IsTornDown())
        {
            throw new ObjectDisposedException(nameof(TmdsSshClientWrapper), ex);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TrySendTerm(RemoteProcess? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            process.SendSignal("TERM");
        }
        catch (Exception)
        {
            // 通道可能已经塌了 —— 这只是尽力而为的礼貌收尾,失败不该盖住原来的取消异常。
        }
    }

    /// <summary>
    /// 在当前连接上异步启动端口转发,返回 <see cref="IPortForwardHandle" />。
    /// 转发的数据面走宿主自建的 <see cref="MeteredPortForwardHandle" />(而非直接用 Tmds.Ssh 的
    /// LocalForward/SocksForward),因为库把搬运整个做在内部、不暴露任何计数,
    /// 而隧道面板要显示每条隧道的连接数与累计流量。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<IPortForwardHandle> StartPortForwardAsync(PortForwardRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            return await MeteredPortForwardHandle.CreateAsync(_client, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 在当前连接上开一条到远端 unix 域套接字的双工字节流。
    /// </summary>
    public async Task<Stream> OpenUnixConnectionAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            return await _client.OpenUnixConnectionAsync(socketPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 在当前连接上开一条到远端 TCP 端点的双工字节流。
    /// </summary>
    public async Task<Stream> OpenTcpConnectionAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is null) throw new InvalidOperationException("Not connected.");
        try
        {
            return await _client.OpenTcpConnectionAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TmdsSshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// 释放当前连接,并将 _client 置 null,不抛出异常。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SafeDisposeClient(ref _client);
        DisposeRelay();
    }

    /// <summary>
    /// 安全释放 Tmds.Ssh.SshClient:通道关闭时 Dispose 可能抛出 SshChannelClosedException,
    /// 视为正常清理噪声,吞掉即可。
    /// </summary>
    private static void SafeDisposeClient(SshClient? client)
    {
        try { client?.Dispose(); } catch { }
    }

    private static void SafeDisposeClient(ref SshClient? client)
    {
        try { client?.Dispose(); } catch { }
        client = null;
    }

    private bool IsTornDown()
    {
        if (_disposed) return true;
        try { return _client is null; } catch { return true; }
    }
}

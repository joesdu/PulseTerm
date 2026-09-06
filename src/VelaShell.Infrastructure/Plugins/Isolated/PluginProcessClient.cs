using System.Diagnostics;
using System.IO.Pipes;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 一个隔离插件的宿主侧进程句柄:创建命名管道(随机名 + 一次性令牌,仅当前用户可连)、
/// 拉起 VelaShell.PluginHost、完成握手、发起激活/停用,并观察进程意外退出。
/// 凭据永不出主进程:插件进程只拿到管道名与令牌。
/// </summary>
internal sealed class PluginProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly RpcConnection _connection;
    private readonly PluginCapabilityRouter _router;
    private int _shuttingDown;
    private int _disposed;

    private PluginProcessClient(Process process, RpcConnection connection, PluginCapabilityRouter router)
    {
        _process = process;
        _connection = connection;
        _router = router;
    }

    /// <summary>子进程 id(测试与诊断用)。</summary>
    internal int ProcessId => _process.Id;

    /// <summary>进程在停用流程之外退出时触发一次(崩溃/被杀)。</summary>
    public event Action? Crashed;

    /// <summary>插件发起了一次 RPC 往来(空闲回收的活跃信号,转发自路由器)。</summary>
    public event Action? Activity;

    /// <summary>插件进程打开面板数变化(转发自路由器)。</summary>
    public event Action<int>? SurfacesChanged;

    /// <summary>
    /// 启动心跳:周期 ping,连续两次失败(超时/异常,断连除外——断连走进程退出路径)
    /// 判定插件进程挂死并强杀,由 Exited 事件统一进入崩溃处置。
    /// </summary>
    public void StartHeartbeat(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            int misses = 0;
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    if (_shuttingDown != 0 || _disposed != 0)
                    {
                        return;
                    }
                    try
                    {
                        await _connection.RequestAsync<string>("ping", null, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        misses = 0;
                    }
                    catch (RpcDisconnectedException)
                    {
                        return; // 连接没了 = 进程退出路径接管
                    }
                    catch
                    {
                        if (++misses >= 2)
                        {
                            Trace.WriteLine("[PluginManager] Heartbeat lost twice; killing hung plugin process.");
                            TryKill(_process); // Exited → Crashed 处置
                            return;
                        }
                    }
                }
            }
            catch
            {
                // 心跳自身绝不成为故障源。
            }
        });
    }

    /// <summary>
    /// 管道名的长度上限。
    /// </summary>
    /// <remarks>
    /// <b>这个名字必须短。</b>Unix 上 .NET 把命名管道实现成一个 Unix 域套接字,路径是
    /// <c>$TMPDIR/CoreFxPipe_&lt;名字&gt;</c>,而 <b>macOS 的 sun_path 上限只有 104 字节</b>。
    /// macOS 的每用户临时目录形如
    /// <c>/var/folders/d8/hvxvltxn0fl4rmnd52sncbth0000gn/T/</c> —— 光它就占了 48,
    /// 再加 <c>CoreFxPipe_</c> 的 11,留给名字的只剩 45。
    /// <para>
    /// 原来的名字是 <c>velashell-plugin-</c> + 32 位 GUID = 49,<b>刚好越界</b>:macOS 上
    /// 隔离插件一个都起不来,报 <c>invalid length for use with domain sockets</c>。
    /// Windows 的命名管道与 Linux 的 108 字节上限都容得下,所以这条一直没被发现,
    /// 直到 CI 加上 macOS。
    /// </para>
    /// <para>
    /// 认证靠的是握手令牌(另一个完整 GUID),名字只需要唯一 —— 24 位十六进制(96 位)绰绰有余。
    /// </para>
    /// </remarks>
    internal const int MaxPipeNameLength = 32;

    /// <summary>生成一个够短、够唯一的管道名(见 <see cref="MaxPipeNameLength" />)。</summary>
    internal static string CreatePipeName() => "vsp-" + Guid.NewGuid().ToString("N")[..24];

    /// <summary>
    /// 建管道 → 拉起进程 → 握手 → 激活。任何一步失败都抛出,并保证进程与管道被回收。
    /// </summary>
    public static async Task<PluginProcessClient> StartAsync(PluginManifest manifest, string entryPath,
        PluginContext context, string hostVersion, string dataDirectory,
        TimeSpan activationTimeout, CancellationToken cancellationToken,
        Func<Task<IReadOnlyList<ThemeTokenDto>>>? themeTokens = null,
        IPluginEmbedHost? embedHost = null, bool waitForDebugger = false)
    {
        string pipeName = CreatePipeName();
        string token = Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        RpcConnection? connection = null;
        PluginCapabilityRouter? router = null;
        try
        {
            process = Launch(manifest, entryPath, pipeName, token, dataDirectory, waitForDebugger);

            // 等连接与等进程夭折二选一:PluginHost 起不来(缺运行时/被杀软拦)时不干等 10 秒。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            Task connect = pipe.WaitForConnectionAsync(connectCts.Token);
            Task exited = process.WaitForExitAsync(connectCts.Token);
            Task first = await Task.WhenAny(connect, exited).ConfigureAwait(false);
            // 落败的那条支路只会以取消/管道释放收场,且没人会 await 它:显式吞掉,
            // 否则它的异常在 GC 时以未观察任务异常的形式冒出来(调试器里一条无源头的噪声)。
            Observe(connect);
            Observe(exited);
            if (first == exited)
            {
                throw new InvalidOperationException($"Plugin host process exited before connecting (exit code {process.ExitCode}).");
            }
            await connect.ConfigureAwait(false);

            connection = new(pipe);
            router = new(context, connection, token, hostVersion, themeTokens, embedHost);
            connection.SetRequestHandler(router.HandleRequestAsync);
            connection.SetNotificationHandler(router.HandleNotification);
            connection.Start();

            await router.HandshakeCompleted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            // 激活前先下发主题令牌:插件在 Activate 里就开面板也能拿到宿主配色。
            await router.PushThemeTokensAsync().ConfigureAwait(false);
            await connection.RequestAsync<object>(PluginRpc.PluginActivate, new ActivateRequest("startup"),
                activationTimeout, cancellationToken).ConfigureAwait(false);

            var client = new PluginProcessClient(process, connection, router);
            router.Activity += () => client.Activity?.Invoke();
            router.SurfacesChanged += count => client.SurfacesChanged?.Invoke(count);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => client.OnExited();
            if (process.HasExited)
            {
                client.OnExited(); // 挂事件前已退出的竞态补偿
            }
            return client;
        }
        catch
        {
            router?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            TryKill(process);
            process?.Dispose();
            throw;
        }
    }

    /// <summary>标记一个无人 await 的任务为"已观察",使其异常不再进入未观察任务异常通道。</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static Process Launch(PluginManifest manifest, string entryPath, string pipeName, string token,
        string dataDirectory, bool waitForDebugger = false)
    {
        string baseDir = AppContext.BaseDirectory;
        string exeName = OperatingSystem.IsWindows() ? "VelaShell.PluginHost.exe" : "VelaShell.PluginHost";
        string exePath = Path.Combine(baseDir, exeName);
        string dllPath = Path.Combine(baseDir, "VelaShell.PluginHost.dll");
        ProcessStartInfo startInfo = File.Exists(exePath)
            ? new(exePath)
            : File.Exists(dllPath)
                ? new("dotnet") { ArgumentList = { dllPath } } // apphost 缺席时的开发/特殊平台退路
                : throw new FileNotFoundException($"VelaShell.PluginHost not found next to the application ({baseDir}).");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment["VELA_PLUGIN_PIPE"] = pipeName;
        startInfo.Environment["VELA_PLUGIN_TOKEN"] = token;
        startInfo.Environment["VELA_PLUGIN_ID"] = manifest.Id;
        startInfo.Environment["VELA_PLUGIN_VERSION"] = manifest.Version;
        startInfo.Environment["VELA_PLUGIN_ENTRY"] = entryPath;
        startInfo.Environment["VELA_PLUGIN_DATA_DIR"] = dataDirectory;
        startInfo.Environment["VELA_PARENT_PID"] = Environment.ProcessId.ToString();
        if (waitForDebugger)
        {
            // 子进程据此在装载插件程序集之前挂起等附加(见 VelaShell.PluginHost/Program.cs)。
            startInfo.Environment["VELA_PLUGIN_WAIT_DEBUGGER"] = "1";
        }
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start VelaShell.PluginHost process.");
        }
        // stdout/stderr 落宿主 Trace(设计稿 02 §6:插件进程输出重定向)。
        Drain(process.StandardOutput, manifest.Id);
        Drain(process.StandardError, manifest.Id);
        return process;
    }

    private static void Drain(StreamReader reader, string pluginId) =>
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    Trace.WriteLine($"[PluginHost:{pluginId}] {line}");
                }
            }
            catch
            {
                // 进程退出即结束。
            }
        });

    private void OnExited()
    {
        if (_shuttingDown == 0 && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Exited 是同步事件回调,不能在这里阻塞等管道回收;交给后台任务并吞掉异常,
            // 既满足"ValueTask 只消费一次"(CA2012),也不留未观察任务异常。
            _ = DisposeConnectionAsync();
            _router.Dispose();
            try
            {
                Crashed?.Invoke();
            }
            catch
            {
                // 崩溃回调不扩散。
            }
        }
    }

    private async Task DisposeConnectionAsync()
    {
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 进程已退出,管道回收失败无处上报也无需上报。
        }
    }

    /// <summary>请求插件停用并回收进程:限时等待干净退出,超时强杀。</summary>
    public async Task DeactivateAsync(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
        try
        {
            await _connection.RequestAsync<object>(PluginRpc.PluginDeactivate, null, timeout).ConfigureAwait(false);
        }
        catch
        {
            // 插件停用失败/超时/已断开:照样回收进程。
        }
        // 停用应答已收到 → 先断管道:PluginHost 的读循环读到干净 EOF 即自行退场
        // (Program.cs 的 connection.Disconnected → ExitCode)。此前是"先干等再断管道",
        // 退出只能靠子进程自己那 100ms 定时器,一旦它没能按时收摊,这里就等满 2 秒再强杀
        // ——每次退出应用都多花 2 秒,并在调试器里留下一条 WaitForExitAsync 的取消异常。
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (!await WaitForExitAsync(_process, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            TryKill(_process);
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 限时等待进程退出,超时返回 false。刻意不抛超时异常:
    /// <see cref="Process.WaitForExitAsync(CancellationToken)" /> 与 <c>WaitAsync(timeout)</c>
    /// 超时都会抛(TaskCanceled/Timeout),而"子进程没在期限内退出"是预期内的常规分支,
    /// 不该在调试器里刷首发异常。因此仍走 WhenAny,但给 Task.Delay 配取消令牌:
    /// 进程先退出时立刻取消,不把定时器留到超时(CA2027)。
    /// </summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        Task exited = process.WaitForExitAsync();
        using var delayCts = new CancellationTokenSource();
        // 取消的 Task.Delay 落在 Canceled 而非 Faulted,不会进未观察任务异常通道,无需 Observe。
        var delay = Task.Delay(timeout, delayCts.Token);
        bool exitedFirst = await Task.WhenAny(exited, delay).ConfigureAwait(false) == exited;
        await delayCts.CancelAsync().ConfigureAwait(false);
        return exitedFirst;
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已退出或无权限:尽力而为。
        }
    }

    /// <summary>断连、杀进程、释放句柄(幂等)。</summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _router.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
        TryKill(_process);
        _process.Dispose();
    }
}

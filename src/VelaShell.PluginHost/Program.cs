using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Hosting;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离插件宿主进程入口(设计稿 04):由主程序拉起,经环境变量携带管道名与一次性令牌,
/// 连接 → 握手 → 装载插件(可收集 ALC)→ 应答激活/停用 → 随连接断开或父进程退出而终结。
/// 主线程运行本进程内建的 Avalonia 派发循环 —— 插件用完整的 Avalonia 开自己的窗口
/// (AXAML/样式/国际化/第三方组件包全可用);RPC 与插件逻辑在后台线程。
/// 一个进程恰好承载一个插件;进程退出即卸载,无程序集卸载残留。
/// </summary>
internal static class Program
{
    private static readonly TaskCompletionSource<int> ExitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [STAThread]
    private static int Main()
    {
        // 全部启动参数经环境变量传递(令牌不进命令行,避免进程列表泄漏)。
        string pipeName = Require("VELA_PLUGIN_PIPE");
        string token = Require("VELA_PLUGIN_TOKEN");
        string pluginId = Require("VELA_PLUGIN_ID");
        string pluginVersion = Require("VELA_PLUGIN_VERSION");
        string entryPath = Require("VELA_PLUGIN_ENTRY");
        string dataDirectory = Require("VELA_PLUGIN_DATA_DIR");

        // 父进程守望:主程序没了(崩溃/被杀,来不及发 deactivate),本进程绝不孤儿常驻。
        if (int.TryParse(Environment.GetEnvironmentVariable("VELA_PARENT_PID"), out int parentPid))
        {
            WatchParent(parentPid);
        }

        // 内建 Avalonia:默认软件渲染 —— 插件面板是轻量界面,不值得每个插件进程
        // 各自映射一整套显卡驱动模块(GPU 后端单进程可多占 ~170MB 常驻)。
        // 需要 GPU 的插件用 VELA_PLUGIN_GPU=1 放开。
        AppBuilder builder = AppBuilder.Configure<PluginHostApp>().UsePlatformDetect();
        if (Environment.GetEnvironmentVariable("VELA_PLUGIN_GPU") != "1")
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }
        builder.SetupWithoutStarting();

        // RPC 与插件逻辑全部转后台;主线程专职跑派发循环直至退出信号。
        Task<int> run = Task.Run(() => RunAsync(pipeName, token, pluginId, pluginVersion, entryPath, dataDirectory));
        using var loopCancel = new CancellationTokenSource();
        _ = run.ContinueWith(_ => loopCancel.Cancel(), TaskScheduler.Default);
        try
        {
            Dispatcher.UIThread.MainLoop(loopCancel.Token);
        }
        catch (OperationCanceledException)
        {
            // 退出信号,正常路径。
        }
        int exitCode = run.IsCompletedSuccessfully ? run.Result : 1;
        // 硬退而不是 return:插件是第三方代码,可能起了前台线程;Main 返回后运行时会等它们,
        // 进程于是赖着不走。派发循环都停了,这里没有任何理由再等谁。
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static async Task<int> RunAsync(string pipeName, string token, string pluginId, string pluginVersion,
        string entryPath, string dataDirectory)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);

        using var shutdownSource = new CancellationTokenSource();
        var connection = new RpcConnection(pipe);
        RemotePluginContext? context = null;
        IVelaPlugin? plugin = null;

        connection.SetNotificationHandler((method, payload) => DispatchNotification(context, method, payload));
        connection.SetRequestHandler(async (method, payload, lifetimeToken) =>
        {
            _ = lifetimeToken; // 生命周期取消由 shutdownSource 表达

            switch (method)
            {
                case PluginRpc.PluginActivate:
                    {
                        // 装载与激活合并在此:失败以错误应答回宿主(那边标 Failed 并回收本进程)。
                        var loadContext = new PluginAssemblyLoadContext(pluginId, entryPath);
                        Assembly assembly = loadContext.LoadFromAssemblyPath(entryPath);
                        plugin = (IVelaPlugin)Activator.CreateInstance(PluginEntryLocator.FindEntryType(assembly))!;
                        await plugin.ActivateAsync(context!, shutdownSource.Token).ConfigureAwait(false);
                        return null;
                    }
                case PluginRpc.PluginDeactivate:
                    {
                        // try/finally 是硬要求:DeactivateAsync 是第三方代码,抛异常(或被上面那句
                        // CancelAsync 撞出 OperationCanceledException)就会跳过下面的关窗与退出信号,
                        // 本进程从此挂在 ExitCode.Task 上不走 —— 主程序退出后它就是个孤儿宿主,
                        // 白占内存不说,还锁着 bin 里的插件 dll 让下次编译直接失败。
                        try
                        {
                            await shutdownSource.CancelAsync().ConfigureAwait(false);
                            if (plugin is not null)
                            {
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                await plugin.DeactivateAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            context?.Dispose(); // 关掉本插件的全部窗口
                                                // 先应答再退出:让宿主拿到干净的完成信号。
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                                ExitCode.TrySetResult(0);
                            });
                        }
                        return null;
                    }
                case "ping":
                    return "pong";
                default:
                    throw new InvalidOperationException($"Unknown method '{method}'.");
            }
        });
        connection.Disconnected += () => ExitCode.TrySetResult(2); // 管道断 = 宿主没了,自行退场
        connection.Start();

        // 握手:第一帧带令牌自证身份;失败(令牌/版本不符)宿主直接拒绝。
        HandshakeResponse hello = await connection.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
                                      new HandshakeRequest(token, pluginId, pluginVersion, [VelaPluginApi.Level]),
                                      TimeSpan.FromSeconds(10)).ConfigureAwait(false)
                                  ?? throw new InvalidOperationException("Empty handshake response.");
        context = new(connection, pluginId, pluginVersion, dataDirectory, hello, shutdownSource.Token);
        PluginHostApp.ApplyHostTheme(hello.Theme); // 明暗跟随宿主

        int code = await ExitCode.Task.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        return code;
    }

    private static void DispatchNotification(RemotePluginContext? context, string method, JsonElement? payload)
    {
        // 主题令牌在激活流程前就会下发(宿主握手后立即推送),不依赖上下文。
        if (method == PluginRpc.ThemeTokens && Deserialize<ThemeTokensNotification>(payload) is { } tokens)
        {
            PluginHostThemeTokens.Apply(tokens);
            return;
        }
        if (context is null)
        {
            return; // 握手完成前不接受其它通知
        }
        switch (method)
        {
            case PluginRpc.CommandExecute when Deserialize<CommandRef>(payload) is { } command:
                context.CommandsProxy.OnExecute(command.Id);
                break;
            case PluginRpc.HostEvent when Deserialize<HostEventNotification>(payload) is { } hostEvent:
                context.EventsHub.OnHostEvent(hostEvent);
                break;
            case PluginRpc.FsProgress when Deserialize<FsProgressNotification>(payload) is { } progress:
                context.RemoteFsProxy.OnProgress(progress);
                break;
        }
    }

    private static T? Deserialize<T>(JsonElement? payload) where T : class
        => payload is { } element ? element.Deserialize<T>() : null;

    private static string Require(string variable)
        => Environment.GetEnvironmentVariable(variable)
           ?? throw new InvalidOperationException($"Missing required environment variable {variable}. " +
                                                  "VelaShell.PluginHost is an internal process launched by VelaShell.");

    private static void WatchParent(int parentPid)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                await parent.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // 拿不到父进程 = 已经没了。
            }
            ExitCode.TrySetResult(3);

            // 父进程没了之后再给优雅收尾两秒就地兜底硬退:此时管道多半是半开的,
            // RPC 拆解可能永远等不到对端,而"父进程都不在了"这件事已经没有任何回旋余地。
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Environment.Exit(3);
        });
    }
}

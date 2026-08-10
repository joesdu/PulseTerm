using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.HelloWorld;

/// <summary>
/// 官方示例插件:每个能力各演示一处最小用法。
/// 命令面板(Ctrl+P / Ctrl+K)提供:
/// - "Hello World: List Sessions" —— 枚举当前会话并记入日志;
/// - "Hello World: Remote Uptime" —— 在第一条已连接会话上执行 <c>uptime</c>;
/// - "Hello World: Open Panel (Tab / Window)" —— 打开 AXAML 面板
///   (完整 Avalonia,进程内可停靠拖拽;隔离进程下 Tab 自动回退为窗口)。
/// </summary>
[VelaPlugin]
public sealed class HelloWorldPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private IPluginPanel? _panel;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;

        // 存储:统计激活次数(数据落在插件私有数据目录,卸载可整体清除)。
        int activations = await context.Storage.GetAsync<int>("activations", cancellationToken) + 1;
        await context.Storage.SetAsync("activations", activations, cancellationToken);
        context.Log.Info($"Hello from {context.PluginId} v{context.PluginVersion} " +
                         $"(activation #{activations}, host {context.Host.AppVersion}, locale {context.Host.Locale}).");

        // 事件:会话连接/断开与主题切换的推送。处理器必须快速返回、不得抛出。
        context.Events.SessionConnected += session =>
            context.Log.Info($"Session connected: {session.Username}@{session.Host}:{session.Port}");
        context.Events.ThemeChanged += theme => context.Log.Info($"Theme changed to {theme}.");

        // 命令:id 必须以 "<pluginId>." 为前缀;命令体在后台线程执行,勿直接碰控件
        //(打开面板经 Ui.ShowPanelAsync,工厂由宿主在 UI 线程调用)。
        context.Commands.Register(new(
            $"{context.PluginId}.list-sessions", "Hello World: List Sessions", "Hello World",
            ListSessionsAsync));
        context.Commands.Register(new(
            $"{context.PluginId}.uptime", "Hello World: Remote Uptime", "Hello World",
            RemoteUptimeAsync));
        context.Commands.Register(new(
            $"{context.PluginId}.panel", "Hello World: Open Panel (Tab)", "Hello World",
            _ => OpenPanelAsync(PanelDisplayMode.Document)));
        context.Commands.Register(new(
            $"{context.PluginId}.panel-window", "Hello World: Open Panel (Window)", "Hello World",
            _ => OpenPanelAsync(PanelDisplayMode.Window)));
        context.Commands.Register(new(
            $"{context.PluginId}.grep-errors", "Hello World: Find 'error' in Terminal", "Hello World",
            GrepTerminalAsync));
        context.Commands.Register(new(
            $"{context.PluginId}.echo-terminal", "Hello World: Type into Terminal", "Hello World",
            WriteTerminalAsync));
    }

    /// <summary>终端读取/搜索:在活动会话的终端输出里找 "error"(大小写不敏感)。</summary>
    private async Task GrepTerminalAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        if (await FirstConnectedAsync(cancellationToken) is not { } session)
        {
            return;
        }
        IReadOnlyList<VelaShell.PluginSdk.Terminal.TerminalMatch> matches =
            await context.Terminal.SearchOutputAsync(session.SessionId, "error", cancellationToken: cancellationToken);
        context.Log.Info(matches.Count == 0
            ? "No 'error' lines in terminal."
            : $"Found {matches.Count} 'error' line(s); first at line {matches[0].Line}: {matches[0].Text}");
    }

    /// <summary>终端回写:向活动会话的终端敲一条 echo(触发用户授权弹窗)。</summary>
    private async Task WriteTerminalAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        if (await FirstConnectedAsync(cancellationToken) is not { } session)
        {
            return;
        }
        try
        {
            await context.Terminal.WriteAsync(session.SessionId, "echo hello-from-plugin\n", cancellationToken);
            context.Log.Info("Wrote a command to the terminal.");
        }
        catch (PluginPermissionDeniedException)
        {
            context.Log.Warn("Terminal write was denied by the user.");
        }
    }

    private async Task<SessionInfo?> FirstConnectedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionInfo> sessions = await _context!.Sessions.ListAsync(cancellationToken);
        SessionInfo? session = sessions.FirstOrDefault(s => s.State == SessionState.Connected);
        if (session is null)
        {
            _context.Log.Warn("No connected session; open one first.");
        }
        return session;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        // 命令、事件订阅与打开的面板由宿主自动清理;这里只需收尾自己的状态。
        _context?.Log.Info("Goodbye.");
        _context = null;
        _panel = null;
        return Task.CompletedTask;
    }

    private async Task OpenPanelAsync(PanelDisplayMode mode)
    {
        IPluginContext context = _context!;
        if (_panel is { IsOpen: true })
        {
            return; // 已开着(面板是活控件,无需重开刷新)
        }
        // 工厂在 UI 线程被调用:直接构造编译期 AXAML 视图。
        _panel = await context.Ui.ShowPanelAsync(
            new() { Title = "Hello World", DisplayMode = mode, WindowWidth = 560, WindowHeight = 480 },
            () => new DemoPanelView(context));
        _panel.Closed += () => context.Log.Info("Demo panel closed.");
    }

    private async Task ListSessionsAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken);
        context.Log.Info(sessions.Count == 0
            ? "No active sessions."
            : $"Sessions: {string.Join(", ", sessions.Select(s => $"{s.Username}@{s.Host} [{s.State}]"))}");
    }

    private async Task RemoteUptimeAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken);
        if (sessions.FirstOrDefault(s => s.State == SessionState.Connected) is not { } session)
        {
            context.Log.Warn("No connected session; open one first.");
            return;
        }
        // 独立 exec 通道执行,不进用户终端、不污染 shell 历史。
        ExecResult result = await context.RemoteExec.RunAsync(session.SessionId, "uptime",
            cancellationToken: cancellationToken);
        context.Log.Info($"uptime@{session.Host}: {result.Output.Trim()}");
    }
}

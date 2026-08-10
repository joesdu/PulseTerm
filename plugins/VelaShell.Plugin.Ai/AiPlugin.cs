using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai;

/// <summary>
/// AI 助手插件入口。经 manifest 的 <c>onCommand</c> 惰性激活:
/// 用户第一次触发 AI 命令才装载本程序集。
/// 命令:打开聊天(标签页/窗口)、解释当前终端输出。
/// </summary>
[VelaPlugin]
public sealed class AiPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private AiSettingsStore? _store;
    private IPluginPanel? _panel;
    private ChatPanelView? _view;
    private readonly List<IDisposable> _commands = [];

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _store = new AiSettingsStore(context);
        RegisterCommands();
        // 命令标题本地化:语言切换时按同 id 重注册替换
        context.Events.LocaleChanged += _ => RegisterCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        // 命令、事件订阅与面板由宿主自动清理;这里只拆自己的引用。
        _view?.Detach();
        _view = null;
        _panel = null;
        _commands.Clear();
        _context = null;
        return Task.CompletedTask;
    }

    private void RegisterCommands()
    {
        IPluginContext context = _context!;
        var loc = new Loc(context.Host.Locale);
        foreach (IDisposable command in _commands)
        {
            command.Dispose();
        }
        _commands.Clear();
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.chat", loc["CmdChat"], "AI",
            _ => OpenPanelAsync(PanelDisplayMode.Document))));
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.chat-window", loc["CmdChatWindow"], "AI",
            _ => OpenPanelAsync(PanelDisplayMode.Window))));
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.explain-terminal", loc["CmdExplain"], "AI",
            ExplainTerminalAsync)));
    }

    private async Task<ChatPanelView?> OpenPanelAsync(PanelDisplayMode mode)
    {
        IPluginContext context = _context!;
        if (_panel is { IsOpen: true } && _view is not null)
        {
            return _view; // 已开着(面板是活控件)
        }
        ChatPanelView? view = null;
        _panel = await context.Ui.ShowPanelAsync(
            new PanelOptions
            {
                Title = new Loc(context.Host.Locale)["Title"],
                DisplayMode = mode,
                WindowWidth = 720,
                WindowHeight = 560
            },
            () => view = new ChatPanelView(context, _store!));
        _view = view;
        _panel.Closed += () =>
        {
            _view?.Detach();
            _view = null;
            _panel = null;
        };
        return _view;
    }

    private async Task ExplainTerminalAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        var loc = new Loc(context.Host.Locale);
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken);
        if (sessions.FirstOrDefault(s => s.State == SessionState.Connected) is not { } session)
        {
            context.Log.Warn(loc["NoConnectedSession"]);
            return;
        }
        string output = await context.Terminal.GetOutputAsync(session.SessionId, 200, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            context.Log.Warn("Terminal buffer is empty; nothing to explain.");
            return;
        }
        ChatPanelView? view = await OpenPanelAsync(PanelDisplayMode.Document);
        view?.SendExternal($"{loc["ExplainPrompt"]}```\n{output}\n```");
    }
}

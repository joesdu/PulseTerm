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
                // 聊天面板的位置对齐 VSCode 的 Copilot:标签区右侧独立一栏,
                // 终端与它并排看得见,而不是把当前终端顶掉
                Placement = PanelPlacement.Right,
                PlacementRatio = await PanelWidthRatioAsync(),
                WindowWidth = 720,
                WindowHeight = 560
            },
            () => view = new ChatPanelView(context, _store!));
        _view = view;
        _panel.PlacementRatioChanged += RememberPanelWidth;
        _panel.Closed += () =>
        {
            _view?.Detach();
            _view = null;
            _panel = null;
        };
        return _view;
    }

    /// <summary>
    /// 读上次记住的侧栏宽度。读不到就用 <see cref="PanelOptions" /> 的默认值 ——
    /// 面板本身能不能打开,不该被一条装饰性配置卡住。
    /// </summary>
    private async Task<double> PanelWidthRatioAsync()
    {
        try
        {
            AiSettings settings = await _store!.LoadAsync();
            return settings.PanelWidthPercent / 100.0;
        }
        catch (Exception ex)
        {
            _context!.Log.Warn($"Reading the panel width setting failed, using the default: {ex.Message}");
            return new PanelOptions { Title = "" }.PlacementRatio;
        }
    }

    /// <summary>
    /// 把用户拖出来的宽度记下来,下次打开就是这个宽度。
    /// </summary>
    /// <remarks>
    /// 这一项<b>不在设置页里</b>:让人去填一个百分比,不如直接把他拖出来的结果记住。
    /// 宿主在拖动<b>结束</b>时才通知一次(见 <c>IPluginPanel.PlacementRatioChanged</c>),
    /// 所以直接落盘,不必防抖。四舍五入到整百分比,免得配置里躺着 30.000000000000004。
    ///
    /// <para><b>交给面板去写,不要自己 Load-改-Save。</b>面板持有整份设置,它每次保存都是
    /// 整体覆盖 —— 背着它写库的话,下一次换模式/勾工具就会拿它内存里那份旧宽度盖回来,
    /// 表现就是"拖了不算数,重开还是老样子"。</para>
    /// </remarks>
    private void RememberPanelWidth(double ratio)
    {
        if (!double.IsFinite(ratio))
        {
            return;
        }
        _view?.RememberPanelWidth(Math.Clamp((int)Math.Round(ratio * 100), 15, 85));
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

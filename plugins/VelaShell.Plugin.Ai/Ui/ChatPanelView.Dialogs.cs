using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 设置、"配置工具"、MCP 服务器配置各开一个独立窗口。
/// </summary>
/// <remarks>
/// <para>
/// 之前设置是挤在面板中间、和聊天流三选一显示的:一进设置就看不见对话,改完还要点回去。
/// 面板本身常常只有三成宽(侧栏),设置页那些字段在那个宽度里也铺不开。
/// 独立窗口两个问题一起解决,也和 VSCode 里 Copilot 的做法一致。
/// </para>
/// <para>
/// <b>窗体走 SDK 的 <see cref="PanelDisplayMode.Window" />,不自己 new Window。</b>
/// 宿主的插件面板窗口是自绘卡片(透明窗 + 8px 圆角 + 自绘标题栏与缩放抓取区),
/// 和链路追踪、资源监视那些窗口同一套规格;插件自己开原生标题栏的窗口会跟整体风格打架。
/// 自绘那套还要配 Win32 的 DWM 调用才不掉圆角/不留残影,那是宿主的事,插件够不着也不该重造。
/// </para>
/// </remarks>
public partial class ChatPanelView
{
    private IPluginPanel? _settingsPanel;
    private IPluginPanel? _globalSettingsPanel;
    private IPluginPanel? _toolsPanel;
    private IPluginPanel? _mcpPanel;

    /// <summary>打开设置窗口(已开着就带到前面,不重复开)。</summary>
    private void OpenSettingsDialog()
    {
        if (Activate(_settingsPanel))
        {
            return;
        }
        // SettingsView 是长表单,自己带滚动;窗口给足宽度让那些两列/三列的行铺得开。
        // 全局设置(系统提示词/压缩/后续提问)不占这页版面:标题栏上、最小化键左侧一枚 ⚙,
        // 与主窗体标题栏那排工具按钮同一套版式,点开是另一个小窗口。
        _ = OpenAsync(
            _loc["ModelSettings"], 940, 760,
            [new PanelTitleAction(SettingsIconPath, _loc["GlobalSettings"], OpenGlobalSettingsDialog)],
            () => _settingsView = new SettingsView(_context, _store, _settings, _loc, OnProvidersChanged),
            panel => _settingsPanel = panel,
            () =>
            {
                _settingsPanel = null;
                _settingsView = null; // 语言切换时只刷还开着的那个
                // 入口没了,附属的全局设置窗口也一起收
                _ = _globalSettingsPanel?.CloseAsync();
            });
    }

    /// <summary>lucide settings(齿轮)路径:标题栏动作按钮要的是路径数据而不是资源键(隔离进程没有宿主 Icon.*)。</summary>
    private const string SettingsIconPath =
        "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z";

    /// <summary>打开全局设置窗口(从模型配置窗口标题栏的 ⚙ 进来;已开着就置前)。</summary>
    private void OpenGlobalSettingsDialog()
    {
        if (Activate(_globalSettingsPanel))
        {
            return;
        }
        _ = OpenAsync(
            _loc["GlobalSettings"], 620, 480, [],
            () => _globalSettingsView = new GlobalSettingsView(_context, _settings, _loc, PersistSettingsAsync),
            panel => _globalSettingsPanel = panel,
            () =>
            {
                _globalSettingsPanel = null;
                _globalSettingsView = null;
            });
    }

    /// <summary>打开"配置工具"窗口。</summary>
    private void OpenToolsDialog()
    {
        if (Activate(_toolsPanel))
        {
            return;
        }
        ToolPickerView? picker = null;
        // 标题栏的 ⚙ 通向 MCP 服务器配置。它是一整套左列表右表单,
        // 压在勾选列表上面会把这一页挤得没法看,所以自己占一个窗口;
        // 入口放标题栏而不是内容区右上角,和模型配置窗口的全局设置一个位置。
        // (picker 在工厂里才造出来,回调只在用户点的时候跑,那时它早就有了。)
        _ = OpenAsync(
            _loc["ConfigureTools"], 720, 680,
            [new PanelTitleAction(SettingsIconPath, _loc["McpServers"], () => OpenMcpDialog(picker!))],
            () => picker = new ToolPickerView(_context, _settings, _loc, PersistSettingsAsync),
            panel => _toolsPanel = panel,
            () =>
            {
                _toolsPanel = null;
                // 勾选列表都没了,单剩一个服务器配置窗口飘在那儿没有意义
                _ = _mcpPanel?.CloseAsync();
            });
    }

    /// <summary>打开 MCP 服务器配置窗口;改完当场重建工具勾选列表。</summary>
    private void OpenMcpDialog(ToolPickerView picker)
    {
        if (Activate(_mcpPanel))
        {
            return;
        }
        _ = OpenAsync(
            _loc["McpServers"], 900, 660, [],
            () =>
            {
                var servers = new McpServersView(_context, _settings, _loc, PersistSettingsAsync);
                servers.ServersChanged += picker.Rebuild;
                return servers;
            },
            panel => _mcpPanel = panel,
            () => _mcpPanel = null);
    }

    /// <summary>已经开着就带到前面并返回 true —— 什么都不做会像是按钮坏了。</summary>
    private static bool Activate(IPluginPanel? panel)
    {
        if (panel is not { IsOpen: true })
        {
            return false;
        }
        _ = panel.ActivateAsync();
        return true;
    }

    /// <summary>
    /// 开一个宿主同款自绘卡片窗口装 <paramref name="factory" /> 造出来的视图。
    /// </summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="width">初始宽。</param>
    /// <param name="height">初始高。</param>
    /// <param name="titleActions">标题栏上、最小化键左侧的动作按钮(可空)。</param>
    /// <param name="factory">内容工厂(宿主在 UI 线程调用)。</param>
    /// <param name="onOpened">拿到面板句柄(用于"已开着就置前"与随面板一并关闭)。</param>
    /// <param name="onClosed">窗口关掉后清账。</param>
    private async Task OpenAsync(string title, double width, double height,
        IReadOnlyList<PanelTitleAction> titleActions,
        Func<Control> factory, Action<IPluginPanel> onOpened, Action onClosed)
    {
        // 视图当场就造,工厂只是把它递给宿主 —— 造在工厂里就拿不到实例,没法给它挂 Esc。
        // 这里本来就在 UI 线程上(按钮点出来的),满足 ShowPanelAsync 对工厂的线程要求。
        Control view = factory();
        try
        {
            IPluginPanel panel = await _context.Ui.ShowPanelAsync(new PanelOptions
            {
                Title = title,
                DisplayMode = PanelDisplayMode.Window,
                WindowWidth = width,
                WindowHeight = height,
                TitleActions = titleActions
            }, () => view);
            onOpened(panel);
            // Esc 关窗,与宿主其它窗口一致。挂在内容上而不是让宿主对所有插件面板统一处理:
            // 聊天面板也能以窗口形态打开,那里 Esc 必须留给输入框(正打着字被关掉窗口很糟)。
            view.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _ = panel.CloseAsync();
                }
            }, RoutingStrategies.Bubble);
            // Closed 是宿主在线程池上回调的(见 PluginPanel.NotifyClosed),清账要回 UI 线程。
            // 注意用类型名限定:Avalonia 的 AvaloniaObject 上也有个实例属性叫 Dispatcher。
            panel.Closed += () => Avalonia.Threading.Dispatcher.UIThread.Post(onClosed);
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Opening the '{title}' window failed.", ex);
            onClosed();
        }
    }

    /// <summary>面板关闭时把这几个窗口一并带走,别留在屏幕上。</summary>
    private void CloseDialogs()
    {
        _ = _settingsPanel?.CloseAsync();
        _ = _globalSettingsPanel?.CloseAsync();
        _globalSettingsPanel = null;
        _settingsPanel = null;
        _ = _mcpPanel?.CloseAsync();
        _mcpPanel = null;
        _ = _toolsPanel?.CloseAsync();
        _toolsPanel = null;
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.HelloWorld;

/// <summary>
/// 示例面板的代码后置:标准 Avalonia UserControl。事件处理器在 UI 线程触发,
/// await 能力调用后自动回到 UI 线程,直接更新控件即可。
/// 国际化由插件自理:这里按 <c>Host.Locale</c> 取双语文案(演示用最简做法)。
/// </summary>
public partial class DemoPanelView : UserControl
{
    private readonly IPluginContext _context;
    private readonly bool _chinese;

    /// <summary>用插件上下文构造面板(必须在 UI 线程,由 ShowPanelAsync 的工厂保证)。</summary>
    public DemoPanelView(IPluginContext context)
    {
        _context = context;
        _chinese = context.Host.Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        InitializeComponent();
        TitleText.Text = T("Hello World 插件面板", "Hello World Plugin Panel");
        SubtitleText.Text = $"{T("宿主", "Host")} {context.Host.AppVersion} · apiLevel {context.Host.ApiLevel} · {context.Host.Theme}";
        RefreshButton.Content = T("刷新会话列表", "Refresh sessions");
        UptimeButton.Content = T("对首个会话执行 uptime", "Run uptime on first session");
        SessionsText.Text = T("(尚未刷新)", "(not refreshed yet)");
        EchoInput.PlaceholderText = T("输入点什么…", "Type something…");
        EchoButton.Content = T("回显", "Echo");
        CopyButton.Content = T("复制", "Copy");

        RefreshButton.Click += OnRefreshClick;
        UptimeButton.Click += OnUptimeClick;
        EchoButton.Click += OnEchoClick;
        CopyButton.Click += OnCopyClick;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 剪贴板能力:两种宿主模式同一写法(隔离模式经 RPC 路由到宿主执行)。
            await _context.Clipboard.SetTextAsync(UptimeText.Text ?? "");
            CopyButton.Content = T("已复制 ✓", "Copied ✓");
        }
        catch (Exception ex)
        {
            _context.Log.Error("Copy failed.", ex);
        }
    }

    private string T(string zh, string en) => _chinese ? zh : en;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<SessionInfo> sessions = await _context.Sessions.ListAsync();
            SessionsText.Text = sessions.Count == 0
                ? T("(没有活动会话 —— 先在宿主里连接一台服务器)", "(no active sessions — connect one in the host first)")
                : string.Join(Environment.NewLine, sessions.Select(s => $"{s.Username}@{s.Host}:{s.Port}  [{s.State}]"));
        }
        catch (Exception ex)
        {
            _context.Log.Error("Refresh failed.", ex);
            SessionsText.Text = $"{T("刷新失败", "Refresh failed")}: {ex.Message}";
        }
    }

    private async void OnUptimeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<SessionInfo> sessions = await _context.Sessions.ListAsync();
            if (sessions.FirstOrDefault(s => s.State == SessionState.Connected) is not { } session)
            {
                UptimeText.Text = T("(没有已连接的会话)", "(no connected session)");
                return;
            }
            BusyBar.IsVisible = true;
            UptimeButton.IsEnabled = false;
            try
            {
                // 独立 exec 通道执行,不进用户终端、不污染 shell 历史。
                ExecResult result = await _context.RemoteExec.RunAsync(session.SessionId, "uptime");
                UptimeText.Text = $"uptime@{session.Host}: {result.Output.Trim()}";
                CopyButton.IsVisible = true;
            }
            finally
            {
                BusyBar.IsVisible = false;
                UptimeButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _context.Log.Error("Uptime failed.", ex);
            UptimeText.Text = $"{T("执行失败", "Failed")}: {ex.Message}";
        }
    }

    private void OnEchoClick(object? sender, RoutedEventArgs e)
        => EchoText.Text = string.IsNullOrEmpty(EchoInput.Text)
            ? ""
            : $"{T("你说的是", "You said")}: {EchoInput.Text}";
}

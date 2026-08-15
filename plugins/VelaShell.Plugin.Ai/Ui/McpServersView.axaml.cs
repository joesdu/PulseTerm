using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// MCP 服务器的增删改查(原先长在设置页底部,现搬到「配置工具」窗口顶部)。
/// </summary>
/// <remarks>
/// 搬家的理由:配好一台服务器,下一步必然是挑它的哪些工具给模型用 —— 那份勾选列表就在本控件正下方。
/// 留在设置页里的话,这两件本就连着做的事被隔在两个窗口,加完还得自己想起来去另一边刷新一次。
/// </remarks>
public partial class McpServersView : UserControl
{
    private static readonly string[] TransportLabels = ["Stdio (local process)", "HTTP (remote)"];

    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;
    private bool _loading;

    /// <summary>服务器集合或它的工具库变了 —— 下方的勾选列表要跟着重建。</summary>
    public event Action? ServersChanged;

    /// <param name="context">插件上下文(只用来记日志)。</param>
    /// <param name="settings">面板共享的设置实例;直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="persist">落盘。</param>
    public McpServersView(IPluginContext context, AiSettings settings, Loc loc, Func<Task> persist)
    {
        _context = context;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        InitializeComponent();
        ApplyLoc();

        McpTransportCombo.ItemsSource = TransportLabels;
        McpList.SelectionChanged += (_, _) => LoadEditor();
        McpTransportCombo.SelectionChanged += (_, _) => UpdateTransportPanels();
        McpAddButton.Click += OnAddClick;
        McpSaveButton.Click += OnSaveClick;
        McpDeleteButton.Click += OnDeleteClick;
        McpTestButton.Click += OnTestClick;

        ReloadList(_settings.McpServers.Count > 0 ? 0 : -1);
    }

    /// <summary>语言切换时调用。</summary>
    public void ApplyLoc()
    {
        McpHintText.Text = _loc["McpHint"];
        McpAddText.Text = _loc["Add"];
        McpEnabledCheck.Content = _loc["McpEnabled"];
        McpNameLabel.Text = _loc["Name"];
        McpTransportLabel.Text = _loc["McpTransport"];
        McpCommandLabel.Text = _loc["McpCommand"];
        McpArgumentsLabel.Text = _loc["McpArguments"];
        McpWorkingDirLabel.Text = _loc["McpWorkingDir"];
        McpEnvLabel.Text = _loc["McpEnv"];
        McpUrlLabel.Text = _loc["McpUrl"];
        McpHeadersLabel.Text = _loc["McpHeaders"];
        McpSaveText.Text = _loc["Save"];
        McpTestText.Text = _loc["Test"];
        McpDeleteText.Text = _loc["Delete"];
    }

    private McpServerConfig? Selected
        => McpList.SelectedIndex >= 0 && McpList.SelectedIndex < _settings.McpServers.Count
            ? _settings.McpServers[McpList.SelectedIndex]
            : null;

    private void ReloadList(int selectIndex)
    {
        McpList.ItemsSource = _settings.McpServers
            .Select(s => (string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name) + (s.Enabled ? "" : " ⏸"))
            .ToList();
        McpList.SelectedIndex = Math.Min(selectIndex, _settings.McpServers.Count - 1);
        if (McpList.SelectedIndex < 0)
        {
            LoadEditor();
        }
    }

    private void LoadEditor()
    {
        _loading = true;
        try
        {
            McpServerConfig? server = Selected;
            McpEnabledCheck.IsChecked = server?.Enabled ?? false;
            McpNameBox.Text = server?.Name ?? "";
            McpTransportCombo.SelectedIndex = server is null ? -1 : (int)server.Transport;
            McpCommandBox.Text = server?.Command ?? "";
            McpArgumentsBox.Text = server?.Arguments ?? "";
            McpWorkingDirBox.Text = server?.WorkingDirectory ?? "";
            McpEnvBox.Text = server?.EnvironmentVariables ?? "";
            McpUrlBox.Text = server?.Url ?? "";
            McpHeadersBox.Text = server?.Headers ?? "";
            bool hasSelection = server is not null;
            McpEnabledCheck.IsEnabled = hasSelection;
            McpSaveButton.IsEnabled = hasSelection;
            McpTestButton.IsEnabled = hasSelection;
            McpDeleteButton.IsEnabled = hasSelection;
            UpdateTransportPanels();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateTransportPanels()
    {
        bool http = McpTransportCombo.SelectedIndex == (int)McpTransportType.Http;
        McpStdioPanel.IsVisible = !http;
        McpHttpPanel.IsVisible = http;
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        _settings.McpServers.Add(new McpServerConfig
        {
            Name = $"server-{_settings.McpServers.Count + 1}",
            Command = "npx"
        });
        ReloadList(_settings.McpServers.Count - 1);
        SaveAndNotify();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } server)
        {
            return;
        }
        ApplyForm(server);
        ReloadList(McpList.SelectedIndex);
        McpStatusText.Text = _loc["Saved"];
        SaveAndNotify();
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } server)
        {
            return;
        }
        int index = McpList.SelectedIndex;
        _settings.McpServers.Remove(server);
        ReloadList(Math.Max(0, index - 1));
        McpStatusText.Text = "";
        SaveAndNotify();
    }

    private void OnTestClick(object? sender, RoutedEventArgs e) => _ = TestAsync();

    /// <summary>
    /// 连一次并把工具库<b>顺手拉下来</b>存进配置 —— 连接都建好了,
    /// 再让用户去下面每台服务器上点一次"更新工具库"纯属多此一举。
    /// </summary>
    private async Task TestAsync()
    {
        if (Selected is not { } server)
        {
            return;
        }
        // 用表单当前值(可能还没保存)去连
        var candidate = new McpServerConfig { Id = server.Id };
        ApplyForm(candidate);
        McpTestButton.IsEnabled = false;
        McpStatusText.Text = _loc["Testing"];
        try
        {
            IReadOnlyList<McpToolInfo> tools = await McpManager.RefreshToolsAsync(candidate, CancellationToken.None);
            // 等待期间用户可能已经切走了,别把结果按到别人头上
            if (Selected?.Id == server.Id)
            {
                server.KnownTools = [.. tools];
                server.ToolsRefreshedAt = DateTimeOffset.UtcNow;
                SaveAndNotify();
            }
            string names = string.Join(", ", tools.Select(t => t.Name));
            McpStatusText.Text = _loc.F("McpTestOk", tools.Count, names.Length > 200 ? names[..200] + "…" : names);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Testing MCP server '{server.Name}' failed: {ex.Message}");
            McpStatusText.Text = _loc.F("TestFail", ex.Message);
        }
        finally
        {
            McpTestButton.IsEnabled = Selected is not null;
        }
    }

    /// <summary>把表单当前值写入目标配置(保存与测试共用)。</summary>
    private void ApplyForm(McpServerConfig target)
    {
        target.Enabled = McpEnabledCheck.IsChecked == true;
        target.Name = McpNameBox.Text?.Trim() ?? "";
        target.Transport = McpTransportCombo.SelectedIndex == (int)McpTransportType.Http
            ? McpTransportType.Http
            : McpTransportType.Stdio;
        target.Command = McpCommandBox.Text?.Trim() ?? "";
        target.Arguments = McpArgumentsBox.Text?.Trim() ?? "";
        target.WorkingDirectory = McpWorkingDirBox.Text?.Trim() ?? "";
        target.EnvironmentVariables = McpEnvBox.Text ?? "";
        target.Url = McpUrlBox.Text?.Trim() ?? "";
        target.Headers = McpHeadersBox.Text ?? "";
        // DisabledTools 不在这儿写:那是下方勾选列表的结果,这里跟着回写就会拿旧值盖掉刚勾的状态。
    }

    private void SaveAndNotify()
    {
        _ = _persist();
        ServersChanged?.Invoke();
    }
}

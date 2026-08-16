using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>服务器列表里的一行。</summary>
/// <param name="Name">服务器名(空的话给个占位,免得列表里出现一行空白)。</param>
/// <param name="Detail">副标题:传输方式 · 工具库状态,停用的另标一下 —— 光一个名字看不出这些。</param>
public sealed record McpListItem(string Name, string Detail);

/// <summary>
/// MCP 服务器的增删改查,自己占一个窗口(入口是「配置工具」标题行右侧的 ⚙)。
/// </summary>
/// <remarks>
/// 不放在设置页:配好一台服务器,下一步必然是挑它的哪些工具给模型用,而那份勾选列表就在
/// 点开本窗口的那个页面上 —— 改完这边会回调过去当场重建它。也不压在那份列表<b>上面</b>:
/// 这是一整套左列表右表单,叠上去会把勾选那一页挤得没法看。
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
        McpEmptyText.Text = _loc["McpNoServers"];
        McpNoSelectionText.Text = _loc["McpNoSelection"];
        McpStdioHintText.Text = _loc["McpStdioHint"];
        McpHttpHintText.Text = _loc["McpHttpHint"];
        McpEnabledCheck.Content = _loc["McpEnabled"];
        McpNameLabel.Text = _loc["Name"];
        McpTransportLabel.Text = _loc["McpTransport"];
        McpCommandLabel.Text = _loc["McpCommand"];
        McpArgumentsLabel.Text = _loc["McpArguments"];
        McpWorkingDirLabel.Text = _loc["McpWorkingDir"];
        McpWorkingDirHintText.Text = _loc.F("McpWorkingDirHint", McpWorkspace.DefaultDirectory);
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
        McpList.ItemsSource = _settings.McpServers.Select(ToListItem).ToList();
        McpEmptyText.IsVisible = _settings.McpServers.Count == 0;
        McpList.SelectedIndex = Math.Min(selectIndex, _settings.McpServers.Count - 1);
        if (McpList.SelectedIndex < 0)
        {
            LoadEditor();
        }
    }

    /// <summary>列表里的一行:名字 + "传输方式 · 工具库状态"。</summary>
    private McpListItem ToListItem(McpServerConfig server)
    {
        string transport = server.Transport == McpTransportType.Http ? "HTTP" : "Stdio";
        string tools = server.KnownTools.Count > 0
            ? _loc.F("McpToolCount", server.KnownTools.Count)
            : _loc["McpToolsNotLoaded"];
        string detail = $"{transport} · {tools}";
        if (!server.Enabled)
        {
            detail += $" · {_loc["McpDisabledMark"]}";
        }
        return new McpListItem(string.IsNullOrWhiteSpace(server.Name) ? "(unnamed)" : server.Name, detail);
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
            // 没选中就整张卡片收起来,不摆一堆灰着的空框
            McpForm.IsVisible = server is not null;
            McpNoSelectionText.IsVisible = server is null;
            if (server is not null)
            {
                McpTitleText.Text = string.IsNullOrWhiteSpace(server.Name) ? "(unnamed)" : server.Name;
                McpToolsText.Text = ToolLibraryLine(server);
                McpStatusText.Text = "";
            }
            UpdateTransportPanels();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>卡片头那行:工具库拉过没有、几个、什么时候拉的。</summary>
    private string ToolLibraryLine(McpServerConfig server)
        => server.KnownTools.Count == 0
            ? _loc["McpToolsUnknown"]
            : _loc.F("McpToolsKnown", server.KnownTools.Count,
                server.ToolsRefreshedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—");

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
                // 只刷卡片上那一行,不整表重载:重载会走 SelectionChanged 把表单按已存的值重读一遍,
                // 而「测试」本来就允许拿没保存的表单值去连 —— 那样会把用户刚敲的东西冲掉。
                // 左边列表里的工具数下次保存/增删时自然跟上。
                McpToolsText.Text = ToolLibraryLine(server);
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

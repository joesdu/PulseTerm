using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 设置页:管理模型接入(名称/协议/基地址/模型/最大输出/API Key)与全局系统提示词。
/// 直接编辑面板共享的 <see cref="AiSettings" /> 实例,保存后经回调通知聊天面板刷新。
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly string[] ProtocolLabels =
        ["OpenAI Chat Completions", "OpenAI Responses", "Anthropic Messages"];

    private static readonly string[] McpTransportLabels =
        ["Stdio (local process)", "HTTP (remote)"];

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Action _onProvidersChanged;
    private bool _loadingEditor;
    private bool _loadingMcpEditor;

    /// <summary>由聊天面板构造(UI 线程)。</summary>
    public SettingsView(IPluginContext context, AiSettingsStore store, AiSettings settings, Loc loc, Action onProvidersChanged)
    {
        _context = context;
        _store = store;
        _settings = settings;
        _loc = loc;
        _onProvidersChanged = onProvidersChanged;
        InitializeComponent();
        ApplyLoc();

        PresetCombo.ItemsSource = ProviderPresets.All.Select(p => p.Label).ToList();
        PresetCombo.SelectedIndex = 0;
        ProtocolCombo.ItemsSource = ProtocolLabels;

        ProvidersList.SelectionChanged += (_, _) => _ = LoadEditorAsync();
        AddButton.Click += OnAddClick;
        SaveButton.Click += OnSaveClick;
        DeleteButton.Click += OnDeleteClick;
        TestButton.Click += OnTestClick;
        RevealKeyToggle.IsCheckedChanged += (_, _) =>
            ApiKeyBox.PasswordChar = RevealKeyToggle.IsChecked == true ? '\0' : '●';

        McpTransportCombo.ItemsSource = McpTransportLabels;
        McpList.SelectionChanged += (_, _) => LoadMcpEditor();
        McpTransportCombo.SelectionChanged += (_, _) => UpdateMcpTransportPanels();
        McpAddButton.Click += OnMcpAddClick;
        McpSaveButton.Click += OnMcpSaveClick;
        McpDeleteButton.Click += OnMcpDeleteClick;
        McpTestButton.Click += OnMcpTestClick;

        SystemPromptBox.Text = _settings.SystemPrompt ?? "";
        ReloadList(selectIndex: _settings.Providers.Count > 0 ? 0 : -1);
        ReloadMcpList(selectIndex: _settings.McpServers.Count > 0 ? 0 : -1);
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        ProvidersHeader.Text = _loc["Providers"];
        AddText.Text = _loc["Add"];
        NameLabel.Text = _loc["Name"];
        ProtocolLabel.Text = _loc["Protocol"];
        BaseUrlLabel.Text = _loc["BaseUrl"];
        ModelLabel.Text = _loc["Model"];
        MaxTokensLabel.Text = _loc["MaxTokens"];
        ApiKeyLabel.Text = _loc["ApiKey"];
        ApiKeyHintText.Text = _loc["ApiKeyHint"];
        SaveText.Text = _loc["Save"];
        TestText.Text = _loc["Test"];
        DeleteText.Text = _loc["Delete"];
        SystemPromptLabel.Text = _loc["SystemPrompt"];
        McpHeader.Text = _loc["McpServers"];
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

    private AiProviderConfig? SelectedProvider
        => ProvidersList.SelectedIndex >= 0 && ProvidersList.SelectedIndex < _settings.Providers.Count
            ? _settings.Providers[ProvidersList.SelectedIndex]
            : null;

    private void ReloadList(int selectIndex)
    {
        ProvidersList.ItemsSource = _settings.Providers
            .Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name)
            .ToList();
        ProvidersList.SelectedIndex = Math.Min(selectIndex, _settings.Providers.Count - 1);
        bool hasSelection = ProvidersList.SelectedIndex >= 0;
        SaveButton.IsEnabled = hasSelection;
        TestButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        if (!hasSelection)
        {
            _ = LoadEditorAsync();
        }
    }

    private async Task LoadEditorAsync()
    {
        _loadingEditor = true;
        try
        {
            AiProviderConfig? provider = SelectedProvider;
            NameBox.Text = provider?.Name ?? "";
            ProtocolCombo.SelectedIndex = provider is null ? -1 : (int)provider.Protocol;
            BaseUrlBox.Text = provider?.BaseUrl ?? "";
            ModelBox.Text = provider?.Model ?? "";
            MaxTokensBox.Text = provider?.MaxTokens.ToString() ?? "";
            ApiKeyBox.Text = "";
            bool hasSelection = provider is not null;
            SaveButton.IsEnabled = hasSelection;
            TestButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            if (provider is not null)
            {
                string? key = await _store.GetApiKeyAsync(provider.Id);
                // 加载期间用户可能已切换选择
                if (SelectedProvider?.Id == provider.Id)
                {
                    ApiKeyBox.Text = key ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log.Error("Load provider editor failed.", ex);
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        int presetIndex = Math.Max(0, PresetCombo.SelectedIndex);
        AiProviderConfig provider = ProviderPresets.All[presetIndex].Create();
        _settings.Providers.Add(provider);
        _settings.ActiveProviderId ??= provider.Id;
        ReloadList(_settings.Providers.Count - 1);
        _ = PersistAsync(notify: true);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (_loadingEditor || SelectedProvider is not { } provider)
        {
            return;
        }
        provider.Name = NameBox.Text?.Trim() ?? "";
        provider.Protocol = ProtocolCombo.SelectedIndex >= 0 ? (ChatProtocol)ProtocolCombo.SelectedIndex : provider.Protocol;
        provider.BaseUrl = BaseUrlBox.Text?.Trim() ?? "";
        provider.Model = ModelBox.Text?.Trim() ?? "";
        if (int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0)
        {
            provider.MaxTokens = maxTokens;
        }
        _settings.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        try
        {
            await _store.SetApiKeyAsync(provider.Id, ApiKeyBox.Text);
            await PersistAsync(notify: true);
            int index = ProvidersList.SelectedIndex;
            ReloadList(index);
            StatusText.Text = _loc["Saved"];
        }
        catch (Exception ex)
        {
            _context.Log.Error("Save AI settings failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task DeleteAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }
        int index = ProvidersList.SelectedIndex;
        _settings.Providers.Remove(provider);
        if (_settings.ActiveProviderId == provider.Id)
        {
            _settings.ActiveProviderId = _settings.Providers.FirstOrDefault()?.Id;
        }
        try
        {
            await _store.DeleteApiKeyAsync(provider.Id);
            await PersistAsync(notify: true);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Delete provider failed.", ex);
        }
        ReloadList(Math.Max(0, index - 1));
    }

    private void OnTestClick(object? sender, RoutedEventArgs e) => _ = TestAsync();

    private async Task TestAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }
        // 用表单当前值(可能未保存)测试
        var candidate = new AiProviderConfig
        {
            Id = provider.Id,
            Name = NameBox.Text ?? "",
            Protocol = ProtocolCombo.SelectedIndex >= 0 ? (ChatProtocol)ProtocolCombo.SelectedIndex : provider.Protocol,
            BaseUrl = BaseUrlBox.Text?.Trim() ?? "",
            Model = ModelBox.Text?.Trim() ?? "",
            MaxTokens = int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0 ? maxTokens : provider.MaxTokens
        };
        TestButton.IsEnabled = false;
        StatusText.Text = _loc["Testing"];
        try
        {
            IChatClient client = await _store.CreateClientAsync(candidate, ApiKeyBox.Text);
            ChatResponse response = await Task.Run(() => client.GetResponseAsync(
                "Reply with exactly: OK", new ChatOptions { MaxOutputTokens = 64 }));
            string text = response.Text.Trim();
            StatusText.Text = _loc.F("TestOk", text.Length > 80 ? text[..80] + "…" : text);
        }
        catch (Exception ex)
        {
            StatusText.Text = _loc.F("TestFail", ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = SelectedProvider is not null;
        }
    }

    // ---------- MCP 服务器管理 ----------

    private McpServerConfig? SelectedMcp
        => McpList.SelectedIndex >= 0 && McpList.SelectedIndex < _settings.McpServers.Count
            ? _settings.McpServers[McpList.SelectedIndex]
            : null;

    private void ReloadMcpList(int selectIndex)
    {
        McpList.ItemsSource = _settings.McpServers
            .Select(s => (string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name) + (s.Enabled ? "" : " ⏸"))
            .ToList();
        McpList.SelectedIndex = Math.Min(selectIndex, _settings.McpServers.Count - 1);
        if (McpList.SelectedIndex < 0)
        {
            LoadMcpEditor();
        }
    }

    private void LoadMcpEditor()
    {
        _loadingMcpEditor = true;
        try
        {
            McpServerConfig? server = SelectedMcp;
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
            UpdateMcpTransportPanels();
        }
        finally
        {
            _loadingMcpEditor = false;
        }
    }

    private void UpdateMcpTransportPanels()
    {
        bool http = McpTransportCombo.SelectedIndex == (int)McpTransportType.Http;
        McpStdioPanel.IsVisible = !http;
        McpHttpPanel.IsVisible = http;
    }

    private void OnMcpAddClick(object? sender, RoutedEventArgs e)
    {
        _settings.McpServers.Add(new McpServerConfig
        {
            Name = $"server-{_settings.McpServers.Count + 1}",
            Command = "npx"
        });
        ReloadMcpList(_settings.McpServers.Count - 1);
        _ = PersistAsync(notify: false);
    }

    private void OnMcpSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_loadingMcpEditor || SelectedMcp is not { } server)
        {
            return;
        }
        ApplyMcpForm(server);
        int index = McpList.SelectedIndex;
        ReloadMcpList(index);
        McpStatusText.Text = _loc["Saved"];
        _ = PersistAsync(notify: false);
    }

    private void OnMcpDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedMcp is not { } server)
        {
            return;
        }
        int index = McpList.SelectedIndex;
        _settings.McpServers.Remove(server);
        ReloadMcpList(Math.Max(0, index - 1));
        McpStatusText.Text = "";
        _ = PersistAsync(notify: false);
    }

    private void OnMcpTestClick(object? sender, RoutedEventArgs e) => _ = TestMcpAsync();

    private async Task TestMcpAsync()
    {
        if (SelectedMcp is not { } server)
        {
            return;
        }
        // 用表单当前值(可能未保存)测试
        var candidate = new McpServerConfig { Id = server.Id };
        ApplyMcpForm(candidate);
        McpTestButton.IsEnabled = false;
        McpStatusText.Text = _loc["Testing"];
        try
        {
            IReadOnlyList<string> tools = await McpManager.ProbeAsync(candidate, CancellationToken.None);
            string names = string.Join(", ", tools);
            if (names.Length > 200)
            {
                names = names[..200] + "…";
            }
            McpStatusText.Text = _loc.F("McpTestOk", tools.Count, names);
        }
        catch (Exception ex)
        {
            McpStatusText.Text = _loc.F("TestFail", ex.Message);
        }
        finally
        {
            McpTestButton.IsEnabled = SelectedMcp is not null;
        }
    }

    /// <summary>把表单当前值写入目标配置(保存与测试共用)。</summary>
    private void ApplyMcpForm(McpServerConfig target)
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
    }

    private async Task PersistAsync(bool notify)
    {
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Persist AI settings failed.", ex);
        }
        if (notify)
        {
            _onProvidersChanged();
        }
    }
}

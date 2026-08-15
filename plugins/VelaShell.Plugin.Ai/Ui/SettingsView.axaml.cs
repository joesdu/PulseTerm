using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.AI;
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

    /// <summary>思考档位下拉的文案键,顺序与 <see cref="ReasoningLevel" /> 一一对应。</summary>
    private static readonly string[] ReasoningKeys =
        ["ReasoningDefault", "ReasoningOff", "ReasoningLow", "ReasoningMedium", "ReasoningHigh"];

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Action _onProvidersChanged;
    private bool _loadingEditor;

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
        ProtocolCombo.SelectionChanged += (_, _) => UpdateProtocolOnlyFields();
        AddButton.Click += OnAddClick;
        SaveButton.Click += OnSaveClick;
        DeleteButton.Click += OnDeleteClick;
        TestButton.Click += OnTestClick;
        RevealKeyToggle.IsCheckedChanged += (_, _) =>
            ApiKeyBox.PasswordChar = RevealKeyToggle.IsChecked == true ? '\0' : '●';

        SystemPromptBox.Text = _settings.SystemPrompt ?? "";
        CompactContextCheck.IsChecked = _settings.CompactContext;
        SuggestFollowUpsCheck.IsChecked = _settings.SuggestFollowUps;
        ReloadList(selectIndex: _settings.Providers.Count > 0 ? 0 : -1);
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        ProvidersHeader.Text = _loc["Providers"];
        SectionEndpointTitle.Text = _loc["SecEndpoint"];
        SectionCapacityTitle.Text = _loc["SecCapacity"];
        SectionSamplingTitle.Text = _loc["SecSampling"];
        SectionGlobalTitle.Text = _loc["SecGlobal"];
        AddText.Text = _loc["Add"];
        NameLabel.Text = _loc["Name"];
        ProtocolLabel.Text = _loc["Protocol"];
        BaseUrlLabel.Text = _loc["BaseUrl"];
        ModelLabel.Text = _loc["Model"];
        MaxTokensLabel.Text = _loc["MaxTokens"];
        MaxInputTokensLabel.Text = _loc["MaxInputTokens"];
        MaxInputTokensHintText.Text = _loc["MaxInputTokensHint"];
        ReasoningLabel.Text = _loc["Reasoning"];
        ReasoningHintText.Text = _loc["ReasoningHint"];
        PromptCacheCheck.Content = _loc["PromptCache"];
        PromptCacheHintText.Text = _loc["PromptCacheHint"];
        TemperatureLabel.Text = _loc["Temperature"];
        TopPLabel.Text = _loc["TopP"];
        StopLabel.Text = _loc["StopSequences"];
        SamplingHintText.Text = _loc["SamplingHint"];
        PriceInLabel.Text = _loc["PriceIn"];
        PriceOutLabel.Text = _loc["PriceOut"];
        PriceCachedLabel.Text = _loc["PriceCached"];
        PriceHintText.Text = _loc["PriceHint"];
        ProviderPromptLabel.Text = _loc["ProviderPrompt"];
        // 语言切换时下拉项也要跟着换,选中项按索引留住
        int reasoning = ReasoningCombo.SelectedIndex;
        ReasoningCombo.ItemsSource = ReasoningKeys.Select(key => _loc[key]).ToList();
        ReasoningCombo.SelectedIndex = reasoning;
        ApiKeyLabel.Text = _loc["ApiKey"];
        ApiKeyHintText.Text = _loc["ApiKeyHint"];
        SaveText.Text = _loc["Save"];
        TestText.Text = _loc["Test"];
        DeleteText.Text = _loc["Delete"];
        SystemPromptLabel.Text = _loc["SystemPrompt"];
        CompactContextCheck.Content = _loc["CompactContext"];
        CompactContextHintText.Text = _loc["CompactContextHint"];
        SuggestFollowUpsCheck.Content = _loc["SuggestFollowUps"];
        SuggestFollowUpsHintText.Text = _loc["SuggestFollowUpsHint"];
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
            MaxInputTokensBox.Text = provider?.MaxInputTokens.ToString() ?? "";
            ReasoningCombo.SelectedIndex = provider is null ? -1 : (int)provider.Reasoning;
            PromptCacheCheck.IsChecked = provider?.PromptCaching ?? true;
            // 留空 = 不发这个参数,所以 null 就显示空串而不是 0
            TemperatureBox.Text = provider?.Temperature?.ToString() ?? "";
            TopPBox.Text = provider?.TopP?.ToString() ?? "";
            StopBox.Text = provider?.StopSequences ?? "";
            PriceInBox.Text = Money(provider?.InputPricePerMillion);
            PriceOutBox.Text = Money(provider?.OutputPricePerMillion);
            PriceCachedBox.Text = Money(provider?.CachedInputPricePerMillion);
            ProviderPromptBox.Text = provider?.SystemPrompt ?? "";
            UpdateProtocolOnlyFields();
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

    /// <summary>空串表示"不发这个参数",所以解析失败与留空都返回 null。</summary>
    private static float? ParseOptional(string? text)
        => float.TryParse(text?.Trim(), out float value) ? value : null;

    /// <summary>单价:解析不出来就当没填(0 = 不估算成本)。</summary>
    private static double ParsePrice(string? text)
        => double.TryParse(text?.Trim(), out double value) && value > 0 ? value : 0;

    /// <summary>0 显示成空串 —— 免得每个新接入的三个单价框里都摆着个 0 招人误会。</summary>
    private static string Money(double? value) => value is > 0 ? value.Value.ToString("0.####") : "";

    /// <summary>只对某一种协议成立的选项跟着协议下拉显隐(现在只有 Anthropic 的提示词缓存)。</summary>
    private void UpdateProtocolOnlyFields()
        => PromptCachePanel.IsVisible = ProtocolCombo.SelectedIndex == (int)ChatProtocol.AnthropicMessages;

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
        // 输入上限允许填 0 —— 那表示"窗口未知",用量只显示累计不显示占比
        if (int.TryParse(MaxInputTokensBox.Text?.Trim(), out int maxInputTokens) && maxInputTokens >= 0)
        {
            provider.MaxInputTokens = maxInputTokens;
        }
        provider.Reasoning = ReasoningCombo.SelectedIndex >= 0
            ? (ReasoningLevel)ReasoningCombo.SelectedIndex
            : provider.Reasoning;
        provider.PromptCaching = PromptCacheCheck.IsChecked == true;
        provider.Temperature = ParseOptional(TemperatureBox.Text);
        provider.TopP = ParseOptional(TopPBox.Text);
        provider.StopSequences = StopBox.Text ?? "";
        provider.InputPricePerMillion = ParsePrice(PriceInBox.Text);
        provider.OutputPricePerMillion = ParsePrice(PriceOutBox.Text);
        provider.CachedInputPricePerMillion = ParsePrice(PriceCachedBox.Text);
        provider.SystemPrompt = string.IsNullOrWhiteSpace(ProviderPromptBox.Text) ? null : ProviderPromptBox.Text;
        _settings.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        _settings.CompactContext = CompactContextCheck.IsChecked == true;
        _settings.SuggestFollowUps = SuggestFollowUpsCheck.IsChecked == true;
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

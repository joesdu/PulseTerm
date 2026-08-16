using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>左栏一行:供应商行(<see cref="Model" /> 为 null)或其下的模型行。</summary>
public sealed record ProviderNavItem(AiProvider Provider, AiModelConfig? Model, string Text, Thickness Indent, FontWeight Weight)
{
    /// <summary>是不是供应商行。</summary>
    public bool IsProvider => Model is null;
}

/// <summary>
/// 设置页:左栏是"供应商 › 模型"两层树,右侧表单随选中的层切换 ——
/// 供应商管地址 / 默认协议 / 共用 API Key,模型管模型 id 与其余一切(可覆盖协议、Key、地址)。
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
    private List<ProviderNavItem> _nav = [];
    private bool _loadingEditor;

    /// <summary>两击确认删供应商:记着第一击是冲着谁的,换了选择就作废。</summary>
    private string? _pendingDeleteProviderId;

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
        ProviderProtocolCombo.ItemsSource = ProtocolLabels;

        ProvidersList.SelectionChanged += (_, _) =>
        {
            _pendingDeleteProviderId = null;
            _ = LoadEditorAsync();
        };
        ProviderProtocolCombo.SelectionChanged += (_, _) => UpdateProtocolOnlyFields();
        ProtocolCombo.SelectionChanged += (_, _) => UpdateProtocolOnlyFields();
        OwnKeyCheck.IsCheckedChanged += (_, _) => OwnKeyPanel.IsVisible = OwnKeyCheck.IsChecked == true;
        AddButton.Click += OnAddProviderClick;
        AddModelButton.Click += OnAddModelClick;
        SaveButton.Click += OnSaveClick;
        DeleteButton.Click += OnDeleteClick;
        TestButton.Click += OnTestClick;
        RevealKeyToggle.IsCheckedChanged += (_, _) =>
            ApiKeyBox.PasswordChar = RevealKeyToggle.IsChecked == true ? '\0' : '●';
        ProviderRevealKeyToggle.IsCheckedChanged += (_, _) =>
            ProviderApiKeyBox.PasswordChar = ProviderRevealKeyToggle.IsChecked == true ? '\0' : '●';

        // 起手选中当前活跃的模型;没有就选第一行
        ReloadList(selectId: _settings.ActiveModelId ?? _settings.Providers.FirstOrDefault()?.Id);
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        ProvidersHeader.Text = _loc["Providers"];
        SectionProviderTitle.Text = _loc["SecProvider"];
        SectionEndpointTitle.Text = _loc["SecEndpoint"];
        SectionCapacityTitle.Text = _loc["SecCapacity"];
        SectionSamplingTitle.Text = _loc["SecSampling"];
        AddText.Text = _loc["AddProvider"];
        AddModelText.Text = _loc["AddModel"];
        ProviderNameLabel.Text = _loc["Name"];
        ProviderBaseUrlLabel.Text = _loc["BaseUrl"];
        ProviderProtocolLabel.Text = _loc["DefaultProtocol"];
        ProviderProtocolHintText.Text = _loc["DefaultProtocolHint"];
        ProviderApiKeyLabel.Text = _loc["ApiKey"];
        ProviderApiKeyHintText.Text = _loc["ProviderKeyHint"];
        NameLabel.Text = _loc["Name"];
        ProtocolLabel.Text = _loc["Protocol"];
        BaseUrlLabel.Text = _loc["BaseUrlOverride"];
        BaseUrlHintText.Text = _loc["BaseUrlOverrideHint"];
        ModelLabel.Text = _loc["Model"];
        OwnKeyCheck.Content = _loc["OwnApiKey"];
        OwnKeyHintText.Text = _loc["OwnApiKeyHint"];
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
        int protocol = ProtocolCombo.SelectedIndex;
        ProtocolCombo.ItemsSource = ProtocolChoices(SelectedProvider);
        ProtocolCombo.SelectedIndex = protocol;
        ApiKeyLabel.Text = _loc["ApiKey"];
        ApiKeyHintText.Text = _loc["ApiKeyHint"];
        SaveText.Text = _loc["Save"];
        TestText.Text = _loc["Test"];
        DeleteText.Text = _loc["Delete"];
    }

    // ---- 选中项 ----

    private ProviderNavItem? SelectedItem
        => ProvidersList.SelectedIndex >= 0 && ProvidersList.SelectedIndex < _nav.Count
            ? _nav[ProvidersList.SelectedIndex]
            : null;

    /// <summary>选中行所属的供应商(选中的是供应商行就是它自己)。</summary>
    private AiProvider? SelectedProvider => SelectedItem?.Provider;

    /// <summary>选中的模型行;选中的是供应商行则为 null。</summary>
    private AiModelConfig? SelectedModel => SelectedItem?.Model;

    /// <summary>模型协议下拉:第 0 项"继承供应商(xxx)",其后按 <see cref="ChatProtocol" /> 枚举顺序。</summary>
    private List<string> ProtocolChoices(AiProvider? provider)
    {
        string inherited = provider is null ? "" : ProtocolLabels[(int)provider.DefaultProtocol];
        return [_loc.F("InheritProtocol", inherited), .. ProtocolLabels];
    }

    private void ReloadList(string? selectId)
    {
        _nav = [];
        int selectIndex = -1;
        foreach (AiProvider provider in _settings.Providers)
        {
            if (provider.Id == selectId)
            {
                selectIndex = _nav.Count;
            }
            _nav.Add(new ProviderNavItem(provider, null,
                string.IsNullOrWhiteSpace(provider.Name) ? _loc["Unnamed"] : provider.Name,
                new Thickness(0), FontWeight.Medium));
            foreach (AiModelConfig model in provider.Models)
            {
                if (model.Id == selectId)
                {
                    selectIndex = _nav.Count;
                }
                _nav.Add(new ProviderNavItem(provider, model,
                    string.IsNullOrWhiteSpace(model.DisplayName) ? _loc["Unnamed"] : model.DisplayName,
                    new Thickness(14, 0, 0, 0), FontWeight.Normal));
            }
        }
        ProvidersList.ItemsSource = _nav;
        ProvidersList.SelectedIndex = selectIndex >= 0 ? selectIndex : Math.Min(0, _nav.Count - 1);
        if (ProvidersList.SelectedIndex < 0)
        {
            _ = LoadEditorAsync();
        }
    }

    private async Task LoadEditorAsync()
    {
        _loadingEditor = true;
        try
        {
            AiProvider? provider = SelectedProvider;
            AiModelConfig? model = SelectedModel;
            bool hasSelection = provider is not null;
            ProviderEditor.IsVisible = hasSelection && model is null;
            ModelEditor.IsVisible = model is not null;
            SaveButton.IsEnabled = hasSelection;
            TestButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            AddModelButton.IsEnabled = hasSelection;
            StatusText.Text = "";

            // 供应商表单
            ProviderNameBox.Text = provider?.Name ?? "";
            ProviderBaseUrlBox.Text = provider?.BaseUrl ?? "";
            ProviderProtocolCombo.SelectedIndex = provider is null ? -1 : (int)provider.DefaultProtocol;
            ProviderApiKeyBox.Text = "";

            // 模型表单
            NameBox.Text = model?.Name ?? "";
            ModelBox.Text = model?.Model ?? "";
            ProtocolCombo.ItemsSource = ProtocolChoices(provider);
            ProtocolCombo.SelectedIndex = model is null ? -1 : model.Protocol is { } p ? (int)p + 1 : 0;
            OwnKeyCheck.IsChecked = model?.HasOwnApiKey ?? false;
            OwnKeyPanel.IsVisible = model?.HasOwnApiKey ?? false;
            BaseUrlBox.Text = model?.BaseUrlOverride ?? "";
            MaxTokensBox.Text = model?.MaxTokens.ToString() ?? "";
            MaxInputTokensBox.Text = model?.MaxInputTokens.ToString() ?? "";
            ReasoningCombo.SelectedIndex = model is null ? -1 : (int)model.Reasoning;
            PromptCacheCheck.IsChecked = model?.PromptCaching ?? true;
            // 留空 = 不发这个参数,所以 null 就显示空串而不是 0
            TemperatureBox.Text = model?.Temperature?.ToString() ?? "";
            TopPBox.Text = model?.TopP?.ToString() ?? "";
            StopBox.Text = model?.StopSequences ?? "";
            PriceInBox.Text = Money(model?.InputPricePerMillion);
            PriceOutBox.Text = Money(model?.OutputPricePerMillion);
            PriceCachedBox.Text = Money(model?.CachedInputPricePerMillion);
            ProviderPromptBox.Text = model?.SystemPrompt ?? "";
            UpdateProtocolOnlyFields();
            ApiKeyBox.Text = "";

            if (provider is not null && model is null)
            {
                string? key = await _store.GetApiKeyAsync(provider.Id);
                // 加载期间用户可能已切换选择
                if (SelectedProvider?.Id == provider.Id && SelectedModel is null)
                {
                    ProviderApiKeyBox.Text = key ?? "";
                }
                if (provider.Models.Count == 0)
                {
                    StatusText.Text = _loc["NoModels"];
                }
            }
            else if (model is { HasOwnApiKey: true })
            {
                string? key = await _store.GetApiKeyAsync(model.Id);
                if (SelectedModel?.Id == model.Id)
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

    /// <summary>0 显示成空串 —— 免得每个新模型的三个单价框里都摆着个 0 招人误会。</summary>
    private static string Money(double? value) => value is > 0 ? value.Value.ToString("0.####") : "";

    /// <summary>表单里此刻解出的协议(模型覆盖 → 供应商默认)。</summary>
    private ChatProtocol? FormProtocol()
    {
        if (ProtocolCombo.SelectedIndex > 0)
        {
            return (ChatProtocol)(ProtocolCombo.SelectedIndex - 1);
        }
        return SelectedProvider is { } provider
            ? ProviderProtocolCombo.SelectedIndex >= 0 && SelectedModel is null
                ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex
                : provider.DefaultProtocol
            : null;
    }

    /// <summary>只对某一种协议成立的选项跟着解出的协议显隐(现在只有 Anthropic 的提示词缓存)。</summary>
    private void UpdateProtocolOnlyFields()
        => PromptCachePanel.IsVisible = FormProtocol() == ChatProtocol.AnthropicMessages;

    // ---- 新增 ----

    private void OnAddProviderClick(object? sender, RoutedEventArgs e)
    {
        int presetIndex = Math.Max(0, PresetCombo.SelectedIndex);
        AiProvider provider = ProviderPresets.All[presetIndex].Create();
        _settings.Providers.Add(provider);
        _settings.ActiveModelId ??= provider.Models.FirstOrDefault()?.Id;
        // 先选供应商行:地址和 Key 得先填,模型 id 预设已经带了
        ReloadList(provider.Id);
        _ = PersistAsync(notify: true);
    }

    private void OnAddModelClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }
        var model = new AiModelConfig();
        provider.Models.Add(model);
        _settings.ActiveModelId ??= model.Id;
        ReloadList(model.Id);
        _ = PersistAsync(notify: true);
    }

    // ---- 保存 ----

    private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (_loadingEditor || SelectedItem is not { } item)
        {
            return;
        }
        string? keyOwnerId = null;
        string? keyText = null;
        if (item.Model is { } model)
        {
            model.Name = NameBox.Text?.Trim() ?? "";
            model.Model = ModelBox.Text?.Trim() ?? "";
            model.Protocol = ProtocolCombo.SelectedIndex > 0 ? (ChatProtocol)(ProtocolCombo.SelectedIndex - 1) : null;
            model.HasOwnApiKey = OwnKeyCheck.IsChecked == true;
            model.BaseUrlOverride = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim();
            if (int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0)
            {
                model.MaxTokens = maxTokens;
            }
            // 输入上限允许填 0 —— 那表示"窗口未知",用量只显示累计不显示占比
            if (int.TryParse(MaxInputTokensBox.Text?.Trim(), out int maxInputTokens) && maxInputTokens >= 0)
            {
                model.MaxInputTokens = maxInputTokens;
            }
            model.Reasoning = ReasoningCombo.SelectedIndex >= 0
                ? (ReasoningLevel)ReasoningCombo.SelectedIndex
                : model.Reasoning;
            model.PromptCaching = PromptCacheCheck.IsChecked == true;
            model.Temperature = ParseOptional(TemperatureBox.Text);
            model.TopP = ParseOptional(TopPBox.Text);
            model.StopSequences = StopBox.Text ?? "";
            model.InputPricePerMillion = ParsePrice(PriceInBox.Text);
            model.OutputPricePerMillion = ParsePrice(PriceOutBox.Text);
            model.CachedInputPricePerMillion = ParsePrice(PriceCachedBox.Text);
            model.SystemPrompt = string.IsNullOrWhiteSpace(ProviderPromptBox.Text) ? null : ProviderPromptBox.Text;
            // 关掉独立 Key 就顺手把它那份机密清掉,免得留个孤儿
            keyOwnerId = model.Id;
            keyText = model.HasOwnApiKey ? ApiKeyBox.Text : "";
        }
        else
        {
            AiProvider provider = item.Provider;
            provider.Name = ProviderNameBox.Text?.Trim() ?? "";
            provider.BaseUrl = ProviderBaseUrlBox.Text?.Trim() ?? "";
            provider.DefaultProtocol = ProviderProtocolCombo.SelectedIndex >= 0
                ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex
                : provider.DefaultProtocol;
            keyOwnerId = provider.Id;
            keyText = ProviderApiKeyBox.Text;
        }
        try
        {
            await _store.SetApiKeyAsync(keyOwnerId, keyText);
            await PersistAsync(notify: true);
            ReloadList(item.Model?.Id ?? item.Provider.Id);
            StatusText.Text = _loc["Saved"];
        }
        catch (Exception ex)
        {
            _context.Log.Error("Save AI settings failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    // ---- 删除 ----

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        AiProvider provider = item.Provider;
        int index = ProvidersList.SelectedIndex;
        try
        {
            if (item.Model is { } model)
            {
                provider.Models.Remove(model);
                await _store.DeleteApiKeyAsync(model.Id);
            }
            else
            {
                // 删供应商连带删它下面所有模型 —— 两击确认,第一击只是提示
                if (_pendingDeleteProviderId != provider.Id)
                {
                    _pendingDeleteProviderId = provider.Id;
                    StatusText.Text = _loc.F("DeleteProviderConfirm", provider.Models.Count);
                    return;
                }
                _pendingDeleteProviderId = null;
                _settings.Providers.Remove(provider);
                foreach (AiModelConfig orphan in provider.Models)
                {
                    await _store.DeleteApiKeyAsync(orphan.Id);
                }
                await _store.DeleteApiKeyAsync(provider.Id);
            }
            if (_settings.FindModel(_settings.ActiveModelId) is null)
            {
                _settings.ActiveModelId = _settings.ResolveModels().FirstOrDefault()?.Id;
            }
            await PersistAsync(notify: true);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Delete provider failed.", ex);
        }
        // 删完落在上一行(模型删了落回供应商;供应商删了落到前一个供应商末尾)
        string? next = index > 0 && index - 1 < _nav.Count ? (_nav[index - 1].Model?.Id ?? _nav[index - 1].Provider.Id) : null;
        ReloadList(next);
    }

    // ---- 测试 ----

    private void OnTestClick(object? sender, RoutedEventArgs e) => _ = TestAsync();

    /// <summary>
    /// 用表单当前值(可能未保存)测试。选中模型就测它;选中供应商就拿它下面第一个模型探活 ——
    /// 供应商本身没法单独"连一下",总得有个模型 id 才发得出请求。
    /// </summary>
    private async Task TestAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        AiProvider provider = item.Provider;
        ResolvedModel candidate;
        string? apiKeyOverride;
        if (item.Model is { } model)
        {
            var draft = new AiModelConfig
            {
                Id = model.Id,
                Name = NameBox.Text ?? "",
                Model = ModelBox.Text?.Trim() ?? "",
                Protocol = ProtocolCombo.SelectedIndex > 0 ? (ChatProtocol)(ProtocolCombo.SelectedIndex - 1) : null,
                HasOwnApiKey = OwnKeyCheck.IsChecked == true,
                BaseUrlOverride = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim(),
                MaxTokens = int.TryParse(MaxTokensBox.Text?.Trim(), out int maxTokens) && maxTokens > 0 ? maxTokens : model.MaxTokens
            };
            candidate = new ResolvedModel(provider, draft);
            // 独立 Key 用表单里的;继承的走供应商已保存的那把
            apiKeyOverride = draft.HasOwnApiKey ? ApiKeyBox.Text : null;
        }
        else
        {
            if (provider.Models.Count == 0)
            {
                StatusText.Text = _loc["NoModels"];
                return;
            }
            var draft = new AiProvider
            {
                Id = provider.Id,
                Name = ProviderNameBox.Text ?? "",
                BaseUrl = ProviderBaseUrlBox.Text?.Trim() ?? "",
                DefaultProtocol = ProviderProtocolCombo.SelectedIndex >= 0
                    ? (ChatProtocol)ProviderProtocolCombo.SelectedIndex
                    : provider.DefaultProtocol
            };
            AiModelConfig first = provider.Models[0];
            candidate = new ResolvedModel(draft, first);
            apiKeyOverride = first.HasOwnApiKey ? null : ProviderApiKeyBox.Text;
        }
        TestButton.IsEnabled = false;
        StatusText.Text = _loc["Testing"];
        try
        {
            IChatClient client = await _store.CreateClientAsync(candidate, apiKeyOverride);
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
            TestButton.IsEnabled = SelectedItem is not null;
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>左栏一行:供应商行(<see cref="Model" /> 为 null)或其下的模型行。</summary>
/// <remarks>
/// 是<b>类</b>不是 record:行尾那颗连通性状态点要在"测试"跑完的那一刻就地变色。
/// record 是不可变的,只能靠重建整个列表来刷新 —— 而重建会重置选中项、
/// 进而触发 <c>LoadEditorAsync</c> 把用户表单里还没保存的改动冲掉。
/// </remarks>
public sealed class ProviderNavItem(
    AiProvider provider, AiModelConfig? model, string text, Thickness indent, FontWeight weight)
    : System.ComponentModel.INotifyPropertyChanged
{
    private Geometry? _icon;
    private IBrush? _dot;
    private IBrush? _tint;
    private string _dotTip = "";

    /// <summary>这一行所属的供应商(模型行也指向它的父供应商)。</summary>
    public AiProvider Provider { get; } = provider;

    /// <summary>模型行指向的模型;供应商行为 null。</summary>
    public AiModelConfig? Model { get; } = model;

    /// <summary>行上显示的名字。</summary>
    public string Text { get; } = text;

    /// <summary>左缩进:模型行比供应商行进一档。</summary>
    public Thickness Indent { get; } = indent;

    /// <summary>字重:供应商行加粗。</summary>
    public FontWeight Weight { get; } = weight;

    /// <summary>
    /// 层级图标:供应商 = 云,模型 = 方块。几何在 <c>RefreshNavVisuals</c> 里解析 ——
    /// 视图挂进可视树之后还要再解析一次,所以跟 <see cref="Dot" /> 一样走通知。
    /// </summary>
    public Geometry? Icon
    {
        get => _icon;
        set => Set(ref _icon, value, nameof(Icon));
    }

    /// <summary>
    /// 图标描边色:比同一行的文字再暗一档,选中时和文字一起转强调色。
    /// 文字的三档在样式里(<c>DialogStyles.axaml</c> 的 nav 选择器),图标这一档只能逐项算,
    /// 所以跟 <see cref="Dot" /> 一样走通知 —— 换选中项时就地改色,不重建列表。
    /// </summary>
    public IBrush? Tint
    {
        get => _tint;
        set => Set(ref _tint, value, nameof(Tint));
    }

    /// <summary>是不是供应商行。</summary>
    public bool IsProvider => Model is null;

    /// <summary>状态点的颜色:灰 = 本次窗口内没测过,绿 = 通过,红 = 失败。</summary>
    public IBrush? Dot
    {
        get => _dot;
        set => Set(ref _dot, value, nameof(Dot));
    }

    /// <summary>状态点的悬停说明。</summary>
    public string DotTip
    {
        get => _dotTip;
        set => Set(ref _dotTip, value, nameof(DotTip));
    }

    /// <summary>图标/状态点变化时通知绑定(<see cref="Icon" /> / <see cref="Dot" /> / <see cref="DotTip" /> / <see cref="Tint" />)。</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
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

    /// <summary>
    /// 本次窗口内"测试"跑出来的结果(键 = 模型 id,供应商行用供应商 id)。
    /// <b>不落盘</b>:它说的是"刚才那一次连通",隔天再打开时那句话已经不成立了 ——
    /// 与其显示一个可能过期的绿点,不如老实退回灰点。
    /// </summary>
    private readonly Dictionary<string, (bool Ok, DateTime At)> _testResults = new(StringComparer.Ordinal);

    /// <summary>左栏图标的三档描边色(见 <see cref="ApplyTints" />),装载后解析一次。</summary>
    private IBrush? _providerTint;
    private IBrush? _modelTint;
    private IBrush? _selectedTint;

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
            ApplyTints();
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
        ProviderKeyBadgeText.Text = _loc["KeyEncrypted"];
        ModelKeyBadgeText.Text = _loc["KeyEncrypted"];
        NameLabel.Text = _loc["DisplayName"];
        ProtocolLabel.Text = _loc["Protocol"];
        BaseUrlLabel.Text = _loc["BaseUrlOverride"];
        BaseUrlHintText.Text = _loc["BaseUrlOverrideHint"];
        ModelLabel.Text = _loc["ModelId"];
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

    /// <summary>状态点/徽章的记忆键:模型行按模型 id,供应商行按供应商 id。</summary>
    private static string NavKey(ProviderNavItem item) => item.Model?.Id ?? item.Provider.Id;

    /// <summary>把某一行的状态点刷成它当前该有的颜色(没测过 = 灰)。</summary>
    private void ApplyDot(ProviderNavItem item)
    {
        bool? result = _testResults.TryGetValue(NavKey(item), out (bool Ok, DateTime At) r) ? r.Ok : null;
        (string brushKey, string tipKey) = result switch
        {
            true => ("VelaStatusConnected", "DotPassed"),
            false => ("VelaError", "DotFailed"),
            _ => ("VelaTextMuted", "DotUntested")
        };
        item.Dot = this.TryFindResource(brushKey, ActualThemeVariant, out object? brush) ? brush as IBrush : null;
        item.DotTip = _loc[tipKey];
    }

    /// <summary>
    /// 挂进可视树之后把左栏的图标与状态点重解析一次。
    /// <b>构造期解析不到宿主令牌</b>:那时 <c>TryFindResource</c> 只走得到本控件自己的
    /// <c>Resources</c>,往上没有父级、也够不着 <c>Application.Resources</c>,
    /// 于是 Vela* 全落空 —— 图标描边与状态点都是 null 画刷,整列层级标记和连通性点直接不显示。
    /// </summary>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _providerTint = null;
        _modelTint = null;
        _selectedTint = null;
        RefreshNavVisuals();
        UpdateTestBadge();
    }

    /// <summary>左栏每一行的层级图标、连通性点与图标描边色,一起重算。</summary>
    private void RefreshNavVisuals()
    {
        Geometry? cloud = Token<Geometry>("AiIcon.cloud");
        Geometry? box = Token<Geometry>("AiIcon.box");
        _providerTint ??= Token<IBrush>("VelaTextSecondary");
        _modelTint ??= Token<IBrush>("VelaTextMuted");
        _selectedTint ??= Token<IBrush>("VelaAccent");
        foreach (ProviderNavItem item in _nav)
        {
            item.Icon = item.IsProvider ? cloud : box;
            ApplyDot(item);
        }
        ApplyTints();
    }

    /// <summary>本视图里解析一个宿主令牌(缺席时回落 null,属性保持默认外观)。</summary>
    private T? Token<T>(string key) where T : class
        => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as T : null;

    /// <summary>
    /// 左栏图标的描边色:选中行跟着文字一起转强调色,其余比同一行的文字再暗一档
    /// (供应商行文字 Primary / 图标 Secondary,模型行文字 Secondary / 图标 Muted)。
    /// 图标是这一行的层级标记,压过文字就成了噪点。
    /// </summary>
    private void ApplyTints()
    {
        ProviderNavItem? selected = SelectedItem;
        foreach (ProviderNavItem item in _nav)
        {
            item.Tint = ReferenceEquals(item, selected)
                ? _selectedTint
                : item.IsProvider ? _providerTint : _modelTint;
        }
    }

    /// <summary>表单顶上那枚测试结果徽章。没测过就整枚隐掉,不假装知道。</summary>
    private void UpdateTestBadge()
    {
        if (SelectedItem is not { } item || !_testResults.TryGetValue(NavKey(item), out (bool Ok, DateTime At) r))
        {
            TestBadge.IsVisible = false;
            return;
        }
        TestBadge.IsVisible = true;
        // 带上时刻:"通过"是有保质期的一句话,不写清是几点测的,隔半小时看还以为是刚测过
        TestBadgeText.Text = $"{_loc[r.Ok ? "DotPassed" : "DotFailed"]} · {r.At:HH:mm}";
        IBrush? tone = Token<IBrush>(r.Ok ? "VelaStatusConnected" : "VelaError");
        TestBadgeText.Foreground = tone;
        TestBadgeIcon.Data = Token<Geometry>(r.Ok ? "AiIcon.circle-check" : "AiIcon.circle-x");
        TestBadgeIcon.Stroke = tone;
        // 同色淡底、不描边(设计图 E):描一圈边会让这枚小标签看起来像个可点的按钮
        TestBadge.Background = tone is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, 0.14)
            : null;
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
        // 图标/状态点排在选中项定下来之后:选中那一行的图标要转强调色,得先知道是哪一行。
        // (SelectedIndex 没变时 SelectionChanged 不会再触发,所以这里必须自己补一次。)
        RefreshNavVisuals();
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
            // 面包屑:左栏的模型行只写模型名,滚到表单中段时"这是哪家的"就没了着落
            string providerName = provider is null
                ? ""
                : string.IsNullOrWhiteSpace(provider.Name) ? _loc["Unnamed"] : provider.Name;
            string modelName = model is null
                ? ""
                : string.IsNullOrWhiteSpace(model.DisplayName) ? _loc["Unnamed"] : model.DisplayName;
            BreadcrumbText.Text = model is null ? providerName : $"{providerName}  ›  {modelName}";
            UpdateTestBadge();

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
            _testResults[NavKey(item)] = (true, DateTime.Now);
        }
        catch (Exception ex)
        {
            StatusText.Text = _loc.F("TestFail", ex.Message);
            _testResults[NavKey(item)] = (false, DateTime.Now);
        }
        finally
        {
            // 就地改这一行的点(item 是同一个实例,绑定自己会跟上),不重建列表 ——
            // 重建会连带重置选中项并触发 LoadEditorAsync,把用户还没保存的编辑冲掉。
            ApplyDot(item);
            UpdateTestBadge();
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

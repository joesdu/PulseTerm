using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「连接供应商」:一页列出内置目录,<b>点一下就登进去</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>设计的第一条:能自动的绝不问用户。</b>参数齐全的那些家,点行(或点行尾的「登录」)
/// 立刻开浏览器,授权完成后自动落库、自动建好模型 —— 全程零输入。
/// 只有"程序确实不知道"的东西才会露出输入框,而且<b>只露缺的那几个</b>:
/// 自建服务不知道地址,那就问地址;还没注册 OAuth 应用,那就问客户端 id;
/// 走 API Key 的,那就只问一把 Key。名称、模型 id、协议这些目录里都有,
/// 收进「高级」里,想改的人找得到,不想改的人看不见。
/// </para>
/// <para>
/// <b>登录不落任何明文</b>:PKCE 的 verifier 只活在这一次调用的栈上,换回来的令牌整组
/// 序列化后经宿主机密存储加密落盘(键 <c>oauth:&lt;供应商 id&gt;</c>),界面上从头到尾不显示它。
/// </para>
/// <para>
/// 整页代码里搭、不写 AXAML:行是按目录动态生成的,而且每行展开出来的东西各不相同
/// (缺什么显什么),写成模板反倒要为这套规则再造一层状态。
/// </para>
/// </remarks>
public sealed class ProviderSetupView : UserControl
{
    private const double Gutter = 10;

    /// <summary>这一条还缺什么才登得上 / 用得起来。</summary>
    [Flags]
    private enum Missing
    {
        /// <summary>什么都不缺 —— 点一下直接登录。</summary>
        None = 0,

        /// <summary>没有 API Key。</summary>
        ApiKey = 1,

        /// <summary>没有基地址(自建 / 按资源分配地址的云服务)。</summary>
        BaseUrl = 2,

        /// <summary>OAuth 客户端 id 还空着(VelaShell 尚未在这家注册应用)。</summary>
        ClientId = 4,

        /// <summary>OAuth 端点还空着(完全自定义的那一条)。</summary>
        Endpoints = 8
    }

    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;

    /// <summary>模型规格库(models.dev):按需拉新,缓存在插件私有数据目录。</summary>
    private readonly ModelsDevCatalog _models;

    private readonly StackPanel _rows = new() { Spacing = 6 };

    /// <summary>每行的把手:展开往 <c>Slot</c> 里塞内容,状态变了改 <c>Dot</c>/<c>Pill</c>/行尾按钮。</summary>
    private readonly List<Row> _cards = [];

    private sealed record Row(
        ProviderCatalogEntry Entry, Border Card, Ellipse Dot, TextBlock Pill, StackPanel Slot);

    /// <summary>当前展开的是哪一条(目录 id);null = 全收起。一次只开一个。</summary>
    private string? _openId;

    /// <summary>正在跑的那次登录 —— 「取消」按的就是它,窗口关掉也要一并取消。</summary>
    private CancellationTokenSource? _login;

    /// <summary>供应商增删改完了(参数是它的 id);设置页据此重建左栏并选中。</summary>
    public event Action<string>? ProviderChanged;

    /// <param name="context">插件上下文(日志 + 剪贴板)。</param>
    /// <param name="store">设置存储(建客户端、存机密都靠它)。</param>
    /// <param name="settings">面板共享的设置实例;直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="persist">落盘。</param>
    /// <param name="focusCatalogId">打开时直接展开的那一条(设置页的「管理登录」按过来时带着它)。</param>
    public ProviderSetupView(IPluginContext context, AiSettingsStore store, AiSettings settings, Loc loc,
        Func<Task> persist, string? focusCatalogId = null)
    {
        _context = context;
        _store = store;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        _models = new ModelsDevCatalog(context);

        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/DialogStyles.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/AiTheme.axaml")
        });

        // 顶部说明与底部脚注都拿掉了:每一行自己已经说清了状态和下一步,
        // 两大段常驻文案只是把列表往下挤(用户验收时点名要删)。
        // 右侧留白拆成 10(根)+ 10(滚动区内):覆盖式滚动条贴着滚动区右缘画,
        // 全放根上它就压在卡片边上了(与「配置工具」同一处理)。
        Content = new Border
        {
            Padding = new Thickness(20, 16, 20 - Gutter, 16),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Padding = new Thickness(0, 0, Gutter, 0), Child = _rows }
            }
        };

        foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
        {
            _rows.Children.Add(BuildCard(entry));
        }
        _ = InitialiseAsync(focusCatalogId);
    }

    private async Task InitialiseAsync(string? focusCatalogId)
    {
        await RefreshStatusAsync().ConfigureAwait(true);
        FocusEntry(focusCatalogId);
    }

    /// <summary>窗口关掉时把还在跑的登录掐掉,别留一个环回端口和一轮轮询在后台空转。</summary>
    public void CancelPendingLogin()
    {
        _login?.Cancel();
        _login = null;
    }

    /// <summary>
    /// 展开目录里的某一条并滚到眼前;传 null / 认不出的 id 就什么都不做。
    /// </summary>
    /// <remarks>
    /// 从设置页点「管理登录」过来时用。<b>这条路不自动发起登录</b> ——
    /// 用户是来"看看/改改"的,窗口一开就把浏览器弹出去太粗暴。
    /// </remarks>
    /// <param name="catalogId">目录 id。</param>
    public void FocusEntry(string? catalogId)
    {
        if (catalogId is null)
        {
            return;
        }
        foreach (Row row in _cards)
        {
            if (row.Entry.Id != catalogId)
            {
                continue;
            }
            Expand(row);
            row.Card.BringIntoView();
            return;
        }
    }

    // ---- 行 ----

    private Border BuildCard(ProviderCatalogEntry entry)
    {
        var monogram = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Monogram,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        monogram[!BackgroundProperty] = new DynamicResourceExtension("VelaBgActive");
        ((TextBlock)monogram.Child)[!ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        ((TextBlock)monogram.Child)[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");

        var name = new TextBlock { Text = entry.Name, FontWeight = FontWeight.Medium };
        name[!ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        name[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize13");
        // 走非公开接口的那几条挂个小标:哪天"AI 突然不能用了",用户得知道该往哪儿想
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name } };
        if (entry.Experimental)
        {
            nameRow.Children.Add(Badge(_loc["SetupExperimental"]));
        }
        var models = new TextBlock
        {
            Text = entry.Models,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        models[!ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        models[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");

        var dot = new Ellipse
        {
            Name = $"SetupDot.{entry.Id}",
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        dot[!Shape.FillProperty] = new DynamicResourceExtension("VelaTextMuted");
        var pillText = new TextBlock
        {
            Name = $"SetupPill.{entry.Id}",
            Text = _loc["StatusNotAdded"],
            VerticalAlignment = VerticalAlignment.Center
        };
        pillText[!ForegroundProperty] = new DynamicResourceExtension("VelaTextTertiary");
        pillText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { dot, pillText }
            }
        };
        pill[!BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        // 行上<b>不放</b>动作按钮:点行只负责展开,登录/添加一律在展开区里点。
        // 一来"点一下就弹浏览器"太突然 —— 用户还没看清这一家要什么就被推去授权;
        // 二来按钮坐在行的命中区里,按下会冒泡、抬起才是 Click,天然是个双触发的坑。
        var header = new Grid
        {
            ColumnDefinitions = [with("Auto,*,Auto")],
            // 整行都要能点,而不是只有那几个字:Grid 没有背景就不吃命中测试
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var titles = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        titles.Children.Add(nameRow);
        titles.Children.Add(models);
        Grid.SetColumn(monogram, 0);
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(pill, 2);
        header.Children.Add(monogram);
        header.Children.Add(titles);
        header.Children.Add(pill);

        var slot = new StackPanel { IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
        var card = new Border
        {
            Name = $"SetupRow.{entry.Id}",
            Classes = { "card" },
            Padding = new Thickness(12, 10),
            Child = new StackPanel { Children = { header, slot } }
        };
        var row = new Row(entry, card, dot, pillText, slot);
        header.PointerPressed += (_, _) => Activate(row);
        _cards.Add(row);
        return card;
    }

    /// <summary>
    /// 点了某一行。<b>能直接登的就直接登</b>,不能的才展开那几个缺的框。
    /// </summary>
    /// <summary>点某一行:只管展开 / 收起,<b>绝不自动发起登录</b>。</summary>
    private void Activate(Row row)
    {
        // 登录正跑着就别再碰这一行:重进会把自己刚起的那次掐掉(Expand → Collapse → CancelPendingLogin),
        // 而用户那边浏览器已经开着了 —— 掐完再开一次,回调就落到一个没人等的端口上。
        if (_openId == row.Entry.Id && _login is not null)
        {
            return;
        }
        if (_openId == row.Entry.Id)
        {
            Collapse();
            return;
        }
        Expand(row);
    }

    private bool IsConnected(ProviderCatalogEntry entry)
        => Existing(entry) is { } provider && _connected.Contains(provider.Id);

    /// <summary>已登录的供应商 id(<see cref="RefreshStatusAsync" /> 刷新;读机密是异步的,不能在点击路径上现读)。</summary>
    private readonly HashSet<string> _connected = [with(StringComparer.Ordinal)];

    /// <summary>已配好 Key 的供应商 id。</summary>
    private readonly HashSet<string> _keyed = [with(StringComparer.Ordinal)];

    private void Collapse()
    {
        CancelPendingLogin();
        foreach (Row row in _cards)
        {
            row.Slot.Children.Clear();
            row.Slot.IsVisible = false;
        }
        _openId = null;
    }

    private void Expand(Row row)
    {
        Collapse();
        _openId = row.Entry.Id;
        row.Slot.Children.Add(BuildDetail(row));
        row.Slot.IsVisible = true;
    }

    // ---- 展开区(每次展开重建,控件引用放在视图字段上)----

    private TextBox _nameBox = new();
    private TextBox _modelBox = new();
    private TextBox _baseUrlBox = new();
    private TextBox _keyBox = new();
    private ComboBox _protocolCombo = new();
    private ComboBox _flowCombo = new();
    private TextBox _authUrlBox = new();
    private TextBox _tokenUrlBox = new();
    private TextBox _deviceUrlBox = new();
    private TextBox _clientIdBox = new();
    private TextBox _scopesBox = new();
    private TextBlock _progress = new();
    private Button _primary = new();
    private Button _pull = new();
    private Button _secondary = new();
    private StackPanel _deviceCodePanel = new();
    private TextBlock _deviceCodeText = new();

    /// <summary>协议下拉的标签,顺序 = <see cref="ChatProtocol" /> 枚举。</summary>
    private static readonly string[] ProtocolLabels =
        ["OpenAI Chat Completions", "OpenAI Responses", "Anthropic Messages"];

    private StackPanel BuildDetail(Row row)
    {
        ProviderCatalogEntry entry = row.Entry;
        AiProvider? existing = Existing(entry);
        // 已经加过就编辑那一份;没加过按目录出厂值起草(按下按钮成功了才真的进设置)
        AiProvider draft = existing ?? entry.CreateProvider();
        AiModelConfig model = draft.Models.Count > 0 ? draft.Models[0] : new AiModelConfig();
        Missing missing = MissingOf(entry, draft);

        var panel = new StackPanel();
        if (entry.Experimental)
        {
            panel.Children.Add(Hint(_loc["SetupExperimentalHint"]));
        }
        // 每个控件都先造出来:后面按"缺不缺"决定摆不摆,ApplyForm 一律照读,
        // 没摆出来的就是目录里那个值,不会被清空。
        _nameBox = new TextBox { Name = "SetupNameBox", Text = draft.Name };
        _modelBox = Mono(new TextBox { Name = "SetupModelBox", Text = model.Model });
        _baseUrlBox = Mono(new TextBox { Name = "SetupBaseUrlBox", Text = Placeholder(draft.BaseUrl) });
        _keyBox = Mono(new TextBox { Name = "SetupKeyBox", PasswordChar = '●' });
        _protocolCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ProtocolLabels,
            SelectedIndex = (int)draft.DefaultProtocol
        };
        BuildOAuthControls(draft);

        if (entry.IsSubscription)
        {
            BuildSubscriptionDetail(panel, entry, missing);
        }
        else
        {
            BuildApiKeyDetail(panel, entry, missing);
        }
        panel.Children.Add(BuildActions(row, existing, missing));

        if (existing is not null && !entry.IsSubscription)
        {
            // 把已保存的 Key 填回去,免得「保存」把它清空。传的是这一次造出来的那个框 ——
            // 读机密要等一下,等回来时 _keyBox 可能已经是别人的了。
            _ = LoadKeyAsync(existing.Id, _keyBox);
        }
        return panel;
    }

    /// <summary>订阅项:缺什么摆什么。什么都不缺时这里<b>一个输入框都没有</b>,只有一行进度。</summary>
    private void BuildSubscriptionDetail(StackPanel panel, ProviderCatalogEntry entry, Missing missing)
    {
        if (IsConnected(entry))
        {
            panel.Children.Add(Hint(_loc["SetupConnectedHint"]));
            return;
        }
        if (missing == Missing.None)
        {
            panel.Children.Add(Hint(_loc["SetupSignInHint"]));
            return;
        }
        if (missing.HasFlag(Missing.ClientId))
        {
            // 客户端 id 空着 = VelaShell 还没在这家注册应用。别只摆个空框让人猜它哪儿来的:
            // 把申请入口一并给出来,愿意自己注册的人当场就能填。
            panel.Children.Add(Hint(_loc["SetupClientIdPending"]));
            if (entry.RegistrationUrl.Length > 0)
            {
                panel.Children.Add(LinkRow(_loc["SetupOpenRegistration"], entry.RegistrationUrl));
            }
            panel.Children.Add(Label(_loc["OAuthClientId"]));
            panel.Children.Add(_clientIdBox);
        }
        if (missing.HasFlag(Missing.BaseUrl))
        {
            panel.Children.Add(Label(_loc["BaseUrl"]));
            panel.Children.Add(_baseUrlBox);
        }
        if (missing.HasFlag(Missing.Endpoints))
        {
            BuildEndpointFields(panel);
        }
        panel.Children.Add(Advanced(entry));
    }

    /// <summary>填 Key 的那一路:只问一把 Key(地址不知道时才多问一句),其余全收进「高级」。</summary>
    private void BuildApiKeyDetail(StackPanel panel, ProviderCatalogEntry entry, Missing missing)
    {
        if (missing.HasFlag(Missing.BaseUrl))
        {
            panel.Children.Add(Label(_loc["BaseUrl"]));
            panel.Children.Add(_baseUrlBox);
        }
        if (NeedsKey(entry))
        {
            panel.Children.Add(Label(_loc["ApiKey"]));
            panel.Children.Add(KeyRow(_keyBox));
            panel.Children.Add(Hint(_loc["ApiKeyHint"]));
        }
        else
        {
            panel.Children.Add(Hint(_loc["SetupNoKeyNeeded"]));
        }
        panel.Children.Add(Advanced(entry));
    }

    private void BuildEndpointFields(StackPanel panel)
    {
        // 客户端 id 那一格由 BuildSubscriptionDetail 按 Missing.ClientId 摆,这里不能再摆一次 ——
        // 同一个控件挂进两个父容器,Avalonia 直接抛
        panel.Children.Add(Label(_loc["OAuthFlow"]));
        panel.Children.Add(_flowCombo);
        TextBlock authLabel = Label(_loc["OAuthAuthorizeUrl"]);
        panel.Children.Add(authLabel);
        panel.Children.Add(_authUrlBox);
        TextBlock deviceLabel = Label(_loc["OAuthDeviceUrl"]);
        panel.Children.Add(deviceLabel);
        panel.Children.Add(_deviceUrlBox);
        panel.Children.Add(Label(_loc["OAuthTokenUrl"]));
        panel.Children.Add(_tokenUrlBox);
        panel.Children.Add(Label(_loc["OAuthScopes"]));
        panel.Children.Add(_scopesBox);

        // 授权码那一行与设备码那一行互斥,连标签一起收
        void SyncFlow()
        {
            bool device = _flowCombo.SelectedIndex == 1;
            authLabel.IsVisible = !device;
            _authUrlBox.IsVisible = !device;
            deviceLabel.IsVisible = device;
            _deviceUrlBox.IsVisible = device;
        }
        _flowCombo.SelectionChanged += (_, _) => SyncFlow();
        SyncFlow();
    }

    private void BuildOAuthControls(AiProvider draft)
    {
        OAuthConfig oauth = draft.OAuth ?? new OAuthConfig();
        _flowCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { _loc["OAuthFlowPkce"], _loc["OAuthFlowDevice"] },
            // OpenRouter 那套 PKCE 变体在界面上仍归"授权码"一档:用户看到的行为一样,
            // 差别全在协议细节里,摆出第三个选项只会让人不知道该选哪个
            SelectedIndex = oauth.Flow == OAuthFlow.DeviceCode ? 1 : 0
        };
        _authUrlBox = Mono(new TextBox { Name = "SetupAuthUrlBox", Text = oauth.AuthorizationUrl });
        _tokenUrlBox = Mono(new TextBox { Name = "SetupTokenUrlBox", Text = oauth.TokenUrl });
        _deviceUrlBox = Mono(new TextBox { Name = "SetupDeviceUrlBox", Text = oauth.DeviceCodeUrl });
        _clientIdBox = Mono(new TextBox { Name = "SetupClientIdBox", Text = oauth.ClientId });
        _scopesBox = Mono(new TextBox { Name = "SetupScopesBox", Text = oauth.Scopes });
    }

    /// <summary>
    /// 「高级」:名称 / 模型 id / 基地址 / 协议。默认收起。
    /// </summary>
    /// <remarks>
    /// 这些目录里都有出厂值,九成用户一辈子不用改;摆在正面就是"填一堆东西"的由来。
    /// 但走中转站的人确实要改地址和模型 id,所以留一个折叠入口,而不是干脆删掉。
    /// </remarks>
    private StackPanel Advanced(ProviderCatalogEntry entry)
    {
        var body = new StackPanel { IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(Label(_loc["Name"]));
        body.Children.Add(_nameBox);
        // 拉到过列表就摆个下拉:接完一家之后想换模型,该是"从真实存在的里面挑",
        // 而不是回去翻文档把 id 一个字一个字敲对
        if (Existing(entry)?.AvailableModels is { Count: > 0 } available)
        {
            body.Children.Add(Label(_loc["ModelPick"]));
            var picker = new ComboBox
            {
                Name = "SetupModelPicker",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = available,
                SelectedItem = available.FirstOrDefault(m =>
                    string.Equals(m, _modelBox.Text, StringComparison.OrdinalIgnoreCase))
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is string id)
                {
                    _modelBox.Text = id;
                }
            };
            body.Children.Add(picker);
        }
        body.Children.Add(Label(_loc["Model"]));
        body.Children.Add(_modelBox);
        body.Children.Add(Hint(_loc["SetupModelHint"]));
        // 缺地址时它已经摆在正面了,别在这儿再来一个(同一个控件也挂不进两处)
        if (_baseUrlBox.Parent is null)
        {
            body.Children.Add(Label(_loc["BaseUrl"]));
            body.Children.Add(_baseUrlBox);
        }
        if (entry.NeedsBaseUrl)
        {
            body.Children.Add(Label(_loc["DefaultProtocol"]));
            body.Children.Add(_protocolCombo);
        }

        var toggle = new ToggleButton
        {
            Name = "SetupAdvancedToggle",
            Content = _loc["SetupAdvanced"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            Height = 22,
            Padding = new Thickness(8, 0)
        };
        toggle[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("AiChipToggleTheme");
        toggle.IsCheckedChanged += (_, _) => body.IsVisible = toggle.IsChecked == true;
        return new StackPanel { Children = { toggle, body } };
    }

    /// <summary>底下那一行:主按钮 + 次按钮(退出登录 / 取消)+ 进度,设备码时上面还有一枚用户码。</summary>
    private StackPanel BuildActions(Row row, AiProvider? existing, Missing missing)
    {
        ProviderCatalogEntry entry = row.Entry;
        _progress = new TextBlock
        {
            Name = "SetupProgressText",
            Classes = { "dim" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _deviceCodeText = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _deviceCodeText[!ForegroundProperty] = new DynamicResourceExtension("VelaAccent");
        _deviceCodeText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize13");
        _deviceCodeText[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        var copy = new Button { Content = _loc["Copy"], Height = 24, Padding = new Thickness(10, 0) };
        copy[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        copy.Click += (_, _) => _ = _context.Clipboard.SetTextAsync(_deviceCodeText.Text ?? "");
        _deviceCodePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _deviceCodeText, copy }
        };

        _primary = new Button { Name = "SetupPrimaryButton", Height = 26, Padding = new Thickness(14, 0) };
        _primary[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaAccentPillButtonTheme");
        _primary.Content = PrimaryLabel(entry, existing, missing);
        _primary.Click += (_, _) => _ = PrimaryAsync(row);

        // 「拉取模型」:接上之后自动拉过一次,但那一次可能没网、可能缓存是旧的,
        // 各家也会不断出新型号 —— 没有这个按钮,用户就只能靠"退出登录再登一次"来重来一遍。
        _pull = new Button { Name = "SetupPullButton", Height = 26, Padding = new Thickness(12, 0) };
        _pull[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        _pull.Content = _loc["ModelsPull"];
        // 已经加进来,而且两条路至少通一条:端点自己的 /models(填了地址就能问),
        // 或 models.dev 收录了这一家
        _pull.IsVisible = existing is not null
                          && (entry.ModelsDevId.Length > 0 || !string.IsNullOrWhiteSpace(existing.BaseUrl));
        _pull.Click += (_, _) => _ = PullNowAsync(row);

        _secondary = new Button { Name = "SetupSecondaryButton", Height = 26, Padding = new Thickness(12, 0) };
        _secondary[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        _secondary.Content = entry.IsSubscription ? _loc["SetupSignOut"] : _loc["SetupRemove"];
        _secondary.IsVisible = existing is not null;
        _secondary.Click += (_, _) => _ = SecondaryAsync(row);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _primary, _pull, _secondary }
        };
        var line = new Grid { ColumnDefinitions = [with("*,Auto")], Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(buttons, 1);
        line.Children.Add(_progress);
        line.Children.Add(buttons);
        return new StackPanel { Children = { _deviceCodePanel, line } };
    }

    private string PrimaryLabel(ProviderCatalogEntry entry, AiProvider? existing, Missing missing)
    {
        if (entry.IsSubscription)
        {
            return IsConnected(entry) ? _loc["SetupReconnect"] : _loc["SetupSignIn"];
        }
        return existing is null || missing.HasFlag(Missing.ApiKey) ? _loc["SetupAdd"] : _loc["Save"];
    }

    // ---- 缺什么 ----

    /// <summary>这一条离"能用"还差哪几样。</summary>
    private Missing MissingOf(ProviderCatalogEntry entry, AiProvider provider)
    {
        Missing missing = Missing.None;
        if (IsPlaceholder(provider.BaseUrl))
        {
            missing |= Missing.BaseUrl;
        }
        if (entry.IsSubscription)
        {
            OAuthConfig oauth = provider.OAuth ?? new OAuthConfig();
            // OpenRouter 那一路本来就没有 client_id,不算缺
            if (oauth.Flow != OAuthFlow.OpenRouterPkce && string.IsNullOrWhiteSpace(oauth.ClientId))
            {
                missing |= Missing.ClientId;
            }
            if (string.IsNullOrWhiteSpace(oauth.TokenUrl)
                || string.IsNullOrWhiteSpace(oauth.Flow == OAuthFlow.DeviceCode
                    ? oauth.DeviceCodeUrl
                    : oauth.AuthorizationUrl))
            {
                missing |= Missing.Endpoints;
            }
            return missing;
        }
        if (NeedsKey(entry) && !_keyed.Contains(provider.Id))
        {
            missing |= Missing.ApiKey;
        }
        return missing;
    }

    /// <summary>本地自部署(Ollama 之类)不需要鉴权,别对着它一直挂个"还没填 Key"。</summary>
    private static bool NeedsKey(ProviderCatalogEntry entry)
    {
        string url = entry.CreateProvider().BaseUrl;
        return !url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
               && !url.Contains("127.0.0.1", StringComparison.Ordinal);
    }

    /// <summary>地址还是空的、或者还带着 <c>&lt;resource&gt;</c> 这种占位符。</summary>
    private static bool IsPlaceholder(string? url)
        => string.IsNullOrWhiteSpace(url) || url.Contains('<', StringComparison.Ordinal);

    /// <summary>占位符不该出现在输入框里当默认值 —— 用户十有八九会直接把它连尖括号一起提交。</summary>
    private static string Placeholder(string url) => url.Contains('<', StringComparison.Ordinal) ? "" : url;

    // ---- 动作 ----

    private async Task PrimaryAsync(Row row)
    {
        ProviderCatalogEntry entry = row.Entry;
        try
        {
            _primary.IsEnabled = false;
            if (entry.IsSubscription)
            {
                await SignInAsync(row).ConfigureAwait(true);
            }
            else
            {
                await AddOrSaveKeyAsync(entry).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            _progress.Text = _loc["LoginCancelled"];
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Provider setup for '{entry.Name}' failed: {ex.Message}");
            // 换令牌这一步同样可能是"根本没连上",那时该说的是代理而不是"登录失败"
            _progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
        finally
        {
            _primary.IsEnabled = true;
            _deviceCodePanel.IsVisible = false;
            // 这一轮之后这家的处境可能变了(刚加进来 / 刚登上),按钮文字跟着改:
            // 加完还写着「添加」、登完还写着「登录」,下一次点它的人不知道会发生什么。
            // 次按钮在登录期间被借去当「取消」了,不管成没成都得还回来。
            AiProvider? existing = Existing(entry);
            _primary.Content = PrimaryLabel(entry, existing, existing is null ? Missing.None : MissingOf(entry, existing));
            _secondary.Content = entry.IsSubscription ? _loc["SetupSignOut"] : _loc["SetupRemove"];
            _secondary.IsVisible = existing is not null;
        }
    }

    /// <summary>
    /// 「拉取模型」按下去:重新问一次清单,把这一家的模型重新落地。
    /// </summary>
    /// <remarks>
    /// 与登录后那次自动拉的区别是 <c>force</c> —— 规格缓存还在有效期内也重下。
    /// 用户是明确点了"拉取"才走到这儿的,这时还拿七天前的缓存糊弄他就没意义了。
    /// </remarks>
    private async Task PullNowAsync(Row row)
    {
        if (Existing(row.Entry) is not { } provider)
        {
            return;
        }
        try
        {
            _pull.IsEnabled = false;
            await PullModelsAsync(row.Entry, provider, force: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn($"Pulling models for '{provider.Name}' failed: {ex.Message}");
            _progress.Text = _loc.F("LoginFailed", Chat.ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]));
        }
        finally
        {
            _pull.IsEnabled = true;
        }
    }

    /// <summary>次按钮:登录中是「取消」,平时是「退出登录」/「移除」。</summary>
    private async Task SecondaryAsync(Row row)
    {
        if (_login is not null)
        {
            CancelPendingLogin();
            return;
        }
        if (Existing(row.Entry) is not { } provider)
        {
            return;
        }
        if (row.Entry.IsSubscription)
        {
            await _store.ClearTokensAsync(provider.Id).ConfigureAwait(true);
            _progress.Text = _loc["LoginSignedOut"];
        }
        else
        {
            // 「移除」连着它下面的模型和机密一起走,与设置页删供应商同一套语义
            _settings.Providers.Remove(provider);
            foreach (AiModelConfig model in provider.Models)
            {
                await _store.DeleteApiKeyAsync(model.Id).ConfigureAwait(true);
            }
            await _store.DeleteApiKeyAsync(provider.Id).ConfigureAwait(true);
            if (_settings.FindModel(_settings.ActiveModelId) is null)
            {
                _settings.ActiveModelId = _settings.ResolveModels().FirstOrDefault()?.Id;
            }
            await _persist().ConfigureAwait(true);
            _progress.Text = _loc["SetupRemoved"];
        }
        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        _secondary.IsVisible = Existing(row.Entry) is not null;
    }

    /// <summary>填 Key 的那一路:没加过就新建供应商,加过就更新它。</summary>
    private async Task AddOrSaveKeyAsync(ProviderCatalogEntry entry)
    {
        AiProvider? existing = Existing(entry);
        AiProvider provider = existing ?? entry.CreateProvider();
        ApplyForm(provider, entry);
        if (IsPlaceholder(provider.BaseUrl))
        {
            _progress.Text = _loc["SetupNeedsBaseUrl"];
            return;
        }
        if (existing is null)
        {
            _settings.Providers.Add(provider);
            _settings.ActiveModelId ??= provider.Models.FirstOrDefault()?.Id;
        }
        // 框里是空的且库里已经有一把,那是"没改 Key,只改了别的",别把它清掉
        string? key = _keyBox.Text;
        if (!string.IsNullOrEmpty(key) || !_keyed.Contains(provider.Id))
        {
            await _store.SetApiKeyAsync(provider.Id, key).ConfigureAwait(true);
        }
        await _persist().ConfigureAwait(true);
        _progress.Text = existing is null ? _loc["SetupAdded"] : _loc["Saved"];
        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        await PullModelsAsync(entry, provider).ConfigureAwait(true);
    }

    /// <summary>
    /// 订阅登录那一路。<b>先登成功再把供应商写进设置</b> —— 登录失败(或用户中途放弃)时,
    /// 列表里不该多出一个连不上的空壳。已经加过的那家则原地重登,不产生副本。
    /// </summary>
    private async Task SignInAsync(Row row)
    {
        ProviderCatalogEntry entry = row.Entry;
        AiProvider? existing = Existing(entry);
        AiProvider provider = existing ?? entry.CreateProvider();
        ApplyForm(provider, entry);
        if (IsPlaceholder(provider.BaseUrl))
        {
            _progress.Text = _loc["SetupNeedsBaseUrl"];
            return;
        }
        if (!provider.CanSignIn)
        {
            _progress.Text = _loc["SetupNeedsOAuth"];
            return;
        }

        _secondary.Content = _loc["Cancel"];
        _secondary.IsVisible = true;
        _deviceCodePanel.IsVisible = false;
        _progress.Text = _loc["LoginStarting"];
        // 取消源在这儿开、在这儿收:CancelPendingLogin 只置 null 不 Dispose,
        // 否则窗口关掉之后再有人碰它就是一个 ObjectDisposedException。
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        _login = cancellation;

        var prompts = new LoginPrompts(
            _loc["LoginPageTitle"], _loc["LoginPageBody"],
            _loc["LoginWaiting"], _loc["LoginExchanging"], _loc["LoginUserCode"]);
        var progress = new Progress<LoginProgress>(step => Dispatcher.UIThread.Post(() =>
        {
            _progress.Text = step.Message;
            if (step.Device is { } device)
            {
                _deviceCodeText.Text = device.UserCode;
                _deviceCodePanel.IsVisible = true;
            }
        }));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var login = new ProviderLogin(new OAuthClient(http), LaunchAsync);
        OAuthTokens tokens;
        try
        {
            tokens = await login.SignInAsync(provider.OAuth!, prompts, progress, cancellation.Token)
                                .ConfigureAwait(true);
        }
        finally
        {
            // 必须在 using 收掉这个源之前松手(顺序:内层 finally → using 的 Dispose)
            _login = null;
        }

        if (existing is null)
        {
            _settings.Providers.Add(provider);
            _settings.ActiveModelId ??= provider.Models.FirstOrDefault()?.Id;
        }
        await _store.SaveTokensAsync(provider.Id, tokens).ConfigureAwait(true);
        await _persist().ConfigureAwait(true);
        _progress.Text = _loc["LoginDone"];
        await RefreshStatusAsync().ConfigureAwait(true);
        ProviderChanged?.Invoke(provider.Id);
        await PullModelsAsync(entry, provider).ConfigureAwait(true);
    }

    /// <summary>
    /// 接上之后把这一家的模型清单配好:id、上下文窗口、三档单价一次填齐。
    /// </summary>
    /// <remarks>
    /// 清单先问端点自己的 <c>/models</c>(只有它知道这个地址实际供应什么),再拿 id 去
    /// <see cref="ModelsDevCatalog" />(models.dev)配窗口与单价 —— 那条接口只给一串 id,
    /// 给不出这两项,而订阅型私有后端根本没有它。两条路的取舍全在 <see cref="ModelPull" /> 里。
    /// <para>
    /// 放在登录/添加<b>成功之后</b>单独跑,拉不到也只是少个便利:
    /// 这一步失败不该让"已经连上了"这个结果打折扣,更不该把异常冒到登录的错误处理里去。
    /// </para>
    /// </remarks>
    private async Task PullModelsAsync(ProviderCatalogEntry entry, AiProvider provider, bool force = false)
    {
        if (string.IsNullOrEmpty(entry.ModelsDevId) && string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return; // 两条路都不通:目录没收录,地址也还空着,保持出厂示例即可
        }
        try
        {
            _progress.Text = _loc["ModelsPulling"];
            ModelPullResult result = await ModelPull
                                           .RunAsync(provider, entry.ModelsDevId, _models, _store, force: force)
                                           .ConfigureAwait(true);
            if (result.Source == ModelSource.None)
            {
                _progress.Text = _loc["ModelsNone"];
                return;
            }
            int added = result.Total;
            await _persist().ConfigureAwait(true);
            ProviderChanged?.Invoke(provider.Id);
            string done = _loc.F("ModelsPulled", added);
            // 展开区是按拉取<b>之前</b>的状态搭的:重建一次,「高级设置」里那个模型下拉才拿得到新清单,
            // 按钮文字也才跟得上("添加"→"保存")。重建会换掉 _progress,所以结果最后再写。
            if (_openId == entry.Id && _cards.Find(r => r.Entry.Id == entry.Id) is { } row)
            {
                Expand(row);
            }
            _progress.Text = done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn($"Loading the model catalogue for '{provider.Name}' failed: {ex.Message}");
            _progress.Text = _loc["ModelsNone"];
        }
    }

    /// <summary>把表单里的值搬进供应商对象。没摆出来的控件带的就是目录出厂值,照读即可。</summary>
    private void ApplyForm(AiProvider provider, ProviderCatalogEntry entry)
    {
        provider.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? entry.Name : _nameBox.Text.Trim();
        provider.BaseUrl = _baseUrlBox.Text?.Trim() ?? "";
        if (entry.NeedsBaseUrl && _protocolCombo.SelectedIndex >= 0)
        {
            provider.DefaultProtocol = (ChatProtocol)_protocolCombo.SelectedIndex;
        }
        if (provider.Models.Count == 0)
        {
            provider.Models.Add(new AiModelConfig());
        }
        provider.Models[0].Model = _modelBox.Text?.Trim() ?? "";
        if (!entry.IsSubscription)
        {
            return;
        }
        OAuthConfig oauth = provider.OAuth ??= new OAuthConfig();
        oauth.ClientId = _clientIdBox.Text?.Trim() ?? "";
        if (!entry.NeedsOAuthSetup)
        {
            return; // 内置项的端点是目录给的,不让界面覆盖掉
        }
        oauth.Flow = _flowCombo.SelectedIndex == 1 ? OAuthFlow.DeviceCode : OAuthFlow.AuthorizationCodePkce;
        oauth.AuthorizationUrl = _authUrlBox.Text?.Trim() ?? "";
        oauth.TokenUrl = _tokenUrlBox.Text?.Trim() ?? "";
        oauth.DeviceCodeUrl = _deviceUrlBox.Text?.Trim() ?? "";
        oauth.Scopes = _scopesBox.Text?.Trim() ?? "";
    }

    private async Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
            return;
        }
        // 拿不到 Launcher(理论上只在没挂进窗口时)—— 把地址显示出来让用户自己开,别静默失败。
        // 但<b>日志里只留主机名</b>:完整授权地址带着 state 与 code_challenge,
        // 那是一次登录握手的一半,不该躺在日志文件里(见汇报 §10.2)。
        _context.Log.Warn($"No launcher available; asking the user to open the sign-in page at {uri.Host} manually.");
        _progress.Text = uri.ToString();
    }

    private async Task LoadKeyAsync(string providerId, TextBox target)
    {
        try
        {
            string? key = await _store.GetApiKeyAsync(providerId).ConfigureAwait(true);
            target.Text = key ?? "";
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reading the stored key failed: {ex.Message}");
        }
    }

    // ---- 状态灯 ----

    /// <summary>
    /// 刷新每一行右侧那枚状态与行尾按钮。要读机密所以是异步的,一次把整页读完 ——
    /// 每行各发一次会在打开窗口时把机密存储敲十几下。
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        _connected.Clear();
        _keyed.Clear();
        foreach (AiProvider provider in _settings.Providers)
        {
            if (await _store.GetTokensAsync(provider.Id).ConfigureAwait(true) is not null)
            {
                _connected.Add(provider.Id);
            }
            if (!string.IsNullOrEmpty(await _store.GetApiKeyAsync(provider.Id).ConfigureAwait(true)))
            {
                _keyed.Add(provider.Id);
            }
        }
        foreach (Row row in _cards)
        {
            (string label, string brush) = StatusOf(row.Entry);
            row.Pill.Text = label;
            row.Dot[!Shape.FillProperty] = new DynamicResourceExtension(brush);
        }
    }

    private (string Label, string Brush) StatusOf(ProviderCatalogEntry entry)
    {
        AiProvider? provider = Existing(entry);
        if (entry.IsSubscription)
        {
            if (provider is null)
            {
                return (_loc["StatusNotConnected"], "VelaTextMuted");
            }
            return _connected.Contains(provider.Id)
                ? (_loc["StatusConnected"], "VelaShellGreen")
                : (_loc["StatusNotConnected"], "VelaWarning");
        }
        if (provider is null)
        {
            return (_loc["StatusNotAdded"], "VelaTextMuted");
        }
        return !NeedsKey(entry) || _keyed.Contains(provider.Id)
            ? (_loc["StatusReady"], "VelaShellGreen")
            : (_loc["StatusNeedsKey"], "VelaWarning");
    }

    /// <summary>设置里已经有没有这一家(按目录 id 认);同一家加了多份时取第一份。</summary>
    private AiProvider? Existing(ProviderCatalogEntry entry)
        => _settings.Providers.Find(p => p.CatalogId == entry.Id);

    // ---- 小零件 ----

    private static TextBlock Label(string text) => new() { Classes = { "label" }, Text = text };

    /// <summary>一枚警示色的小标(目前只有"实验性"用它)。</summary>
    private static Border Badge(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label[!ForegroundProperty] = new DynamicResourceExtension("VelaWarning");
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        var badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label
        };
        badge[!BorderBrushProperty] = new DynamicResourceExtension("VelaWarning");
        return badge;
    }

    private static TextBlock Hint(string text) => new() { Classes = { "hint" }, Text = text };

    private static TextBox Mono(TextBox box)
    {
        box[!FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        return box;
    }

    /// <summary>一枚"去这儿注册"的按钮,点了开浏览器。</summary>
    private Button LinkRow(string text, string url)
    {
        var button = new Button
        {
            Content = text,
            Height = 24,
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        button[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        button.Click += (_, _) =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                _ = LaunchAsync(uri, CancellationToken.None);
            }
        };
        return button;
    }

    /// <summary>Key 输入框 + 右边一枚"看一眼"的眼睛,与设置页同一形状。</summary>
    private static Grid KeyRow(TextBox box)
    {
        var reveal = new ToggleButton { Width = 30, Height = 30, Padding = new Thickness(0) };
        reveal[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("AiChipToggleTheme");
        var eye = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        eye[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.eye");
        eye[!Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        reveal.Content = new Viewbox { Width = 13, Height = 13, Child = eye };
        reveal.IsCheckedChanged += (_, _) => box.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var grid = new Grid { ColumnDefinitions = [with("*,6,Auto")] };
        Grid.SetColumn(reveal, 2);
        grid.Children.Add(box);
        grid.Children.Add(reveal);
        return grid;
    }
}

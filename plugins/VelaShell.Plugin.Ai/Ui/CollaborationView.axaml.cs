using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「协作接入」设置页:IM 桥接(往外)与对外 MCP 服务端(往内)。
/// </summary>
/// <remarks>
/// 渠道卡片是<b>代码建的</b>,不是 DataTemplate 绑上去的 —— 每种平台要填的字段不一样
/// (Telegram 没有 AppId、只有飞书有国际版、只有企微要 AgentID),用模板就得靠一堆
/// 转换器去藏字段,不如直接按平台建对应的那几行。
/// </remarks>
public partial class CollaborationView : UserControl
{
    private readonly IPluginContext _context;
    private readonly BridgeSettingsStore _bridgeStore;
    private readonly McpServerSettingsStore _mcpStore;
    private readonly Loc _loc;
    private readonly Func<Task> _restart;

    private readonly BridgeService? _bridge;
    private readonly DispatcherTimer _refresh;
    private BridgeSettings _settings = new();
    private McpServerSettings _mcp = new();
    private readonly List<ChannelRow> _rows = [];
    private string _token = "";

    /// <summary>一张渠道卡片上那些要回读的控件。</summary>
    private sealed class ChannelRow
    {
        public required ChannelConfig Config { get; init; }

        public required CheckBox Enabled { get; init; }

        public required TextBox Name { get; init; }

        public TextBox? AppId { get; init; }

        public TextBox? AgentId { get; init; }

        public required TextBox Secret { get; init; }

        /// <summary>企微回调用的 Token(与 <see cref="Secret" /> 是两样东西)。</summary>
        public TextBox? CallbackToken { get; init; }

        /// <summary>企微的 EncodingAESKey。</summary>
        public TextBox? AesKey { get; init; }

        /// <summary>企微回调监听的端口与路径。</summary>
        public TextBox? Port { get; init; }

        /// <inheritdoc cref="Port" />
        public TextBox? Path { get; init; }

        public CheckBox? International { get; init; }

        /// <summary>这个渠道下每个聊天的授权(范围 / 挡位 / 审批)。</summary>
        public required List<GrantRow> Grants { get; init; }

        /// <summary>装着那些授权行的面板(「允许」按钮往里插新行)。</summary>
        public required StackPanel GrantsList { get; init; }

        public required TextBox Users { get; init; }

        public required TextBox Approvers { get; init; }

        public required TextBox Target { get; init; }

        public bool Removed { get; set; }
    }

    /// <summary>由插件入口构造(UI 线程)。</summary>
    /// <param name="context">插件上下文。</param>
    /// <param name="loc">文案。</param>
    /// <param name="restart">保存后按新配置重起桥接与 MCP 服务端。</param>
    /// <param name="bridge">
    /// 正在跑的桥接;为 null 表示还没起来(配对码与待放行清单那一节会说明原因)。
    /// </param>
    public CollaborationView(IPluginContext context, Loc loc, Func<Task> restart, BridgeService? bridge = null)
    {
        _context = context;
        _loc = loc;
        _restart = restart;
        _bridge = bridge;
        _bridgeStore = new BridgeSettingsStore(context);
        _mcpStore = new McpServerSettingsStore(context);
        InitializeComponent();
        ApplyLoc();

        // 两个码:给自己的不限范围,给群的必须先勾范围。它们的默认值本来就该不一样。
        IssuePairSelfButton.Click += (_, _) => IssuePairCode(null);
        IssuePairGroupButton.Click += (_, _) => IssuePairCode(_pairScope?.Invoke());
        // 敲门是异步发生的(人在手机上操作),所以这一页开着时自己刷 ——
        // 否则用户得关掉再打开才看得见刚才那个群
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            RefreshPending();
            RefreshPairCode();
        });
        _refresh.Start();
        DetachedFromVisualTree += (_, _) => _refresh.Stop();

        BridgeEnabledCheck.IsCheckedChanged += (_, _) => UpdateVisibility();
        McpEnabledCheck.IsCheckedChanged += (_, _) => UpdateVisibility();
        McpPortBox.TextChanged += (_, _) => UpdateCommandSample();
        AddChannelButton.Click += (_, _) => AddChannel();
        SaveButton.Click += (_, _) => _ = SaveAsync();
        CopyTokenButton.Click += (_, _) => _ = _context.Clipboard.SetTextAsync(_token);
        CopyCommandButton.Click += (_, _) => _ = _context.Clipboard.SetTextAsync(McpCommandBox.Text ?? "");
        RotateTokenButton.Click += (_, _) => _ = RotateTokenAsync();

        _ = LoadAsync();
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        SectionBridgeTitle.Text = _loc["SecBridge"];
        BridgeEnabledCheck.Content = _loc["BridgeEnabled"];
        BridgeEnabledHint.Text = _loc["BridgeEnabledHint"];
        BridgeModeLabel.Text = _loc["BridgeMode"];
        BridgeApprovalLabel.Text = _loc["BridgeApproval"];
        BridgeModeHint.Text = _loc["BridgeModeHint"];
        EscalationCheck.Content = _loc["BridgeEscalation"];
        EscalationHint.Text = _loc["BridgeEscalationHint"];
        TurnTimeoutLabel.Text = _loc["BridgeTurnTimeout"];
        ApprovalTimeoutLabel.Text = _loc["BridgeApprovalTimeout"];
        ConcurrencyLabel.Text = _loc["BridgeConcurrency"];
        BridgeModelLabel.Text = _loc["BridgeModel"];
        BridgeModelHint.Text = _loc["BridgeModelHint"];

        SectionChannelsTitle.Text = _loc["SecChannels"];
        AddChannelButton.Content = _loc["ChannelAdd"];
        NoChannelsHint.Text = _loc["ChannelNone"];

        SectionPairingTitle.Text = _loc["SecPairing"];
        PairCodeLabel.Text = _loc["PairCode"];
        IssuePairSelfButton.Content = _loc["PairForSelf"];
        PairCodeHint.Text = _loc["PairHint"];
        PairGroupLabel.Text = _loc["PairForGroup"];
        IssuePairGroupButton.Content = _loc["PairIssue"];
        PairScopeHint.Text = _loc["PairScopeHint"];
        PendingLabel.Text = _loc["PairPending"];
        NoPendingHint.Text = _loc["PairNoPending"];

        SectionMcpTitle.Text = _loc["SecMcpServer"];
        McpEnabledCheck.Content = _loc["McpServerEnabled"];
        McpEnabledHint.Text = _loc["McpServerEnabledHint"];
        McpPortLabel.Text = _loc["McpServerPort"];
        McpModeLabel.Text = _loc["BridgeMode"];
        McpApprovalLabel.Text = _loc["BridgeApproval"];
        McpApprovalHint.Text = _loc["McpServerApprovalHint"];
        McpTargetsLabel.Text = _loc["McpServerTargets"];
        McpTargetsHint.Text = _loc["McpServerTargetsHint"];
        McpTokenLabel.Text = _loc["McpServerToken"];
        McpTokenHint.Text = _loc["McpServerTokenHint"];
        McpCommandLabel.Text = _loc["McpServerCommand"];
        CopyTokenButton.Content = _loc["Copy"];
        CopyCommandButton.Content = _loc["Copy"];
        RotateTokenButton.Content = _loc["McpServerRotate"];
        SaveButton.Content = _loc["Save"];

        FillModes(BridgeModeCombo);
        FillModes(McpModeCombo);
        FillApprovals(BridgeApprovalCombo);
        FillApprovals(McpApprovalCombo);
        FillKinds();
    }

    private void FillModes(ComboBox combo)
    {
        int selected = combo.SelectedIndex;
        combo.ItemsSource = new[] { _loc["ModeChat"], _loc["ModePlan"], _loc["ModeAgent"] };
        combo.SelectedIndex = selected < 0 ? 1 : selected;
    }

    private void FillApprovals(ComboBox combo)
    {
        int selected = combo.SelectedIndex;
        combo.ItemsSource = new[] { _loc["ApprovalAsk"], _loc["ApprovalReadOnly"], _loc["ApprovalBypass"] };
        combo.SelectedIndex = selected < 0 ? 0 : selected;
    }

    private void FillKinds()
    {
        int selected = AddKindCombo.SelectedIndex;
        // 平台名带上拉丁写法:界面可以切到日/韩,而"钉钉"两个字对那边的用户不是可读的品牌名
        AddKindCombo.ItemsSource = new[] { "飞书 / Lark", "钉钉 / DingTalk", "Telegram", "企业微信 / WeCom" };
        AddKindCombo.SelectedIndex = selected < 0 ? 0 : selected;
    }

    /// <summary>可选的模型(下拉里第 0 项是"跟随聊天面板",所以下标要减一)。</summary>
    private List<ResolvedModel> _models = [];

    private async Task LoadAsync()
    {
        _settings = await _bridgeStore.LoadAsync();
        // 范围选择器要按会话树里的分组来勾。取一次就够:这一页开着的时候会话树很少在动,
        // 而每建一行就去问一次宿主,一个有二十条授权的渠道会问二十遍。
        _saved = [.. await _context.Sessions.ListSavedAsync()];
        _mcp = await _mcpStore.LoadAsync();
        _token = await _mcpStore.TokenAsync();
        await LoadModelsAsync();

        BridgeEnabledCheck.IsChecked = _settings.Enabled;
        BridgeModeCombo.SelectedIndex = (int)_settings.Mode;
        BridgeApprovalCombo.SelectedIndex = (int)_settings.Approval;
        EscalationCheck.IsChecked = _settings.AllowModeEscalation;
        TurnTimeoutBox.Text = _settings.TurnTimeoutSeconds.ToString();
        ApprovalTimeoutBox.Text = _settings.ApprovalTimeoutSeconds.ToString();
        ConcurrencyBox.Text = _settings.MaxConcurrentTurns.ToString();

        McpEnabledCheck.IsChecked = _mcp.Enabled;
        McpPortBox.Text = _mcp.Port.ToString();
        McpModeCombo.SelectedIndex = (int)_mcp.Mode;
        McpApprovalCombo.SelectedIndex = (int)_mcp.Approval;
        McpTargetsBox.Text = _mcp.AllowedTargets;
        McpTokenBox.Text = _token;

        PairScopePanel.Children.Clear();
        PairScopePanel.Children.Add(BuildPairScopePicker(out Func<SessionScope> readPairScope));
        _pairScope = readPairScope;

        await RebuildChannelsAsync();
        UpdateVisibility();
        UpdateCommandSample();
        // 立刻刷一次,别让人对着空白等三秒 —— 定时器只负责"页面开着时后来又有人敲门"
        RefreshPending();
        RefreshPairCode();
    }

    /// <summary>
    /// 把已配好的模型灌进下拉。第 0 项是"跟随聊天面板"。
    /// </summary>
    /// <remarks>
    /// 一个模型都没配时只留第 0 项 —— 这时桥接跑起来会回一句"还没配置模型",
    /// 比在下拉里摆一堆空条目诚实。
    /// </remarks>
    private async Task LoadModelsAsync()
    {
        AiSettings ai = await new AiSettingsStore(_context).LoadAsync();
        _models = ai.ResolveModels();
        List<string> items = [_loc["BridgeModelFollow"]];
        items.AddRange(_models.Select(m => $"{m.ProviderName} / {m.Name}"));
        BridgeModelCombo.ItemsSource = items;
        int index = _settings.ModelId is { Length: > 0 } id
            ? _models.FindIndex(m => m.Id == id) + 1
            : 0;
        BridgeModelCombo.SelectedIndex = Math.Max(0, index);
    }

    private async Task RebuildChannelsAsync()
    {
        _rows.Clear();
        ChannelsPanel.Children.Clear();
        foreach (ChannelConfig config in _settings.Channels)
        {
            string? secret = await _bridgeStore.GetSecretAsync(config.Id, "secret");
            ChannelsPanel.Children.Add(BuildCard(config, secret ?? ""));
        }
        NoChannelsHint.IsVisible = _settings.Channels.Count == 0;
    }

    private Border BuildCard(ChannelConfig config, string secret)
    {
        var title = new TextBlock { Text = KindLabel(config.Kind), VerticalAlignment = VerticalAlignment.Center };
        title.Classes.Add("section-title");
        Button test = HostButton(_loc["ChannelTest"], 72);
        Button remove = HostButton(_loc["ChannelRemove"], 72);
        remove.Margin = new Thickness(8, 0, 0, 0);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(test, 1);
        header.Children.Add(test);
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);

        // 体检结果与「拉机器人进群」的二维码:平时不占版面,点了测试才出现
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        testResult.Classes.Add("hint");
        var qr = new Image { MaxWidth = 168, MaxHeight = 168 };
        // 二维码本身是黑白的(为了扫得动,不跟着主题反色),所以给它一圈 card 的描边与内边距,
        // 让它像页面上的一块内容,而不是一张糊在深色卡片上的白纸。
        var qrFrame = new Border
        {
            Child = qr,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false
        };
        qrFrame.Classes.Add("card");
        var qrHint = new TextBlock
        {
            Text = _loc["ChannelInviteHint"],
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        qrHint.Classes.Add("hint");

        CheckBox enabled = HostCheckBox(_loc["ChannelEnabled"], config.Enabled);
        TextBox name = Field(_loc["ChannelName"], config.DisplayName, out StackPanel namePanel);
        TextBox? appId = null;
        StackPanel? appIdPanel = null;
        if (config.Kind != ChannelKind.Telegram)
        {
            appId = Field(AppIdLabel(config.Kind), config.AppId, out appIdPanel);
        }
        TextBox secretBox = Field(SecretLabel(config.Kind), secret, out StackPanel secretPanel);
        secretBox.PasswordChar = '●';

        TextBox? agentId = null, callbackToken = null, aesKey = null, port = null, path = null;
        StackPanel? agentPanel = null, callbackPanel = null, aesPanel = null, endpointPanel = null;
        if (config.Kind == ChannelKind.WeCom)
        {
            agentId = Field("AgentID", config.AgentId, out agentPanel);
            callbackToken = Field(_loc["ChannelCallbackToken"], "", out callbackPanel);
            callbackToken.PasswordChar = '●';
            aesKey = Field("EncodingAESKey", "", out aesPanel);
            aesKey.PasswordChar = '●';
            // 端口与路径并排:它们合起来才是一个地址,分两行读着费劲
            port = Field(_loc["ChannelWebhookPort"], config.WebhookPort.ToString(), out StackPanel portPanel);
            path = Field(_loc["ChannelWebhookPath"], config.WebhookPath, out StackPanel pathPanel);
            endpointPanel = new StackPanel();
            var endpoint = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,2*") };
            endpoint.Children.Add(portPanel);
            Grid.SetColumn(pathPanel, 2);
            endpoint.Children.Add(pathPanel);
            var callbackHint = new TextBlock { Text = _loc["ChannelWeComCallbackHint"], TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            callbackHint.Classes.Add("hint");
            endpointPanel.Children.Add(endpoint);
            endpointPanel.Children.Add(callbackHint);
        }
        CheckBox? international = null;
        if (config.Kind == ChannelKind.Feishu)
        {
            international = HostCheckBox(_loc["ChannelInternational"], config.International);
        }

        List<GrantRow> grantRows = [];
        StackPanel chatsPanel = BuildGrantsSection(config, grantRows, out StackPanel grantsList);
        TextBox users = Field(_loc["ChannelUsers"], string.Join("\n", config.AllowedUsers), out StackPanel usersPanel, multiline: true);
        TextBox approvers = Field(_loc["ChannelApprovers"], string.Join("\n", config.Approvers), out StackPanel approversPanel, multiline: true);
        TextBox target = Field(_loc["ChannelTarget"], config.DefaultTarget, out StackPanel targetPanel);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(enabled);
        body.Children.Add(namePanel);
        if (appIdPanel is not null)
        {
            body.Children.Add(appIdPanel);
        }
        if (agentPanel is not null)
        {
            body.Children.Add(agentPanel);
        }
        body.Children.Add(secretPanel);
        if (callbackPanel is not null)
        {
            body.Children.Add(callbackPanel);
        }
        if (aesPanel is not null)
        {
            body.Children.Add(aesPanel);
        }
        if (endpointPanel is not null)
        {
            body.Children.Add(endpointPanel);
        }
        if (international is not null)
        {
            body.Children.Add(international);
        }
        body.Children.Add(chatsPanel);
        body.Children.Add(usersPanel);
        body.Children.Add(approversPanel);
        body.Children.Add(targetPanel);
        body.Children.Add(testResult);
        body.Children.Add(qrHint);
        body.Children.Add(qrFrame);

        var card = new Border { Child = body };
        card.Classes.Add("section");

        var row = new ChannelRow
        {
            Config = config,
            Enabled = enabled,
            Name = name,
            AppId = appId,
            AgentId = agentId,
            Secret = secretBox,
            CallbackToken = callbackToken,
            AesKey = aesKey,
            Port = port,
            Path = path,
            International = international,
            Grants = grantRows,
            GrantsList = grantsList,
            Users = users,
            Approvers = approvers,
            Target = target
        };
        _rows.Add(row);
        remove.Click += (_, _) =>
        {
            row.Removed = true;
            ChannelsPanel.Children.Remove(card);
            NoChannelsHint.IsVisible = _rows.All(r => r.Removed);
        };
        test.Click += (_, _) => _ = TestAsync(row, test, testResult, qr, qrFrame, qrHint);
        return card;
    }

    /// <summary>
    /// 拿框里<b>当前</b>填的东西去试一次连接(不保存)。
    /// </summary>
    /// <remarks>
    /// 用界面上的值而不是 <see cref="ChannelRow.Config" /> 里那份 —— 用户改完密钥
    /// 最想做的就是当场验一下,让他先保存再测,等于把"试错"这件事变成"改配置"。
    /// </remarks>
    private async Task TestAsync(ChannelRow row, Button button, TextBlock result, Image qr, Border qrFrame,
        TextBlock qrHint)
    {
        button.IsEnabled = false;
        result.IsVisible = true;
        result.Text = _loc["ChannelTesting"];
        qrFrame.IsVisible = false;
        qrHint.IsVisible = false;
        try
        {
            var probe = new ChannelConfig
            {
                Id = row.Config.Id,
                Kind = row.Config.Kind,
                AppId = (row.AppId?.Text ?? row.Config.AppId).Trim(),
                AgentId = (row.AgentId?.Text ?? row.Config.AgentId).Trim(),
                International = row.International?.IsChecked == true
            };
            ChannelProbeResult outcome = await ChannelProbe.TestAsync(probe,
                (row.Secret.Text ?? "").Trim(), (row.AesKey?.Text ?? "").Trim(),
                _context.Log, CancellationToken.None);
            result.Text = (outcome.Ok ? "✓ " : "✗ ") + outcome.Summary;
            if (outcome is { Ok: true, InviteUrl: { Length: > 0 } url })
            {
                qr.Source = RenderQr(url);
                qrFrame.IsVisible = true;
                qrHint.Text = _loc["ChannelInviteHint"];
                qrHint.IsVisible = true;
            }
            else if (outcome.Ok && row.Config.Kind == ChannelKind.Feishu)
            {
                // 飞书没有"扫码加机器人进群"的链接(applink 那条是打开小程序用的,
                // 纯机器人应用扫出来是"此页面无效")。所以这里不摆码,直接把手工步骤写出来。
                qrHint.Text = _loc["ChannelAddBotFeishu"];
                qrHint.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            result.Text = "✗ " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>一个模块画多少像素。位图会被 <see cref="Image" /> 缩到 168,取 6 让缩的是"变小"。</summary>
    private const int QrScale = 6;

    /// <summary>静默区宽度,规范要求四周留 4 个模块 —— 少了识读器可能框不出符号边界。</summary>
    private const int QrQuietZone = 4;

    /// <summary>把一段链接画成二维码。</summary>
    /// <remarks>
    /// 自己算模块矩阵(<see cref="QrCode" />)再直接写进 <see cref="WriteableBitmap" />,
    /// 中间不经 PNG:原先走 QRCoder 的 <c>PngByteQRCode</c> 出字节流再解回位图,
    /// 是为了绕开它那几个吐 <c>System.Drawing.Bitmap</c> 的类型;把编码器换成自己的之后,
    /// 这一趟编码 + 解码就没有存在的理由了。换掉 QRCoder 的原因见 <see cref="QrCode" /> 的注释。
    /// </remarks>
    private static WriteableBitmap RenderQr(string text)
    {
        QrCode qr = QrCode.Encode(text, QrEcc.Medium);
        int side = (qr.Size + (QrQuietZone * 2)) * QrScale;
        WriteableBitmap bitmap = new(
            new PixelSize(side, side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        // 二维码固定黑白,不跟着主题反色 —— 反色的码有相当一部分识读器扫不动。
        byte[] scanline = new byte[side * 4];
        using ILockedFramebuffer frame = bitmap.Lock();
        for (int y = 0; y < side; y++)
        {
            int moduleY = (y / QrScale) - QrQuietZone;
            for (int x = 0; x < side; x++)
            {
                int moduleX = (x / QrScale) - QrQuietZone;
                bool dark = moduleY >= 0 && moduleY < qr.Size
                            && moduleX >= 0 && moduleX < qr.Size
                            && qr[moduleX, moduleY];
                byte level = dark ? (byte)0 : (byte)255;
                int offset = x * 4;
                scanline[offset] = level;
                scanline[offset + 1] = level;
                scanline[offset + 2] = level;
                scanline[offset + 3] = 255;
            }
            // 只拷一行的有效字节:RowBytes 可能比 side*4 大(行首对齐的填充)。
            Marshal.Copy(scanline, 0, frame.Address + (y * frame.RowBytes), scanline.Length);
        }
        return bitmap;
    }

    /// <summary>
    /// 建一个和这一页其余按钮同款的按钮。
    /// </summary>
    /// <remarks>
    /// <b>必须挂宿主的 <c>VelaOutlineButtonTheme</c>。</b>不挂的话不只是"颜色不一样" ——
    /// 那套主题里带着 <c>HorizontalContentAlignment="Center"</c>,不挂就是文字不居中
    /// (实测,用户先看出来的)。见 <c>DESIGN.md</c> 与 <c>src/VelaShell/Themes/ButtonThemes.axaml</c>。
    /// <para>
    /// 用 <c>TryFindResource</c> 而不是 <c>FindResource</c>:这是<b>宿主</b>的资源,
    /// headless 测试里没有宿主那套主题字典,取不到就退回默认外观,而不是把整页构造炸掉。
    /// </para>
    /// </remarks>
    private static Button HostButton(string content, double minWidth)
    {
        var button = new Button { Content = content, MinWidth = minWidth };
        button.Classes.Add("host");
        return button;
    }

    /// <summary>
    /// 建一个和 XAML 里那几个同款的勾选框。
    /// </summary>
    /// <remarks>
    /// 字号与前景色要跟着一起给:<c>AiCheckBoxTheme</c> 只管勾选框那个方块的画法,
    /// 旁边那行字仍旧用控件自己的 <c>FontSize</c> / <c>Foreground</c> ——
    /// 只挂主题不给这两项,代码建的勾选框会比 XAML 里的大一号、而且颜色不跟主题走。
    /// </remarks>
    private static CheckBox HostCheckBox(string content, bool isChecked)
    {
        var box = new CheckBox { Content = content, IsChecked = isChecked };
        box.Classes.Add("host");
        return box;
    }

    /// <summary>一个"标签 + 输入框"的竖排。</summary>
    private static TextBox Field(string label, string value, out StackPanel panel, bool multiline = false)
    {
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("label"); // 间距由 label 这一档自己给
        var box = new TextBox { Text = value };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.MinHeight = 56;
            box.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        }
        panel = new StackPanel();
        panel.Children.Add(caption);
        panel.Children.Add(box);
        return box;
    }

    private void AddChannel()
    {
        ChannelKind kind = AddKindCombo.SelectedIndex switch
        {
            1 => ChannelKind.DingTalk,
            2 => ChannelKind.Telegram,
            3 => ChannelKind.WeCom,
            _ => ChannelKind.Feishu
        };
        if (kind == ChannelKind.WeCom)
        {
            // 加得出来,但要提醒一句它和另外三家不一样:它需要一个公网可达的入口
            StatusText.Text = _loc["ChannelWeComHint"];
        }
        var config = new ChannelConfig { Kind = kind, DisplayName = KindLabel(kind) };
        _settings.Channels.Add(config);
        ChannelsPanel.Children.Add(BuildCard(config, ""));
        NoChannelsHint.IsVisible = false;
    }

    private async Task RotateTokenAsync()
    {
        _token = await _mcpStore.RotateTokenAsync();
        McpTokenBox.Text = _token;
        UpdateCommandSample();
        StatusText.Text = _loc["McpServerRotated"];
    }

    /// <summary>发一个新的配对码。</summary>
    /// <summary>读出「给群的配对码」那一段现在勾了什么范围。</summary>
    private Func<SessionScope>? _pairScope;

    /// <param name="scope">
    /// 这个码兑现之后的范围。<see langword="null" /> = 不限范围(给自己单聊的那个按钮)。
    /// </param>
    private void IssuePairCode(SessionScope? scope)
    {
        if (_bridge is null)
        {
            StatusText.Text = _loc["PairNeedsBridge"];
            return;
        }
        _bridge.Pairing.Issue(scope is null ? null : new ChatGrant { Scope = scope, IsGroup = true });
        RefreshPairCode();
    }

    /// <summary>刷新配对码那一行(含剩余时间;过期了就退回成"点一下生成")。</summary>
    private void RefreshPairCode()
    {
        if (_bridge?.Pairing.Code is not { } code)
        {
            PairCodeText.Text = "—";
            return;
        }
        TimeSpan left = _bridge.Pairing.ExpiresAt - DateTimeOffset.UtcNow;
        PairCodeText.Text = left > TimeSpan.Zero
            ? $"{code}    {_loc.F("PairExpiresIn", (int)left.TotalMinutes, left.Seconds)}"
            : "—";
    }

    /// <summary>
    /// 刷新"敲过门的聊天"清单。
    /// </summary>
    /// <remarks>
    /// 整块重建而不是做增量比对:这个列表最多几十行,而且只在这一页开着时刷 ——
    /// 为它写一套差异算法,省下的那点开销还不够读那段代码的时间。
    /// </remarks>
    private void RefreshPending()
    {
        IReadOnlyList<PendingChat> pending = _bridge?.Pairing.Pending() ?? [];
        if (pending.Count == PendingPanel.Children.Count
            && pending.Select(p => p.ChatKey).SequenceEqual(_shownPending))
        {
            return; // 没变就别重建,否则用户的鼠标悬停状态每三秒被抹一次
        }
        _shownPending = [.. pending.Select(p => p.ChatKey)];
        PendingPanel.Children.Clear();
        foreach (PendingChat chat in pending)
        {
            PendingPanel.Children.Add(BuildPendingRow(chat));
        }
        NoPendingHint.IsVisible = pending.Count == 0;
    }

    private List<string> _shownPending = [];

    private Border BuildPendingRow(PendingChat chat)
    {
        string channel = _settings.Channels.FirstOrDefault(c => c.Id == chat.ChannelId)?.Label ?? chat.ChannelId;
        var title = new TextBlock
        {
            Text = $"{channel} · {(chat.IsGroup ? _loc["PairGroup"] : _loc["PairDirect"])} · {chat.UserName}",
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Classes.Add("body");
        var id = new TextBlock { Text = chat.ChatId, VerticalAlignment = VerticalAlignment.Center };
        id.Classes.Add("hint");
        Button allow = HostButton(_loc["PairAllow"], 72);
        Button ignore = HostButton(_loc["PairIgnore"], 72);
        ignore.Margin = new Thickness(8, 0, 0, 0);

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(id);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(text);
        Grid.SetColumn(allow, 1);
        grid.Children.Add(allow);
        Grid.SetColumn(ignore, 2);
        grid.Children.Add(ignore);

        var card = new Border { Child = grid };
        card.Classes.Add("card"); // 内边距由 card 这一档自己给,别在这儿另写一套
        allow.Click += (_, _) => _ = AllowPendingAsync(chat);
        ignore.Click += (_, _) =>
        {
            _bridge?.Pairing.Forget(chat.ChannelId, chat.ChatId);
            RefreshPending();
        };
        return card;
    }

    private async Task AllowPendingAsync(PendingChat chat)
    {
        if (_bridge is null)
        {
            return;
        }
        try
        {
            await _bridge.AllowAsync(chat);
            // 授权列表里也要跟着出现一行,否则用户点了保存反而把刚放行的又抹掉了
            if (_rows.Find(r => r.Config.Id == chat.ChannelId) is { } row)
            {
                AppendGrant(row, chat);
            }
            StatusText.Text = _loc.F("PairAllowed", chat.ChatId);
            RefreshPending();
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Allowing chat {chat.ChatKey} failed: {ex}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private void UpdateVisibility()
    {
        BridgeDetailPanel.IsVisible = BridgeEnabledCheck.IsChecked == true;
        McpDetailPanel.IsVisible = McpEnabledCheck.IsChecked == true;
    }

    /// <summary>把接入方式写成可以直接粘走的两段:Claude Code 的命令,以及通用的 mcp.json 片段。</summary>
    private void UpdateCommandSample()
    {
        int port = int.TryParse(McpPortBox.Text, out int parsed) ? parsed : _mcp.Port;
        string url = $"http://127.0.0.1:{port}/mcp";
        // 两段:Claude Code 的一行命令,以及其它客户端通用的 mcp.json 片段。
        // 大括号在插值字符串里要转义,索性用拼接写 —— 这段是要被原样复制走的,不能写错一个字符。
        string json = "{\"mcpServers\":{\"velashell\":{\"type\":\"http\",\"url\":\"" + url
                      + "\",\"headers\":{\"Authorization\":\"Bearer " + _token + "\"}}}}";
        McpCommandBox.Text = $"claude mcp add --transport http velashell {url} --header \"Authorization: Bearer {_token}\""
                             + Environment.NewLine + Environment.NewLine + json;
    }

    private async Task SaveAsync()
    {
        try
        {
            _settings.Enabled = BridgeEnabledCheck.IsChecked == true;
            _settings.Mode = (ChatMode)Math.Max(0, BridgeModeCombo.SelectedIndex);
            _settings.Approval = (ApprovalMode)Math.Max(0, BridgeApprovalCombo.SelectedIndex);
            _settings.AllowModeEscalation = EscalationCheck.IsChecked == true;
            _settings.TurnTimeoutSeconds = ParseInt(TurnTimeoutBox.Text, _settings.TurnTimeoutSeconds, 30, 3600);
            _settings.ApprovalTimeoutSeconds = ParseInt(ApprovalTimeoutBox.Text, _settings.ApprovalTimeoutSeconds, 10, 3600);
            _settings.MaxConcurrentTurns = ParseInt(ConcurrencyBox.Text, _settings.MaxConcurrentTurns, 1, 16);
            // 第 0 项是"跟随聊天面板",落库时写 null
            _settings.ModelId = BridgeModelCombo.SelectedIndex > 0
                ? _models[BridgeModelCombo.SelectedIndex - 1].Id
                : null;

            var channels = new List<ChannelConfig>();
            foreach (ChannelRow row in _rows)
            {
                if (row.Removed)
                {
                    // 渠道删掉了,它那几份机密也别留在库里
                    foreach (string slot in (string[])["secret", "token", "aeskey"])
                    {
                        await _bridgeStore.SetSecretAsync(row.Config.Id, slot, null);
                    }
                    continue;
                }
                ChannelConfig config = row.Config;
                config.Enabled = row.Enabled.IsChecked == true;
                config.DisplayName = (row.Name.Text ?? "").Trim();
                config.AppId = (row.AppId?.Text ?? config.AppId).Trim();
                config.AgentId = (row.AgentId?.Text ?? config.AgentId).Trim();
                config.International = row.International?.IsChecked == true;
                // 授权是"能不能说话 + 能碰哪些机器 + 能做到什么程度"三个轴的那一份;
                // AllowedChats 由 NormalizeGrants 从它派生出来,这里不再单独写。
                config.Grants = [.. row.Grants.Select(g => g.Harvest()).OfType<ChatGrant>()];
                config.NormalizeGrants();
                config.AllowedUsers = Lines(row.Users.Text);
                config.Approvers = Lines(row.Approvers.Text);
                config.DefaultTarget = (row.Target.Text ?? "").Trim();
                if (row.Port is { } portBox)
                {
                    config.WebhookPort = ParseInt(portBox.Text, config.WebhookPort, 1024, 65535);
                }
                if (row.Path is { } pathBox)
                {
                    string value = (pathBox.Text ?? "").Trim();
                    config.WebhookPath = value.StartsWith('/') ? value : "/" + value;
                }
                channels.Add(config);
                await _bridgeStore.SetSecretAsync(config.Id, "secret", row.Secret.Text);
                // 企微那两份机密只有填了才写:框里是空的通常意味着"这次没改",
                // 而不是"把它清掉" —— 加载时也没有把它们回显出来。
                if (row.CallbackToken is { Text.Length: > 0 } tokenBox)
                {
                    await _bridgeStore.SetSecretAsync(config.Id, "token", tokenBox.Text);
                }
                if (row.AesKey is { Text.Length: > 0 } aesBox)
                {
                    await _bridgeStore.SetSecretAsync(config.Id, "aeskey", aesBox.Text);
                }
            }
            _settings.Channels = channels;
            await _bridgeStore.SaveAsync(_settings);

            _mcp.Enabled = McpEnabledCheck.IsChecked == true;
            _mcp.Port = ParseInt(McpPortBox.Text, _mcp.Port, 1024, 65535);
            _mcp.Mode = (ChatMode)Math.Max(0, McpModeCombo.SelectedIndex);
            _mcp.Approval = (ApprovalMode)Math.Max(0, McpApprovalCombo.SelectedIndex);
            _mcp.AllowedTargets = McpTargetsBox.Text ?? "";
            await _mcpStore.SaveAsync(_mcp);

            await _restart();
            StatusText.Text = _loc["Saved"];
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Saving the collaboration settings failed: {ex}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private static List<string> Lines(string? text) =>
    [
        .. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    private static int ParseInt(string? text, int fallback, int min, int max)
        => int.TryParse(text, out int value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>平台名。带上拉丁写法 —— 界面可以切到日/韩,"钉钉"两个字对那边的用户不是可读的品牌名。</summary>
    private static string KindLabel(ChannelKind kind) => kind switch
    {
        ChannelKind.Feishu => "飞书 / Lark",
        ChannelKind.DingTalk => "钉钉 / DingTalk",
        ChannelKind.Telegram => "Telegram",
        _ => "企业微信 / WeCom"
    };

    private static string AppIdLabel(ChannelKind kind) => kind switch
    {
        ChannelKind.Feishu => "App ID",
        ChannelKind.DingTalk => "Client ID (AppKey)",
        _ => "CorpID"
    };

    private static string SecretLabel(ChannelKind kind) => kind switch
    {
        ChannelKind.Feishu => "App Secret",
        ChannelKind.DingTalk => "Client Secret",
        ChannelKind.Telegram => "Bot Token",
        _ => "Secret"
    };
}

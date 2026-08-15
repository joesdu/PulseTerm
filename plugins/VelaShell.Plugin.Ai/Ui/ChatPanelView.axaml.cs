using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSharpMath.Avalonia;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.AI;
using TextMateSharp.Grammars;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// AI 聊天面板:回复以 Markdown 渲染(流式期间节流重渲染,收尾整段定稿),
/// 工具调用为紧凑单行卡片(点击展开参数与结果),Agent 模式下危险操作带审批交互。
/// 会话历史用 Microsoft.Extensions.AI 的 <see cref="ChatMessage" /> 维护,
/// 每轮请求前临时前置 system 消息。
/// </summary>
public partial class ChatPanelView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettingsStore _store;
    private readonly Loc _loc;
    private readonly AgentToolbox _toolbox;
    private readonly McpManager _mcp;
    private readonly List<ChatMessage> _history = [];
    private readonly ChatHistoryStore _historyStore;

    private AiSettings _settings = new();
    private SettingsView? _settingsView;
    private List<AiProviderConfig> _providers = [];
    private List<SessionInfo> _sessions = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    // 当前会话在时序库中的身份:摘要点的时间戳恒为 _conversationStartedAt(覆盖式更新),
    // _persistedCount 既是已入库条数,也是下一条消息的序号。
    private string _conversationId = ChatHistoryStore.NewConversationId();
    private DateTimeOffset _conversationStartedAt = DateTimeOffset.UtcNow;
    private int _persistedCount;
    private bool _switchingView;
    private bool _autoScroll = true;
    private bool _scrollScheduled;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalReasoningTokens;
    // 最近一轮的输入 token ≈ 当前上下文占用(整段对话每轮都重发),用作输入框下方那个占比的分子
    private long _lastInputTokens;
    // 缓存命中:两家的口径实测已被适配器抹平 —— InputTokenCount 都含缓存读取,
    // 于是 Cached/Input 在两边都是同一个意思(见 UpdateUsageText 的注释)
    private long _lastCachedInputTokens;
    private long _totalCachedInputTokens;
    private long _totalCacheWriteTokens;
    private ThemeName _codeBlockTheme = ThemeName.DarkPlus;
    private Color _mathTextColor = Colors.Black;
    private Color _mathErrorColor = Colors.Red;

    /// <summary>由插件构造(UI 线程,经 ShowPanelAsync 工厂)。</summary>
    public ChatPanelView(IPluginContext context, AiSettingsStore store)
    {
        _context = context;
        _store = store;
        _loc = new Loc(context.Host.Locale);
        _historyStore = new ChatHistoryStore(context);
        _toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => SelectedSessionId,
            ApprovalHandler = RequestApprovalAsync
        };
        _mcp = new McpManager(context) { ApprovalHandler = RequestApprovalAsync };
        // 必须在 InitializeComponent 之前:注册 Markdown 扩展(pipeline 只在渲染器构造时建一次),
        // 同时把 LiveMarkdown 各程序集拉进插件 ALC —— XAML 里的 avares://LiveMarkdown.*/ 靠
        // "已加载程序集按名查找"定位,插件依赖不在装载方探测路径上,没被引用过就找不到(勿调序)。
        MarkdownSetup.EnsureRegistered();
        InitializeComponent();
        ConfigureMarkdown();
        ApplyLoc();

        SendButton.Click += (_, _) => _ = SendAsync(InputBox.Text ?? "");
        StopButton.Click += (_, _) => _cts?.Cancel();
        ChatScroll.ScrollChanged += OnChatScrollChanged;
        NewChatButton.Click += (_, _) => StartNewChat();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.TextChanged += (_, _) =>
        {
            InputPlaceholder.IsVisible = InputBox.Document.TextLength == 0;
            OnInputTextChanged();
        };
        SetUpInputEditor();
        FilePopup.PlacementTarget = InputWrap;
        SettingsToggle.IsCheckedChanged += (_, _) => OnViewToggled(SettingsToggle, PanelView.Settings);
        HistoryToggle.IsCheckedChanged += (_, _) =>
        {
            OnViewToggled(HistoryToggle, PanelView.History);
            if (HistoryToggle.IsChecked == true)
            {
                _ = RefreshHistoryListAsync();
            }
        };
        ClearHistoryButton.Click += (_, _) => _ = OnClearHistoryClickedAsync();
        AgentToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.AgentMode = AgentToggle.IsChecked == true;
            AutoApproveCheck.IsVisible = _settings.AgentMode;
            _ = PersistSettingsAsync();
        };
        AutoApproveCheck.IsCheckedChanged += (_, _) =>
        {
            _settings.AutoApproveCommands = AutoApproveCheck.IsChecked == true;
            _ = PersistSettingsAsync();
        };
        ProviderCombo.SelectionChanged += (_, _) =>
        {
            if (ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < _providers.Count)
            {
                _settings.ActiveProviderId = _providers[ProviderCombo.SelectedIndex].Id;
                _ = PersistSettingsAsync();
            }
            // 上下文窗口是按接入配的,换模型就得按新分母重算占比
            UpdateUsageText();
        };

        _context.Events.SessionConnected += OnSessionEvent;
        _context.Events.SessionDisconnected += OnSessionEvent;
        _context.Events.LocaleChanged += OnLocaleChanged;

        _ = InitAsync();
    }

    /// <summary>面板关闭时由插件调用,拆除宿主事件订阅并取消进行中的请求。</summary>
    public void Detach()
    {
        _context.Events.SessionConnected -= OnSessionEvent;
        _context.Events.SessionDisconnected -= OnSessionEvent;
        _context.Events.LocaleChanged -= OnLocaleChanged;
        SetBusyGlow(false); // 计时器不停,面板关了它还在跑
        DisposeSuggestions();
        try
        {
            _cts?.Cancel();
            // 面板级取消源:补全请求之间靠代次判废,只有面板真的关了才在这里取消
            _fileCts?.Cancel();
            _fileCts?.Dispose();
            _fileCts = null;
        }
        catch
        {
            // 已释放则忽略
        }
        _ = _mcp.DisposeAsync().AsTask();
    }

    /// <summary>
    /// 从命令入口外部注入一条消息并直接发送(任意线程可调)。
    /// 注入的内容(如整段终端输出)不当作用户输入:既不进 ↑↓ 历史,
    /// 里面碰巧出现的 <c>@/path</c> 也不会被当成文件引用去读远端。
    /// </summary>
    public void SendExternal(string text) => Dispatcher.UIThread.Post(() => _ = SendAsync(text, fromUser: false));

    // ---------- 初始化与状态 ----------

    private async Task InitAsync()
    {
        try
        {
            _settings = await _store.LoadAsync();
            AgentToggle.IsChecked = _settings.AgentMode;
            AutoApproveCheck.IsChecked = _settings.AutoApproveCommands;
            AutoApproveCheck.IsVisible = _settings.AgentMode;
            _settingsView = new SettingsView(_context, _store, _settings, _loc, OnProvidersChanged);
            SettingsHost.Content = _settingsView;
            ReloadProviderCombo();
            await RefreshSessionsAsync();
            if (_providers.Count == 0)
            {
                StatusText.Text = _loc["NoProvider"];
                SettingsToggle.IsChecked = true;
            }
            // 历史能力可选:时序不可用的宿主上按钮直接禁用,聊天照常
            await _historyStore.InitAsync();
            HistoryToggle.IsEnabled = _historyStore.IsAvailable;
            if (_historyStore.IsAvailable)
            {
                _inputHistory = [.. await _historyStore.RecentUserInputsAsync()];
            }
            ShowStarterSuggestions();
        }
        catch (Exception ex)
        {
            _context.Log.Error("AI panel init failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private void ApplyLoc()
    {
        AgentText.Text = _loc["Agent"];
        ToolTip.SetTip(AgentToggle, _loc["AgentTip"]);
        AutoApproveCheck.Content = _loc["AutoApprove"];
        ToolTip.SetTip(AutoApproveCheck, _loc["AutoApproveTip"]);
        NewChatText.Text = _loc["NewChat"];
        ToolTip.SetTip(NewChatButton, _loc["NewChatTip"]);
        ToolTip.SetTip(SettingsToggle, _loc["Settings"]);
        ToolTip.SetTip(HistoryToggle, _loc["History"]);
        ClearHistoryButton.Content = _loc["ClearHistory"];
        HistoryHeader.Text = _loc["HistoryHeader"];
        InputPlaceholder.Text = _loc["InputPlaceholder"];
        // 发送/停止是图标按钮(工具条宽度紧张,文字标签让给模型名):文案走提示
        ToolTip.SetTip(SendButton, _loc["Send"]);
        ToolTip.SetTip(StopButton, _loc["Stop"]);
        ToolTip.SetTip(ProviderCombo, _loc["Model"]);
        UpdateUsageText();
        // 代码块头部按钮的提示藏在 LiveMarkdown 的 ControlTemplate 里,只能经 DynamicResource 灌进去
        Resources["AiCopyTip"] = _loc["Copy"];
        Resources["AiWrapTip"] = _loc["ToggleWrap"];
    }

    // ---------- Markdown 渲染(LiveMarkdown.Avalonia) ----------

    /// <summary>
    /// 装配 Markdown 渲染:链接点击交给宿主打开、禁掉远程图片抓取、皮肤跟随主题。
    /// 样式与资源的并入在 ChatPanelView.axaml,这里只做选择器表达不了的部分。
    /// </summary>
    private void ConfigureMarkdown()
    {
        // 链接是冒泡路由事件,挂在消息流上就覆盖所有气泡,不必逐段订阅
        MessagesPanel.AddHandler(MarkdownTextBlock.LinkClickEvent, OnMarkdownLinkClicked);

        // 库默认第一个处理器就是 HTTP,模型回复里的图片 URL 会被直接抓取 —— 那等于给
        // 任意模型输出开了个追踪像素通道。这里摘掉 HTTP,只留本地文件 / data: / 内嵌资源。
        Styles.Add(new Style(x => x.OfType<Image>().Class("Link"))
        {
            Setters =
            {
                new Setter(AsyncImageLoader.HandlersProperty, new AsyncImageLoaderHandler[]
                {
                    LocalFileAsyncImageLoaderHandler.Shared,
                    AvaloniaResourceAsyncImageLoaderHandler.Shared,
                    DataUrlAsyncImageLoaderHandler.Shared,
                    RawAsyncImageLoaderHandler.Shared
                })
            }
        });

        SyncMarkdownSkin();
        ActualThemeVariantChanged += (_, _) => SyncMarkdownSkin();
    }

    /// <summary>
    /// 把 LiveMarkdown 的默认令牌换成 Vela 令牌的当前解析值,并按明暗切换代码高亮配色。
    /// 大部分观感能用选择器覆盖(见 axaml),但代码块头/体的底色只在 ControlTemplate 内
    /// 以 <c>{DynamicResource}</c> 引用,选择器够不到,只能改键;库里这些键全部落在画刷属性上,
    /// 所以直接塞画刷即可。写成直接键 —— 直接键优先于 MergedDictionaries 里的 Defaults.axaml。
    /// </summary>
    private void SyncMarkdownSkin()
    {
        // BorderColor 不能挪作背景用:Mermaid 拿它画全部线条(节点框/连线/分组框),
        // 给成背景色调会让整张图糊掉。代码块标题栏底色改由 axaml 的 nth-child 选择器给。
        Skin("BorderColor", "VelaBorderPrimary");              // 线条:表格/分隔线/Mermaid 描边
        Skin("BorderBrush", "VelaBorderPrimary");              // 代码块外框
        Skin("SecondaryCardBackgroundColor", "VelaBgInput");   // 代码块正文底 / Mermaid 分组头
        Skin("CardBackgroundColor", "VelaBgHover");            // 表格底 / Mermaid 节点填充
        Skin("ForegroundColor", "VelaTextPrimary");            // 正文 / Mermaid 图内文字
        Skin("CodeInlineColor", "VelaAccent");
        Skin("QuoteBorderColor", "VelaAccent");

        _mathTextColor = SkinColor("VelaTextPrimary") ?? Colors.Black;
        _mathErrorColor = SkinColor("VelaError") ?? Colors.Red;

        // 这两样不是资源键而是控件属性,已生成的渲染器要逐个改
        ThemeName codeTheme = ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
        _codeBlockTheme = codeTheme;
        foreach (MarkdownRenderer renderer in MessagesPanel.GetVisualDescendants().OfType<MarkdownRenderer>())
        {
            renderer.CodeBlockColorTheme = codeTheme;
            ApplyMathColors(renderer);
        }

        void Skin(string key, string velaKey)
        {
            if (this.TryFindResource(velaKey, out object? value) && value is IBrush brush)
            {
                Resources[key] = brush;
            }
        }

        Color? SkinColor(string velaKey)
            => this.TryFindResource(velaKey, out object? value) && value is ISolidColorBrush brush
                ? brush.Color
                : null;
    }

    /// <summary>
    /// 给这一段里的 LaTeX 公式上色。CSharpMath 的 <c>MathView</c> 吃不到类型选择器
    /// (基类是泛型,StyleKey 对不上;实测连字面色的运行期样式都不生效),而它的
    /// 默认色是黑的 —— 暗色主题下公式等于看不见。只能在每次渲染定稿后就地设。
    /// </summary>
    private void ApplyMathColors(MarkdownRenderer renderer)
    {
        foreach (MathView view in renderer.GetVisualDescendants().OfType<MathView>())
        {
            view.TextColor = _mathTextColor;
            view.ErrorColor = _mathErrorColor;
        }
    }

    /// <summary>Markdown 链接点击:只放行 http/https,交给宿主的默认浏览器打开。</summary>
    private void OnMarkdownLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (e.HRef is { IsAbsoluteUri: true } uri && uri.Scheme is "http" or "https")
        {
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }
        e.Handled = true;
    }

    private void OnLocaleChanged(string locale) => Dispatcher.UIThread.Post(() =>
    {
        _loc.Switch(locale);
        ApplyLoc();
        _settingsView?.ApplyLoc();
        RefreshStarterSuggestions();
    });

    private void OnProvidersChanged()
    {
        ReloadProviderCombo();
        if (_providers.Count > 0 && StatusText.Text == _loc["NoProvider"])
        {
            StatusText.Text = "";
        }
        _ = PersistSettingsAsync();
    }

    private void ReloadProviderCombo()
    {
        _providers = [.. _settings.Providers];
        ProviderCombo.ItemsSource = _providers
            .Select(p => string.IsNullOrWhiteSpace(p.Model) ? p.Name : $"{p.Name} · {p.Model}")
            .ToList();
        int active = _providers.FindIndex(p => p.Id == _settings.ActiveProviderId);
        ProviderCombo.SelectedIndex = active >= 0 ? active : (_providers.Count > 0 ? 0 : -1);
    }

    private AiProviderConfig? ActiveProvider
        => ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < _providers.Count
            ? _providers[ProviderCombo.SelectedIndex]
            : null;

    /// <summary>
    /// 刷新输入框下方那块用量:可见文字只留最要紧的一项(知道上下文窗口就给占比,
    /// 否则给累计的进/出),完整明细压进悬停提示 —— 工具条那点宽度经不起铺开。
    /// </summary>
    private void UpdateUsageText()
    {
        int window = ActiveProvider?.MaxInputTokens ?? 0;
        if (_totalInputTokens == 0 && _totalOutputTokens == 0)
        {
            UsageText.Text = "";
            ToolTip.SetTip(UsageText, _loc["UsageIdle"]);
            return;
        }

        var detail = new StringBuilder();
        var label = new StringBuilder();
        if (window > 0 && _lastInputTokens > 0)
        {
            int percent = (int)Math.Min(100, Math.Round(_lastInputTokens * 100.0 / window));
            label.Append($"{Compact(_lastInputTokens)}/{Compact(window)} · {percent}%");
            detail.AppendLine(_loc.F("UsageContextLine", $"{_lastInputTokens:N0}", $"{window:N0}", percent));
        }
        else
        {
            label.Append($"↑{Compact(_totalInputTokens)} ↓{Compact(_totalOutputTokens)}");
        }

        // 命中率 = 缓存读取 / 输入。两家口径实测已被适配器抹平:OpenAI 的 prompt_tokens 本就含
        // cached_tokens;Anthropic 的 input_tokens 原本不含缓存,但适配器把 cache_read 与
        // cache_creation 都并进了 InputTokenCount(200+800+120=1120)。所以同一个式子两边都成立。
        if (_lastCachedInputTokens > 0 && _lastInputTokens > 0)
        {
            int hit = (int)Math.Min(100, Math.Round(_lastCachedInputTokens * 100.0 / _lastInputTokens));
            label.Append($" · {_loc["CacheShort"]} {hit}%");
            detail.AppendLine(_loc.F("UsageCacheLine", $"{_lastCachedInputTokens:N0}", $"{_lastInputTokens:N0}", hit));
        }

        UsageText.Text = label.ToString();
        detail.AppendLine(_loc.F("UsageTotalsLine", $"{_totalInputTokens:N0}", $"{_totalOutputTokens:N0}"));
        if (_totalCachedInputTokens > 0 || _totalCacheWriteTokens > 0)
        {
            detail.AppendLine(_loc.F("UsageCacheTotalsLine", $"{_totalCachedInputTokens:N0}", $"{_totalCacheWriteTokens:N0}"));
        }
        if (_totalReasoningTokens > 0)
        {
            detail.AppendLine(_loc.F("UsageReasoningLine", $"{_totalReasoningTokens:N0}"));
        }
        if (ActiveProvider is { } provider)
        {
            detail.Append(_loc.F("UsageLimitsLine",
                $"{provider.MaxTokens:N0}",
                window > 0 ? $"{window:N0}" : "—"));
        }
        ToolTip.SetTip(UsageText, detail.ToString().TrimEnd());
    }

    /// <summary>把 token 计数压成 <c>12.3k</c> / <c>1.2M</c> 这种短形式(工具条按字符宽度计价)。</summary>
    private static string Compact(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
        >= 10_000 => $"{value / 1000.0:0}k",
        >= 1_000 => $"{value / 1000.0:0.#}k",
        _ => value.ToString()
    };

    private string? SelectedSessionId
        => _sessions.Count > 0 && SessionCombo.SelectedIndex >= 0 && SessionCombo.SelectedIndex < _sessions.Count
            ? _sessions[SessionCombo.SelectedIndex].SessionId
            : null;

    private void OnSessionEvent(SessionInfo info) => Dispatcher.UIThread.Post(() => _ = RefreshSessionsAsync());

    private async Task RefreshSessionsAsync()
    {
        try
        {
            IReadOnlyList<SessionInfo> all = await _context.Sessions.ListAsync();
            string? previous = SelectedSessionId;
            _sessions = [.. all.Where(s => s.State == SessionState.Connected)];
            SessionCombo.ItemsSource = _sessions.Count == 0
                ? [_loc["NoSession"]]
                : _sessions.Select(s => $"{s.Username}@{s.Host}").ToList();
            int keep = _sessions.FindIndex(s => s.SessionId == previous);
            SessionCombo.SelectedIndex = keep >= 0 ? keep : 0;
        }
        catch (Exception ex)
        {
            _context.Log.Error("Refresh sessions failed.", ex);
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Persist AI settings failed.", ex);
        }
    }

    // ---------- 发送与流式渲染 ----------

    /// <summary>
    /// 输入框按键(隧道阶段,先于 TextBox 自己的处理):
    /// @ 选择弹层开着时键盘归它;否则 ↑↓ 调取历史消息(仅当光标在首/末行,
    /// 多行编辑时的上下移动不受影响),回车发送。
    /// </summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // 已完成的 @ 引用是一整块:退格/删除整块带走(见 HandleReferenceBlockDelete)
        if (HandleReferenceBlockDelete(e))
        {
            return;
        }
        if (FilePopup.IsOpen && HandleFilePickerKey(e))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                _ = SendAsync(InputBox.Text ?? "");
                break;
            case Key.Up when CaretOnFirstLine():
                e.Handled = RecallInput(older: true);
                break;
            case Key.Down when CaretOnLastLine():
                e.Handled = RecallInput(older: false);
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsVisible = !busy;
        StopButton.IsVisible = busy;
        SetBusyGlow(busy);
    }

    /// <summary>
    /// 只有"纯滚动"(内容尺寸没变)才更新粘底意图:流式期间内容增长引起的
    /// 相对位移不算用户上滚,否则粘底会被内容自己的增长误关。
    /// </summary>
    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0)
        {
            return;
        }
        _autoScroll = ChatScroll.Offset.Y + ChatScroll.Viewport.Height >= ChatScroll.Extent.Height - 8;
    }

    /// <summary>
    /// 请求滚到底:同帧内的多次请求合并为一次(Background 优先级,排在布局之后),
    /// 用户主动上滚阅读时不打扰;<paramref name="force" /> 用于用户自己发消息等
    /// 明确要回底部的场合。
    /// </summary>
    private void RequestAutoScroll(bool force = false)
    {
        if (force)
        {
            _autoScroll = true;
        }
        if (!_autoScroll || _scrollScheduled)
        {
            return;
        }
        _scrollScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollScheduled = false;
            if (_autoScroll)
            {
                ChatScroll.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>发送一轮对话:展开引用 → 流式渲染 → 记账与入库。</summary>
    /// <param name="text">消息正文。</param>
    /// <param name="fromUser">
    /// 是否为用户在输入框里键入的内容:只有它才进 ↑↓ 历史、才做 <c>@</c> 文件引用展开。
    /// </param>
    private async Task SendAsync(string text, bool fromUser = true)
    {
        text = text.Trim();
        if (text.Length == 0 || _busy)
        {
            return;
        }
        if (ActiveProvider is not { } provider)
        {
            StatusText.Text = _loc["NoProvider"];
            SettingsToggle.IsChecked = true;
            return;
        }

        SetBusy(true);
        InputBox.Text = "";
        // 状态行只说"这一刻"的事:新一轮开始,上一轮的取消/报错提示就该退场
        StatusText.Text = "";
        ClearSuggestions(); // 上一轮的后续提问已经过期,并掐掉可能还在途的那次求建议
        CloseFilePicker();
        if (fromUser)
        {
            RememberInput(text);
        }
        SetActiveView(PanelView.Chat);

        AddUserBubble(text);
        RequestAutoScroll(force: true);

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        AssistantBubble? bubble = null;
        long startedAt = Environment.TickCount64;
        bool cancelled = false;
        string replyText = "";
        try
        {
            // @ 引用的远端文件在这里展开:气泡里显示的是短名芯片,只有送给模型的那份带完整路径与文件内容。
            (string modelText, IReadOnlyList<string> _, IReadOnlyList<string> unreadable) = fromUser
                ? await ResolveAttachmentsAsync(text, token)
                : (text, [], []);
            if (unreadable.Count > 0)
            {
                AddAttachmentFailureNote(unreadable);
            }
            _history.Add(new ChatMessage(ChatRole.User, modelText));
            await PersistAsync("user", text);
            bubble = new AssistantBubble(this);
            MessagesPanel.Children.Add(bubble.Root);
            RequestAutoScroll(force: true);

            IChatClient client = await _store.CreateClientAsync(provider, cancellationToken: token);
            var options = new ChatOptions { MaxOutputTokens = provider.MaxTokens };
            // 思考档位:Default 表示"不带这个参数",交给服务端的默认行为。两家协议的翻译方式
            // 不同(OpenAI 认 ChatOptions.Reasoning,Anthropic 只认请求体里的 thinking),
            // 差异全收在 AiSettingsStore.ApplyReasoning 里。
            AiSettingsStore.ApplyReasoning(options, provider);
            bool agentMode = _settings.AgentMode;
            if (agentMode)
            {
                _toolbox.AutoApprove = _settings.AutoApproveCommands;
                _mcp.AutoApprove = _settings.AutoApproveCommands;
                IList<AITool> tools = _toolbox.CreateTools();
                if (_settings.McpServers.Any(s => s.Enabled))
                {
                    StatusText.Text = _loc["McpConnecting"];
                    (List<AITool> mcpTools, List<string> mcpErrors) = await _mcp.GetToolsAsync(_settings.McpServers, token);
                    foreach (AITool tool in mcpTools)
                    {
                        tools.Add(tool);
                    }
                    StatusText.Text = mcpErrors.Count > 0
                        ? $"{_loc["Error"]} (MCP): {string.Join("; ", mcpErrors)}"
                        : "";
                }
                options.Tools = tools;
                client = client.AsBuilder()
                    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 25)
                    .Build();
            }

            var requestMessages = new List<ChatMessage>(_history.Count + 1)
            {
                new(ChatRole.System, BuildSystemPrompt(agentMode))
            };
            requestMessages.AddRange(_history);
            // Anthropic 的提示词缓存断点(其它协议不认这个标记,打了也只是多一个被忽略的字段)
            if (provider.Protocol == ChatProtocol.AnthropicMessages && provider.PromptCaching)
            {
                PromptCache.Apply(requestMessages);
            }
            else
            {
                // 关掉之后要把历史上残留的标记抹干净,否则一直挂着(内容对象跨轮复用)
                PromptCache.Clear(_history);
            }

            var updates = new List<ChatResponseUpdate>();
            // 网络流在线程池上消费,增量封送回 UI 线程,避免 SSE 读循环占用 UI。
            // 快流下逐 token Post 会打爆 UI 调度器:增量先入队,队列非空时只挂一次
            // 封送,UI 侧一次批量渲染(渲染本身已各自节流,批量只省调度开销)。
            var pendingUpdates = new List<ChatResponseUpdate>();
            bool drainScheduled = false;
            object pendingSync = new();

            void DrainUpdates()
            {
                ChatResponseUpdate[] batch;
                lock (pendingSync)
                {
                    batch = [.. pendingUpdates];
                    pendingUpdates.Clear();
                    drainScheduled = false;
                }
                foreach (ChatResponseUpdate update in batch)
                {
                    RenderUpdate(bubble, update);
                }
                RequestAutoScroll();
            }

            await Task.Run(async () =>
            {
                await foreach (ChatResponseUpdate update in client
                                   .GetStreamingResponseAsync(requestMessages, options, token)
                                   .ConfigureAwait(false))
                {
                    updates.Add(update);
                    bool schedule;
                    lock (pendingSync)
                    {
                        pendingUpdates.Add(update);
                        schedule = !drainScheduled;
                        drainScheduled = true;
                    }
                    if (schedule)
                    {
                        Dispatcher.UIThread.Post(DrainUpdates);
                    }
                }
            }, token);
            DrainUpdates(); // 兜底清空残留批(此处已回到 UI 线程)

            var response = updates.ToChatResponse();
            _history.AddMessages(response);
            await PersistAsync("assistant", response.Text);
            if (response.Usage is { } usage)
            {
                _lastInputTokens = usage.InputTokenCount ?? _lastInputTokens;
                _totalInputTokens += usage.InputTokenCount ?? 0;
                _totalOutputTokens += usage.OutputTokenCount ?? 0;
                _totalReasoningTokens += usage.ReasoningTokenCount ?? 0;
                _lastCachedInputTokens = usage.CachedInputTokenCount ?? 0;
                _totalCachedInputTokens += _lastCachedInputTokens;
                // 缓存"写入"只有 Anthropic 报(它单独收费),OpenAI 系没有这个概念
                _totalCacheWriteTokens += usage.AdditionalCounts?.GetValueOrDefault("CacheCreationInputTokens") ?? 0;
            }
            UpdateUsageText();
            replyText = response.Text;
        }
        catch (OperationCanceledException)
        {
            // 取消是"这条回复"的属性,记在气泡头部;状态行不留话(留了就一直挂着,见截图反馈)
            cancelled = true;
        }
        catch (Exception ex)
        {
            _context.Log.Error("AI request failed.", ex);
            bubble?.AppendText($"\n\n**[{_loc["Error"]}]** {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            // 一轮到此为止(成功/取消/出错都算):收起思考区,补上"耗时 · 步数"与"时间 · 模型"
            bubble?.FinishStreaming(
                ModelLabel(provider),
                DateTimeOffset.Now,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt),
                cancelled);
            RequestAutoScroll();
        }

        // 顺利答完才给后续提问:取消/报错时用户要的是重试,不是被塞几条建议
        if (!cancelled && replyText.Length > 0)
        {
            await SuggestFollowUpsAsync(provider, text, replyText);
        }
    }

    /// <summary>回复底部显示的模型名:优先模型 id,没填就退回接入名称。</summary>
    private static string ModelLabel(AiProviderConfig provider)
        => string.IsNullOrWhiteSpace(provider.Model) ? provider.Name : provider.Model;

    /// <summary>用量归零(换会话时用:计数是"这一段对话"的,不是进程级的)。</summary>
    private void ResetUsage()
    {
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        _totalReasoningTokens = 0;
        _lastInputTokens = 0;
        _lastCachedInputTokens = 0;
        _totalCachedInputTokens = 0;
        _totalCacheWriteTokens = 0;
        UpdateUsageText();
    }

    /// <summary>把一条消息写进历史(时序库);能力不可用或空文本时什么也不做。</summary>
    private async Task PersistAsync(string role, string text)
    {
        if (!_historyStore.IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        await _historyStore.AppendAsync(_conversationId, _conversationStartedAt, _persistedCount++, role, text);
    }

    private void RenderUpdate(AssistantBubble bubble, ChatResponseUpdate update)
    {
        // 这一帧什么都没解析出来 → 可能是"OpenAI 兼容"线上某家自造的思考字段,
        // 去原始报文里翻一遍(见 ReasoningPeek)。正常帧不会走到这儿。
        if (ReasoningPeek.IsBlank(update)
            && ReasoningPeek.TryRead(update.RawRepresentation, out string peeked))
        {
            bubble.AppendThinking(peeked);
            return;
        }
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    bubble.AppendThinking(reasoning.Text);
                    break;
                case TextContent textContent:
                    bubble.AppendText(textContent.Text);
                    break;
                case FunctionCallContent call:
                    bubble.AddToolCall(call.CallId, call.Name, SerializeArguments(call.Arguments));
                    break;
                case FunctionResultContent result:
                    bubble.CompleteToolCall(result.CallId, result.Result?.ToString() ?? "");
                    break;
                case ErrorContent error:
                    StatusText.Text = $"{_loc["Error"]}: {error.Message}";
                    break;
            }
        }
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }
        try
        {
            return JsonSerializer.Serialize(arguments);
        }
        catch
        {
            return string.Join(", ", arguments.Keys);
        }
    }

    private string BuildSystemPrompt(bool agentMode)
    {
        if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
        {
            return _settings.SystemPrompt;
        }
        string prompt =
            "You are the AI assistant embedded in VelaShell, an SSH terminal application. " +
            "Help the user with servers, shell commands, log analysis and DevOps questions. Be concise and practical. " +
            "Format responses in Markdown. " +
            $"Respond in the user's language (UI locale: {_context.Host.Locale}).";
        if (agentMode)
        {
            prompt +=
                " You can call tools to inspect the user's selected SSH session (read terminal output, run one-shot commands, list directories, read files) " +
                "and to edit remote files (write_remote_file overwrites the whole file — read it first, then send the complete new content). " +
                "Prefer read-only commands; destructive commands and file writes require user approval and should be proposed carefully. " +
                "The user can attach remote files to a message with @path; their content is included verbatim after the message. " +
                "Additional tools may come from user-configured MCP servers (their names are prefixed with the server name).";
        }
        return prompt;
    }

    /// <summary>
    /// 新建会话:终止进行中的请求,清空消息流并换一个会话 id ——
    /// 已发生的对话此刻已在时序库里,可从历史里翻回来。
    /// </summary>
    private void StartNewChat()
    {
        _cts?.Cancel();
        _history.Clear();
        MessagesPanel.Children.Clear();
        ResetUsage();
        _conversationId = ChatHistoryStore.NewConversationId();
        _conversationStartedAt = DateTimeOffset.UtcNow;
        _persistedCount = 0;
        _inputHistoryIndex = -1;
        StatusText.Text = "";
        ClearSuggestions();
        ShowStarterSuggestions();
        SetActiveView(PanelView.Chat);
        InputBox.TextArea.Focus();
    }

    // ---------- 审批交互 ----------

    private async Task<bool> RequestApprovalAsync(string summary)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => AddApprovalCard(summary, tcs));
        return await tcs.Task.ConfigureAwait(false);
    }

    private void AddApprovalCard(string summary, TaskCompletionSource<bool> tcs)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc["ApprovalTitle"] });
        stack.Children.Add(new SelectableTextBlock { Classes = { "mono" }, Text = Truncate(summary, 600) });
        // 主/次操作对齐宿主按钮方案:批准 = 强调药丸,拒绝 = 描边换危险色
        var approveButton = new Button { Content = _loc["Approve"], Height = 24, Padding = new Thickness(12, 0) };
        var denyButton = new Button { Content = _loc["Deny"], Height = 24, Padding = new Thickness(12, 0) };
        ApplyThemeResource(approveButton, "VelaAccentPillButtonTheme");
        ApplyThemeResource(denyButton, "VelaOutlineButtonTheme");
        if (FindBrush("VelaError") is { } error)
        {
            denyButton.Foreground = error;
            denyButton.BorderBrush = error;
        }
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        buttons.Children.Add(approveButton);
        buttons.Children.Add(denyButton);
        stack.Children.Add(buttons);
        var card = new Border { Classes = { "toolCard" }, Child = stack };

        void Finish(bool approved)
        {
            approveButton.IsEnabled = false;
            denyButton.IsEnabled = false;
            stack.Children.Add(new TextBlock { Classes = { "dim" }, Text = approved ? _loc["Approve"] + " ✓" : _loc["Deny"] + " ✕" });
            tcs.TrySetResult(approved);
        }

        approveButton.Click += (_, _) => Finish(true);
        denyButton.Click += (_, _) => Finish(false);
        MessagesPanel.Children.Add(card);
        // 审批卡阻塞整轮对话,必须让用户看见
        RequestAutoScroll(force: true);
    }

    // ---------- 气泡构建 ----------

    /// <summary>
    /// 用户气泡:与 VSCode 里 GitHub Copilot 的提问块一致 —— 引用到的文件先以芯片列出,
    /// 正文按 Markdown 渲染(与助手气泡同一套渲染器)。
    /// </summary>
    /// <remarks>
    /// 两处刻意与输入框保持一致:①正文里的 <c>@</c> 引用显示成短名(<c>@abc.txt</c>),
    /// 不再把 <c>@/root/abc.txt</c> 这种长路径原样铺出来 —— 用户在输入框里看到的就是短名,
    /// 发出去后不该突然变回长路径;②用户自己写的 Markdown 该渲染出来,而不是显示源码。
    /// 送给模型的那份文本不受影响(仍是带完整路径的原文,见 ResolveAttachmentsAsync)。
    /// </remarks>
    private void AddUserBubble(string text)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Classes = { "roleHeader" }, Text = _loc["You"] });

        List<string> references = FileReference.Parse(text);
        if (references.Count > 0)
        {
            var chips = new WrapPanel { Margin = new(0, 0, 0, 4) };
            foreach (string path in references)
            {
                chips.Children.Add(BuildReferenceChip(path));
            }
            stack.Children.Add(chips);
        }

        var body = new MarkdownSegment(this);
        body.Append(FileReference.Shorten(text));
        stack.Children.Add(body.Host);
        MessagesPanel.Children.Add(new Border { Classes = { "msg", "userMsg" }, Child = stack });
    }

    /// <summary>一枚文件引用芯片(与输入框里那段彩色引用同色同名,悬停给全路径)。</summary>
    private Border BuildReferenceChip(string path)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(MakeIcon("Icon.file", "VelaAccent", 10));
        row.Children.Add(new TextBlock
        {
            Classes = { "refChipText" },
            Text = FileReference.DisplayName(path),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var chip = new Border { Classes = { "refChip" }, Child = row };
        ToolTip.SetTip(chip, path);
        return chip;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>套用宿主 ControlTheme(进程内可查;隔离进程缺失时保持默认外观)。</summary>
    private void ApplyThemeResource(Avalonia.Controls.Primitives.TemplatedControl control, string themeKey)
    {
        if (this.TryFindResource(themeKey, out object? value) && value is Avalonia.Styling.ControlTheme theme)
        {
            control.Theme = theme;
        }
    }

    private Geometry? FindIcon(string key)
        => this.TryFindResource(key, out object? value) && value is Geometry geometry ? geometry : null;

    private IBrush? FindBrush(string key)
        => this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : null;

    /// <summary>lucide 描边图标(24 视图框经 Viewbox 等比缩放)。</summary>
    private Viewbox MakeIcon(string geometryKey, string brushKey, double size)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            Data = FindIcon(geometryKey),
            Stroke = FindBrush(brushKey) ?? Brushes.Gray,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    /// <summary>
    /// 一条 assistant 回复的可视化容器:头部(角色 · 步数 · 耗时)、思考折叠区、
    /// Markdown 段落与工具卡片,末尾是"复制整段 | 时间 · 模型"的元信息条。
    /// 版式对齐 GitHub Copilot 的回复块。
    /// </summary>
    private sealed class AssistantBubble
    {
        private readonly ChatPanelView _owner;
        private readonly StackPanel _stack;
        private readonly TextBlock _header;
        private readonly Dictionary<string, ToolCard> _toolCards = [];
        private readonly StringBuilder _thinkingText = new();
        // 复制整段回复用的原文:正文按到达顺序原样攒着(Markdown 源码,不是渲染后的可视树)
        private readonly StringBuilder _replyText = new();
        private MarkdownSegment? _currentSegment;
        private Collapsible? _thinking;
        private bool _thinkingRenderScheduled;
        private long _thinkingStartedAt;
        private TimeSpan? _thinkingElapsed;
        private int _steps;

        public Border Root { get; }

        public AssistantBubble(ChatPanelView owner)
        {
            _owner = owner;
            _stack = new StackPanel { Spacing = 4 };
            _header = new TextBlock { Classes = { "roleHeader" }, Text = owner._loc["AssistantRole"] };
            _stack.Children.Add(_header);
            Root = new Border { Classes = { "msg" }, Child = _stack };
        }

        public void AppendText(string text)
        {
            if (text.Length == 0)
            {
                return;
            }
            // 正文开始 = 思考结束,但<b>不在这里收起来</b>:
            // 思考往往刚吐完正文就跟上,立刻折叠等于让人根本没机会读(用户反馈的
            // "思考和正文一起冒出来"就是这么来的)。留到整轮结束再收。
            MarkThinkingDone();
            _replyText.Append(text);
            // 工具卡片之后的新一轮文本另起 Markdown 段
            if (_currentSegment is null || !ReferenceEquals(_stack.Children[^1], _currentSegment.Host))
            {
                _currentSegment = new MarkdownSegment(_owner);
                _stack.Children.Add(_currentSegment.Host);
            }
            _currentSegment.Append(text);
        }

        public void AppendThinking(string text)
        {
            if (text.Length == 0)
            {
                return;
            }
            _thinkingText.Append(text);
            if (_thinking is null)
            {
                // 默认收起(用户决策):标题一行就够说明"在想",要看内容自己点开。
                // 展开过就一路留着,标题从"正在思考…"变成"已思考 N 秒",正文照样往里灌。
                _thinkingStartedAt = Environment.TickCount64;
                _thinking = new Collapsible(_owner, _owner._loc["ThinkingActive"]);
                _stack.Children.Insert(1, _thinking.Root);
            }
            // 逐 token 全量刷文本是 O(n²) 的字符串与排版开销,所以节流;但别压太狠 ——
            // 思考经常只有一两秒,200ms 一刷会让人觉得"根本没在流"。
            if (!_thinkingRenderScheduled)
            {
                _thinkingRenderScheduled = true;
                DispatcherTimer.RunOnce(() =>
                {
                    _thinkingRenderScheduled = false;
                    _thinking?.SetBody(_thinkingText.ToString());
                }, TimeSpan.FromMilliseconds(80));
            }
        }

        public void AddToolCall(string callId, string name, string argumentsJson)
        {
            if (_toolCards.ContainsKey(callId))
            {
                return;
            }
            MarkThinkingDone();
            _steps++;
            var card = new ToolCard(_owner, name, argumentsJson);
            _toolCards[callId] = card;
            _stack.Children.Add(card.Root);
            _currentSegment = null;
        }

        public void CompleteToolCall(string callId, string result)
        {
            if (_toolCards.TryGetValue(callId, out ToolCard? card))
            {
                card.Complete(result);
            }
            _currentSegment = null;
        }

        /// <summary>
        /// 流结束(成功/取消/出错都会走到):思考区补齐尾部并收起,头部补上步数与耗时,
        /// 末尾挂出"复制整段 | 时间 · 模型"。
        /// </summary>
        /// <param name="modelLabel">调用的模型名;历史回放时没有,那就只显示时间。</param>
        /// <param name="at">这条回复的时刻(本地时区)。</param>
        /// <param name="elapsed">整轮耗时;为 null 时头部不显示耗时。</param>
        /// <param name="cancelled">这一轮是被用户按停的 —— 在头部标出来,而不是在状态行留一句话。</param>
        public void FinishStreaming(string? modelLabel = null, DateTimeOffset? at = null, TimeSpan? elapsed = null,
            bool cancelled = false)
        {
            // Markdown 段不需要收口:LiveMarkdown 自己会把最后一次追加渲染完
            _thinking?.SetBody(_thinkingText.ToString());
            MarkThinkingDone();
            // 默认本来就是收起的;这里不再强制折叠 —— 用户中途点开了就让它开着,
            // 别在他正读的时候把内容抽走。

            var head = new StringBuilder(_owner._loc["AssistantRole"]);
            if (_steps > 0)
            {
                head.Append(" · ").Append(_owner._loc.F("Steps", _steps));
            }
            if (elapsed is { } span)
            {
                head.Append(" · ").Append(FormatDuration(span));
            }
            if (cancelled)
            {
                head.Append(" · ").Append(_owner._loc["Cancelled"]);
            }
            _header.Text = head.ToString();

            if (at is not null || modelLabel is not null)
            {
                _stack.Children.Add(BuildFooter(modelLabel, at));
            }
        }

        /// <summary>
        /// 思考停了(正文或工具调用开始):定格耗时,标题从"正在思考…"换成"已思考 N 秒"。
        /// <b>不收起</b> —— 收起是整轮结束时的事,见 <see cref="FinishStreaming" />。
        /// </summary>
        private void MarkThinkingDone()
        {
            if (_thinking is null || _thinkingElapsed is not null)
            {
                return;
            }
            _thinkingElapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _thinkingStartedAt);
            _thinking.SetTitle(_owner._loc.F("ThinkingDone", FormatDuration(_thinkingElapsed.Value)));
        }

        /// <summary>底部元信息条:左边"复制整段回复",右边"时间 · 模型"(不显示积分/评价)。</summary>
        private Border BuildFooter(string? modelLabel, DateTimeOffset? at)
        {
            var grid = new Grid { ColumnDefinitions = [with("Auto,*,Auto")] };

            var copyIcon = new Decorator { Child = _owner.MakeIcon("Icon.copy", "VelaTextMuted", 12) };
            var copy = new Button { Content = copyIcon };
            _owner.ApplyThemeResource(copy, "AiGhostIconButtonTheme");
            ToolTip.SetTip(copy, _owner._loc["CopyReply"]);
            copy.Click += (_, _) => _owner.CopyToClipboard(_replyText.ToString(), copy, copyIcon);
            grid.Children.Add(copy);

            var meta = new TextBlock { Classes = { "meta" } };
            var parts = new List<string>(2);
            if (at is { } stamp)
            {
                parts.Add(stamp.ToString("HH:mm"));
            }
            if (!string.IsNullOrWhiteSpace(modelLabel))
            {
                parts.Add(modelLabel);
            }
            meta.Text = string.Join(" · ", parts);
            ToolTip.SetTip(meta, meta.Text);
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            return new Border { Classes = { "replyFooter" }, Child = grid };
        }
    }

    /// <summary>
    /// 把文本送进剪贴板,并让触发按钮短暂变成对勾 —— 复制这种"看不见结果"的动作
    /// 必须给一次确认反馈,否则用户只能再点一次试试。
    /// </summary>
    /// <remarks>走宿主的剪贴板能力而不是 <c>TopLevel.Clipboard</c>:隔离进程里没有窗口。</remarks>
    private void CopyToClipboard(string text, Control button, Decorator iconHost)
    {
        if (text.Length == 0)
        {
            return;
        }
        _ = _context.Clipboard.SetTextAsync(text);
        iconHost.Child = MakeIcon("Icon.circle-check", "VelaStatusConnected", 12);
        ToolTip.SetTip(button, _loc["Copied"]);
        DispatcherTimer.RunOnce(() =>
        {
            iconHost.Child = MakeIcon("Icon.copy", "VelaTextMuted", 12);
            ToolTip.SetTip(button, _loc["CopyReply"]);
        }, TimeSpan.FromSeconds(1.6));
    }

    /// <summary>把一段时长写成人读的短形式(<c>0.8s</c> / <c>12.3s</c> / <c>1m 5s</c>)。</summary>
    private static string FormatDuration(TimeSpan span) => span.TotalSeconds switch
    {
        < 60 => $"{span.TotalSeconds:0.#}s",
        _ => $"{(int)span.TotalMinutes}m {span.Seconds}s"
    };

    /// <summary>
    /// 一段流式 Markdown。追加直接进 <see cref="ObservableStringBuilder" />,由
    /// LiveMarkdown 合并变更、后台解析、按脏节点更新可视树 —— 这里不再需要自建节流,
    /// 半截文本也由它自己兜住。约束:builder 只能在 UI 线程改(调用方经 DrainUpdates 保证)。
    /// </summary>
    private sealed class MarkdownSegment
    {
        private readonly ObservableStringBuilder _text = new();

        public MarkdownRenderer Host { get; }

        public MarkdownSegment(ChatPanelView owner)
        {
            Host = new MarkdownRenderer
            {
                MarkdownBuilder = _text,
                CodeBlockColorTheme = owner._codeBlockTheme
            };
            // 公式控件是渲染时新建的,每次定稿后补一次颜色(见 ApplyMathColors)
            Host.RenderedTextProjectionChanged += (_, _) => owner.ApplyMathColors(Host);
        }

        public void Append(string text) => _text.Append(text);
    }

    /// <summary>紧凑可折叠区(思考过程):单行头部点击展开,正文等宽文本。</summary>
    private sealed class Collapsible
    {
        private readonly SelectableTextBlock _body;
        private readonly TextBlock _title;
        private readonly Avalonia.Controls.Shapes.Path _chevron;
        private readonly ScrollViewer _details;
        private bool _userToggled;
        private bool _scrollScheduled;

        public Border Root { get; }

        /// <param name="owner">宿主面板(取图标与令牌)。</param>
        /// <param name="title">头部文案。</param>
        /// <param name="expanded">初始是否展开。</param>
        public Collapsible(ChatPanelView owner, string title, bool expanded = false)
        {
            var header = new Grid
            {
                ColumnDefinitions = [with("Auto,Auto,*")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                Data = owner.FindIcon("Icon.chevron-right"),
                Stroke = owner.FindBrush("VelaTextMuted") ?? Brushes.Gray,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            var chevronBox = new Viewbox { Width = 10, Height = 10, Child = _chevron, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            header.Children.Add(chevronBox);
            _title = new TextBlock { Classes = { "dim" }, Text = title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(_title, 1);
            header.Children.Add(_title);

            _body = new SelectableTextBlock { Classes = { "mono" } };
            _details = new ScrollViewer
            {
                MaxHeight = 200,
                Content = _body,
                Margin = new Thickness(15, 4, 0, 0)
            };
            header.PointerPressed += (_, _) =>
            {
                // 用户自己动过之后就别再替他开合了(整轮结束时的自动收起也不做)
                _userToggled = true;
                Apply(!_details.IsVisible);
            };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_details);
            Root = new Border { Classes = { "toolCard" }, Child = stack };
            Apply(expanded);
        }

        /// <summary>
        /// 换正文并把可视区顶到最新一行。<b>这个滚动是必须的</b>:思考区高度封顶 200,
        /// 一旦内容超出,不滚动的话看到的永远是最开头那几行 —— 明明在流,看上去像卡住了。
        /// 滚动排在布局之后(Background 优先级),否则此刻 Extent 还是旧的,滚不到底。
        /// </summary>
        public void SetBody(string text)
        {
            _body.Text = text;
            if (!_details.IsVisible || _scrollScheduled)
            {
                return;
            }
            _scrollScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollScheduled = false;
                if (_details.IsVisible)
                {
                    _details.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>换头部文案(思考结束后从"正在思考…"变成"已思考 N 秒")。</summary>
        public void SetTitle(string title) => _title.Text = title;

        /// <summary>代码驱动的展开/收起;用户自己动过之后就不再插手他的选择。</summary>
        public void SetExpanded(bool expanded)
        {
            if (!_userToggled)
            {
                Apply(expanded);
            }
        }

        private void Apply(bool expanded)
        {
            _details.IsVisible = expanded;
            _chevron.RenderTransform = expanded ? new RotateTransform(90) : null;
        }
    }

    /// <summary>
    /// 一次工具调用的紧凑卡片(主流 Agent 风格):
    /// 单行 = 状态图标 + 工具名 + 参数摘要 + 箭头;点击展开完整参数与结果。
    /// </summary>
    private sealed class ToolCard
    {
        private readonly ChatPanelView _owner;
        private readonly Decorator _statusIconHost;
        private readonly SelectableTextBlock _detailsText;
        private readonly ScrollViewer _details;
        private readonly Avalonia.Controls.Shapes.Path _chevron;
        private readonly string _argumentsJson;
        private string _result = "";

        public Border Root { get; }

        public ToolCard(ChatPanelView owner, string name, string argumentsJson)
        {
            _owner = owner;
            _argumentsJson = argumentsJson;

            var header = new Grid
            {
                ColumnDefinitions = [with("Auto,Auto,*,Auto")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _statusIconHost = new Decorator
            {
                Child = owner.MakeIcon("Icon.ellipsis", "VelaAccent", 11),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            ToolTip.SetTip(_statusIconHost, owner._loc["ToolRunning"]);
            header.Children.Add(_statusIconHost);

            var nameText = new TextBlock { Classes = { "toolName" }, Text = name };
            Grid.SetColumn(nameText, 1);
            header.Children.Add(nameText);

            var argsText = new TextBlock
            {
                Classes = { "toolArgs" },
                Text = OneLine(argumentsJson, 160),
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(argsText, 2);
            header.Children.Add(argsText);

            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                Data = owner.FindIcon("Icon.chevron-right"),
                Stroke = owner.FindBrush("VelaTextMuted") ?? Brushes.Gray,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            var chevronBox = new Viewbox { Width = 10, Height = 10, Child = _chevron, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(chevronBox, 3);
            header.Children.Add(chevronBox);

            _detailsText = new SelectableTextBlock { Classes = { "mono" } };
            _details = new ScrollViewer
            {
                MaxHeight = 220,
                Content = _detailsText,
                IsVisible = false,
                Margin = new Thickness(17, 4, 0, 0)
            };
            header.PointerPressed += (_, _) => Toggle();

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_details);
            Root = new Border { Classes = { "toolCard" }, Child = stack };
        }

        public void Complete(string result)
        {
            _result = result;
            _statusIconHost.Child = _owner.MakeIcon("Icon.circle-check", "VelaStatusConnected", 11);
            ToolTip.SetTip(_statusIconHost, _owner._loc["ToolDone"]);
        }

        private void Toggle()
        {
            if (!_details.IsVisible)
            {
                var sb = new StringBuilder();
                sb.Append(_argumentsJson);
                if (_result.Length > 0)
                {
                    sb.Append("\n────────\n");
                    sb.Append(Truncate(_result, 4000));
                }
                _detailsText.Text = sb.ToString();
            }
            _details.IsVisible = !_details.IsVisible;
            _chevron.RenderTransform = _details.IsVisible ? new RotateTransform(90) : null;
        }

        private static string OneLine(string text, int max)
        {
            string flat = text.Replace('\n', ' ').Replace('\r', ' ');
            return flat.Length <= max ? flat : flat[..max] + "…";
        }
    }
}

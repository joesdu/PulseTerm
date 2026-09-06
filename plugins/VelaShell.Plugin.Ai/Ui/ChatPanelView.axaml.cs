using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSharpMath.Avalonia;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.AI;
using TextMateSharp.Grammars;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Agent.Web;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>顶栏会话下拉的一项:主机标签 + 连接状态点(<c>SessionCombo</c> 的 ItemTemplate 用)。</summary>
/// <param name="Text">显示文字(<c>用户@主机</c>)。</param>
/// <param name="Dot">状态点的颜色。画笔在构造项时就解析好,免得为它写一个 bool→Brush 的转换器。</param>
public sealed record SessionNavItem(string Text, IBrush Dot);

/// <summary>
/// 审批方式下拉的一项(<c>ApprovalCombo</c> 的 ItemTemplate 用)。
/// 形状与颜色都挂在数据项上:用户把强调色调成偏红时,"全部自动"的 error 色会和强调色撞在一起,
/// 光靠颜色分不出挡位 —— 盾牌的三种形状(打勾 / 感叹号 / 划掉)才是那个分得开的信号。
/// </summary>
/// <param name="Text">挡位文案。</param>
/// <param name="Icon">盾牌几何。</param>
/// <param name="Tone">这一档的语义色(中性 / warn / error)。</param>
public sealed record ApprovalNavItem(string Text, Geometry? Icon, IBrush? Tone);

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
    private readonly ChatHistoryStore _historyStore;

    // ---------- 按会话分桶的对话 ----------
    // 每台机器(顶栏下拉选中的会话;""=不绑机器的通用对话)各持一份 Conversation。
    // 切下拉 = 换 _active 的引用并把它的面板挂进 ChatScroll;各自的请求在后台独立跑完。
    private readonly Dictionary<string, Conversation> _conversations = [with(StringComparer.Ordinal)];

    /// <summary>当前正显示的这一份(顶栏共享控件——状态行/用量/忙态——反映的都是它)。</summary>
    private Conversation _active = null!;

    /// <summary>
    /// "这一轮属于哪一份"。一轮开始时置为它所属的 Conversation,那之后被 await 串起来的
    /// 整条流水线都读它 —— 于是后台轮次写自己那一份,而不是"当前正显示的那一份"。
    /// <see cref="AsyncLocal{T}" /> 按异步流各自独立,并发的多轮不会互相串。
    /// </summary>
    private readonly AsyncLocal<Conversation?> _turnScope = new();

    /// <summary>本次读写落到哪一份:在某一轮里 = 那一轮的;否则 = 正显示的那一份。</summary>
    private Conversation Cur => _turnScope.Value ?? _active;

    /// <summary>当前上下文是否就是正显示的那一份(后台轮次据此跳过一切对共享控件的改动)。</summary>
    private bool IsForeground => _turnScope.Value is null || ReferenceEquals(_turnScope.Value, _active);

    /// <summary>正在重建会话下拉(别把代码回填选中项当成用户在切对话)。</summary>
    private bool _switchingSessions;

    // 下面这些以前是散落各处的实例字段,现在一律代理到 Cur(=某一轮的那一份 / 正显示的那一份)。
    // 好处是原来一百多处 `_history`/`_cts`/... 的写法一个字都不用改,后台并行却能落到对的那一份上。
    private List<ChatMessage> History => Cur.History;
    private AssistantBubble? ActiveBubble { get => Cur.ActiveBubble; set => Cur.ActiveBubble = value; }
    private CancellationTokenSource? Cts { get => Cur.Cts; set => Cur.Cts = value; }
    private bool Busy { get => Cur.Busy; set => Cur.Busy = value; }
    private int TurnHistoryStart { get => Cur.TurnHistoryStart; set => Cur.TurnHistoryStart = value; }
    private string ConversationId { get => Cur.ConversationId; set => Cur.ConversationId = value; }
    private DateTimeOffset ConversationStartedAt { get => Cur.ConversationStartedAt; set => Cur.ConversationStartedAt = value; }
    private int PersistedCount { get => Cur.PersistedCount; set => Cur.PersistedCount = value; }
    private int DroppedFromContext { get => Cur.DroppedFromContext; set => Cur.DroppedFromContext = value; }
    private int SequenceHighWater { get => Cur.SequenceHighWater; set => Cur.SequenceHighWater = value; }
    private long TotalInputTokens { get => Cur.TotalInputTokens; set => Cur.TotalInputTokens = value; }
    private long TotalOutputTokens { get => Cur.TotalOutputTokens; set => Cur.TotalOutputTokens = value; }
    private long TotalReasoningTokens { get => Cur.TotalReasoningTokens; set => Cur.TotalReasoningTokens = value; }
    private long LastInputTokens { get => Cur.LastInputTokens; set => Cur.LastInputTokens = value; }
    private long LastCachedInputTokens { get => Cur.LastCachedInputTokens; set => Cur.LastCachedInputTokens = value; }
    private long TotalCachedInputTokens { get => Cur.TotalCachedInputTokens; set => Cur.TotalCachedInputTokens = value; }
    private long TotalCacheWriteTokens { get => Cur.TotalCacheWriteTokens; set => Cur.TotalCacheWriteTokens = value; }
    private bool ThinkingHintShown { get => Cur.ThinkingHintShown; set => Cur.ThinkingHintShown = value; }
    private SteeringQueue SteeringQueue => Cur.SteeringQueue;
    private SteeringChatClient? Steering { get => Cur.Steering; set => Cur.Steering = value; }
    private int SteeringCommitted { get => Cur.SteeringCommitted; set => Cur.SteeringCommitted = value; }
    private string ContextSummary { get => Cur.ContextSummary; set => Cur.ContextSummary = value; }
    private int SummarizedThrough { get => Cur.SummarizedThrough; set => Cur.SummarizedThrough = value; }
    private HashSet<string> AlwaysApproved => Cur.AlwaysApproved;
    private List<Control> CollapsedMessages => Cur.CollapsedMessages;
    private Border? CollapsedBanner { get => Cur.CollapsedBanner; set => Cur.CollapsedBanner = value; }
    private Dictionary<Control, int> UserBubbleIndex => Cur.UserBubbleIndex;

    /// <summary>正显示对话的消息流面板(命令式建气泡都往这挂;后台轮次经 <see cref="Cur" /> 落到自己那条)。</summary>
    private StackPanel MessagesPanel => Cur.Messages;

    private AiSettings _settings = new();
    /// <summary>当前该不该显示空状态(还要再与"中部正显示聊天流"取与,见 <see cref="SetActiveView" />)。</summary>
    private bool _showEmptyState;
    /// <summary>已摆出来的是哪一版空状态(true = 引导去配模型那版);null = 还没摆。</summary>
    private bool? _emptyStateNeedsProvider;
    private SettingsView? _settingsView;
    private GlobalSettingsView? _globalSettingsView;
    private List<ResolvedModel> _providers = [];

    /// <summary>
    /// 输入框旁边那个下拉设的临时思考档位;<c>null</c> = 就用模型自己配的那个。
    /// </summary>
    /// <remarks>
    /// <b>不落盘,也不写回模型配置</b>:"这一问要不要多想想"是逐条变的,
    /// 而设置页里那个值代表的是这个模型平常怎么用。换模型时清掉(见 ProviderCombo 的处理)。
    /// </remarks>
    private ReasoningLevel? _reasoningOverride;

    /// <summary>正在由代码回填下拉选中项(别把这一下当成用户在选)。</summary>
    private bool _syncingReasoning;
    private List<SessionInfo> _sessions = [];
    private bool _switchingView;
    private bool _autoScroll = true;
    private bool _scrollScheduled;
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
        // 至少要有一份对话在手(不绑机器的通用对话):任何代理到 Cur 的读写都得有落点。
        // 连上机器后顶栏下拉一选,就切到对应机器那一份(见 SwitchConversation)。
        _active = NewConversation("");
        _conversations[""] = _active;
        _toolbox = new AgentToolbox(context)
        {
            // 工具打在<b>这一轮所属对话</b>绑的那台机器上,而不是此刻下拉选中的那台 ——
            // 否则后台跑着的 A 轮会把命令打到你刚切过去正看的 B 上。工具调用发生在本轮的异步流里,
            // Cur 经 _turnScope 落到 A,取到的正是 A 绑的会话。
            SessionIdProvider = CurrentToolSessionId,
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
        StopButton.Click += (_, _) => Cts?.Cancel();
        ChatScroll.ScrollChanged += OnChatScrollChanged;
        // 跳到末尾:点一下回到最新一条,并恢复"粘底"(此后新消息继续跟着走)。
        // 先乐观隐藏,真正滚到底后 ScrollChanged 会再确认一次。
        JumpToBottomButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            JumpToBottomButton.IsVisible = false;
            RequestAutoScroll(force: true);
        };
        // 消息流的实际容器是"当前这一份对话"的面板,运行时挂上;切对话时整体替换(见 SwitchConversation)。
        ChatScroll.Content = _active.Messages;
        NewChatButton.Click += (_, _) => StartNewChat();
        // 顶栏下拉换一台机器 = 换一份对话:面板即时切到那台机器自己的聊天(它的请求在后台照跑)。
        // 重建下拉时的代码回填不算(_switchingSessions 挡着),那一步由 RefreshSessionsAsync 自己收尾。
        SessionCombo.SelectionChanged += (_, _) =>
        {
            if (_switchingSessions)
            {
                return;
            }
            SwitchConversation(SelectedSessionId ?? "");
        };
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.TextChanged += (_, _) =>
        {
            InputPlaceholder.IsVisible = InputBox.Document.TextLength == 0;
            OnInputTextChanged();
        };
        SetUpInputEditor();
        SetUpAttachments();
        FilePopup.PlacementTarget = InputWrap;
        SettingsButton.Click += (_, _) => OpenSettingsDialog();
        ToolsButton.Click += (_, _) => OpenToolsDialog();
        HistoryToggle.IsCheckedChanged += (_, _) =>
        {
            OnViewToggled(HistoryToggle, PanelView.History);
            if (HistoryToggle.IsChecked == true)
            {
                _ = RefreshHistoryListAsync();
            }
        };
        ClearHistoryButton.Click += (_, _) => _ = OnClearHistoryClickedAsync();
        HistorySearchBox.TextChanged += (_, _) => RenderHistoryList();
        ModeCombo.SelectionChanged += (_, _) =>
        {
            if (ModeCombo.SelectedIndex < 0)
            {
                return;
            }
            _settings.Mode = (ChatMode)ModeCombo.SelectedIndex;
            SyncModeUi();
            _ = PersistSettingsAsync();
        };
        ApprovalCombo.SelectionChanged += (_, _) =>
        {
            if (ApprovalCombo.SelectedIndex < 0)
            {
                return;
            }
            _settings.Approval = (ApprovalMode)ApprovalCombo.SelectedIndex;
            SyncModeUi();        // 挡位换了,芯片的语义色跟着换
            ApplyApprovalMode(); // 并且当场对这一轮生效(含放行已经挂出来的卡)
            _ = PersistSettingsAsync();
        };
        ProviderCombo.SelectionChanged += (_, _) =>
        {
            if (ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < _providers.Count)
            {
                _settings.ActiveModelId = _providers[ProviderCombo.SelectedIndex].Id;
                _ = PersistSettingsAsync();
            }
            // 换了模型,上一个模型的临时档位就不作数了 —— 各家的档位含义与默认值本来就不同,
            // 留着它等于把"我为 Codex 选的高"悄悄套到 Claude 头上
            _reasoningOverride = null;
            SyncReasoningUi();
            // 上下文窗口是按接入配的,换模型就得按新分母重算占比
            UpdateUsageText();
        };
        ReasoningCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingReasoning || ReasoningCombo.SelectedIndex < 0)
            {
                return;
            }
            // 选回模型自己配的那一档 = 取消覆盖(而不是"覆盖成同一个值")——
            // 否则芯片会一直亮着,而它本该只在"这一轮不一样"时才亮
            var picked = (ReasoningLevel)ReasoningCombo.SelectedIndex;
            _reasoningOverride = picked == (ActiveProvider?.Config.Reasoning ?? ReasoningLevel.Default)
                ? null
                : picked;
            SyncReasoningUi();
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
        CloseDialogs();     // 设置/配置工具窗口不该在面板关掉之后还留在屏幕上
        DisposeSuggestions();
        try
        {
            // 关面板 = 所有对话都收摊:每份对话在途的那一轮都掐掉(不只当前显示的这份)。
            foreach (Conversation conversation in _conversations.Values)
            {
                conversation.Cts?.Cancel();
            }
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
    /// 从命令入口外部注入一条消息并发送(任意线程可调)。
    /// 注入的内容(如整段终端输出)不当作用户输入:既不进 ↑↓ 历史,
    /// 里面碰巧出现的 <c>@/path</c> 也不会被当成文件引用去读远端。
    /// </summary>
    /// <remarks>
    /// 上一轮还在跑时不会被丢掉,而是排进插话队列(见 ChatPanelView.Steering.cs)——
    /// 用户在 Agent 干活时把一段报错扔进来,正是要它照着这段接着往下查。
    /// </remarks>
    public void SendExternal(string text) => Dispatcher.UIThread.Post(() => _ = SendAsync(text, fromUser: false));

    // ---------- 初始化与状态 ----------

    private async Task InitAsync()
    {
        try
        {
            _settings = await _store.LoadAsync();
            _settings.Migrate(); // 旧版的两个布尔开关折算成新的模式枚举
            ModeCombo.SelectedIndex = (int)_settings.Mode;
            ApprovalCombo.SelectedIndex = (int)_settings.Approval;
            SyncModeUi();
            ReloadProviderCombo();
            await RefreshSessionsAsync();
            // 历史能力可选:时序不可用的宿主上按钮直接禁用,聊天照常
            await _historyStore.InitAsync();
            HistoryToggle.IsEnabled = _historyStore.IsAvailable;
            // 空会话的居中空状态(图标 + 标题 + 说明 + 三条起手示例)。
            // 不自动弹配置窗:面板可能是随宿主启动一起开的,冷不丁弹一个窗在用户脸上不合适 ——
            // 点「添加模型接入」是他自己的决定。
            UpdateEmptyState();
            if (_historyStore.IsAvailable)
            {
                // ↑↓ 历史不挡首屏:面板先能用,这份在后台补齐(用户不可能在几十毫秒内就按 ↑)
                _ = LoadInputHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            _context.Log.Error("AI panel init failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private void ApplyLoc()
    {
        // 下拉项跟着语言换,选中项按索引留住(枚举值 = 索引,见 ChatMode / ApprovalMode)
        int mode = ModeCombo.SelectedIndex, approval = ApprovalCombo.SelectedIndex;
        ModeCombo.ItemsSource = (string[])[_loc["ModeChat"], _loc["ModePlan"], _loc["ModeAgent"]];
        ReloadApprovalItems();
        ModeCombo.SelectedIndex = mode;
        ApprovalCombo.SelectedIndex = approval;
        // 顺序与 ReasoningLevel 一一对应(默认 / 关 / 低 / 中 / 高),下面按索引强转回枚举
        _syncingReasoning = true;
        ReasoningCombo.ItemsSource = (string[])
        [
            _loc["ReasoningAuto"], _loc["ReasoningOff"],
            _loc["ReasoningLow"], _loc["ReasoningMedium"], _loc["ReasoningHigh"]
        ];
        _syncingReasoning = false;
        SyncReasoningUi();
        SyncModeUi();
        // 「新会话」也是纯图标钮了(顶栏四枚统一 26×30),文案只剩提示这一处
        ToolTip.SetTip(NewChatButton, $"{_loc["NewChat"]} — {_loc["NewChatTip"]}");
        ToolTip.SetTip(JumpToBottomButton, _loc["ScrollToBottom"]);
        ToolTip.SetTip(UsageMeterTrack, _loc["MeterTip"]);
        ToolTip.SetTip(SettingsButton, _loc["ModelSettings"]);
        ToolTip.SetTip(ToolsButton, _loc["ConfigureTools"]);
        ToolTip.SetTip(HistoryToggle, _loc["History"]);
        ClearHistoryText.Text = _loc["ClearHistory"];
        HistoryHeader.Text = _loc["HistoryHeader"];
        HistorySearchBox.PlaceholderText = _loc["SearchHistory"];
        SyncSendButton(); // 发送键的文案与输入框提示随"忙不忙"变,取词收在一处
        StopText.Text = _loc["Stop"];
        ToolTip.SetTip(StopButton, _loc["Stop"]);
        ToolTip.SetTip(ProviderCombo, _loc["Model"]);
        UpdateUsageText();
        // 代码块头部按钮的提示藏在 LiveMarkdown 的 ControlTemplate 里,只能经 DynamicResource 灌进去
        Resources["AiCopyTip"] = _loc["Copy"];
        Resources["AiWrapTip"] = _loc["ToggleWrap"];
    }

    /// <summary>
    /// 审批下拉的三档数据项(盾牌形状 + 语义色都挂在数据项上)。
    /// </summary>
    /// <remarks>
    /// <b>装载到可视树之后必须再建一次。</b>颜色取自宿主令牌(<c>Vela*</c>),而构造期这个控件
    /// 还没有 TopLevel,<c>TryFindResource</c> 一律返回 null —— 那样建出来的项前景是 null,
    /// 文字和盾牌<b>全是隐形的</b>,表象就是"下拉框里空空如也、展开也是三行空白"(实测)。
    /// 兜底画笔同样不能省:headless 测试宿主与隔离进程首帧都可能一个 Vela 令牌都没有。
    /// </remarks>
    private void ReloadApprovalItems()
    {
        int keep = ApprovalCombo.SelectedIndex;
        ApprovalCombo.ItemsSource = (ApprovalNavItem[])
        [
            new(_loc["ApprovalAsk"], FindIcon("AiIcon.shield-check"),
                FindBrush("VelaTextSecondary") ?? Brushes.Gainsboro),
            new(_loc["ApprovalReadOnly"], FindIcon("AiIcon.shield-alert"),
                FindBrush("VelaWarning") ?? Brushes.Orange),
            new(_loc["ApprovalBypass"], FindIcon("AiIcon.shield-off"),
                FindBrush("VelaError") ?? Brushes.IndianRed)
        ];
        ApprovalCombo.SelectedIndex = keep;
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // 到这一刻才查得到宿主资源,构造期(以及 InitAsync 续体里)建的东西要重来一遍。
        // 空状态尤其明显:三条示例的图标取自宿主的 Icon.*,建早了就是三个空 Path ——
        // 位置占着、图形没有(品牌脑图标反而在,因为它在插件自己的资源字典里)。
        ReloadApprovalItems();
        SyncModeUi();
        _emptyStateNeedsProvider = null;
        UpdateEmptyState();
    }

    // ---------- Markdown 渲染(LiveMarkdown.Avalonia) ----------

    /// <summary>
    /// 装配 Markdown 渲染:链接点击交给宿主打开、禁掉远程图片抓取、皮肤跟随主题。
    /// 样式与资源的并入在 ChatPanelView.axaml,这里只做选择器表达不了的部分。
    /// </summary>
    private void ConfigureMarkdown()
    {
        // 链接点击是冒泡路由事件,逐个对话面板在建面板时挂一次即可(见 NewConversation)——
        // 现在消息流按对话各持一份,不能只挂在某一份上。

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
        // 每份对话各有一条消息流面板,后台那几条此刻不在可视树上但气泡还在 —— 全都要换皮,
        // 否则切回一份后台对话时它的代码块还停在上一套配色上。
        foreach (Conversation conversation in _conversations.Values)
        {
            foreach (MarkdownRenderer renderer in conversation.Messages.GetVisualDescendants().OfType<MarkdownRenderer>())
            {
                renderer.CodeBlockColorTheme = codeTheme;
                ApplyMathColors(renderer);
            }
        }

        void Skin(string key, string velaKey)
        {
            if (this.TryFindResource(velaKey, ActualThemeVariant, out object? value) && value is IBrush brush)
            {
                Resources[key] = brush;
            }
        }

        Color? SkinColor(string velaKey)
            => this.TryFindResource(velaKey, ActualThemeVariant, out object? value) && value is ISolidColorBrush brush
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
        _globalSettingsView?.ApplyLoc();
        RefreshStarterSuggestions();
    });

    private void OnProvidersChanged()
    {
        ReloadProviderCombo();
        if (_providers.Count > 0 && StatusText.Text == _loc["NoProvider"])
        {
            StatusText.Text = "";
        }
        UpdateEmptyState();
        _ = PersistSettingsAsync();
    }

    /// <summary>
    /// 空状态:一个模型都没配的时候,中部给出"下一步该干什么",而不是状态行里一句
    /// 「尚未配置模型」。第一次打开面板的人要的是一个按钮和几个例子,不是一句陈述。
    /// </summary>
    private void UpdateEmptyState()
    {
        // 空状态是共享中部,只反映正显示的那份;后台那份改不着这里(切回它时会重算)。
        if (!IsForeground)
        {
            return;
        }
        // 这段对话一个字都还没有 —— 无论模型配没配,中部都不该是一整块空白
        _showEmptyState = History.Count == 0 && MessagesPanel.Children.Count == 0;
        EmptyStateHost.IsVisible = _showEmptyState && ChatScroll.IsVisible;
        if (!_showEmptyState)
        {
            EmptyStateHost.Children.Clear();
            _emptyStateNeedsProvider = null;
            return;
        }
        bool noProvider = _providers.Count == 0;
        if (_emptyStateNeedsProvider == noProvider)
        {
            return; // 已经是对的那一版了,别每次刷新都重建一遍
        }
        _emptyStateNeedsProvider = noProvider;
        EmptyStateHost.Children.Clear();
        BuildEmptyState(noProvider);
        // 起手示例现在长在空状态里,输入框上方那排药丸留给<b>对话开始之后</b>的后续提问。
        // 两处同时摆着同样的三条,只是重复。
        ClearSuggestions();
    }

    /// <summary>
    /// 摆空状态:图标 + 标题 + 说明 + 三条示例。两版只差"要不要引导去配模型" ——
    /// 没配的多一枚「添加模型接入」,而且点示例是<b>填进输入框</b>(这会儿还没有模型能答);
    /// 配好的点一下直接发出去。
    /// </summary>
    private void BuildEmptyState(bool noProvider)
    {
        EmptyStateHost.Children.Add(new Decorator
        {
            Child = MakeIcon("AiIcon.brain", "VelaTextMuted", 34),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });
        EmptyStateHost.Children.Add(new TextBlock
        {
            Classes = { "emptyTitle" },
            Text = _loc[noProvider ? "EmptyTitle" : "ReadyTitle"]
        });
        EmptyStateHost.Children.Add(new TextBlock
        {
            Classes = { "emptyBody" },
            Text = _loc[noProvider ? "EmptyBody" : "ReadyBody"]
        });
        if (noProvider)
        {
            var cta = new Button
            {
                Content = _loc["EmptyAction"],
                Height = 28,
                Padding = new Thickness(14, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            ApplyThemeResource(cta, "VelaAccentPillButtonTheme");
            cta.Click += (_, _) => OpenSettingsDialog();
            EmptyStateHost.Children.Add(cta);
        }

        EmptyStateHost.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = _loc[noProvider ? "EmptyExamples" : "ReadyExamples"],
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        foreach ((string icon, string text) in (ReadOnlySpan<(string, string)>)
                 [("Icon.terminal", _loc["Starter1"]),
                  ("Icon.hard-drive", _loc["Starter2"]),
                  ("Icon.network", _loc["Starter3"])])
        {
            var grid = new Grid { ColumnDefinitions = [with("Auto,*")] };
            grid.Children.Add(new Decorator
            {
                Child = MakeIcon(icon, "VelaTextTertiary", 12),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var label = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            var row = new Border { Classes = { "exampleRow" }, Child = grid };
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (noProvider)
                {
                    // 还没有模型能答,直接发只会撞回一句"尚未配置" —— 填进输入框放着,
                    // 配完接着按回车就行。
                    InputBox.Text = text;
                    InputBox.TextArea.Focus();
                    InputBox.CaretOffset = InputBox.Document.TextLength;
                    return;
                }
                _ = SendAsync(text);
            };
            EmptyStateHost.Children.Add(row);
        }
    }

    private void ReloadProviderCombo()
    {
        _providers = _settings.ResolveModels();
        // 只有一家供应商时前缀是纯噪音;多家并存才需要"供应商 · 模型"来区分同名模型
        bool prefix = _settings.Providers.Count > 1;
        ProviderCombo.ItemsSource = _providers
            .Select(p => prefix && !string.IsNullOrWhiteSpace(p.ProviderName) ? $"{p.ProviderName} · {p.Name}" : p.Name)
            .ToList();
        int active = _providers.FindIndex(p => p.Id == _settings.ActiveModelId);
        ProviderCombo.SelectedIndex = active >= 0 ? active : (_providers.Count > 0 ? 0 : -1);
    }

    private ResolvedModel? ActiveProvider
        => ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < _providers.Count
            ? _providers[ProviderCombo.SelectedIndex]
            : null;

    /// <summary>这一轮真正要用的接入:带上输入框旁边那个临时档位(没改过就是原样)。</summary>
    private ResolvedModel? ActiveProviderForRequest => ActiveProvider?.WithReasoning(_reasoningOverride);

    /// <summary>
    /// 把下拉的选中项、可用性与外观同步到当前状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 改过档就挂 <c>.overridden</c> 点亮成芯片,否则一个属性都不设 ——
    /// 「这一轮和我平常设的不一样」得能被余光看见,不然过几条消息就忘了自己动过它。
    /// </para>
    /// <para>
    /// <b>外观走样式类而不是在这里取色写属性。</b>本插件允许在拿不到宿主主题令牌的环境下装载
    /// (见 <c>Panel_Loads_WithoutHostThemeTokens</c>),那时 <c>FindResource</c> 返回 null,
    /// 把前景色设成 null 会让文字整个消失、只剩一个下拉箭头 —— 真机上就是这么撞见的。
    /// </para>
    /// </remarks>
    private void SyncReasoningUi()
    {
        ResolvedModel? active = ActiveProvider;
        ReasoningLevel effective = _reasoningOverride ?? active?.Config.Reasoning ?? ReasoningLevel.Default;
        _syncingReasoning = true;
        ReasoningCombo.SelectedIndex = (int)effective;
        _syncingReasoning = false;

        // 只有 models.dev 明说"不会思考"时才灰掉。不知道就放开 ——
        // 凭"默认不支持"去灰掉一个其实能思考的模型,比多给一个无效档位糟得多
        bool adjustable = active?.ReasoningAdjustable ?? true;
        ReasoningCombo.IsEnabled = adjustable;

        bool overridden = _reasoningOverride is not null;
        ReasoningCombo.Classes.Set("overridden", overridden);
        ToolTip.SetTip(ReasoningCombo, _loc[!adjustable
            ? "ReasoningUnsupportedTip"
            : overridden
                ? "ReasoningOverrideTip"
                : "ReasoningTip"]);
    }

    /// <summary>
    /// 刷新输入框下方那块用量:可见文字只留最要紧的一项(知道上下文窗口就给占比,
    /// 否则给累计的进/出),完整明细压进悬停提示 —— 工具条那点宽度经不起铺开。
    /// </summary>
    private void UpdateUsageText()
    {
        // 用量条是共享顶栏,只反映正显示的那份;后台那份记在自己身上,切回它时重算贴出。
        if (!IsForeground)
        {
            return;
        }
        int window = ActiveProvider?.MaxInputTokens ?? 0;
        if (TotalInputTokens == 0 && TotalOutputTokens == 0)
        {
            UsageText.Text = "";
            UsageMeterTrack.IsVisible = false;
            ToolTip.SetTip(UsageText, _loc["UsageIdle"]);
            return;
        }

        var detail = new StringBuilder();
        var label = new StringBuilder();
        if (window > 0 && LastInputTokens > 0)
        {
            int percent = (int)Math.Min(100, Math.Round(LastInputTokens * 100.0 / window));
            label.Append($"{ChatFormatting.Compact(LastInputTokens)}/{ChatFormatting.Compact(window)} · {percent}%");
            detail.AppendLine(_loc.F("UsageContextLine", $"{LastInputTokens:N0}", $"{window:N0}", percent));
            SetUsageMeter(percent);
        }
        else
        {
            label.Append($"↑{ChatFormatting.Compact(TotalInputTokens)} ↓{ChatFormatting.Compact(TotalOutputTokens)}");
            // 不知道窗口多大就画不出占比,整条隐掉(留一根空槽反而像"用量为零")
            UsageMeterTrack.IsVisible = false;
        }

        // 命中率 = 缓存读取 / 输入。两家口径实测已被适配器抹平:OpenAI 的 prompt_tokens 本就含
        // cached_tokens;Anthropic 的 input_tokens 原本不含缓存,但适配器把 cache_read 与
        // cache_creation 都并进了 InputTokenCount(200+800+120=1120)。所以同一个式子两边都成立。
        if (LastCachedInputTokens > 0 && LastInputTokens > 0)
        {
            int hit = (int)Math.Min(100, Math.Round(LastCachedInputTokens * 100.0 / LastInputTokens));
            label.Append($" · {_loc["CacheShort"]} {hit}%");
            detail.AppendLine(_loc.F("UsageCacheLine", $"{LastCachedInputTokens:N0}", $"{LastInputTokens:N0}", hit));
        }

        UsageText.Text = label.ToString();
        detail.AppendLine(_loc.F("UsageTotalsLine", $"{TotalInputTokens:N0}", $"{TotalOutputTokens:N0}"));
        if (TotalCachedInputTokens > 0 || TotalCacheWriteTokens > 0)
        {
            detail.AppendLine(_loc.F("UsageCacheTotalsLine", $"{TotalCachedInputTokens:N0}", $"{TotalCacheWriteTokens:N0}"));
        }
        if (TotalReasoningTokens > 0)
        {
            detail.AppendLine(_loc.F("UsageReasoningLine", $"{TotalReasoningTokens:N0}"));
        }
        if (DroppedFromContext > 0)
        {
            // 裁剪不能悄悄发生:用户得知道模型"看不到"最早那几条了
            detail.AppendLine(_loc.F("UsageTrimmedLine", DroppedFromContext));
        }
        if (ActiveProvider is { } provider)
        {
            if (EstimateCost(provider) is { } cost)
            {
                // 单价是用户自己填的(各家计价单位不同,插件不猜),所以只给数字不给货币符号
                detail.AppendLine(_loc.F("UsageCostLine", cost.ToString("0.####")));
            }
            detail.Append(_loc.F("UsageLimitsLine",
                $"{provider.MaxTokens:N0}",
                window > 0 ? $"{window:N0}" : "—"));
        }
        ToolTip.SetTip(UsageText, detail.ToString().TrimEnd());
    }

    /// <summary>
    /// 上下文占用条:把"上一轮吃掉了窗口的百分之几"画成一根 56px 的槽。
    /// 数字仍在文字与悬停提示里,这根条只负责让占比进入余光 —— 逼近上限时颜色先变,
    /// 用户不必先去读数才知道该开新会话了。
    /// </summary>
    /// <param name="percent">0–100。低于 1% 也给 2px,免得"有用量"看上去像"没用量"。</param>
    private void SetUsageMeter(int percent)
    {
        double track = UsageMeterTrack.Width;
        UsageMeterTrack.IsVisible = true;
        UsageMeterFill.Width = percent <= 0 ? 0 : Math.Max(2, Math.Round(track * Math.Min(100, percent) / 100.0));
        string key = percent switch
        {
            >= 95 => "VelaError",
            >= 80 => "VelaWarning",
            _ => "VelaAccent"
        };
        if (FindBrush(key) is { } brush)
        {
            UsageMeterFill.Background = brush;
        }
    }

    /// <summary>
    /// 按接入里填的单价估这段会话花了多少。三个单价都为 0(没填)就返回 null,不显示这一行。
    /// 命中缓存的那部分单独按缓存价算 —— 那正是缓存值不值得开的关键数字。
    /// </summary>
    private double? EstimateCost(ResolvedModel provider)
    {
        if (provider.InputPricePerMillion <= 0 && provider.OutputPricePerMillion <= 0)
        {
            return null;
        }
        double cachedPrice = provider.CachedInputPricePerMillion > 0
            ? provider.CachedInputPricePerMillion
            : provider.InputPricePerMillion;
        long freshInput = Math.Max(0, TotalInputTokens - TotalCachedInputTokens);
        return ((freshInput * provider.InputPricePerMillion)
                + (TotalCachedInputTokens * cachedPrice)
                + (TotalOutputTokens * provider.OutputPricePerMillion)) / 1_000_000d;
    }

    private string? SelectedSessionId
        => _sessions.Count > 0 && SessionCombo.SelectedIndex >= 0 && SessionCombo.SelectedIndex < _sessions.Count
            ? _sessions[SessionCombo.SelectedIndex].SessionId
            : null;

    /// <summary>
    /// 当前这一轮的工具该打在哪台机器上:本轮所属对话绑的那条会话(<c>""</c> 通用对话 = 不绑,给 null)。
    /// </summary>
    /// <remarks>
    /// 工具调用发生在本轮的异步流里,<see cref="Cur" /> 经 <see cref="_turnScope" /> 落到本轮那份对话,
    /// 所以后台轮次取到的是它自己那台机器,而不是此刻下拉选中的那台。
    /// </remarks>
    private string? CurrentToolSessionId()
    {
        string key = Cur.SessionKey;
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private void OnSessionEvent(SessionInfo info) => Dispatcher.UIThread.Post(() => _ = RefreshSessionsAsync());

    private async Task RefreshSessionsAsync()
    {
        try
        {
            IReadOnlyList<SessionInfo> all = await _context.Sessions.ListAsync();
            string? previous = SelectedSessionId;
            _sessions = [.. all.Where(s => s.State == SessionState.Connected)];
            // 顶栏的会话行显示的是<b>连接的名字</b>,不是 user@host。名字在已保存的配置里
            // (会话树上那个用户认得的名),而在途会话本身不带它 —— 按 主机/端口/用户 对回去取。
            // 对不上的(临时会话、没起名的)退回 user@host,总有个能认的落点。
            Dictionary<(string, int, string), string> names = await LoadSessionNamesAsync();
            // 顶栏的会话行前面带一颗状态点。列表里只有"已连接"的会话,所以点恒为绿;
            // 一台都没有时那条占位项给灰点 —— 空着不画反而会让整行的对齐跳一下。
            IBrush online = FindBrush("VelaStatusConnected") ?? Brushes.LimeGreen;
            IBrush offline = FindBrush("VelaTextMuted") ?? Brushes.Gray;
            // 重建下拉这一下会连带触发 SelectionChanged,但那只是代码回填、不是用户在切对话 ——
            // 用 _switchingSessions 挡住它,选中项定下来之后由本方法自己按最终选中值切一次。
            _switchingSessions = true;
            try
            {
                SessionCombo.ItemsSource = _sessions.Count == 0
                    ? (IReadOnlyList<SessionNavItem>)[new SessionNavItem(_loc["NoSession"], offline)]
                    : [.. _sessions.Select(s => new SessionNavItem(SessionLabel(s, names), online))];
                int keep = _sessions.FindIndex(s => s.SessionId == previous);
                SessionCombo.SelectedIndex = keep >= 0 ? keep : 0;
            }
            finally
            {
                _switchingSessions = false;
            }
            // 选中值可能变了(原会话断了、掉到别台上):把显示的这份对话对齐到它。
            // 同一份 = 无操作,所以刷新列表不会打断你正在看的对话。
            SwitchConversation(SelectedSessionId ?? "");
        }
        catch (Exception ex)
        {
            _context.Log.Error("Refresh sessions failed.", ex);
        }
    }

    /// <summary>建一份新对话(连带它自己那条消息流面板,并把链接点击处理挂上)。</summary>
    private Conversation NewConversation(string sessionKey)
    {
        var messages = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(5, 4, 19, 4)
        };
        // 链接是冒泡路由事件,挂在这条面板上就覆盖它里面的所有气泡。
        messages.AddHandler(MarkdownTextBlock.LinkClickEvent, OnMarkdownLinkClicked);
        return new Conversation(sessionKey, messages);
    }

    /// <summary>
    /// 切到某台机器(<paramref name="key" /> = 会话 id;<c>""</c> = 通用对话)的那份对话:
    /// 换 <see cref="_active" /> 的引用、把它的面板挂进 <c>ChatScroll</c>、再把共享顶栏对齐到它。
    /// </summary>
    /// <remarks>
    /// 是<b>换引用</b>不是重建:目标那份对话(含它的历史气泡、在途请求)一直在内存里活着,
    /// 所以切换即时、后台不断、也不串台。没有这份就现建一份空的。
    /// </remarks>
    private void SwitchConversation(string key)
    {
        if (ReferenceEquals(Cur, _active) && _active.SessionKey == key)
        {
            return;
        }
        if (!_conversations.TryGetValue(key, out Conversation? target))
        {
            target = NewConversation(key);
            _conversations[key] = target;
        }
        if (ReferenceEquals(target, _active))
        {
            return;
        }
        _active = target;
        ChatScroll.Content = _active.Messages;
        RefreshForeground();
    }

    /// <summary>把共享的顶栏控件(忙态 / 状态行 / 用量 / 排队芯片 / 空状态)对齐到当前显示的这份对话。</summary>
    private void RefreshForeground()
    {
        SetBusy(_active.Busy);
        StatusText.Text = _active.Status;
        StatusText.Classes.Remove("retrying");
        RenderQueuedChips();
        UpdateUsageText();
        UpdateEmptyState();
        // 切到别的对话时上一份的后续提问药丸不再相关;各份的后续提问是即时算的,不做保留。
        ClearSuggestions();
        RequestAutoScroll(force: true);
        UpdateJumpToBottomButton();
    }

    /// <summary>
    /// 设置状态行文字:只有正显示的这份对话才真的去改共享的状态行;后台那份只把话记在自己身上,
    /// 切回来时由 <see cref="RefreshForeground" /> 贴回去(否则后台一句"MCP 连接中"会盖住你正看的对话)。
    /// </summary>
    private void SetStatus(string text)
    {
        Cur.Status = text;
        if (IsForeground)
        {
            StatusText.Text = text;
        }
    }

    /// <summary>
    /// 已保存配置的「名字」查表:键是能把在途会话对回配置的那三样(主机 / 端口 / 用户)。
    /// </summary>
    /// <remarks>
    /// 取不到(宿主没给已保存列表、或调用出错)就返回空表,顶栏照样能用 user@host 兜底 ——
    /// 名字只是更好认,拿不到不该让会话列表本身塌掉。
    /// 同一 主机/端口/用户 下配了多条(少见),留先出现的那个名。
    /// </remarks>
    private async Task<Dictionary<(string, int, string), string>> LoadSessionNamesAsync()
    {
        try
        {
            IReadOnlyList<SavedSessionInfo> saved = await _context.Sessions.ListSavedAsync();
            var map = new Dictionary<(string, int, string), string>();
            foreach (SavedSessionInfo p in saved)
            {
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    map.TryAdd((p.Host, p.Port, p.Username), p.Name);
                }
            }
            return map;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Loading saved session names failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>顶栏一条会话该显示的名字:优先已保存配置的名,对不上退回 user@host。</summary>
    private static string SessionLabel(SessionInfo s, Dictionary<(string, int, string), string> names)
        => names.TryGetValue((s.Host, s.Port, s.Username), out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"{s.Username}@{s.Host}";

    /// <summary>记住用户拖分割条拖出来的侧栏宽度(百分比,宿主在拖动结束时通知一次)。</summary>
    /// <remarks>
    /// <b>必须改这份在内存里的 <c>_settings</c>,不能绕过面板直接往库里写。</b>
    /// 面板持有整份设置,任何一次改动(换模式、改审批、勾工具、存接入)都是把这份整体覆盖回去 ——
    /// 背着它写库的话,下一次这类操作就会拿旧的宽度把刚记下的值盖掉,表现就是"拖了不算数"。
    /// </remarks>
    public void RememberPanelWidth(int percent)
    {
        if (_settings.PanelWidthPercent == percent)
        {
            return;
        }
        _settings.PanelWidthPercent = percent;
        _ = PersistSettingsAsync();
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

    /// <summary>
    /// 切换"这一轮在跑"的界面状态。
    /// </summary>
    /// <remarks>
    /// 发送键<b>不再随忙隐藏</b>:一轮进行中它换成「排队」,再发的消息插进当前这一轮
    /// (见 ChatPanelView.Steering.cs)。停止键与它并排 —— 两件事此刻都做得了:
    /// 补一句让它照着改,或者干脆把这一轮掐掉。
    /// </remarks>
    private void SetBusy(bool busy)
    {
        Busy = busy; // 落到当前这一轮的那份对话
        // 忙态指示(停止键/发送键文案/发光)是共享顶栏,只反映正显示的那份;
        // 后台那份自己忙自己的,不该动这里(切回它时 RefreshForeground 会照它的忙态贴回)。
        if (!IsForeground)
        {
            return;
        }
        StopButton.IsVisible = busy;
        SyncSendButton();
        SetBusyGlow(busy);
    }

    /// <summary>发送键与输入框提示按"忙不忙"取词(语言切换与忙闲切换都经这里)。</summary>
    private void SyncSendButton()
    {
        SendText.Text = _loc[Busy ? "Queue" : "Send"];
        ToolTip.SetTip(SendButton, _loc[Busy ? "QueueTip" : "Send"]);
        InputPlaceholder.Text = _loc[Busy ? "InputPlaceholderBusy" : "InputPlaceholder"];
    }

    /// <summary>
    /// 只有"纯滚动"(内容尺寸没变)才更新粘底意图:流式期间内容增长引起的
    /// 相对位移不算用户上滚,否则粘底会被内容自己的增长误关。
    /// </summary>
    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 只有"纯滚动"(内容尺寸没变)才更新粘底意图:流式期间内容增长引起的相对位移不算用户上滚。
        if (e.ExtentDelta.Y == 0)
        {
            _autoScroll = ChatScroll.Offset.Y + ChatScroll.Viewport.Height >= ChatScroll.Extent.Height - 8;
        }
        // 但"跳到末尾"按钮的显隐要跟着每一次变化走 —— 包括新消息把内容顶长(此时离底更远了)。
        UpdateJumpToBottomButton();
    }

    /// <summary>
    /// "跳到末尾"圆钮的显隐:只有在聊天流可见、内容超出一屏、且当前不在底部时才显形。
    /// 在底部(含流式粘底)时收起 —— 那会儿没有"跳"的必要。
    /// </summary>
    private void UpdateJumpToBottomButton()
    {
        bool hasOverflow = ChatScroll.Extent.Height > ChatScroll.Viewport.Height + 8;
        bool atBottom = ChatScroll.Offset.Y + ChatScroll.Viewport.Height >= ChatScroll.Extent.Height - 8;
        JumpToBottomButton.IsVisible = ChatScroll.IsVisible && hasOverflow && !atBottom;
    }

    /// <summary>
    /// 请求滚到底:同帧内的多次请求合并为一次(Background 优先级,排在布局之后),
    /// 用户主动上滚阅读时不打扰;<paramref name="force" /> 用于用户自己发消息等
    /// 明确要回底部的场合。
    /// </summary>
    private void RequestAutoScroll(bool force = false)
    {
        // 后台那份的气泡长在不可见的面板里,别去动共享的滚动条。
        if (!IsForeground)
        {
            return;
        }
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
    /// <param name="prepared">
    /// 已经备好的那一条(排队时就展开过引用、并过附件)。给了它就不再重新展开 ——
    /// 排队期间远端文件可能已经变了,发出去的该是用户按下回车那一刻的那份。
    /// </param>
    private async Task SendAsync(string text, bool fromUser = true, SteeringMessage? prepared = null)
    {
        text = text.Trim();
        // 记下这一轮属于哪份对话:通常就是正显示的这份;后台续跑的下一轮(队列排空)沿用原来那份。
        // 置进 _turnScope 后,这条方法(及它 await 出去的整条流水线)所有代理都落到这份上,
        // 而不是"此刻正显示的那份"—— 这就是切走之后它还能把话答完、且不串台的关键。
        Conversation conv = Cur;
        _turnScope.Value = conv;
        // 只带附件、正文为空也算一次有效发送(比如"看看这张图")
        if (text.Length == 0 && _attachments.Count == 0 && prepared is null)
        {
            return;
        }
        // 上一轮还在跑就不抢它:排进队列,由插话通道在模型下一步之前送进去
        // (见 ChatPanelView.Steering.cs —— 这正是"边跑边补"的入口)。
        if (Busy)
        {
            await QueueWhileBusyAsync(text, fromUser);
            return;
        }
        // 取带临时档位的那一份:输入框旁边选的"这一问想多深"就是在这儿生效的
        if (ActiveProviderForRequest is not { } provider)
        {
            // 这次是用户主动发消息才撞上"没配接入",直接把设置窗口开给他
            SetStatus(_loc["NoProvider"]);
            if (IsForeground)
            {
                OpenSettingsDialog();
            }
            return;
        }

        SetBusy(true);
        // 输入框/文件选择器/视图切换都是共享控件:只有正显示这份时才动它们,
        // 否则后台续跑会把你正在别份对话里敲的字清掉、把你正看的历史页切走。
        if (IsForeground)
        {
            InputBox.Text = "";
            CloseFilePicker();
            SetActiveView(PanelView.Chat);
        }
        // 状态行只说"这一刻"的事:新一轮开始,上一轮的取消/报错提示就该退场
        SetStatus("");
        if (IsForeground)
        {
            ClearSuggestions(); // 上一轮的后续提问已经过期,并掐掉可能还在途的那次求建议
        }
        if (fromUser)
        {
            RememberInput(text);
        }

        // 界面与库里留的是这一份(短名引用 + 附件留痕);送给模型的那份在下面单独装配
        string display = prepared?.DisplayText ?? text + AttachmentTrace();
        AddUserBubble(display);
        UpdateEmptyState(); // 消息流有东西了,居中的空状态得让位
        RequestAutoScroll(force: true);

        Cts = new CancellationTokenSource();
        CancellationToken token = Cts.Token;
        AssistantBubble? bubble = null;
        long startedAt = Environment.TickCount64;
        bool cancelled = false;
        bool failed = false;
        string replyText = "";
        try
        {
            ChatMessage userMessage;
            if (prepared is { } queued)
            {
                userMessage = queued.Message;
            }
            else
            {
                // @ 引用的远端文件在这里展开:气泡里显示的是短名芯片,只有送给模型的那份带完整路径与文件内容。
                (string modelText, IReadOnlyList<string> _, IReadOnlyList<string> unreadable) = fromUser
                    ? await ResolveAttachmentsAsync(text, token)
                    : (text, [], []);
                if (unreadable.Count > 0)
                {
                    AddAttachmentFailureNote(unreadable);
                }
                // 本地附件并进这一轮:图片作为视觉输入,文本附件接在正文后面
                userMessage = new ChatMessage(ChatRole.User, BuildUserContents(modelText));
            }
            // 这一轮往历史里加的第一条 —— 一个字都没换来时要按它整批撤回(见 SettleUnfinishedTurnAsync)
            TurnHistoryStart = History.Count;
            History.Add(userMessage);
            await PersistAsync("user", display);
            ClearAttachments();
            bubble = new AssistantBubble(this);
            ActiveBubble = bubble;
            MessagesPanel.Children.Add(bubble.Root);
            TrimMessageWindow(); // 常驻条数封顶,别让可视树越聊越长
            RequestAutoScroll(force: true);

            // 插话通道垫在最里层(下面那层函数调用循环<b>之内</b>):循环每跑一步都要经过它,
            // 排队中的补充说明就能赶在模型下一步之前进上下文(见 SteeringChatClient)。
            var steering = new SteeringChatClient(
                await _store.CreateClientAsync(provider, cancellationToken: token),
                SteeringQueue, () => OnSteeringDelivered(conv));
            BeginSteering(steering);
            IChatClient client = steering;
            var options = new ChatOptions
            {
                MaxOutputTokens = provider.MaxTokens,
                Temperature = provider.Temperature,
                TopP = provider.TopP,
                StopSequences = SplitLines(provider.StopSequences)
            };
            // 思考档位:Default 表示"不带这个参数",交给服务端的默认行为。两家协议的翻译方式
            // 不同(OpenAI 认 ChatOptions.Reasoning,Anthropic 只认请求体里的 thinking),
            // 差异全收在 AiSettingsStore.ApplyReasoning 里。
            AiSettingsStore.ApplyReasoning(options, provider);
            // 这一家端点不认的参数在这儿摘掉(私有后端常常只是标准协议的受限子集,
            // 多发一个字段就整轮 400)。差异全在目录数据里,见 UnsupportedParameters。
            AiSettingsStore.ApplyEndpointQuirks(options, provider);
            ChatMode mode = _settings.Mode;
            // 检索优先走供应商自带的服务端工具:它跑在模型那一侧,不经本机,结果自带引用。
            // 但只有 Anthropic Messages 与 OpenAI Responses 认这套,其余协议(Chat Completions、
            // Ollama、多数中转站)解不出来,回落到插件自带的 web_search。用户可以在全局设置里关掉。
            bool nativeSearch = mode != ChatMode.Chat
                                && _settings.WebSearch.Enabled
                                && _settings.WebSearch.PreferProviderNative
                                && NativeWebSearch.IsSupported(provider.Protocol);
            // 纯对话模式不给任何工具;计划模式只给只读工具(见 AgentToolbox.CreateTools)
            if (mode != ChatMode.Chat)
            {
                ApplyApprovalMode(); // 挡位推给工具箱与 MCP(中途再改也会经这条路重推)
                _toolbox.DisabledTools = new HashSet<string>(
                    SplitLines(_settings.DisabledBuiltinTools) ?? [], StringComparer.OrdinalIgnoreCase);
                _toolbox.WebSearch = _settings.WebSearch;
                IList<AITool> tools = _toolbox.CreateTools(mode, nativeSearch);
                // 计划模式下不接 MCP:那些工具的副作用由第三方服务器说了算,插件无从判断,
                // 而"计划"的承诺是这一步不动任何东西。
                if (mode == ChatMode.Agent && _settings.McpServers.Any(s => s.Enabled))
                {
                    SetStatus(_loc["McpConnecting"]);
                    (List<AITool> mcpTools, List<string> mcpErrors) = await _mcp.GetToolsAsync(_settings.McpServers, token);
                    foreach (AITool tool in mcpTools)
                    {
                        tools.Add(tool);
                    }
                    SetStatus(mcpErrors.Count > 0
                        ? $"{_loc["Error"]} (MCP): {string.Join("; ", mcpErrors)}"
                        : "");
                }
                if (nativeSearch)
                {
                    // 必须排在 ApplyReasoning 之后:Anthropic 那条路是在思考配置留下的
                    // RawRepresentationFactory 上叠一层,先叠会被后设的整个盖掉。
                    NativeWebSearch.Apply(options, provider, tools, _settings.WebSearch.MaxResults);
                }
                options.Tools = tools;
                client = client.AsBuilder()
                    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 25)
                    .Build();
            }

            // 快撑满窗口就先把早期对话折成摘要(压不动也不拦,下面的装配还会兜底丢最早几条)
            await CompactIfNeededAsync(provider, token);
            // 装配上下文:摘要 + 近几轮原文,按窗口裁剪并把相邻同角色的消息并起来(见 ContextBuilder)
            RequestContext request = ContextBuilder.Build(
                BuildSystemPrompt(mode, nativeSearch), History, provider.MaxInputTokens, provider.MaxTokens,
                ContextSummary, SummarizedThrough);
            List<ChatMessage> requestMessages = request.Messages;
            DroppedFromContext = request.DroppedMessages;
            // 有的订阅型端点不收 system 角色(ChatGPT 的 Codex 后端会回
            // 400 {"detail":"System messages are not allowed"})。那时把系统提示词挪到
            // Responses 协议自己的 instructions 字段上 —— 内容一个字不少,只是换了个位置。
            if (!EndpointQuirks.Of(provider.Provider).AllowSystemMessages)
            {
                options.Instructions = ContextBuilder.MoveSystemPromptOut(requestMessages);
            }
            // Anthropic 的提示词缓存断点(其它协议不认这个标记,打了也只是多一个被忽略的字段)
            if (provider.Protocol == ChatProtocol.AnthropicMessages && provider.PromptCaching)
            {
                PromptCache.Apply(requestMessages);
            }
            else
            {
                // 关掉之后要把历史上残留的标记抹干净,否则一直挂着(内容对象跨轮复用)
                PromptCache.Clear(History);
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

            async Task StreamOnceAsync() => await Task.Run(async () =>
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

            // 断在开口之前就重来 —— 一次网络抖动不该让整轮作废。
            // 已经吐出内容再断就不重试了:没有断点续传,重来会把已显示的那半截重复一遍。
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await StreamOnceAsync();
                    break;
                }
                catch (Exception ex) when (attempt < StreamRetries && updates.Count == 0
                                           && !token.IsCancellationRequested && TransientFailure.IsTransient(ex))
                {
                    _context.Log.Warn($"Stream failed before any content (attempt {attempt + 1}): {ex.Message}");
                    // 重试是瞬时故障,给 warn 色区别于普通提示;成功后连色带字一起撤掉
                    if (IsForeground)
                    {
                        StatusText.Classes.Add("retrying");
                    }
                    SetStatus(_loc.F("Retrying", attempt + 1));
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), token);
                }
            }
            if (IsForeground)
            {
                StatusText.Classes.Remove("retrying");
            }
            SetStatus("");
            DrainUpdates(); // 兜底清空残留批(此处已回到 UI 线程)

            var response = updates.ToChatResponse();
            // 兜底补齐插话:送达回调的 Post 可能还排在队里没跑到,而它必须排在
            // 这一轮的回复之前进历史与库(顺序:原消息 → 插话 → 回复)。
            await CommitSteeringAsync(conv);
            History.AddMessages(response);
            int sequence = await PersistAsync("assistant", response.Text);
            if (sequence >= 0 && bubble is not null)
            {
                // 思考、工具调用、模型、耗时另存一行 —— 翻回旧会话时这些才是"Agent 做了什么"的证据
                await _historyStore.AppendMetaAsync(ConversationId, sequence,
                    bubble.Snapshot(ModelLabel(provider),
                        TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt)));
            }
            HintIfThinkingWasNeverRequested(bubble, provider);
            if (response.Usage is { } usage)
            {
                LastInputTokens = usage.InputTokenCount ?? LastInputTokens;
                TotalInputTokens += usage.InputTokenCount ?? 0;
                TotalOutputTokens += usage.OutputTokenCount ?? 0;
                TotalReasoningTokens += usage.ReasoningTokenCount ?? 0;
                LastCachedInputTokens = usage.CachedInputTokenCount ?? 0;
                TotalCachedInputTokens += LastCachedInputTokens;
                // 缓存"写入"只有 Anthropic 报(它单独收费),OpenAI 系没有这个概念
                TotalCacheWriteTokens += usage.AdditionalCounts?.GetValueOrDefault("CacheCreationInputTokens") ?? 0;
            }
            UpdateUsageText();
            replyText = response.Text;
        }
        catch (OperationCanceledException)
        {
            // 取消是"这条回复"的属性,记在气泡头部;状态行不留话(留了就一直挂着,见截图反馈)
            cancelled = true;
            await CommitSteeringAsync(conv); // 已经送到模型那儿的插话照样算数
            await SettleUnfinishedTurnAsync(bubble);
        }
        catch (Exception ex)
        {
            failed = true;
            // 带上服务端正文:Anthropic 的异常消息只有一句 "Status Code: BadRequest",
            // 真正说清哪儿不对的那段在 ResponseBody 里(见 ApiErrorText)。
            // 根本没连上的那一类另给一句 —— 那时该去查网络/代理,而不是翻 Key 有没有填错。
            string detail = ApiErrorText.Describe(ex, _loc["ErrorUnreachable"]);
            _context.Log.Error($"AI request failed. — {detail}", ex);
            // 失败不再当成一段 Markdown 追加进正文:它不是模型说的话,混排会让人分不清
            // 哪句是回答、哪句是故障。改成一张 error 卡挂在这条回复里(见 AddErrorCard)。
            AddErrorCard(bubble, detail);
            await CommitSteeringAsync(conv);
            await SettleUnfinishedTurnAsync(bubble);
        }
        finally
        {
            Cts?.Dispose();
            Cts = null;
            ActiveBubble = null;
            EndSteering();
            if (IsForeground)
            {
                StatusText.Classes.Remove("retrying");
            }
            SetBusy(false);
            // 一轮到此为止(成功/取消/出错都算):收起思考区,补上"耗时 · 步数"与"时间 · 模型"
            bubble?.FinishStreaming(
                ModelLabel(provider),
                DateTimeOffset.Now,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt),
                cancelled);
            RequestAutoScroll();
        }

        // 这一轮结束时队里还有货 = 这些插话谁也没赶上(纯对话模式只发一次请求;
        // 或者是在最后一步之后才排进来的)。答完了就直接当作下一轮发出去;
        // 被停掉或出错了就原样放回输入框 —— 该重试还是该改写由用户决定。
        if (cancelled || failed)
        {
            // 放回输入框只对正显示这份有意义;后台那份被停/出错就把余下插话丢掉(输入框不是它的)。
            if (IsForeground)
            {
                RestoreQueuedToInput();
            }
            else
            {
                SteeringQueue.DrainAll();
            }
            return;
        }
        if (SteeringQueue.DrainMerged() is { } next)
        {
            RenderQueuedChips();
            await SendAsync(next.DisplayText, fromUser: false, prepared: next);
            return;
        }

        // 顺利答完才给后续提问:取消/报错时用户要的是重试,不是被塞几条建议;
        // 后台那份也不弹后续提问(那是给正看着的人的)。
        if (replyText.Length > 0 && IsForeground)
        {
            await SuggestFollowUpsAsync(provider, text, replyText);
        }
    }

    /// <summary>
    /// 流还没开口就断掉时最多重来几次。<b>只给 1 次</b>:各家 SDK 自己已经对连接级失败退避重试过
    /// (实测连接被拒会重试三次、约 6 秒),这一层再叠太多,只会让"服务端真的挂了"这种情况
    /// 拖到二十秒后才告诉用户。
    /// </summary>
    private const int StreamRetries = 1;

    /// <summary>回复底部显示的模型名:优先模型 id,没填就退回接入名称。</summary>
    private static string ModelLabel(ResolvedModel provider)
        => string.IsNullOrWhiteSpace(provider.Model) ? provider.Name : provider.Model;

    /// <summary>
    /// 模式变了要跟着调整界面:审批方式只在<b>有工具</b>的模式下才有意义,
    /// 纯对话模式里摆着它只会让人以为哪里还能被自动执行点什么。
    /// </summary>
    private void SyncModeUi()
    {
        ApprovalCombo.IsVisible = _settings.Mode != ChatMode.Chat;
        ToolTip.SetTip(ModeCombo, _loc[_settings.Mode switch
        {
            ChatMode.Agent => "ModeAgentTip",
            ChatMode.Plan => "ModePlanTip",
            _ => "ModeChatTip"
        }]);
        ToolTip.SetTip(ApprovalCombo, _loc[_settings.Approval switch
        {
            ApprovalMode.Bypass => "ApprovalBypassTip",
            ApprovalMode.ReadOnlyAuto => "ApprovalReadOnlyTip",
            _ => "ApprovalAskTip"
        }]);
        // 审批是安全开关:当前挡位的风险高低要能被余光读到,而不是靠去读那四个字。
        // 每次询问 = 中性(什么都不会替你按),只读自动 = warn,全部自动 = error。
        string tone = _settings.Approval switch
        {
            ApprovalMode.Bypass => "VelaError",
            ApprovalMode.ReadOnlyAuto => "VelaWarning",
            _ => "VelaTextSecondary"
        };
        if (FindBrush(tone) is { } brush)
        {
            ApprovalCombo.Foreground = brush;
            ApprovalCombo.BorderBrush = brush;
        }
    }

    /// <summary>用量归零(换会话时用:计数是"这一段对话"的,不是进程级的)。</summary>
    private void ResetUsage()
    {
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        TotalReasoningTokens = 0;
        LastInputTokens = 0;
        LastCachedInputTokens = 0;
        TotalCachedInputTokens = 0;
        TotalCacheWriteTokens = 0;
        UpdateUsageText();
    }

    /// <summary>
    /// 一轮没能正常收尾(用户按停、或请求出错)时收拾历史:
    /// 已经吐出来的半截回复照样进历史与库(用户看得见,模型下一轮也该知道自己说过什么);
    /// 一个字都没吐就把这一轮加进去的 user 消息整批撤回来 —— 否则历史里会留下没有回复的提问。
    /// </summary>
    /// <remarks>
    /// <para>
    /// "整批"不是一条:一轮里除了开头那条提问,还可能有中途送进去的插话
    /// (见 ChatPanelView.Steering.cs),它们同样是没换来回复的 user 消息。
    /// </para>
    /// <para>
    /// 发送前 <see cref="ContextBuilder" /> 还会兜一次(相邻同角色合并),两处都要:
    /// 这里让"界面/库/上下文"三者一致,那里防的是任何来路的历史(含旧版本留下的)。
    /// </para>
    /// </remarks>
    private async Task SettleUnfinishedTurnAsync(AssistantBubble? bubble)
    {
        string partial = bubble?.ReplyText.Trim() ?? "";
        if (partial.Length > 0)
        {
            History.Add(new ChatMessage(ChatRole.Assistant, partial));
            await PersistAsync("assistant", partial);
            return;
        }
        if (History.Count > TurnHistoryStart && History[^1].Role == ChatRole.User)
        {
            History.RemoveRange(TurnHistoryStart, History.Count - TurnHistoryStart);
        }
    }

    /// <summary>
    /// 把一条消息写进历史(时序库),返回它拿到的会话内序号;
    /// 能力不可用或空文本时什么也不做并返回 -1(附加信息也就无从挂靠)。
    /// </summary>
    private async Task<int> PersistAsync(string role, string text)
    {
        if (!_historyStore.IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }
        int sequence = PersistedCount++;
        await _historyStore.AppendAsync(ConversationId, ConversationStartedAt, sequence, role, text);
        return sequence;
    }

    // “本段对话是否已提示过没请求思考”按对话各持一份(见 Conversation):_thinkingHintShown。

    /// <summary>
    /// 一整轮下来一点思考都没有,而模型的"思考过程"正是「不请求」时,说明一下为什么。
    /// </summary>
    /// <remarks>
    /// 这是用户最容易困惑的一刻:本该有思考卡片的位置什么都没有,而原因在另一个窗口的一个下拉里。
    /// <b>实测(2026-08-24,假端点抓请求体)</b>:档位为 Default 时发出去的请求里
    /// 连 <c>thinking</c> / <c>reasoning_effort</c> 字段都没有,服务端自然什么都不回 ——
    /// 光看界面根本推不出这一点。
    /// 一次会话只说一次,而且下一轮发送时状态行本来就会清空,不会一直挂着。
    /// </remarks>
    private void HintIfThinkingWasNeverRequested(AssistantBubble? bubble, ResolvedModel provider)
    {
        if (ThinkingHintShown || bubble is null || bubble.HasThinking
            || provider.Reasoning != ReasoningLevel.Default)
        {
            return;
        }
        ThinkingHintShown = true;
        SetStatus(_loc["ReasoningOffHint"]);
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

    /// <summary>非空行拆成数组;全空返回 null(<c>ChatOptions</c> 上 null 才表示"不发这个参数")。</summary>
    private static string[]? SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines : null;
    }

    /// <summary>
    /// 系统提示词:接入专用的优先,其次全局的,最后才是内置默认。
    /// 内置那份会按对话模式追加不同的交代 —— 三种模式对模型的要求本来就不一样。
    /// </summary>
    /// <remarks>
    /// 用户自定义的提示词<b>不追加模式说明</b>:他既然自己写了,就该完全由他说了算。
    /// 模式的实际约束靠"给不给工具"来兜底(计划模式根本拿不到写工具),不依赖提示词自觉。
    /// </remarks>
    private string BuildSystemPrompt(ChatMode mode, bool nativeWebSearch = false)
    {
        if (ActiveProvider is { SystemPrompt: { } own } && !string.IsNullOrWhiteSpace(own))
        {
            return own;
        }
        if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
        {
            return _settings.SystemPrompt;
        }
        string prompt =
            "You are the AI assistant embedded in VelaShell, an SSH terminal application. " +
            "Help the user with servers, shell commands, log analysis and DevOps questions. Be concise and practical. " +
            "Format responses in Markdown. " +
            $"Respond in the user's language (UI locale: {_context.Host.Locale}). " +
            "The user can attach remote files to a message with @path; their content is included verbatim after the message.";
        // 服务端检索没有工具声明摆在模型面前,不点破它就以为自己不能联网,
        // 张口就是"我无法访问互联网"。自定义提示词那两条路上不加 —— 与本方法的既有约定一致。
        prompt += nativeWebSearch ? " " + NativeWebSearch.SystemHint : "";
        return prompt + mode switch
        {
            ChatMode.Agent =>
                " You can call tools to inspect the user's selected SSH session (read terminal output, run one-shot commands, list directories, read files) " +
                "and to edit remote files (write_remote_file overwrites the whole file — read it first, then send the complete new content). " +
                "Prefer read-only commands; destructive commands and file writes require user approval and should be proposed carefully. " +
                LocalVersusRemoteNote(),
            ChatMode.Plan =>
                " You are in PLAN mode. You have read-only tools (read terminal output, list directories, read files) and you may use them freely to investigate. " +
                "You must NOT change anything, and no tool that could change anything is available to you. " +
                "Produce a concrete, ordered plan: what to check, what to change, the exact commands you would run, what could go wrong, and how to roll back. " +
                "End by telling the user to switch to Agent mode if they want you to carry it out.",
            _ =>
                " You have no tools in this mode: answer from the conversation itself. " +
                "If something genuinely needs to be checked on the server, say so and suggest switching to Agent mode rather than guessing."
        };
    }

    /// <summary>
    /// 告诉模型:内置工具作用于<b>远程服务器</b>,MCP 工具跑在<b>用户本机</b>。
    /// </summary>
    /// <remarks>
    /// 不说清楚就会出事,而且是实际出过的事:用户让本机的 xmind MCP 生成一个文件,模型
    /// 转头按远端路径(<c>/root/xxx.xmind</c>)汇报,结果服务器上没有、本机工作目录里也
    /// "看不到" —— 因为那本来就是两台机器。顺带把用户为各台 MCP 服务器配的工作目录报给它,
    /// 那是本机产物最可能落脚的地方;再指一下 <c>upload_local_file</c>,想放到服务器上就用它。
    /// </remarks>
    private string LocalVersusRemoteNote()
    {
        var note = new StringBuilder(
            " Additional tools may come from user-configured MCP servers (their names are prefixed with the server name). " +
            "IMPORTANT: MCP servers run on the USER'S OWN MACHINE, not on the SSH server. " +
            "Any file an MCP tool creates is a LOCAL file — it does not exist on the SSH server, and paths like /root/... " +
            "mean different things on the two machines. Never report a locally produced file as if it were on the server. " +
            "To put a local file on the server, call upload_local_file.");
        List<string> dirs =
        [
            // 本地进程型的每台都有工作目录(没填就是 ~/.velashell),都告诉模型;HTTP 型的没有"本机目录"可言
            .. _settings.McpServers
                        .Where(s => s.Enabled && s.Transport == McpTransportType.Stdio)
                        .Select(s => $"{DisplayName(s)}: {McpWorkspace.Resolve(s.WorkingDirectory)}")
        ];
        if (dirs.Count > 0)
        {
            note.Append(" Local MCP working directories (where their output most likely lands): ")
                .Append(string.Join("; ", dirs))
                .Append('.');
        }
        return note.ToString();

        static string DisplayName(McpServerConfig server)
            => string.IsNullOrWhiteSpace(server.Name) ? "(unnamed)" : server.Name.Trim();
    }

    /// <summary>
    /// 新建会话:终止进行中的请求,清空消息流并换一个会话 id ——
    /// 已发生的对话此刻已在时序库里,可从历史里翻回来。
    /// </summary>
    private void StartNewChat()
    {
        Cts?.Cancel();
        History.Clear();
        ClearQueuedMessages(); // 排着的插话是给上一段对话的,换会话就作废
        MessagesPanel.Children.Clear();
        ResetMessageWindow();
        ResetEditing();
        ResetCompaction();
        AlwaysApproved.Clear(); // 放行记忆只在同一段对话里有效
        _pendingApprovals.Clear(); // 连同卡片一起被清出可视树了,名册不能留着
        ResetUsage();
        ConversationId = ChatHistoryStore.NewConversationId();
        ThinkingHintShown = false;
        ConversationStartedAt = DateTimeOffset.UtcNow;
        PersistedCount = 0;
        _inputHistoryIndex = -1;
        StatusText.Text = "";
        ClearSuggestions();
        SetActiveView(PanelView.Chat);
        UpdateEmptyState();
        InputBox.TextArea.Focus();
    }

    // ---------- 审批交互 ----------

    // “总是允许”的操作键按对话各持一份(见 Conversation),只活在内存里:_alwaysApproved。

    /// <summary>一张还悬着的审批卡:它请求的是什么,以及怎么替用户按下去。</summary>
    /// <param name="Request">这次请求。</param>
    /// <param name="Resolve">落定回调,参数同 <c>Finish(approved, remember)</c>。</param>
    private sealed record PendingApproval(ApprovalRequest Request, Action<bool, bool> Resolve);

    /// <summary>
    /// 悬而未决的审批卡。<b>会同时有好几张</b>:模型一帧里发起多个工具调用时,
    /// 每个都各自 <c>await</c> 一次审批,卡片是一起挂出来的。
    /// 有了这份名册,"总是允许"和"把挡位切成全部自动"才能把<b>同批已经挂出来的</b>
    /// 那几张一并放行 —— 否则用户点完之后还得对着剩下的卡再点一遍,
    /// 看上去就像"设置根本没生效"。
    /// </summary>
    private readonly List<PendingApproval> _pendingApprovals = [];

    private async Task<bool> RequestApprovalAsync(ApprovalRequest request)
    {
        if (request.RepeatKey is { } key && AlwaysApproved.Contains(key))
        {
            return true;
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => AddApprovalCard(request, tcs));
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 把还悬着的审批卡按条件批量放行(挡位改成自动、或用户点了"总是允许"时)。
    /// 先拷一份再遍历:<c>Resolve</c> 会把自己从名册里摘掉。
    /// </summary>
    private void ReleasePendingApprovals(Func<ApprovalRequest, bool> match)
    {
        foreach (PendingApproval pending in _pendingApprovals.ToArray())
        {
            if (match(pending.Request))
            {
                pending.Resolve(true, false);
            }
        }
    }

    /// <summary>
    /// 审批挡位变了:<b>当场</b>推给工具箱与 MCP,并把已经挂出来、按新挡位本就不该问的卡放掉。
    /// </summary>
    /// <remarks>
    /// 以前这两个赋值只在 <c>SendAsync</c> 起手做一次,于是"跑到一半把挡位改成全部自动"
    /// 对这一轮完全无效 —— 工具箱手里还攥着旧值,照旧一条条问(用户实测反馈)。
    /// </remarks>
    private void ApplyApprovalMode()
    {
        _toolbox.Approval = _settings.Approval;
        _mcp.Approval = _settings.Approval;
        switch (_settings.Approval)
        {
            case ApprovalMode.Bypass:
                ReleasePendingApprovals(_ => true);
                break;
            case ApprovalMode.ReadOnlyAuto:
                // 与 AgentToolbox.ApproveAsync 同一把尺子:只放"确定无副作用"的命令
                ReleasePendingApprovals(r => r.Kind == "run_command" && ReadOnlyCommand.IsSafe(r.Detail));
                break;
        }
    }

    /// <summary>
    /// 审批卡上那枚风险标签的文案:只说这次调用<b>会做什么</b>(写远端文件 / 执行命令 / 写入终端),
    /// 不替用户判断危不危险 —— 同一条命令在不同机器上的分量完全不同,那是他的判断。
    /// 认不出的工具就把工具名原样摆出来,总好过瞎归类。
    /// </summary>
    private string RiskLabel(string kind)
    {
        if (kind.StartsWith("mcp", StringComparison.OrdinalIgnoreCase))
        {
            return _loc["RiskMcp"];
        }
        if (kind.Contains("write", StringComparison.OrdinalIgnoreCase))
        {
            return _loc["RiskWrite"];
        }
        if (kind.Contains("send", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("input", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("terminal", StringComparison.OrdinalIgnoreCase))
        {
            return _loc["RiskInput"];
        }
        return kind.Contains("command", StringComparison.OrdinalIgnoreCase)
               || kind.Contains("exec", StringComparison.OrdinalIgnoreCase)
            ? _loc["RiskExec"]
            : kind;
    }

    /// <summary>
    /// 记忆键去掉工具名前缀,只留人认得出的那半截:<c>run_command:du</c> → <c>du</c>。
    /// 按钮上要写的是"总是允许什么",写全键反而更绕。
    /// </summary>
    private static string RepeatKeyLabel(string repeatKey)
    {
        int colon = repeatKey.IndexOf(':');
        return colon >= 0 && colon + 1 < repeatKey.Length ? repeatKey[(colon + 1)..] : repeatKey;
    }

    /// <summary>卡片的头行:语义色图标 + 标题占满中间 + 右端一枚小标签(可省)。</summary>
    private Grid BuildCardHeader(string iconKey, string toneKey, string title, string? badge)
    {
        var head = new Grid { ColumnDefinitions = [with("Auto,*,Auto")] };
        head.Children.Add(new Decorator
        {
            Child = MakeIcon(iconKey, toneKey, 12),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var titleText = new TextBlock
        {
            Classes = { "meta" },
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = FindBrush(toneKey)
        };
        Grid.SetColumn(titleText, 1);
        head.Children.Add(titleText);
        if (badge is { Length: > 0 })
        {
            var chip = new Border
            {
                Classes = { "riskBadge" },
                Child = new TextBlock { Text = badge },
                BorderBrush = FindBrush(toneKey)
            };
            Grid.SetColumn(chip, 2);
            head.Children.Add(chip);
        }
        return head;
    }

    /// <summary>
    /// 一轮里出的错(请求失败、服务端拒绝)。挂成一张 error 卡而不是接在正文后面:
    /// 它不是模型说的话,混排会让人分不清哪句是回答、哪句是故障。
    /// 没有气泡可挂(还没开口就炸了)时退回状态行,至少别把消息吞掉。
    /// </summary>
    private void AddErrorCard(AssistantBubble? bubble, string detail)
    {
        if (bubble is null)
        {
            StatusText.Text = $"{_loc["Error"]}: {detail}";
            return;
        }
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(BuildCardHeader("Icon.triangle-alert", "VelaError", _loc["Error"], null));
        stack.Children.Add(new Border
        {
            Classes = { "cardCode" },
            Child = new SelectableTextBlock { Classes = { "mono" }, Text = Truncate(detail, 1200) }
        });
        stack.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc["ErrorKept"], TextWrapping = TextWrapping.Wrap });
        bubble.AddCard(new Border { Classes = { "errorCard" }, Child = stack });
        RequestAutoScroll(force: true);
    }

    private void AddApprovalCard(ApprovalRequest request, TaskCompletionSource<bool> tcs)
    {
        // 就地抓住"这张卡属于哪条回复":整轮结束后 _activeBubble 会被清掉,
        // 而卡上的按钮此刻可能还活着(用户按了停止、审批却没落定),那时仍要能把芯片撤回去。
        AssistantBubble? host = ActiveBubble;
        var stack = new StackPanel { Spacing = 6 };
        // 头行的盾牌与标题在落定时要换成绿/红,所以两者都留个把手
        Grid header = BuildCardHeader("AiIcon.shield-alert", "VelaWarning",
            _loc["ApprovalTitle"], RiskLabel(request.Kind));
        var headerIcon = (Decorator)header.Children[0];
        var headerTitle = (TextBlock)header.Children[1];
        stack.Children.Add(header);
        // 命令原文压在输入底色上:它是要被逐字读的东西,不该和说明文字一个背景
        stack.Children.Add(new Border
        {
            Classes = { "cardCode" },
            Child = new SelectableTextBlock { Classes = { "mono" }, Text = Truncate(request.Summary, 600) }
        });
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

        // 只有可重复、语义稳定的操作才给"总是允许"(写文件/敲终端不给,见 ApprovalRequest.RepeatKey)
        Button? alwaysButton = null;
        if (request.RepeatKey is { } repeatKey)
        {
            // 按钮上写清"总是允许的到底是什么"。原来只写「本次会话总是允许」,
            // 读起来像"这一整段对话里什么都别再问了",实际只对同名命令生效
            // (键是 run_command:du 这种)—— 用户点完 du 又被 journalctl 拦住,
            // 以为是功能坏了。把键摆到脸上,歧义就没了。
            alwaysButton = new Button
            {
                Content = _loc.F("ApproveAlwaysKey", RepeatKeyLabel(repeatKey)),
                Height = 24,
                Padding = new Thickness(12, 0)
            };
            ApplyThemeResource(alwaysButton, "VelaOutlineButtonTheme");
            ToolTip.SetTip(alwaysButton, _loc.F("ApproveAlwaysTip", repeatKey));
            buttons.Children.Add(alwaysButton);
        }
        buttons.Children.Add(denyButton);
        stack.Children.Add(buttons);
        // 这一轮此刻是停着的 —— 说出来,免得用户以为是卡住了(落定后这句就撤掉)
        var paused = new TextBlock
        {
            Classes = { "dim" },
            Text = _loc["ApprovalPaused"],
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(paused);
        var card = new Border { Classes = { "approvalCard" }, Child = stack };
        // 名册项要引用 Finish、Finish 又要引用名册项,先留个空位再回填
        PendingApproval? pending = null;

        void Finish(bool approved, bool remember)
        {
            // 先摘名册再干别的:下面放行同键的其它卡时会重入这里
            if (pending is null || !_pendingApprovals.Remove(pending))
            {
                return; // 已经落定过了(比如刚被批量放行)
            }
            buttons.IsVisible = false;
            host?.SetWaitingForApproval(false);
            if (remember && request.RepeatKey is { } key)
            {
                AlwaysApproved.Add(key);
            }

            // 落定之后整张卡换成结果态:左边条、盾牌、标题一起转绿(批准)或转红(拒绝)。
            // 只在末尾补一行小字是不够的 —— 一屏堆着五六张卡时,得能一眼扫出哪些已经过了。
            string toneKey = approved ? "VelaStatusConnected" : "VelaError";
            IBrush? tone = FindBrush(toneKey);
            card.Classes.Remove("approvalCard");
            card.Classes.Add(approved ? "approvedCard" : "deniedCard");
            headerIcon.Child = MakeIcon(approved ? "AiIcon.shield-check" : "AiIcon.shield-off", toneKey, 12);
            headerTitle.Foreground = tone;
            headerTitle.Text = approved
                ? (remember ? _loc.F("ApproveAlwaysKey", RepeatKeyLabel(request.RepeatKey ?? "")) : _loc["Approve"])
                : _loc["Deny"];
            paused.IsVisible = false;
            tcs.TrySetResult(approved);

            // 同一帧里模型常常一次发好几个同名命令,卡是一起挂出来的。
            // 点了"总是允许"却还要对着剩下那几张再点一遍,看上去就像设置没生效。
            if (remember && request.RepeatKey is { } sameKey)
            {
                ReleasePendingApprovals(other => other.RepeatKey == sameKey);
            }
        }

        pending = new PendingApproval(request, Finish);
        _pendingApprovals.Add(pending);

        approveButton.Click += (_, _) => Finish(true, remember: false);
        denyButton.Click += (_, _) => Finish(false, remember: false);
        alwaysButton?.Click += (_, _) => Finish(true, remember: true);
        // 挂进正在流的那条回复里(而不是气泡外面):后续正文还会继续往气泡里长,
        // 卡片摆在外头就会排到正文<b>前面</b>去,前后颠倒。
        if (host is not null)
        {
            host.AddCard(card);
            host.SetWaitingForApproval(true);
        }
        else
        {
            MessagesPanel.Children.Add(card);
        }
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
        // 头部一行:角色 + 悬停才显形的编辑/删除(平时不该有两个按钮杵在每条消息上)
        var header = new Grid { ColumnDefinitions = [with("*,Auto")] };
        header.Children.Add(new TextBlock { Classes = { "roleHeader" }, Text = _loc["You"] });
        var actions = new StackPanel
        {
            Classes = { "msgActions" },
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2
        };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        stack.Children.Add(header);

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

        var bubble = new Border { Classes = { "msg", "userMsg" }, Child = stack };
        actions.Children.Add(IconAction("Icon.pencil", _loc["EditMessage"], () => _ = EditUserMessageAsync(bubble, text)));
        actions.Children.Add(IconAction("Icon.trash-2", _loc["DeleteFromHere"], () => _ = DeleteFromAsync(bubble)));
        // 登记在加进 _history 之前:此刻的 Count 正是这条消息将要落到的下标
        TrackUserBubble(bubble, History.Count);
        MessagesPanel.Children.Add(bubble);
    }

    /// <summary>消息上的一枚小图标按钮(编辑/删除/重新生成共用)。</summary>
    private Button IconAction(string iconKey, string tip, Action onClick)
    {
        var host = new Decorator { Child = MakeIcon(iconKey, "VelaTextMuted", 12) };
        var button = new Button { Content = host };
        ApplyThemeResource(button, "AiGhostIconButtonTheme");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>一枚文件引用芯片(与输入框里那段彩色引用同色同名,悬停给全路径)。</summary>
    private static Border BuildReferenceChip(string path)
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
        if (this.TryFindResource(themeKey, ActualThemeVariant, out object? value) && value is ControlTheme theme)
        {
            control.Theme = theme;
        }
    }

    /// <summary>
    /// 按当前主题变体查资源。<b>必须带 <see cref="StyledElement.ActualThemeVariant" /></b>:
    /// 宿主把大半令牌(<c>VelaAccent</c> / <c>VelaStatusConnected</c> / <c>VelaText*</c> …)放在
    /// <c>ResourceDictionary.ThemeDictionaries</c> 的 Dark/Light 块里,不带变体的那个重载查不到,
    /// 一律返回 null —— 于是 <c>MakeIcon</c> 退回灰色、<c>Foreground = null</c> 的文字直接隐形。
    /// XAML 里的 <c>{DynamicResource}</c> 自己是主题感知的,所以毛病只出在代码后置这一侧:
    /// 表象就是"工具卡完成了图标还是灰的""审批卡的标题看不见"。
    /// </summary>
    private Geometry? FindIcon(string key)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is Geometry geometry
            ? geometry
            : null;

    /// <inheritdoc cref="FindIcon" />
    private IBrush? FindBrush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : null;

    /// <summary>
    /// lucide 描边图标(24 视图框经 Viewbox 等比缩放)。
    /// </summary>
    /// <remarks>
    /// 几何与描边都用 <see cref="DynamicResourceExtension" /> <b>绑</b>,不要一次性取值:
    /// ① 宿主的 <c>Icon.*</c> 与 <c>Vela*</c> 令牌要面板装载之后才查得到,建早了(构造期、
    /// <c>InitAsync</c> 的续体里)取到的是 null,结果是"位置占着、图形不见了";
    /// ② 一次性取值扛不住主题切换 —— 绑上去才会跟着 Dark/Light 变。
    /// 同一条经验在 <see cref="ToolPickerView" /> 里也写着,这里是它的统一版本。
    /// </remarks>
    private static Viewbox MakeIcon(string geometryKey, string brushKey, double size)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        path[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(geometryKey);
        path[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension(brushKey);
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    /// <summary>
    /// 一条 assistant 回复的可视化容器:头部(角色 · 步数 · 耗时)、思考折叠区、
    /// Markdown 段落与工具卡片,末尾是"复制整段 | 时间 · 模型"的元信息条。
    /// 版式对齐 GitHub Copilot 的回复块。
    /// </summary>
    internal sealed class AssistantBubble
    {
        private readonly ChatPanelView _owner;
        private readonly StackPanel _stack;
        private readonly TextBlock _header;
        private readonly TextBlock _phaseText;
        private readonly Border _phaseChip;
        private readonly Dictionary<string, ToolCard> _toolCards = [];
        private readonly StringBuilder _thinkingText = new();
        // 复制整段回复用的原文:正文按到达顺序原样攒着(Markdown 源码,不是渲染后的可视树)
        private readonly StringBuilder _replyText = new();
        private MarkdownSegment? _currentSegment;
        private Collapsible? _thinking;
        private bool _thinkingRenderScheduled;
        private string _phaseKey = "";
        private long _thinkingStartedAt;
        private TimeSpan? _thinkingElapsed;
        private int _steps;

        public Border Root { get; }

        /// <summary>目前为止收到的正文(Markdown 源码)。复制整段与"半截回复补进历史"都用它。</summary>
        public string ReplyText => _replyText.ToString();

        /// <summary>这一轮到底有没有收到过思考内容(用来判断"该有却没有")。</summary>
        public bool HasThinking => _thinkingText.Length > 0;

        /// <summary>这一轮除正文以外的东西,用于入库(见 <see cref="ChatTurnMeta" />)。</summary>
        public ChatTurnMeta Snapshot(string model, TimeSpan elapsed) => new(
            model,
            (long)elapsed.TotalMilliseconds,
            _thinkingText.ToString(),
            (long)(_thinkingElapsed?.TotalMilliseconds ?? 0),
            [.. _toolCards.Values.Select(c => c.Snapshot())]);

        /// <summary>从历史回放:先摆思考,再摆工具卡,最后才是正文(与直播时的先后一致)。</summary>
        public void Restore(ChatTurnMeta meta)
        {
            if (meta.Thinking.Length > 0)
            {
                _thinkingText.Append(meta.Thinking);
                _thinking = new Collapsible(_owner, _owner._loc.F("ThinkingDone",
                    ChatFormatting.Duration(TimeSpan.FromMilliseconds(meta.ThinkingMs))), trailingIconKey: "AiIcon.sparkles");
                _stack.Children.Insert(1, _thinking.Root);
                _thinking.SetBody(meta.Thinking);
                // 时长已经是存下来的,别让后面的 AppendText 再去按"现在"算一遍
                _thinkingElapsed = TimeSpan.FromMilliseconds(meta.ThinkingMs);
            }
            foreach (ChatToolCall tool in meta.Tools ?? [])
            {
                string id = Guid.NewGuid().ToString("N");
                AddToolCall(id, tool.Name, tool.Arguments);
                CompleteToolCall(id, tool.Result);
            }
        }

        public AssistantBubble(ChatPanelView owner)
        {
            _owner = owner;
            _stack = new StackPanel { Spacing = 4 };
            _header = new TextBlock
            {
                Classes = { "roleHeader" },
                Text = owner._loc["AssistantRole"],
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            // 阶段芯片:输入框那圈流光只说明"在动",这几个字说明"在干什么"。
            // 与角色行同处一行,读完"助手 · 3 步"顺势就读到了当前阶段。
            _phaseText = new TextBlock();
            _phaseChip = new Border
            {
                Classes = { "phaseChip" },
                Child = _phaseText,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var headerRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            headerRow.Children.Add(_header);
            headerRow.Children.Add(_phaseChip);
            _stack.Children.Add(headerRow);
            Root = new Border { Classes = { "msg" }, Child = _stack };
            // 第一个 token 到达之前,模型确实在"想这道题",即便它不吐 reasoning
            SetPhase("PhaseThinking");
        }

        /// <summary>
        /// 换阶段芯片的文案。<paramref name="waiting" /> 会把芯片换成实心 warn ——
        /// 那一档意味着界面此刻是<b>停着的</b>,分量得比"在动"更重。
        /// </summary>
        private void SetPhase(string key, bool waiting = false)
        {
            // AppendText 是逐块调的,同一档反复写会白白触发排版
            if (_phaseKey == key)
            {
                return;
            }
            _phaseKey = key;
            _phaseText.Text = _owner._loc[key];
            _phaseChip.Classes.Set("waiting", waiting);
            _phaseChip.IsVisible = true;
        }

        /// <summary>审批卡出现/落定时切换"等待你确认"这一档(由 <see cref="AddApprovalCard" /> 调)。</summary>
        public void SetWaitingForApproval(bool waiting) => SetPhase(waiting ? "PhaseWaiting" : "PhaseTool", waiting);

        /// <summary>
        /// 把一张卡(审批 / 失败)挂进这条回复里。
        /// 以前审批卡是直接扔进 <c>MessagesPanel</c> 的 —— 那样它排在整个气泡<b>下面</b>,
        /// 而后续正文还在气泡里继续往下长,读起来前后颠倒。
        /// </summary>
        public void AddCard(Control card)
        {
            _stack.Children.Add(card);
            _currentSegment = null; // 卡之后的新文本另起一个 Markdown 段
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
            SetPhase("PhaseWriting");
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
            SetPhase("PhaseThinking");
            if (_thinking is null)
            {
                // 默认收起(用户决策):标题一行就够说明"在想",要看内容自己点开。
                // 展开过就一路留着,标题从"正在思考…"变成"已思考 N 秒",正文照样往里灌。
                _thinkingStartedAt = Environment.TickCount64;
                _thinking = new Collapsible(_owner, _owner._loc["ThinkingActive"], trailingIconKey: "AiIcon.sparkles");
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
            SetPhase("PhaseTool");
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
            _phaseChip.IsVisible = false; // 一轮到此为止,阶段芯片退场(历史回放也走这里)
            // 默认本来就是收起的;这里不再强制折叠 —— 用户中途点开了就让它开着,
            // 别在他正读的时候把内容抽走。

            // 头部只留"这条回复是谁、走了几步、是不是被停掉的";时刻 / 模型 / 耗时归底部元信息条
            var head = new StringBuilder(_owner._loc["AssistantRole"]);
            if (_steps > 0)
            {
                head.Append(" · ").Append(_owner._loc.F("Steps", _steps));
            }
            if (cancelled)
            {
                head.Append(" · ").Append(_owner._loc["Cancelled"]);
            }
            _header.Text = head.ToString();

            if (at is not null || modelLabel is not null || elapsed is not null)
            {
                _stack.Children.Add(BuildFooter(modelLabel, at, elapsed));
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
            _thinking.SetTitle(_owner._loc.F("ThinkingDone", ChatFormatting.Duration(_thinkingElapsed.Value)));
        }

        /// <summary>
        /// 底部元信息条:<b>左边"时刻 · 模型 · 耗时",右边两枚动作</b>(复制整段 / 重新生成)。
        /// 要读的信息排在起手位置,次要动作靠右让位 —— 与设计图一致。
        /// </summary>
        private Border BuildFooter(string? modelLabel, DateTimeOffset? at, TimeSpan? elapsed)
        {
            var grid = new Grid { ColumnDefinitions = [with("*,Auto")] };

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var copyIcon = new Decorator { Child = ChatPanelView.MakeIcon("Icon.copy", "VelaTextMuted", 12) };
            var copy = new Button { Content = copyIcon };
            _owner.ApplyThemeResource(copy, "AiGhostIconButtonTheme");
            ToolTip.SetTip(copy, _owner._loc["CopyReply"]);
            copy.Click += (_, _) => _owner.CopyToClipboard(_replyText.ToString(), copy, copyIcon);
            buttons.Children.Add(copy);
            // 重新生成只挂在<b>最后一条</b>回复上:重跑中间那条,后面的全都作废了,
            // 与其悄悄连坐,不如让用户走"编辑上一条"这条明确的路。
            buttons.Children.Add(_owner.IconAction("Icon.refresh-cw", _owner._loc["Regenerate"],
                () => _ = _owner.RegenerateIfLastAsync(Root)));
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            var meta = new TextBlock { Classes = { "meta" } };
            var parts = new List<string>(3);
            if (at is { } stamp)
            {
                parts.Add(stamp.ToString("HH:mm"));
            }
            if (!string.IsNullOrWhiteSpace(modelLabel))
            {
                parts.Add(modelLabel);
            }
            if (elapsed is { } span)
            {
                parts.Add(ChatFormatting.Duration(span));
            }
            meta.Text = string.Join(" · ", parts);
            ToolTip.SetTip(meta, meta.Text);
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
            // 公式控件是渲染时新建的,每次定稿后补一次颜色(见 ApplyMathColors)。
            // LiveMarkdown 2.4.0 撤掉了 RenderedTextProjectionChanged 事件,改由
            // RenderedTextProjection 属性发变更通知 —— 每次渲染定稿仍恰好来一次。
            Host.PropertyChanged += (_, e) =>
            {
                if (e.Property == MarkdownRenderer.RenderedTextProjectionProperty)
                {
                    owner.ApplyMathColors(Host);
                }
            };
            // 回复里的链接要能点开(远端路径 = 下载下来)—— 见 OnMarkdownLinkClicked
            Host.LinkClick += (_, e) => owner.OnMarkdownLinkClicked(e);
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
        /// <param name="iconKey">
        /// 折叠箭头后面那枚小图标(压缩分隔条 = 剪刀)。
        /// 省略就只有箭头 —— 那一列是 Auto,不给东西就是 0 宽,不占位。
        /// </param>
        /// <param name="iconBrushKey">图标颜色令牌;省略走弱化色。</param>
        /// <param name="trailingIconKey">贴在行尾的图标(思考 = 星火,见设计图)。</param>
        public Collapsible(ChatPanelView owner, string title, bool expanded = false,
            string? iconKey = null, string? iconBrushKey = null, string? trailingIconKey = null)
        {
            var header = new Grid
            {
                // 箭头 | 前置图标 | 标题 | 行尾图标 —— 两个图标位都是 Auto,不给就 0 宽
                ColumnDefinitions = [with("Auto,Auto,*,Auto")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            // 同 MakeIcon:绑而不是取一次,否则建早了没图形、换主题也不跟着变
            _chevron[!Avalonia.Controls.Shapes.Path.DataProperty] =
                new DynamicResourceExtension("Icon.chevron-right");
            _chevron[!Avalonia.Controls.Shapes.Shape.StrokeProperty] =
                new DynamicResourceExtension("VelaTextMuted");
            var chevronBox = new Viewbox { Width = 10, Height = 10, Child = _chevron, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            header.Children.Add(chevronBox);
            if (iconKey is { Length: > 0 })
            {
                var iconBox = new Decorator
                {
                    Child = ChatPanelView.MakeIcon(iconKey, iconBrushKey ?? "VelaTextMuted", 11),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                Grid.SetColumn(iconBox, 1);
                header.Children.Add(iconBox);
            }
            _title = new TextBlock { Classes = { "dim" }, Text = title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(_title, 2);
            header.Children.Add(_title);
            if (trailingIconKey is { Length: > 0 })
            {
                var trailing = new Decorator
                {
                    Child = ChatPanelView.MakeIcon(trailingIconKey, "VelaTextMuted", 11),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                Grid.SetColumn(trailing, 3);
                header.Children.Add(trailing);
            }

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
        private readonly string _name;
        private string _result = "";

        public Border Root { get; }

        public ToolCard(ChatPanelView owner, string name, string argumentsJson)
        {
            _owner = owner;
            _name = name;
            _argumentsJson = argumentsJson;

            var header = new Grid
            {
                ColumnDefinitions = [with("Auto,Auto,*,Auto")],
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _statusIconHost = new Decorator
            {
                Child = ChatPanelView.MakeIcon("Icon.ellipsis", "VelaAccent", 11),
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
                Text = ChatFormatting.OneLine(argumentsJson, 160),
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(argsText, 2);
            header.Children.Add(argsText);

            _chevron = new Avalonia.Controls.Shapes.Path
            {
                Width = 24,
                Height = 24,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            // 同 MakeIcon:绑而不是取一次,否则建早了没图形、换主题也不跟着变
            _chevron[!Avalonia.Controls.Shapes.Path.DataProperty] =
                new DynamicResourceExtension("Icon.chevron-right");
            _chevron[!Avalonia.Controls.Shapes.Shape.StrokeProperty] =
                new DynamicResourceExtension("VelaTextMuted");
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

        /// <summary>入库用的存档。</summary>
        public ChatToolCall Snapshot() => new(_name, _argumentsJson, _result);

        public void Complete(string result)
        {
            _result = result;
            _statusIconHost.Child = ChatPanelView.MakeIcon("Icon.circle-check", "VelaStatusConnected", 11);
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
    }
}

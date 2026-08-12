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

    private AiSettings _settings = new();
    private SettingsView? _settingsView;
    private List<AiProviderConfig> _providers = [];
    private List<SessionInfo> _sessions = [];
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _autoScroll = true;
    private bool _scrollScheduled;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private ThemeName _codeBlockTheme = ThemeName.DarkPlus;
    private Color _mathTextColor = Colors.Black;
    private Color _mathErrorColor = Colors.Red;

    /// <summary>由插件构造(UI 线程,经 ShowPanelAsync 工厂)。</summary>
    public ChatPanelView(IPluginContext context, AiSettingsStore store)
    {
        _context = context;
        _store = store;
        _loc = new Loc(context.Host.Locale);
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
        SettingsToggle.IsCheckedChanged += (_, _) =>
        {
            bool showSettings = SettingsToggle.IsChecked == true;
            SettingsHost.IsVisible = showSettings;
            ChatScroll.IsVisible = !showSettings;
        };
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
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 已释放则忽略
        }
        _ = _mcp.DisposeAsync().AsTask();
    }

    /// <summary>从命令入口外部注入一条消息并直接发送(任意线程可调)。</summary>
    public void SendExternal(string text) => Dispatcher.UIThread.Post(() => _ = SendAsync(text));

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
        InputBox.PlaceholderText = _loc["InputPlaceholder"];
        SendText.Text = _loc["Send"];
        StopText.Text = _loc["Stop"];
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
        _providers = _settings.Providers.ToList();
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
            _sessions = all.Where(s => s.State == SessionState.Connected).ToList();
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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = SendAsync(InputBox.Text ?? "");
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsVisible = !busy;
        StopButton.IsVisible = busy;
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

    private async Task SendAsync(string text)
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
        SettingsToggle.IsChecked = false;

        AddUserBubble(text);
        _history.Add(new ChatMessage(ChatRole.User, text));
        var bubble = new AssistantBubble(this);
        MessagesPanel.Children.Add(bubble.Root);
        RequestAutoScroll(force: true);

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        try
        {
            IChatClient client = await _store.CreateClientAsync(provider, cancellationToken: token);
            var options = new ChatOptions();
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
            if (response.Usage is { } usage)
            {
                _totalInputTokens += usage.InputTokenCount ?? 0;
                _totalOutputTokens += usage.OutputTokenCount ?? 0;
            }
            StatusText.Text = _loc.F("Usage", _totalInputTokens, _totalOutputTokens);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _loc["Cancelled"];
        }
        catch (Exception ex)
        {
            _context.Log.Error("AI request failed.", ex);
            bubble.AppendText($"\n\n**[{_loc["Error"]}]** {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            bubble.FinishStreaming();
            RequestAutoScroll();
        }
    }

    private void RenderUpdate(AssistantBubble bubble, ChatResponseUpdate update)
    {
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
                " You can call tools to inspect the user's selected SSH session (read terminal output, run one-shot commands, read files). " +
                "Prefer read-only commands; destructive commands require user approval and should be proposed carefully. " +
                "Additional tools may come from user-configured MCP servers (their names are prefixed with the server name).";
        }
        return prompt;
    }

    /// <summary>新建会话:终止进行中的请求,清空历史与消息流,回到聊天视图。</summary>
    private void StartNewChat()
    {
        _cts?.Cancel();
        _history.Clear();
        MessagesPanel.Children.Clear();
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        StatusText.Text = "";
        SettingsToggle.IsChecked = false;
        InputBox.Focus();
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

    private void AddUserBubble(string text)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Classes = { "roleHeader" }, Text = _loc["You"] });
        stack.Children.Add(new SelectableTextBlock { Classes = { "body" }, Text = text });
        MessagesPanel.Children.Add(new Border { Classes = { "msg", "userMsg" }, Child = stack });
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
    private Control MakeIcon(string geometryKey, string brushKey, double size)
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

    /// <summary>一条 assistant 回复的可视化容器:Markdown 段落、思考折叠区与工具卡片。</summary>
    private sealed class AssistantBubble
    {
        private readonly ChatPanelView _owner;
        private readonly StackPanel _stack;
        private readonly Dictionary<string, ToolCard> _toolCards = [];
        private MarkdownSegment? _currentSegment;
        private Collapsible? _thinking;
        private readonly StringBuilder _thinkingText = new();
        private bool _thinkingRenderScheduled;

        public Border Root { get; }

        public AssistantBubble(ChatPanelView owner)
        {
            _owner = owner;
            _stack = new StackPanel { Spacing = 4 };
            _stack.Children.Add(new TextBlock { Classes = { "roleHeader" }, Text = owner._loc["AssistantRole"] });
            Root = new Border { Classes = { "msg" }, Child = _stack };
        }

        public void AppendText(string text)
        {
            if (text.Length == 0)
            {
                return;
            }
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
                _thinking = new Collapsible(_owner, _owner._loc["Thinking"]);
                _stack.Children.Insert(1, _thinking.Root);
            }
            // 逐 token 全量刷文本是 O(n²) 的字符串与排版开销,与 Markdown 段同款节流
            if (!_thinkingRenderScheduled)
            {
                _thinkingRenderScheduled = true;
                DispatcherTimer.RunOnce(() =>
                {
                    _thinkingRenderScheduled = false;
                    _thinking?.SetBody(_thinkingText.ToString());
                }, TimeSpan.FromMilliseconds(200));
            }
        }

        public void AddToolCall(string callId, string name, string argumentsJson)
        {
            if (_toolCards.ContainsKey(callId))
            {
                return;
            }
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

        /// <summary>流结束(成功/取消/出错都会走到):思考区补齐尾部。</summary>
        public void FinishStreaming()
        {
            // Markdown 段不需要收口:LiveMarkdown 自己会把最后一次追加渲染完
            _thinking?.SetBody(_thinkingText.ToString());
        }
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
            // 公式控件是渲染时新建的,每次定稿后补一次颜色(见 ApplyMathColors)
            Host.RenderedTextProjectionChanged += (_, _) => owner.ApplyMathColors(Host);
        }

        public void Append(string text) => _text.Append(text);
    }

    /// <summary>紧凑可折叠区(思考过程):单行头部点击展开,正文等宽文本。</summary>
    private sealed class Collapsible
    {
        private readonly SelectableTextBlock _body;
        private readonly Avalonia.Controls.Shapes.Path _chevron;
        private readonly ScrollViewer _details;

        public Border Root { get; }

        public Collapsible(ChatPanelView owner, string title)
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
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
            var titleText = new TextBlock { Classes = { "dim" }, Text = title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(titleText, 1);
            header.Children.Add(titleText);

            _body = new SelectableTextBlock { Classes = { "mono" } };
            _details = new ScrollViewer
            {
                MaxHeight = 200,
                Content = _body,
                IsVisible = false,
                Margin = new Thickness(15, 4, 0, 0)
            };
            header.PointerPressed += (_, _) => Toggle();

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_details);
            Root = new Border { Classes = { "toolCard" }, Child = stack };
        }

        public void SetBody(string text) => _body.Text = text;

        private void Toggle()
        {
            _details.IsVisible = !_details.IsVisible;
            _chevron.RenderTransform = _details.IsVisible ? new RotateTransform(90) : null;
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
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
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

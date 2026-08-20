using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;
using KeyModifiers = VelaShell.Services.KeyModifiers;

namespace VelaShell.Views;

/// <summary>
/// 单个终端标签页的视图:承载终端控件,并叠加行内搜索、命令补全弹层、
/// 滚动条同步与快捷键回退等交互逻辑。
/// </summary>
public partial class TerminalTabView : UserControl
{
    private readonly IKeyboardShortcutService _shortcutService;

    // ---- 终端内搜索(spec §5.3) ------------------------------------

    private IReadOnlyList<BufferSearchHit> _searchHits = [];

    private int _searchIndex = -1;
    private bool _syncingScrollBar;
    private VelaTerminalControl? _termControl;

    /// <summary>无参构造:使用默认的 <see cref="KeyboardShortcutService"/>(供 XAML/设计器实例化)。</summary>
    public TerminalTabView()
        : this(new KeyboardShortcutService()) { }

    /// <summary>使用指定的快捷键服务构造视图并完成事件接线。</summary>
    public TerminalTabView(IKeyboardShortcutService shortcutService)
    {
        _shortcutService =
            shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        InitializeComponent();
        Focusable = true;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += (_, _) =>
        {
            HookTerminalControl();
            HookSuggestions();
        };
        ScrollBarView?.Scroll += OnScrollBarScroll;

        // 采用 Tunnel 路由,使已断开的标签能在终端控件把按键交给 PTY 之前截获 Enter / Ctrl+R
        // 用于重连(以及 Ctrl+F 打开搜索)。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 平台侧关闭弹层(如系统截图覆盖层/激活切换导致宿主隐藏它)时,IsOpen 与
        // 幽灵/列表状态会与本类脱钩;订阅 Closed 统一对账,防止"看不见却仍拦键"的
        // 僵尸弹层
        SuggestPopup.Closed += (_, _) => OnSuggestPopupClosedByPlatform();
        SearchBox.TextChanged += (_, _) => RunSearch();
        SearchNext.Click += (_, _) => MoveHit(+1);
        SearchPrev.Click += (_, _) => MoveHit(-1);
        SearchClose.Click += (_, _) => CloseSearch();
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SuggestList.Tapped += (_, _) => AcceptSuggestion();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 自愈守卫:弹层开着但焦点既不在终端控件也不在本视图(回退层)上,说明焦点
        // 被外因(系统截图覆盖层抢焦点、原生弹层被平台隐藏等)拖进了弹层或别处——
        // 正常交互期间焦点始终在终端/视图上。此时任何按键都先收口弹层并把焦点还给
        // 终端,保证输入永远可自行恢复,而不是只能关标签页重连。
        if (SuggestPopup.IsOpen && !IsFocused && _termControl is { IsFocused: false })
        {
            SuggestDiag.Log("self-heal", $"view={GetHashCode():X} key={e.Key} focus escaped -> dismiss+refocus");
            DismissSuggestions(suppress: false);
            FocusTerminal();
        }

        // 补全弹层的键位优先(↑↓/Tab/Esc),否则方向键会被终端吃掉发成 ESC 序列。
        if (HandleSuggestionKey(e))
        {
            return;
        }

        // Ctrl+F 切换终端内搜索栏(spec §5.3)。
        if (e is { Key: Key.F, KeyModifiers: Avalonia.Input.KeyModifiers.Control })
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }
        if (DataContext is not TerminalTabViewModel vm)
        {
            return;
        }

        // Esc 关闭一个已经断开的标签页。条件挂在覆盖层可见性上而非单看连接状态:
        // 连接正常时 Esc 必须原样透传给远端(vim/less 等重度依赖),只有覆盖层挡在
        // 终端前面、按键本就到不了 PTY 时才改写它的语义。
        if (e is { Key: Key.Escape, KeyModifiers: Avalonia.Input.KeyModifiers.None }
            && vm.ShowDisconnectedOverlay)
        {
            vm.RequestClose();
            e.Handled = true;
            return;
        }
        if (vm.ConnectionStatus != SessionStatus.Disconnected)
        {
            return;
        }
        bool reconnect =
            e
            is { Key: Key.Enter, KeyModifiers: Avalonia.Input.KeyModifiers.None }
                or { Key: Key.R, KeyModifiers: Avalonia.Input.KeyModifiers.Control };
        if (!reconnect)
        {
            return;
        }
        vm.RequestReconnect();
        e.Handled = true;
    }

    // ---- 命令补全弹层(plan.md #16) ----------------------------------------

    /// <summary>补全数据接线的目标 VM(视图被 Dock 回收复用,随 DataContext 重挂)。</summary>
    private TerminalTabViewModel? _suggestVm;

    private IReadOnlyList<CommandSuggestion> _suggestions = [];
    private string? _suppressedInput;
    private int _suggestSeq;

    // 建议查询防抖:每个可打印字节都触发 InputChanged,连打时不该每键都对
    // 500 条历史全量过滤 + 排序 + 分配一轮(幽灵文本的本地即时消耗不受影响,手感不变)。
    private IDisposable? _suggestDebounce;
    private const int SuggestDebounceMs = 90;

    /// <summary>幽灵文本对应的完整候选命令(接受时据此设置抑制值);null 即无幽灵。</summary>
    private string? _ghostFull;

    private void HookSuggestions()
    {
        _suggestVm?.InputTracker.InputChanged -= OnTrackedInputChanged;
        _suggestVm?.Disconnected -= OnSuggestVmDisconnected;
        _suggestVm = DataContext as TerminalTabViewModel;
        _suggestVm?.InputTracker.InputChanged += OnTrackedInputChanged;
        _suggestVm?.Disconnected += OnSuggestVmDisconnected;
        SuggestDiag.Log(
            "hook",
            $"view={GetHashCode():X} vm={_suggestVm?.GetHashCode():X} provider={(_suggestVm?.SuggestionProvider is null ? "null" : "ok")}"
        );
        DismissSuggestions(suppress: false);
    }

    /// <summary>断连横幅会移动光标,残留的幽灵会被画到新光标处——断连即收口。</summary>
    private void OnSuggestVmDisconnected(object? sender, EventArgs e) =>
        DismissSuggestions(suppress: false);

    /// <summary>统一收起智能提示:弹层与幽灵文本同生共灭,防止各清一半的残留。</summary>
    private void DismissSuggestions(bool suppress)
    {
        CloseSuggestPopup(suppress);
        ClearGhost();
    }

    private void ClearGhost()
    {
        _ghostFull = null;
        _termControl?.ClearGhostSuggestion();
    }

    private void OnTrackedInputChanged()
    {
        string input = EffectiveInput();
        // 每键路径:先查开关再拼实参(急切求值防线,见 SuggestDiag.IsEnabled)。
        if (SuggestDiag.IsEnabled)
        {
            SuggestDiag.Log(
                "changed",
                $"""
                view={GetHashCode():X} attached={IsAttachedToVisualTree()} known="{_suggestVm?.InputTracker.CurrentInput
                    ?? "<unknown>"}" effective="{input}" suppressed="{_suppressedInput}"
                """
            );
        }
        // 程序注入(快捷命令下发)不是用户键入:收起并记为已抑制,待用户继续
        // 编辑(输入内容变化)后再恢复正常补全。
        if (_suggestVm?.IsProgrammaticInput == true)
        {
            CloseSuggestPopup(suppress: true);
            ClearGhost();
            return;
        }
        // 备用屏(vim/htop 等全屏程序)内不做命令补全。
        if (_termControl?.IsAlternateScreenActive == true)
        {
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        if (string.IsNullOrEmpty(input) || input == _suppressedInput || IsInteractivePromptLine())
        {
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        _suppressedInput = null;

        // 键入/退格仍与当前候选同前缀时,保持候选不动:控件在每帧按真实光标列自行切出
        // 剩余部分(见 CurrentGhostRemainder),内容与位置同源于回显后的光标,恒不失步——
        // 故这里不再逐键推送剩余文本(那会让缩短/增长的幽灵在回显前画到旧光标处而抖动/卡顿)。
        // InputChanged 在"发往 PTY"时同步触发、早于回显,因此一旦键入偏离候选(下面 else),
        // 会在该帧回显重绘之前就清除,不会闪出错位的幽灵。未知态(按过方向键)下 EffectiveInput
        // 降级为试探段,同样按"紧贴光标之前"安全地追加式提示。
        if (
            _ghostFull is { } full
            && full.Length > input.Length
            && full.StartsWith(input, StringComparison.Ordinal)
            && !HasTextRightOfCursor()
        )
        {
            // 保持:控件持有完整候选与锚列,重绘即现切,无需在此推送或重绘。
        }
        else
        {
            ClearGhost();
        }

        // 面板未开时结果落到幽灵文本;面板已开(Alt+Enter 召出)则持续跟随键入刷新列表。
        // 查询防抖:停键 90ms 后才真正打提供者;输入与面板状态在触发时重新取,
        // 避免用调度时刻的陈旧快照查询。
        _suggestDebounce?.Dispose();
        _suggestDebounce = DispatcherTimer.RunOnce(() =>
        {
            _suggestDebounce = null;
            string current = EffectiveInput();
            if (string.IsNullOrEmpty(current) || current == _suppressedInput)
            {
                return;
            }
            bool panelOpen = SuggestPopup.IsOpen;
            _ = UpdateSuggestionsAsync(current, panelOpen ? 20 : 8, openPanel: panelOpen);
        }, TimeSpan.FromMilliseconds(SuggestDebounceMs));
    }

    /// <summary>
    /// 光标右侧同一行是否还有非空白内容(行中编辑、zsh 右提示符等):有则不显示
    /// 幽灵文本——叠画会盖住既有内容,且此时"补全在行尾"的语义不成立。
    /// 宽字符行的列→索引映射有偏差,保守放行(测不准时宁可显示)。
    /// </summary>
    private bool HasTextRightOfCursor()
    {
        if (_termControl is null)
        {
            return false;
        }
        try
        {
            string line = _termControl.GetBufferLine(_termControl.CursorRow);
            int col = _termControl.CursorCol;
            return line.Length > col && !string.IsNullOrWhiteSpace(line[col..]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 交互提示行不弹命令补全(sudo 密码提示、apt 的 "[Y/n]" 确认行下按键
    /// 仍弹智能提示)。把光标行末尾已回显的输入剥掉后,按三类特征判定"此刻不是在编写
    /// shell 命令,而是在回答程序的提问":
    ///   ① 密码/口令/验证码 —— 冒号(全/半角)结尾 + 密码类关键词;
    ///   ② 是否类确认      —— 结尾括号选项 [Y/n]/(yes/no)/[Y/I/N/O/D/Z]/[是/否],
    ///                        或整行以问号结尾(cp/rm 的 "overwrite 'x'?"/"remove 'x'?");
    ///   ③ 编号选择菜单    —— 冒号结尾 + "选择/编号/选项/number/selection/choice" 等词
    ///                        (update-alternatives、tzdata、npm init 的序号选单)。
    /// 这些场景把整条历史命令塞进程序输入里有害无益,一律不弹(含 Alt+Enter 主动召出)。
    /// </summary>
    private bool IsInteractivePromptLine()
    {
        if (_termControl is null)
        {
            return false;
        }
        try
        {
            return IsInteractivePrompt(
                _termControl.GetBufferLine(_termControl.CursorRow),
                EffectiveInput()
            );
        }
        catch
        {
            return false; // 读缓冲失败(极端竞态):宁可照常弹出,不影响正常补全。
        }
    }

    /// <summary>剥掉光标行末尾已回显的输入,只留下程序打印的提示部分。</summary>
    private static string StripEcho(string line, string typed)
    {
        string prompt = line.TrimEnd();
        if (typed.Length > 0 && prompt.EndsWith(typed, StringComparison.Ordinal))
        {
            prompt = prompt[..^typed.Length].TrimEnd();
        }
        return prompt;
    }

    /// <summary>密码/是否/编号/REPL 四类交互提示行的合并判定(供智能补全与 Alt+Enter 共用)。</summary>
    internal static bool IsInteractivePrompt(string line, string typed)
    {
        string prompt = StripEcho(line, typed);
        return prompt.Length != 0
            && (
                IsSecretPromptCore(prompt)
                || IsChoicePromptCore(prompt)
                || IsSelectionPromptCore(prompt)
                || IsReplPromptCore(prompt)
            );
    }

    // 单独保留:密码类比"是否/编号"更严格,某些位置(如口令输入)只想拦密码而不误伤确认行。
    internal static bool IsSecretPrompt(string line, string typed) =>
        IsSecretPromptCore(StripEcho(line, typed));

    private static bool IsSecretPromptCore(string prompt)
    {
        if (prompt.Length == 0 || (prompt[^1] != ':' && prompt[^1] != '：'))
        {
            return false;
        }
        foreach (
            string keyword in (ReadOnlySpan<string>)
                [
                    "password",
                    "passphrase",
                    "passwd",
                    "密码",
                    "口令",
                    "verification code",
                    "验证码",
                    "认证码",
                ]
        )
        {
            if (prompt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 是否类确认行:结尾带斜杠分隔的括号选项([Y/n]/(yes/no)/[是/否] 等),或整行以问号
    /// 结尾(未显式列出选项的 "overwrite 'x'?"/"是否覆盖?")。问号分支要求提示含空格或
    /// 非 ASCII 字(即成句),排除主题里以单个 "?" 作装饰的提示符。
    /// </summary>
    private static bool IsChoicePromptCore(string prompt)
    {
        char tail = prompt[^1];
        if ((tail == '?' || tail == '？') && (prompt.Contains(' ') || HasNonAscii(prompt)))
        {
            return true;
        }
        return ChoiceTokenRegex().IsMatch(prompt);
    }

    private static bool HasNonAscii(string s)
    {
        foreach (char c in s)
        {
            if (c > 0x7F)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>编号选择菜单:冒号结尾且含"选择/编号/选项/number/selection/choice"等词。</summary>
    private static bool IsSelectionPromptCore(string prompt)
    {
        if (prompt[^1] is not ':' and not '：')
        {
            return false;
        }
        foreach (
            string keyword in (ReadOnlySpan<string>)
                [
                    "selection",
                    "select",
                    "choose",
                    "choice",
                    "number",
                    "请选择",
                    "请输入",
                    "选择",
                    "编号",
                    "序号",
                    "选项",
                ]
        )
        {
            if (prompt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// REPL/交互客户端提示符:Python(">>>" 主提示、"..." 续行,均按整段精确匹配)、
    /// mysql/mariadb/sqlite/psql、IPython、gdb/lldb/pdb、irb 等。裸 ">"(node/R/mongosh)
    /// 与用户自定义 shell 提示符无法区分,刻意不纳入,以免误伤正常补全。
    /// </summary>
    private static bool IsReplPromptCore(string prompt) =>
        prompt is ">>>" or "..." || ReplPromptRegex().IsMatch(prompt);

    /// <summary>
    /// 建议所依据的"当前输入":确定态用整行;未知态(按过方向键/F 键后)降级为
    /// 自最后一个控制键以来键入的字符段——建议不因一次按键失灵,补全只追加/等量回删,
    /// 永远与屏幕一致。
    /// </summary>
    private string EffectiveInput() =>
        _suggestVm?.InputTracker.CurrentInput
        ?? _suggestVm?.InputTracker.TentativeRun
        ?? string.Empty;

    private bool IsAttachedToVisualTree() => VisualRoot is not null;

    private async Task UpdateSuggestionsAsync(string prefix, int max, bool openPanel)
    {
        if (_suggestVm?.SuggestionProvider is not { } provider)
        {
            return;
        }
        int seq = ++_suggestSeq;
        try
        {
            IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(
                prefix,
                max
            );

            // 建议查询可能异步恢复;UI 变更强制回到 UI 线程(fire-and-forget 场景下
            // 线程亲和性异常会被静默吞掉,表现为"弹层时灵时不灵")。
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ApplySuggestions(seq, items, openPanel));
                return;
            }
            ApplySuggestions(seq, items, openPanel);
        }
        catch
        {
            // 建议是锦上添花,任何失败都只安静收起,不得污染后续键入的状态。
            CloseSuggestPopup(suppress: false);
            ClearGhost();
        }
    }

    private void ApplySuggestions(int seq, IReadOnlyList<CommandSuggestion> items, bool openPanel)
    {
        // 键入速度快于查询时只应用最后一次结果;期间行内容变了(seq 落后)直接丢弃。
        if (seq != _suggestSeq)
        {
            SuggestDiag.Log("apply", $"seq {seq} stale (now {_suggestSeq})");
            return;
        }
        if (items.Count == 0)
        {
            SuggestDiag.Log("apply", "0 items -> close");
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        // 结果落地时输入已被抑制(接受补全/程序注入赛过了在途查询):丢弃,不重开。
        string input = EffectiveInput();
        if (input == _suppressedInput)
        {
            SuggestDiag.Log("apply", "input suppressed -> drop");
            return;
        }
        if (!openPanel)
        {
            ApplyGhost(input, items);
            return;
        }
        // 查询在途期间窗口失活(切去截图/别的应用):不得把弹层开到别的程序上面,
        // 也不得在自己被系统隐藏时开出"看不见却拦键"的僵尸弹层——直接丢弃。
        if (_hostWindow is { IsActive: false })
        {
            SuggestDiag.Log("apply", "host window inactive -> drop, no open");
            CloseSuggestPopup(suppress: false);
            ClearGhost();
            return;
        }
        ClearGhost();
        _suggestions = items;
        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = 0;
        SuggestPopup.PlacementTarget ??= TerminalHost;
        if (_termControl is not null)
        {
            // 锚定在输入起点列(而非光标列):面板不随键入逐列漂移,与主流终端一致。
            SuggestPopup.PlacementRect = _termControl.GetCursorRect(input.Length);
        }
        SuggestPopup.IsOpen = true;
        SuggestDiag.Log(
            "apply",
            $"opened with {items.Count} items, isOpen={SuggestPopup.IsOpen}, target={(SuggestPopup.PlacementTarget is null ? "null" : "ok")}"
        );
    }

    /// <summary>
    /// 幽灵文本模式:取首个与当前输入严格同前缀(区分大小写——剩余部分要原样写入
    /// shell)且更长的候选,把剩余部分叠画在光标后;无合格候选则清除。
    /// </summary>
    private void ApplyGhost(string input, IReadOnlyList<CommandSuggestion> items)
    {
        if (_termControl is null || input.Length == 0 || HasTextRightOfCursor())
        {
            ClearGhost();
            return;
        }
        foreach (CommandSuggestion item in items)
        {
            if (
                item.Text.Length > input.Length
                && item.Text.StartsWith(input, StringComparison.Ordinal)
            )
            {
                _ghostFull = item.Text;
                // 只交完整候选;剩余部分由控件每帧按屏上"光标左侧已回显文本"现算(见
                // CurrentGhostRemainder),不再逐键回推,故键入/退格/空格均恒贴光标、不受回显延迟影响。
                _termControl.SetGhostSuggestion(item.Text);
                SuggestDiag.Log("ghost", $"\"{item.Text}\" remainder {item.Text.Length - input.Length} chars");
                return;
            }
        }
        ClearGhost();
    }

    /// <summary>
    /// 弹层被平台侧关闭后的对账:清掉本类残留的列表与幽灵,避免 IsOpen 已 false
    /// 而键位分支仍按"面板开着"决策;若焦点被弹层带走,交还终端。
    /// </summary>
    private void OnSuggestPopupClosedByPlatform()
    {
        SuggestDiag.Log(
            "popup-closed",
            $"view={GetHashCode():X} termFocused={_termControl?.IsFocused} attached={IsAttachedToVisualTree()}"
        );
        _suggestions = [];
        ClearGhost();
        if (_termControl is { IsFocused: false } && IsAttachedToVisualTree())
        {
            FocusTerminal();
        }
    }

    private void CloseSuggestPopup(bool suppress)
    {
        if (suppress)
        {
            _suppressedInput = EffectiveInput();
        }
        if (SuggestPopup.IsOpen)
        {
            // 只记真实的开→关转换,不记每键空转(诊断关闭时本行零开销)。
            SuggestDiag.Log("close", $"view={GetHashCode():X} suppress={suppress}");
            SuggestPopup.IsOpen = false;
        }
        _suggestions = [];
    }

    /// <summary>补全弹层键位;返回 true 表示按键已被弹层消费。</summary>
    private bool HandleSuggestionKey(KeyEventArgs e)
    {
        // Alt+Enter:主动弹出补全面板(空输入 = 快捷命令全量 + 最近历史)。
        // 交互提示行(密码/是否确认/编号选单)与备用屏连主动召出也不给:把整条
        // 历史命令插进程序输入里有害无益。
        if (e is { Key: Key.Enter, KeyModifiers: Avalonia.Input.KeyModifiers.Alt })
        {
            if (IsInteractivePromptLine() || _termControl?.IsAlternateScreenActive == true)
            {
                e.Handled = true;
                return true;
            }
            _suppressedInput = null;
            _ = UpdateSuggestionsAsync(EffectiveInput(), 20, openPanel: true);
            e.Handled = true;
            return true;
        }
        if (!SuggestPopup.IsOpen)
        {
            // → / End 接受幽灵文本(fish/zsh-autosuggestions 语义)。幽灵只在确定态
            // 展示,此时光标必在行尾——行尾的 → 在 shell 里本就是空操作,不会误伤移动。
            if (
                e.KeyModifiers == Avalonia.Input.KeyModifiers.None
                && e.Key is Key.Right or Key.End
                && _ghostFull is not null
                && _termControl?.CurrentGhostRemainder() is { Length: > 0 } remainder
            )
            {
                // 不设抑制:接受后 InputChanged 里发出的查询若命中更长的候选
                // (如 "git checkout" → "git checkout -b"),继续以幽灵形式接力提示
                // (fish 语义);幽灵只显示严格更长的候选,不会原样重现。
                ClearGhost();
                _termControl.WriteInput(Encoding.UTF8.GetBytes(remainder));
                e.Handled = true;
                return true;
            }
            return false;
        }
        switch (e.Key)
        {
            case Key.Down:
                MoveSuggestion(+1);
                e.Handled = true;
                return true;
            case Key.Up:
                MoveSuggestion(-1);
                e.Handled = true;
                return true;
            case Key.Escape:
                CloseSuggestPopup(suppress: true);
                e.Handled = true;
                return true;
            case Key.Enter when e.KeyModifiers == Avalonia.Input.KeyModifiers.None:
                // 弹层存在时 Enter 始终输入当前选中项(VS 语义,无需先按方向键;
                // 不想要建议按 Esc 退出)。唯一例外:选中项与已输入完全相同
                // (如手动敲完了整条命令)→ 直通 shell 执行,免去二次回车。
                if (
                    SuggestList.SelectedItem is CommandSuggestion selected
                    && selected.Text != EffectiveInput()
                )
                {
                    AcceptSuggestion();
                    e.Handled = true;
                    return true;
                }
                CloseSuggestPopup(suppress: false);
                return false;
            default:
                // 其余按键(含 Tab)直通终端:Tab 保留给 shell 的原生补全,
                // 行内容随之不可知,弹层会经 InputChanged 自动收起。
                return false;
        }
    }

    private void MoveSuggestion(int delta)
    {
        if (_suggestions.Count == 0)
        {
            return;
        }
        int next =
            ((SuggestList.SelectedIndex + delta) % _suggestions.Count + _suggestions.Count)
            % _suggestions.Count;
        SuggestList.SelectedIndex = next;
        SuggestList.ScrollIntoView(next);
    }

    private void AcceptSuggestion()
    {
        if (_termControl is null || SuggestList.SelectedItem is not CommandSuggestion suggestion)
        {
            return;
        }
        string current = EffectiveInput();
        string payload;
        if (suggestion.Text.StartsWith(current, StringComparison.Ordinal))
        {
            payload = suggestion.Text[current.Length..];
        }
        else
        {
            // 候选与已输入不同前缀:回删已键入的字符再写入完整候选(shell 每次 DEL 删一个字符)。
            payload = new string('\x7f', current.Length) + suggestion.Text;
        }
        CloseSuggestPopup(suppress: false);
        ClearGhost();

        // 经用户输入通道写入(shell 负责回显),跟踪器同路径感知,行状态保持一致。
        // 不设抑制:面板已关,WriteInput 触发的查询结果只会落到幽灵文本,且幽灵
        // 只显示严格更长的候选——接受后若存在更长的延续(参数、子选项)就接力提示。
        if (payload.Length > 0)
        {
            _termControl.WriteInput(Encoding.UTF8.GetBytes(payload));
        }
        FocusTerminal();
    }

    internal void OpenSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RunSearch();
    }

    private void CloseSearch()
    {
        SearchBar.IsVisible = false;
        _searchHits = [];
        _searchIndex = -1;
        _termControl?.ClearSearchHighlights();
        FocusTerminal();
    }

    private void RunSearch()
    {
        if (_termControl is null || !SearchBar.IsVisible)
        {
            return;
        }
        string query = SearchBox.Text ?? string.Empty;
        _searchHits = _termControl.SearchBuffer(query);
        _searchIndex = _searchHits.Count > 0 ? 0 : -1;
        ShowCurrentHit();
    }

    private void MoveHit(int delta)
    {
        if (_searchHits.Count == 0)
        {
            return;
        }
        _searchIndex =
            (((_searchIndex + delta) % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;
        ShowCurrentHit();
    }

    private void ShowCurrentHit()
    {
        SearchCount.Text =
            _searchHits.Count == 0
                ? string.IsNullOrEmpty(SearchBox.Text)
                    ? ""
                    : Strings.Get("Term_NoMatches")
                : $"{_searchIndex + 1}/{_searchHits.Count}";

        // 所有命中项均保持高亮;当前项以 accent 着色(§5.3)。
        _termControl?.SetSearchHighlights(_searchHits, _searchIndex);
        if (_searchIndex >= 0 && _termControl is not null)
        {
            _termControl.ShowHit(_searchHits[_searchIndex]);
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveHit(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    /// <summary>宿主窗口(挂接失活事件用;弹层是独立顶层,不随主窗口失活自动收起)。</summary>
    private Window? _hostWindow;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookTerminalControl();
        // Dock 回收复用视图时 DataContext 可能经历 null→原值的往返,这里重挂一次
        // 补全接线,保证切换标签后建议仍然工作(幂等:先退订再订阅)。
        HookSuggestions();

        // 切到其他应用时收起补全弹层——Popup 是独立顶层窗口,不主动关就会
        // 悬浮在别的程序上面。
        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        _hostWindow?.Deactivated += OnHostWindowDeactivated;
        // 重新激活时兜底恢复:系统截图覆盖层(Win+Shift+S)偶尔不触发 Deactivated,
        // 弹层就会保持打开、把焦点困在自己的顶层窗口里,导致终端"输入死亡、内容被
        // 灰色残影盖住",只能关标签重开(时序竞态,非必现)。
        // 截完图切回本窗口时必然重新激活——这一刻不管失活有没有触发过,只要弹层还开
        // 着就统一收口并把焦点还给终端。此路径不依赖 Deactivated,也不依赖按键能到达
        // 本视图(焦点困在弹层时按键根本到不了),是该竞态的唯一可靠恢复点。
        _hostWindow?.Activated += OnHostWindowActivated;
        FocusTerminal();
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        SuggestDiag.Log("deactivated", $"view={GetHashCode():X} popupOpen={SuggestPopup.IsOpen}");
        DismissSuggestions(suppress: false);
    }

    /// <summary>
    /// 窗口重新激活时的双重兜底(时序竞态,非必现):
    ///
    /// ① 强制终端重绘 —— 根因:系统截图覆盖层(Win+Shift+S)+ 微信粘贴会让窗口某个
    ///    矩形区域在 DWM 层被置脏并要求重绘,而空闲终端(无输出、失焦后连光标闪烁的
    ///    周期重绘也停了)没有任何 visual 被标脏,那块区域就不重绘,露出下层 surface
    ///    底色,表现为"内容变空白/一块灰"。用户从覆盖窗口点回本窗口时窗口必然重新
    ///    激活——此刻强制终端跑一遍 Render(呈现一整帧新画面到 DWM)即可消除残影。
    ///    点击终端本会经 OnGotFocus 重绘,但失活前后焦点未变(终端一直"持有"焦点),
    ///    OnGotFocus 不重新触发,故必须在激活时补这一刀。立即 + 延迟各一次:后者兜住
    ///    "激活事件早于 DWM 完成表面恢复"的时序。
    ///
    /// ② 收口僵尸弹层 —— 弹层仍开着说明它熬过了切走(失活未触发或未收口),一并收口
    ///    并把焦点还给终端,让可能被困的输入自动复活,无需关标签重连。
    /// </summary>
    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        SuggestDiag.Log(
            "activated",
            $"view={GetHashCode():X} popupOpen={SuggestPopup.IsOpen} termFocused={_termControl?.IsFocused} -> force repaint"
        );

        // ① 立即重绘终端 + 下一拍强制整棵可视树重绘(DWM 表面恢复可能晚于激活事件;
        //    整树重绘确保合成器一定提交一整帧新画面到 DWM,兜住单控件 InvalidateVisual
        //    被"内容未变"优化掉、或空白区跨出终端边界的情形)。激活不频繁,代价可忽略。
        _termControl?.InvalidateVisual();
        DispatcherTimer.RunOnce(ForceFullRepaint, TimeSpan.FromMilliseconds(120));

        // ② 顺带收口可能残留的僵尸弹层(内容消失常与输入死亡耦合出现)。
        if (SuggestPopup.IsOpen)
        {
            DismissSuggestions(suppress: false);
            FocusTerminal();
        }
    }

    /// <summary>
    /// 强制宿主窗口整棵可视树重绘:遍历所有可视后代逐个 <see cref="Visual.InvalidateVisual" />,
    /// 逼合成器提交一整帧新画面到 DWM。用于截图覆盖层导致某区域"内容消失/残留灰块"后的恢复。
    /// </summary>
    private void ForceFullRepaint()
    {
        if (_hostWindow is not { } window)
        {
            _termControl?.InvalidateVisual();
            return;
        }
        window.InvalidateVisual();
        foreach (Visual visual in window.GetVisualDescendants())
        {
            visual.InvalidateVisual();
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _suggestDebounce?.Dispose();
        _suggestDebounce = null;
        DismissSuggestions(suppress: false);
        // 脱离可视树后不该再响应跟踪器(重新 attach 时 HookSuggestions 会幂等重订)。
        _suggestVm?.InputTracker.InputChanged -= OnTrackedInputChanged;
        _suggestVm?.Disconnected -= OnSuggestVmDisconnected;
        _hostWindow?.Deactivated -= OnHostWindowDeactivated;
        _hostWindow?.Activated -= OnHostWindowActivated;
        _hostWindow = null;
        if (_termControl is not null)
        {
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
            _termControl.LostFocus -= OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged -= OnTerminalPaddingChanged;
        }
        _termControl = null;
    }

    /// <summary>
    /// 终端失焦(点击 SFTP 面板/侧边栏等)时收起弹层与幽灵;建议列表项不可聚焦,
    /// 因此点击建议项本身不会触发这里。
    /// </summary>
    private void OnTerminalLostFocus(object? sender, RoutedEventArgs e) =>
        DismissSuggestions(suppress: false);

    // 终端控件是跨分屏重新挂载的单一共享实例,因此每个视图都把滚动条(重新)绑定到它
    // 当前承载的那个控件上。
    private void HookTerminalControl()
    {
        var ctrl =
            (DataContext as TerminalTabViewModel)?.TerminalEmulator.Control as VelaTerminalControl;
        if (ReferenceEquals(ctrl, _termControl))
        {
            SyncScrollBar();
            return;
        }
        if (_termControl is not null)
        {
            _termControl.ScrollChanged -= OnTerminalScrollChanged;
            _termControl.LostFocus -= OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged -= OnTerminalPaddingChanged;
            // 幽灵是画在控件上的覆盖层:换出的旧控件若在别的面板仍可见,
            // 不清会把幽灵永久残留在旧光标处。
            _termControl.ClearGhostSuggestion();
        }
        _ghostFull = null;
        _termControl = ctrl;
        if (_termControl is not null)
        {
            _termControl.ScrollChanged += OnTerminalScrollChanged;
            _termControl.LostFocus += OnTerminalLostFocus;
            _termControl.LayoutPaddingChanged += OnTerminalPaddingChanged;
            SyncScrollBarGeometry();
        }
        SyncScrollBar();
    }

    /// <summary>
    /// 让覆盖式滚动条正好铺满终端正文右侧那条留白带(RightPadding 已从列数里扣掉),
    /// 于是它不再压住文字(#117);用户内边距(设置 → 终端 → 内边距)则以外边距让开,
    /// 使滚动条始终紧贴正文而非窗口边缘(#227)。几何全部取自控件,单一事实来源。
    /// </summary>
    private void SyncScrollBarGeometry()
    {
        if (ScrollBarView is null || _termControl is null)
        {
            return;
        }
        double pad = _termControl.ContentPadding;
        ScrollBarView.Width = _termControl.RightPadding;
        ScrollBarView.Margin = new(0, pad, pad, pad);
    }

    private void OnTerminalPaddingChanged() => SyncScrollBarGeometry();

    private void OnTerminalScrollChanged() => SyncScrollBar();

    private void SyncScrollBar()
    {
        if (ScrollBarView is null || _termControl is null)
        {
            return;
        }
        _syncingScrollBar = true;
        try
        {
            int max = _termControl.MaxScrollOffset;
            ScrollBarView.Maximum = max;
            ScrollBarView.ViewportSize = Math.Max(1, _termControl.Rows);
            // 跟随实时输出时(偏移 0)滑块位于底部,完全回滚到历史时位于顶部。
            ScrollBarView.Value = max - _termControl.ScrollOffset;
            ScrollBarView.IsEnabled = max > 0;
            // 滚动条覆盖在终端右边缘;没有回滚内容时无可滚动项,因此整体隐藏,
            // 而非显示禁用的滑块。
            ScrollBarView.IsVisible = max > 0;
        }
        finally
        {
            _syncingScrollBar = false;
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_syncingScrollBar || _termControl is null || ScrollBarView is null)
        {
            return;
        }
        int max = _termControl.MaxScrollOffset;
        _termControl.ScrollOffset = max - (int)Math.Round(ScrollBarView.Value);
    }

    /// <summary>指针按下时将焦点交还终端,确保后续键入直接进入 PTY。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        FocusTerminal();
        base.OnPointerPressed(e);
    }

    /// <summary>
    /// 标签视图持有焦点时的快捷键处理:复制/粘贴/发送中断与终端控件行为保持一致;
    /// 全局动作(新建/关闭/切换标签、打开设置)经快捷键服务解析后转发到主窗口命令。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers modifiers = MapModifiers(e.KeyModifiers);
        KeyCode key = MapKey(e.Key);
        if (key == KeyCode.None)
        {
            base.OnKeyDown(e);
            return;
        }
        ShortcutAction action = _shortcutService.Resolve(modifiers, key, ShortcutContext.Terminal);
        switch (action)
        {
            // 本层是焦点落在标签视图(而非终端控件)时的回退:正常情况下终端控件自己的
            // OnKeyDown 已处理这些键。回退层必须与控件行为一致地跟随设置,否则同一个
            // 快捷键会因焦点位置不同而表现不同(选中即复制/Ctrl+C 不受控)。
            case ShortcutAction.Copy:
                // 复用控件的复制:只复制选中内容,并尊重「复制时去除尾部空格」等设置。
                if (_termControl is not null)
                {
                    _ = _termControl.CopyAsync();
                }
                else
                {
                    _ = CopySelectionAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.Paste:
                if (_termControl is not null)
                {
                    _ = _termControl.PasteAsync();
                }
                else
                {
                    _ = PasteFromClipboardAsync();
                }
                e.Handled = true;
                return;
            case ShortcutAction.SendInterrupt:
                // 与 VelaTerminalControl.OnKeyDown 同规则:「选中时 Ctrl+C 复制」开启且有
                // 选区 → 复制;否则发送中断信号 ^C。
                if (_termControl?.TryCopyOnCtrlC() != true)
                {
                    SendBytesToTerminal([0x03]);
                }
                e.Handled = true;
                return;
            case ShortcutAction.NewTab:
            case ShortcutAction.CloseTab:
            case ShortcutAction.NextTab:
            case ShortcutAction.PreviousTab:
            case ShortcutAction.OpenSettings:
                if (ExecuteGlobalShortcut(action))
                {
                    e.Handled = true;
                    return;
                }
                break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 把快捷键服务解析出的全局动作接到主窗口的既有命令上,与 MainWindow.KeyBindings
    /// 同源。经服务解析而非写死手势,macOS 上才是 Cmd+T/W/, 而非 Ctrl。找不到宿主 VM
    /// 时返回 false,让事件继续冒泡由 Window.KeyBindings 兜底,避免双重执行。
    /// </summary>
    private bool ExecuteGlobalShortcut(ShortcutAction action)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel vm)
        {
            return false;
        }
        switch (action)
        {
            case ShortcutAction.NewTab:
                return vm.Commands.Execute("session.new");
            case ShortcutAction.CloseTab:
                vm.CloseActiveTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.NextTab:
                vm.TabBar.NextTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.PreviousTab:
                vm.TabBar.PreviousTabCommand.Execute().Subscribe();
                return true;
            case ShortcutAction.OpenSettings:
                vm.OpenSettingsCommand.Execute().Subscribe();
                return true;
            default:
                return false;
        }
    }

    /// <summary>将文本输入以 UTF-8 编码转发到终端。</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(e.Text);
            SendBytesToTerminal(bytes);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    private void SendBytesToTerminal(byte[] data)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.TerminalEmulator.WriteInput(data);
        }
    }

    private async Task CopySelectionAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string selectedText = GetSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        IClipboard? clipboard = GetClipboard();
        if (clipboard == null)
        {
            return;
        }
        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            SendBytesToTerminal(bytes);
        }
    }

    private IClipboard? GetClipboard() => TopLevel.GetTopLevel(this)?.Clipboard;

    private string GetSelectedText()
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            ITerminalEmulator emulator = vm.TerminalEmulator;
            var sb = new StringBuilder();
            for (int row = 0; row < emulator.Rows; row++)
            {
                string line = emulator.GetBufferLine(row);
                if (!string.IsNullOrEmpty(line))
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }
                    sb.Append(line);
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    /// <summary>把键盘焦点交给终端模拟器控件。</summary>
    public void FocusTerminal()
    {
        if (DataContext is not TerminalTabViewModel vm)
        {
            return;
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                Focus();
                vm.TerminalEmulator.Control.Focus();
            },
            DispatcherPriority.Input
        );
    }

    private static KeyModifiers MapModifiers(Avalonia.Input.KeyModifiers avaloniaModifiers)
    {
        KeyModifiers result = KeyModifiers.None;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= KeyModifiers.Ctrl;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= KeyModifiers.Meta;
        }
        return result;
    }

    private static KeyCode MapKey(Key avaloniaKey)
    {
        return avaloniaKey switch
        {
            Key.C => KeyCode.C,
            Key.V => KeyCode.V,
            Key.T => KeyCode.T,
            Key.W => KeyCode.W,
            Key.Tab => KeyCode.Tab,
            Key.OemComma => KeyCode.Comma,
            _ => KeyCode.None,
        };
    }

    // 结尾括号选项:[Y/n]、(yes/no)、[Y/I/N/O/D/Z]、[是/否],允许尾随 ?/:/。及空白。
    // 每段选项限 1~3 个字母,避免误伤 prompt 主题里的 "(feature/xxx)" 分支括号。
    [GeneratedRegex(
        @"[\[(]\s*\p{L}{1,3}(?:\s*/\s*\p{L}{1,3})+\s*[\])]\s*[?？:：.。]?\s*$",
        RegexOptions.Compiled
    )]
    private static partial Regex ChoiceTokenRegex();

    /*
        @"(?:(?:mysql|mariadb|sqlite|clickhouse|ftp|sftp|telnet)>" +   // 具名 SQL/网络客户端
        @"|MariaDB \[[^\]]*\]>" +                                       // MariaDB [db]>
        @"|\w+=[#>]" +                                                  // postgres 就绪提示:db=# / db=>
        @"|In \[\d+\]:" +                                               // IPython In [n]:
        @"|\((?:gdb|lldb|Pdb)\)" +                                      // (gdb) (lldb) (Pdb)
        @"|i?pdb>" +                                                    // pdb> / ipdb>
        @"|irb\([^)]*\)[^>\n]*>" +                                      // irb(main):001:0>
        @")\s*$"
    */
    // REPL 交互提示符:此时补全给的是 shell 快捷命令,语义上是错的,故一并不弹。
    // 只匹配"带库名/工具名"等高辨识度形态;裸 ">"(node/R/mongosh)与自定义 shell 提示符
    // 无法区分,一律不拦,避免误伤把 PS1 设成 "> " 的用户。
    [GeneratedRegex(
        @"(?:(?:mysql|mariadb|sqlite|clickhouse|ftp|sftp|telnet)>|MariaDB \[[^\]]*\]>|\w+=[#>]|In \[\d+\]:|\((?:gdb|lldb|Pdb)\)|i?pdb>|irb\([^)]*\)[^>\n]*>)\s*$",
        RegexOptions.Compiled
    )]
    private static partial Regex ReplPromptRegex();
}

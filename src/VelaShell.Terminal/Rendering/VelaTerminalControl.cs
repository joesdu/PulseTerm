using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Input;
using VelaShell.Terminal.Semantics;

// ReSharper disable AsyncVoidMethod
// ReSharper disable AsyncVoidEventHandlerMethod
// ReSharper disable UnusedMember.Global

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 完全自绘的终端控件。它持有一个 <see cref="TerminalEmulator" />,用缓存的字形运行
/// 渲染屏幕缓冲,并把键盘 / 鼠标 / 剪贴板输入翻译成主机字节。实现 <see cref="ITerminalEmulator" />,
/// 因此能直接嵌入现有的 <c>SshTerminalBridge</c> 与各个视图,无需改动任何接线。
/// </summary>
public sealed partial class VelaTerminalControl : Control, ITerminalEmulator
{
    private const int WheelScrollLines = 3;
    private const int FastWheelScrollMultiplier = 5;

    private static readonly ImmutableSolidColorBrush BellFlashBrush = new(
        Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
    );

    // ---- Search highlights (spec §5.3: 命中项高亮) --------------------------

    private static readonly Rgba SearchMatchBg = new(0x59, 0xFD, 0xCB, 0x6E); // amber, ~35%
    private static readonly Rgba SearchCurrentBg = new(0x73, 0x00, 0xD4, 0xAA); // accent, ~45%
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = [];
    private readonly Dictionary<uint, ImmutablePen> _penCache = [];

    // 已塑形、着色的字形缓存,键为 (rune, combining, foreground, style)。终端
    // 输出只从很小的字符集绘制,因此命中率约 100%,每帧文本塑形 ——
    // 这一主要渲染开销 —— 实际上消失了。字体/字号变化时清空。
    private readonly Dictionary<GlyphKey, FormattedText> _glyphCache = [];
    // 待发出的 GlyphRun 内容。两者都跨 run、跨帧复用(见 FlushGlyphRun 对所有权的说明):
    // 字符缓冲手工扩容以便按精确长度切 ReadOnlyMemory;字形表直接以 List 交出去 ——
    // List<T> 本身就是长度精确的 IReadOnlyList<T>,不必再 ToArray 一份。
    private char[] _runChars = new char[256];
    private int _runCharCount;
    private readonly List<GlyphInfo> _runGlyphs = [];

    // 客户端语义着色(URL、IP、错误/警告/成功词、选项标志、数字),针对
    // 远端程序留在默认颜色下的文本,使普通日志/MOTD 也能被高亮,
    // 且绝不破坏显式 SGR 颜色(ls --color、git 等)。正则结果按行文本缓存,
    // 因为可见行每一帧都会被重新扫描(光标闪烁、输出)。
    // 用 StringComparer.Ordinal 建表是为了能取到 span 备用查找(GetAlternateLookup):
    // 缓存命中路径因此不必先把行文本物化成 string 才查得了表 —— 只有 miss 时才建键。
    private readonly Dictionary<string, IReadOnlyList<SemanticSpan>> _semanticSpanCache = [with(StringComparer.Ordinal)];

    private readonly Dictionary<string, IReadOnlyList<SemanticSpan>>.AlternateLookup<ReadOnlySpan<char>> _semanticSpanCacheBySpan;

    // 侧栏文本(时间戳/行号/折叠标记)的 FormattedText 缓存:这些文本帧间高度重复
    // (行号在滚动稳定时完全不变),不缓存则每行每帧做一次文本塑形。
    // 键为文本本身;暗色画刷随主题变化时整体失效(见 GutterText)。
    private readonly Dictionary<string, FormattedText> _gutterTextCache = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, FormattedText>.AlternateLookup<ReadOnlySpan<char>> _gutterTextCacheBySpan;
    private ImmutableSolidColorBrush? _gutterTextCacheBrush;

    // ComputeSemanticColumns 的复用缓冲:该方法对每个可见行、每一帧都会执行,
    // 若每次都 new StringBuilder/List/数组,全屏 TUI 下就是每帧几百次堆分配直喂 GC。
    // 三者都只在 UI 线程的渲染路径内短暂使用,跨行复用安全。
    // 行文本用裸 char[] 而非 StringBuilder:语义匹配与缓存查表全程走 span,
    // 命中路径因此一个字符串都不产生(见 SemanticSpansFor)。
    private char[] _semanticLineChars = new char[256];
    private readonly List<int> _semanticColByChar = [];
    private SemanticKind?[] _semanticByColumn = [];

    // ---- Glyph-run batching -------------------------------------------------
    // 每个可见行被绘制为少数几个 GlyphRun —— 每个连续且共享同一字体风格与前景色的
    // 单元格运行对应一个 —— 而不是每格一次 DrawText。全屏 TUI(htop/vim/nano)有成千上万个
    // 单元格;每一格一次绘制操作,正是过去光标卡顿的元凶,因为每帧会在 UI 线程
    // 记录成千上万次绘制操作。步进被钉在单元格宽度上,因此等宽对齐精确,空格被
    // 并入步进(从不绘制),而主字体缺失的任何字形(CJK、符号)或任何
    // 组合序列,会回退到逐单元 FormattedText 路径,从而保证回退依旧可用。
    private readonly GlyphTypeface?[] _styleTypefaces = new GlyphTypeface?[4];
    private double _baselineOffset;

    private DateTime _bellFlashUntil = DateTime.MinValue;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorBlinkVisible = true;

    // 一旦批量化 GlyphRun 路径在运行时首次抛异常(意外的平台行为),
    // 就永久回退到久经考验的逐单元 FormattedText 路径,使渲染 API 的意外
    // 绝不会让文本缺失 —— 只是放弃了批处理带来的加速。
    private bool _glyphRunUnsupported;
    private double _glyphYOffset;
    private bool _hasFocus;

    // ---- IME ------------------------------------------------------------------

    private TerminalImeClient? _imeClient;
    private (int Col, int Row) _lastMouseReportCell = (-1, -1);
    private int _lastScrollbackCount; // 上一次输出更新时的回滚大小

    // 宿主认定的当前网格尺寸:布局下发、公开 Resize、或宿主主动接纳的主机端几何改动都会更新它。
    // 0 = 尚未下发过任何网格(控件还没拿到真实布局)。模拟器几何一旦与它对不上,就说明有人
    // 绕过宿主改了网格,ApplyOutputUpdate 的自愈闸会把网格拉回当前布局(见 issue #253)。
    private int _appliedColumns;
    private int _appliedRows;

    // 向应用上报鼠标(htop/btop/vim/tmux):记录上报按下后保持的按钮,以及
    // 最近上报的单元格,使得拖拽/移动仅在单元格真正变化时才发送。
    private TerminalMouseButton? _mouseButtonDown;
    private ImmutableSolidColorBrush? _runBrush;
    private uint _runFg;
    private int _runPrevCol;
    private int _runPrevWidth;
    private int _runStartCol;
    private int _runStyle = -1; // -1 = 无活动运行;否则 (bold?1) | (italic?2)

    private int _scrollOffset; // 从底部向上滚动的行数(0 = 实时)

    /// <summary>每个绝对缓冲行的搜索区间;当前命中项以不同色调着色。</summary>
    private Dictionary<int, List<(int Start, int End, bool Current)>>? _searchHighlights;

    private bool _selecting;

    // 本次选区是否为 Alt+拖拽的矩形块选(#128)。与 Windows Terminal 一致:按下鼠标那一刻由 Alt
    // 决定,拖拽途中改变 Alt 不切换模式。
    private bool _blockSelection;

    // 选区(线性或矩形块选),位于绝对行空间。
    private (int Row, int Col)? _selectionAnchor;
    private (int Row, int Col)? _selectionCaret;

    // 已定稿的附加选区段:Ctrl+Shift+拖拽会把进行中那段定稿到这里,再另起一段,
    // 于是"第 1 行 + 第 3 行"这种不连续选区可以一次复制。进行中那段始终在
    // _selectionAnchor/_selectionCaret 里,只在渲染与复制时才与这些段合到一起。
    private readonly List<SelectionSpan> _extraSelections = [];

    // 渲染/复制时重建的合并段列表,以及逐行列区间缓冲 —— 两者都是复用的,热路径上不分配。
    private readonly List<SelectionSpan> _spanBuffer = [];
    private (int From, int To)[] _rowSpanBuffer = new (int From, int To)[4];
    private bool _styleTypefacesReady;

    /// <summary>创建一个使用默认 120×32 网格的终端控件。</summary>
    public VelaTerminalControl()
        : this(new(120, 32)) { }

    /// <summary>
    /// 光标/幽灵叠加层。挂在 <c>VisualChildren</c> 上(而非仅 LogicalChildren):只有可视子元素
    /// 才会被渲染器访问并拿到自己的绘制记录 —— 这正是它能独立于正文失效的前提。
    /// 排在正文之后加入,因此恒画在正文之上。
    /// </summary>
    private readonly CursorOverlay _overlay;

    /// <summary>
    /// 失效整个终端(正文 + 光标/幽灵叠加层)。
    /// </summary>
    /// <remarks>
    /// <b>本类内部一律用它,不要直接写 <c>InvalidateVisual()</c></b>:光标与幽灵住在独立的
    /// <see cref="CursorOverlay" /> 里,只失效正文会让光标停在旧位置(输入时光标不跟手)。
    /// 唯一的例外是光标闪烁计时器 —— 它只失效叠加层,这正是拆层的意义。
    /// 外部宿主强制重绘时同样应当调本方法(见 <c>TerminalTabView.ForceFullRepaint</c>)。
    /// </remarks>
    public void InvalidateTerminal()
    {
        InvalidateVisual();
        _overlay.InvalidateVisual();
        RaiseSelectionChangedIfNeeded();
    }

    /// <summary>选区发生变化时触发,携带选中的字符数(0 = 没有选区)。</summary>
    /// <remarks>状态栏据此显示"已选 N 字符"。</remarks>
    public event Action<int>? SelectionChanged;

    // 上一次上报过的选区指纹与字符数。指纹是三个廉价字段的组合;只有它变了才去
    // 真的把选中文本materialise 一遍 —— 否则输出洪流下每帧算一次选区文本会很贵。
    private (int Extra, (int, int)? Anchor, (int, int)? Caret, bool Block) _selectionFingerprint = (-1, null, null, false);
    private int _reportedSelectionLength = -1;

    private void RaiseSelectionChangedIfNeeded()
    {
        if (SelectionChanged is null)
        {
            return;
        }
        (int, (int, int)?, (int, int)?, bool) fingerprint =
            (_extraSelections.Count, _selectionAnchor, _selectionCaret, _blockSelection);
        if (fingerprint == _selectionFingerprint)
        {
            return;
        }
        _selectionFingerprint = fingerprint;
        int length = HasSelectionAnchor ? GetSelectedText().Length : 0;
        if (length == _reportedSelectionLength)
        {
            return;
        }
        _reportedSelectionLength = length;
        SelectionChanged(length);
    }

    private VelaTerminalControl(TerminalEmulator emulator)
    {
        // span 备用查找必须在字典建好之后取一次并留住:它是个包住底层桶的结构体视图,
        // 每次现取虽然也对,但在每行每帧的热路径上白白多做一次比较器类型检查。
        _semanticSpanCacheBySpan = _semanticSpanCache.GetAlternateLookup<ReadOnlySpan<char>>();
        _gutterTextCacheBySpan = _gutterTextCache.GetAlternateLookup<ReadOnlySpan<char>>();
        _overlay = new(this);
        VisualChildren.Add(_overlay);
        Emulator = emulator;
        Focusable = true;
        ClipToBounds = true;
        ApplyDesignPalette(Emulator.Palette);
        RecomputeMetrics();
        Emulator.Updated += OnEmulatorUpdated;
        Emulator.Response += bytes => UserInput?.Invoke(bytes); // 协议自动应答:发往 PTY 但不算用户键入(不进 TypedInput)。
        Emulator.Bell += OnBell;
        Emulator.ClipboardWriteRequested += OnRemoteClipboardWrite;
        Emulator.HostGeometryChanged += OnHostGeometryChanged;

        // 终端配色跟随应用主题(暗=Dracula,亮=Solarized Light);切换主题时重灌调色板并重绘。
        ActualThemeVariantChanged += (_, _) => ApplyThemePalette();
        AddHandler(TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested);
    }

    /// <summary>切换对默认颜色输出的客户端语义高亮。</summary>
    private bool SemanticHighlightingEnabled { get; } = true;

    /// <summary>为 true 时,松开选区会自动将其复制到剪贴板。</summary>
    public bool CopyOnSelect { get; set; } = true;

    // ---- 设置 → 终端(行为选项,由 ApplyLiveTerminalSettings 下发) ----------

    /// <summary>光标形状:"bar"(竖线)、"block"(实心单元)或 "underline"(下划线)。</summary>
    public string CursorStyle
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                InvalidateTerminal();
            }
        }
        // 默认值与设置模型(TerminalBehaviorOptions.CursorStyle)一致;运行时由
        // ApplyLiveTerminalSettings 下发,这里不声明独立的业务默认值(设置审计 C-07)。
    } = "bar";

    /// <summary>聚焦光标是否闪烁(设置 → 终端 → 光标闪烁)。</summary>
    public bool CursorBlink
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            field = value;
            UpdateCursorBlinkTimer();
        }
    } = true;

    /// <summary>
    /// 行高倍数(1.0 = 字体自然高度)。多余空间在字形上下均匀分配。
    /// </summary>
    public double LineHeight
    {
        get;
        set
        {
            double clamped = Math.Clamp(
                double.IsFinite(value) && value > 0 ? value : 1.0,
                0.8,
                2.0
            );
            if (Math.Abs(clamped - field) < 0.001)
            {
                return;
            }
            field = clamped;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateTerminal();
        }
    } = 1.0;

    /// <summary>右键粘贴剪贴板(关闭 = 右键无动作)。</summary>
    public bool RightClickPaste { get; set; } = true;

    /// <summary>复制每行时去除行尾空白。</summary>
    public bool TrimTrailingWhitespaceOnCopy { get; set; } = true;

    /// <summary>双击选中指针下的单词。</summary>
    public bool DoubleClickSelectsWord { get; set; } = true;

    /// <summary>粘贴含换行符的文本前先询问(避免误执行多行内容)。</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;

    /// <summary>
    /// 由宿主提供的多行粘贴确认(返回 false 则中止)。
    /// null = 从不询问,控件本身无法弹出对话框。
    /// </summary>
    public Func<string, Task<bool>>? MultilinePasteConfirmation { get; set; }

    /// <summary>
    /// 选中时 Ctrl+C 复制:开 = 有选区时 Ctrl+C 复制选中内容而不发送中断(无选区
    /// 仍发送中断);关 = Ctrl+C 始终作为中断信号 ^C 发往 PTY。
    /// </summary>
    public bool CtrlCCopiesWhenSelected { get; set; }

    /// <summary>打字时把视图拉回实时底部。</summary>
    public bool ScrollOnKeystroke { get; set; } = true;

    /// <summary>
    /// 本地回显(设置 → 终端):对端不回显的链路(Telnet 半双工、串口设备)需要开启,
    /// 否则打字看不见。默认关 —— SSH 下远端 shell 自己回显,再本地回显会出现双字符。
    /// 主机以 <c>CSI 12 l</c> 复位 SRM 时即便本项为关也会生效(见 <see cref="LocalEcho.IsEnabled" />)。
    /// </summary>
    public bool LocalEchoEnabled { get; set; }

    /// <summary>
    /// 对端是否自己回显键入。SSH(远端 PTY)与本地终端(ConPTY 里的 shell)都会,故均置 true——
    /// 此时 <see cref="LocalEchoEnabled" /> 被忽略,避免用户为串口开了开关后 SSH/本地标签全部双字符。
    /// 将来的 Telnet 半双工 / 串口置 false,走正常逻辑。
    /// 默认 true:新传输接入时若忘了设,宁可不回显(看得见但要按两下),也好过满屏重影。
    /// </summary>
    public bool PeerEchoesInput { get; set; } = true;

    /// <summary>
    /// 新输出会把历史滚动视图拉回底部;关闭则保持
    /// 用户的历史视图固定不动(#15 行为)。
    /// </summary>
    public bool ScrollOnOutput { get; set; }

    /// <summary>BEL 处理:"system"(蜂鸣)、"none"(静默)或 "visual"(屏幕闪烁)。</summary>
    public string BellMode { get; set; } = "system";

    /// <summary>允许远端通过 OSC 52 写入本机剪贴板;默认关闭。</summary>
    public bool AllowRemoteClipboardWrite
    {
        get => Emulator.AllowOsc52ClipboardWrite;
        set => Emulator.AllowOsc52ClipboardWrite = value;
    }

    /// <summary>
    /// 左侧栏显示每行的收行时间 <c>[HH:mm:ss]</c>(设置 → 终端 / 侧栏右键)。与 <see cref="ShowLineNumber" />
    /// 等相互独立。任一侧栏部件开启都会占用左侧宽度(减少可用列数,PTY 随之改列宽)。
    /// </summary>
    public bool ShowLineTimestamp
    {
        get;
        set => SetGutterOption(ref field, value);
    }

    /// <summary>左侧栏显示每行的缓冲区行号。与其他侧栏部件相互独立。</summary>
    public bool ShowLineNumber
    {
        get;
        set => SetGutterOption(ref field, value);
    }

    /// <summary>左侧栏显示折叠标记列:可折叠标记之前的历史内容(WindTerm 式)。</summary>
    public bool ShowFoldMarker
    {
        get;
        set => SetGutterOption(ref field, value);
    }

    /// <summary>在侧栏与命令输出之间插入约 5px 的空白间隔。</summary>
    public bool GutterBlank
    {
        get;
        set => SetGutterOption(ref field, value);
    }

    /// <summary>
    /// 正文右侧默认留白(px)。取值同时满足两件事(参照 Windows Terminal):
    /// 容得下覆盖式滚动条,且让最右一列离开窗口边缘 5px 的缩放抓取区。
    /// </summary>
    public const double DefaultRightPadding = 12;

    /// <summary>
    /// 正文右侧保留的空白带宽度(px)。这条带子不参与列数计算,于是:覆盖式滚动条落在带内
    /// 而不再盖住文字,最右一列也不再压在窗口边缘的缩放抓取区下(那会让末列字符选不中、
    /// 指针变成缩放光标)。写入即重排网格(可用列数 → PTY)。
    /// </summary>
    public double RightPadding
    {
        get;
        set
        {
            double sanitized = Math.Max(0, value);
            if (Math.Abs(field - sanitized) < 0.01)
            {
                return;
            }
            field = sanitized;
            RelayoutFromBounds();
            InvalidateTerminal();
            LayoutPaddingChanged?.Invoke();
        }
    } = DefaultRightPadding;

    /// <summary>内边距上限(px);再大就只剩不了几列,且滚动条留白带会被挤没。</summary>
    public const double MaxContentPadding = 40;

    /// <summary>
    /// 用户可调的正文四周内边距(px,设置 → 终端 → 内边距;0 = 历史行为)。
    /// <para>
    /// 语义与 <see cref="RightPadding" /> 一致 —— 留白先从可用宽高里扣掉,再算列/行数,
    /// 因此加大内边距只会减少格子数,绝不会让文字被裁掉。右侧是两段叠加:
    /// 用户内边距 + 滚动条留白带。
    /// </para>
    /// <para>
    /// 取整数像素:侧栏的 1px 导引线/折叠方框按 <c>floor(x)+0.5</c> 做像素对齐,
    /// 平移量若带小数会把这些笔画糊成 2px 灰线。
    /// </para>
    /// </summary>
    public double ContentPadding
    {
        get;
        set
        {
            double sanitized = Math.Round(
                Math.Clamp(double.IsFinite(value) ? value : 0, 0, MaxContentPadding)
            );
            if (Math.Abs(field - sanitized) < 0.01)
            {
                return;
            }
            field = sanitized;
            RelayoutFromBounds();
            InvalidateTerminal();
            LayoutPaddingChanged?.Invoke();
        }
    }

    /// <summary>
    /// 内边距(<see cref="ContentPadding" /> / <see cref="RightPadding" />)变化后触发,
    /// 供宿主重新摆放覆盖在控件上的部件(当前是回滚滚动条,它必须落在右侧留白带内)。
    /// </summary>
    public event Action? LayoutPaddingChanged;

    /// <summary>把控件坐标换算到正文坐标系(扣掉左/上内边距),供指针命中测试使用。</summary>
    private Point ToContent(Point p) => new(p.X - ContentPadding, p.Y - ContentPadding);

    /// <summary>
    /// 侧栏部件开关的公共写入:变化时重排布局(侧栏宽度→可用列数→PTY)并重绘。
    /// 不在此上报持久化——由设置应用与右键菜单区分来源,菜单侧显式触发,避免「应用设置→上报→再存」死循环。
    /// </summary>
    private void SetGutterOption(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        RelayoutFromBounds();
        InvalidateTerminal();
    }

    /// <summary>侧栏右键菜单改动部件开关后上报(时间戳, 行号, 折叠标记, 空白),供上层持久化。</summary>
    public event Action<bool, bool, bool, bool>? GutterOptionsChanged;

    /// <summary>侧栏右键菜单的本地化标签(行号 / 时间戳 / 折叠标记 / 空白),由上层按当前语言注入。</summary>
    public GutterMenuLabels GutterMenu { get; set; } = new("行号", "时间戳", "折叠标记", "空白");

    /// <summary>
    /// 启用操作系统输入法(中文/日文/韩文组字)。关闭 = 终端从不提供 IME 客户端。
    /// </summary>
    public bool ImeEnabled { get; set; } = true;

    /// <summary>可向上滚动的最大行数(回滚历史的大小)。</summary>
    public int MaxScrollOffset => Emulator.Screen.ScrollbackCount;

    /// <summary>当前从实时底部向上滚动的行数(0 = 跟随输出)。</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxScrollOffset);
            if (clamped == _scrollOffset)
            {
                return;
            }
            _scrollOffset = clamped;
            InvalidateTerminal();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>
    /// 当前界面主题配套的**整套**终端配色(前景/背景/光标/选区 + ANSI 16 色),由宿主随
    /// 主题切换下发。null = 用控件自带的明暗缺省。
    /// <para>
    /// 为什么不能只看 <see cref="Avalonia.Styling.ThemeVariant" />:具名主题里有五套暗色、
    /// 四套亮色,VelaDark 换到 Tokyo Night 时变体根本没变,控件无从得知该换配色。
    /// </para>
    /// </summary>
    public TerminalPaletteOverrides? ThemePalette
    {
        get;
        set
        {
            field = value;
            ApplyThemePalette();
        }
    }

    /// <summary>
    /// 用户自定义终端配色(设置 → 外观 → 终端颜色/ANSI 调色板):只包含用户实际
    /// 改过的颜色,叠加在主题调色板之上;null 或空对象 = 完全跟随主题。
    /// </summary>
    public TerminalPaletteOverrides? PaletteOverrides
    {
        get;
        set
        {
            field = value;
            ApplyThemePalette();
        }
    }

    private TerminalEmulator Emulator { get; }

    /// <summary>底层仿真器的终端类型(xterm/vt100 等);写入即切换仿真行为。</summary>
    public TerminalType TerminalType
    {
        get => Emulator.Type;
        set => Emulator.SetTerminalType(value);
    }

    /// <summary>等宽终端字体族;修改后重算单元格度量、重排网格并重绘。</summary>
    public FontFamily FontFamily
    {
        get;
        set
        {
            // 同名即什么都不做:重算度量会连字形缓存一起丢,下一帧整屏重新塑形,
            // 再加一次全网格重排 —— 实测比一次普通重绘多出 2~7ms/标签。
            // 宿主的"把当前设置刷到所有终端"是一条通用路径(保存设置、换主题、插件面板都会走),
            // 字体多半根本没变,这个判断是它们不掉帧的前提。
            if (field == value)
            {
                return;
            }
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateTerminal();
        }
    } = new("fonts:VelaShell#Cascadia Mono, Cascadia Mono, JetBrains Mono, Consolas, Microsoft YaHei, Segoe UI, monospace");

    /// <summary>终端字号(磅);修改后重算单元格度量、重排网格并重绘。</summary>
    public double FontSize
    {
        get;
        set
        {
            // 同 FontFamily:值没变就别丢字形缓存、别重排网格。
            if (Math.Abs(field - value) < 0.001)
            {
                return;
            }
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateTerminal();
        }
    } = 14;

    // ---- ITerminalEmulator --------------------------------------------------

    /// <summary>需要发往 PTY 的字节(用户键入、鼠标上报、粘贴及协议自动应答)。</summary>
    public event Action<byte[]>? UserInput;

    /// <summary>网格 reflow 后新的列数/行数,供上层同步调整 PTY 尺寸。</summary>
    public event Action<int, int>? PtySizeChanged;

    /// <summary>将原始主机输出字节喂入模拟器以解析并显示。</summary>
    public void Feed(byte[] data) => Emulator.Feed(data);

    /// <summary>Feed 的 span 重载:桥的合批热路径直喂复用缓冲,避免物化精确尺寸数组。</summary>
    public void Feed(ReadOnlySpan<byte> data) => Emulator.Feed(data);

    /// <summary>将模拟器网格调整为给定行列数,重置滚动、折叠与选区。</summary>
    public void Resize(int cols, int rows)
    {
        Emulator.Resize(cols, rows);
        _appliedColumns = Emulator.Columns; // 宿主自己下发的网格,自愈闸不该把它当成"分家"。
        _appliedRows = Emulator.Rows;
        _scrollOffset = 0;
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        ClearFolds(); // reflow 会重建行对象,折叠引用失效。
        // 选区的行索引是绝对的,会在调整大小时偏移;与其让陈旧的范围
        // 标记(或复制)错误的文本,不如直接丢弃它。
        ClearSelection();
        InvalidateTerminal();
        ScrollChanged?.Invoke();
    }

    /// <summary>把程序生成的字节当作用户输入发送往 PTY。</summary>
    public void WriteInput(byte[] data) => SendTypedInput(data);

    /// <inheritdoc />
    public void WriteTextInput(string text)
    {
        byte[] encoded = InputEncoder.EncodeText(text);
        if (encoded.Length == 0)
        {
            return;
        }
        SendTypedInput(encoded);
        AfterProgrammaticInput();
    }

    /// <inheritdoc />
    public bool WriteKeyInput(Key key, KeyModifiers modifiers)
    {
        if (key == Key.ImeProcessed)
        {
            return false;
        }
        if (key is Key.Home or Key.End && modifiers == KeyModifiers.Shift)
        {
            modifiers = KeyModifiers.None;
        }
        byte[]? encoded = InputEncoder.Encode(key, modifiers, Emulator.Modes, Emulator.Type);
        if (encoded is not { Length: > 0 })
        {
            return false;
        }
        SendTypedInput(encoded);
        AfterProgrammaticInput();
        return true;
    }

    /// <inheritdoc />
    public void WritePasteInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var payload = new StringBuilder();
        if (Emulator.Modes.BracketedPaste)
        {
            payload.Append("\e[200~");
        }
        payload.Append(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        if (Emulator.Modes.BracketedPaste)
        {
            payload.Append("\e[201~");
        }
        SendTypedInput(Encoding.UTF8.GetBytes(payload.ToString()));
        AfterProgrammaticInput();
    }

    /// <summary>用户产生的输入字节(不含协议自动应答),供命令补全等跟踪键入。</summary>
    public event Action<byte[]>? TypedInput;

    /// <summary>
    /// 用户产生的输入(键盘/IME/鼠标上报/粘贴/程序化写入)统一出口:发往 PTY
    /// (<see cref="UserInput" />)并同步通知补全跟踪(<see cref="TypedInput" />)。
    /// 终端协议自动应答不走这里(见构造函数 Response 挂接)。
    /// </summary>
    private void SendTypedInput(byte[] data)
    {
        TypedInput?.Invoke(data);
        UserInput?.Invoke(data);
        EchoLocally(data);
    }

    /// <summary>
    /// 本地回显:对端不回显时(Telnet 半双工、串口设备,或主机以 <c>CSI 12 l</c> 复位 SRM),
    /// 把键入的可见部分喂回终端自己显示。默认关闭 —— SSH 下远端 shell 自己回显,再回显会出双字符。
    /// </summary>
    /// <remarks>
    /// 放在发送**之后**:回显只是显示层的补偿,不该影响或延后真正的发送。
    /// </remarks>
    private void EchoLocally(byte[] data)
    {
        if (!LocalEcho.IsEnabled(LocalEchoEnabled, Emulator.Modes.SendReceive, PeerEchoesInput))
        {
            return;
        }
        byte[] echo = LocalEcho.Compute(data, Emulator.Modes.NewLineMode);
        if (echo.Length > 0)
        {
            Emulator.Feed(echo);
        }
    }

    private void AfterProgrammaticInput()
    {
        if (ScrollOnKeystroke)
        {
            _scrollOffset = 0;
        }
        ClearSelection();
        ResetCursorBlink();
    }

    /// <summary>返回给定活动屏幕行的纯文本。</summary>
    public string GetBufferLine(int row) => Emulator.Screen.ActiveLine(row).GetText();

    /// <summary>活动屏幕中的当前光标行。</summary>
    public int CursorRow => Emulator.CursorY;

    /// <summary>活动屏幕中的当前光标列。</summary>
    public int CursorCol => Emulator.CursorX;

    /// <summary>缓冲区保留的最大回滚行数。</summary>
    public int ScrollbackLines
    {
        get => Emulator.Screen.MaxScrollback;
        set => Emulator.Screen.MaxScrollback = value;
    }

    /// <summary>渲染此终端的 Avalonia 控件(即本实例)。</summary>
    public Control Control => this;

    /// <summary>当前网格的列数。</summary>
    public int Columns => Emulator.Columns;

    /// <summary>当前网格的行数。</summary>
    public int Rows => Emulator.Rows;

    /// <summary>缓冲行总数(回滚区 + 可见屏幕)。</summary>
    public int TotalLines => Emulator.Screen.TotalRows;

    /// <summary>当前显示在视口顶部的绝对缓冲行。</summary>
    public int ViewportRow =>
        Math.Max(0, Emulator.Screen.TotalRows - Emulator.Rows - _scrollOffset);

    /// <summary>解除模拟器事件订阅并停止光标闪烁计时器。</summary>
    public void Dispose()
    {
        Emulator.Updated -= OnEmulatorUpdated;
        Emulator.Bell -= OnBell;
        Emulator.ClipboardWriteRequested -= OnRemoteClipboardWrite;
        Emulator.HostGeometryChanged -= OnHostGeometryChanged;
        _cursorBlinkTimer?.Stop();
        _cursorBlinkTimer = null;
    }

    /// <summary>
    /// 每当远端发送 BEL 时触发(在 UI 线程上)—— 宿主用它做标签闪烁提醒。
    /// </summary>
    public event Action? BellRang;

    /// <summary>每当滚动位置或可滚动范围变化时触发。</summary>
    public event Action? ScrollChanged;

    /// <summary>
    /// 计算滚动偏移,使新行被推入回滚区后,相同的历史内容仍保持可见。在实时底部
    /// (偏移 0)时视图跟随输出;当用户向上滚动后,偏移随回滚区增长,
    /// 使视图保持固定不动。
    /// </summary>
    internal static int PinScrollOffset(int currentOffset, int lastScrollback, int newScrollback)
    {
        if (currentOffset <= 0)
        {
            return 0;
        }
        int growth = newScrollback - lastScrollback;
        int pinned = growth > 0 ? currentOffset + growth : currentOffset;
        return Math.Clamp(pinned, 0, Math.Max(0, newScrollback));
    }

    /// <summary>挂载后、实际主题变体确定时重新应用主题调色板。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ActualThemeVariant 在挂树后才最终确定,构造时灌的是暗色缺省。
        ApplyThemePalette();
    }

    private void ApplyThemePalette()
    {
        // 三层叠加,后者盖前者:
        //   ① 控件自带的明暗缺省(宿主没下发主题配色时的兜底,也是 headless 测试看到的那套)
        //   ② 当前界面主题配套的整套终端配色(宿主按具名主题下发)
        //   ③ 用户在设置里改过的单色(稀疏覆盖)
        ApplyDesignPalette(Emulator.Palette, ActualThemeVariant == ThemeVariant.Light);
        ApplyPaletteOverrides(Emulator.Palette, ThemePalette);
        ApplyPaletteOverrides(Emulator.Palette, PaletteOverrides);
        InvalidateTerminal();
    }

    private static void ApplyPaletteOverrides(TerminalPalette palette, TerminalPaletteOverrides? overrides)
    {
        if (overrides is not { } o)
        {
            return;
        }
        if (o.Foreground is { } fg)
        {
            palette.DefaultForeground = fg;
        }
        if (o.Background is { } bg)
        {
            palette.DefaultBackground = bg;
        }
        if (o.Cursor is { } cur)
        {
            palette.CursorColor = cur;
        }
        if (o.Selection is { } sel)
        // 用户给的是不带透明度的选区色;按既有方案以 ~35% 透明叠加,避免盖住文字。
        {
            palette.SelectionBackground = new(0x59, sel.R, sel.G, sel.B);
        }
        for (int i = 0; i < TerminalPaletteOverrides.AnsiCount; i++)
        {
            if (o.Ansi[i] is { } c)
            {
                palette.SetAnsi(i, c);
            }
        }
    }

    // ReSharper disable once EventNeverSubscribedTo.Global
    /// <summary>远端设置窗口/标签标题时触发;转发底层模拟器事件。</summary>
    public event Action<string>? TitleChanged
    {
        add => Emulator.TitleChanged += value;
        remove => Emulator.TitleChanged -= value;
    }

    /// <summary>OSC 7 当前工作目录变更(直通仿真器)。</summary>
    public event Action<string>? WorkingDirectoryChanged
    {
        add => Emulator.WorkingDirectoryChanged += value;
        remove => Emulator.WorkingDirectoryChanged -= value;
    }

    /// <summary>设置主机输出字符集(默认 UTF-8;支持 GBK/Big5 等)。</summary>
    public void SetEncoding(Encoding encoding) => Emulator.SetEncoding(encoding);

    /// <summary>OSC 52:远端 yank(tmux/vim)写入系统剪贴板;事件来自 feed 线程,落板走 UI 线程。</summary>
    private void OnRemoteClipboardWrite(string text)
    {
        // ReSharper disable once AsyncVoidMethod
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text);
                }
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程独占是常事(Windows 上尤其)。这里是 async void:
                // 不兜住就是进程级未处理异常 —— 远端一次 yank 把整个应用带走。
                System.Diagnostics.Trace.WriteLine($"[VelaTerminalControl] 远端剪贴板写入失败:{ex.Message}");
            }
        });
    }

    // ---- Bell (设置 → 终端 → 提示音与通知) ----------------------------------

    /// <summary>
    /// 在喂入线程上触发;编组到 UI 线程后,按 <see cref="BellMode" /> 闪烁 / 蜂鸣,
    /// 并通知宿主(标签闪烁)。
    /// </summary>
    private void OnBell()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnBell);
            return;
        }
        BellRang?.Invoke();
        if (BellMode == "visual")
        {
            _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(120);
            InvalidateTerminal();
            DispatcherTimer.RunOnce(InvalidateTerminal, TimeSpan.FromMilliseconds(140));
        }
        else if (BellMode == "system" && OperatingSystem.IsWindows())
        {
            NativeMethods.MessageBeep(0);
        }
    }

    // ---- Cursor blink --------------------------------------------------------

    /// <summary>
    /// 仅在聚焦且启用闪烁时运行闪烁计时器;否则光标保持实心,
    /// 不会发生每 500ms 一次的重新绘制。
    /// </summary>
    private void UpdateCursorBlinkTimer()
    {
        bool shouldRun = _hasFocus && (CursorBlink || Emulator.Modes.CursorBlink);
        if (shouldRun)
        {
            _cursorBlinkTimer ??= new(
                TimeSpan.FromMilliseconds(530),
                DispatcherPriority.Background,
                (_, _) => BlinkTick()
            );
            if (!_cursorBlinkTimer.IsEnabled)
            {
                _cursorBlinkTimer.Start();
            }
        }
        else
        {
            _cursorBlinkTimer?.Stop();
            if (_cursorBlinkVisible)
            {
                return;
            }
            _cursorBlinkVisible = true;
            InvalidateTerminal();
        }
    }

    /// <summary>
    /// 翻转闪烁相位并<b>只</b>失效光标叠加层。
    /// </summary>
    /// <remarks>
    /// 这是拆出 <see cref="CursorOverlay" /> 的全部意义所在:闪烁不改变正文一个像素,
    /// 却曾经每 530ms 逼着整屏(1 万格的解析色 + 逐行语义扫描 + 全部 GlyphRun)重记录一遍。
    /// 本方法是本类里唯一允许绕过 <see cref="InvalidateTerminal" /> 的地方。
    /// internal 供 <c>CursorOverlayUiTests</c> 免去等待真实计时器。
    /// </remarks>
    internal void BlinkTick()
    {
        _cursorBlinkVisible = !_cursorBlinkVisible;
        _overlay.InvalidateVisual();
    }

    /// <summary>本控件正文(不含叠加层)的累计渲染次数,仅供 headless 测试断言重绘范围。</summary>
    /// <summary>当前生效的调色板(主题底 + 用户覆盖叠加后的结果),供叠加顺序的用例断言。</summary>
    internal TerminalPalette PaletteForTest => Emulator.Palette;

    internal int BodyRenderCountForTest { get; private set; }

    /// <summary>光标/幽灵叠加层的累计渲染次数,仅供 headless 测试断言重绘范围。</summary>
    internal int OverlayRenderCountForTest { get; private set; }

    /// <summary>输入会重置闪烁相位,使光标在输入落点处立即可见。</summary>
    private void ResetCursorBlink()
    {
        if (_cursorBlinkTimer is { IsEnabled: true } timer)
        {
            timer.Stop();
            timer.Start();
        }
        if (!_cursorBlinkVisible)
        {
            _cursorBlinkVisible = true;
            InvalidateTerminal();
        }
    }

    // 跨线程输出更新的合批标志:同一帧内多次 Feed 只排一次 UI 回调,后续搭便车。
    private int _outputUpdateQueued;

    private void OnEmulatorUpdated()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyOutputUpdate();
        }
        else if (Interlocked.CompareExchange(ref _outputUpdateQueued, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _outputUpdateQueued, 0);
                ApplyOutputUpdate();
            });
        }
    }

    private void ApplyOutputUpdate()
    {
        ReconcileGridWithLayout();
        // 仅在已处于底部时才跟随输出;否则保持用户的历史
        // 视图固定不动,以免后台输出把其拽回下方(修复 #15)—— 除非
        // 设置 → 终端 → 有输出时自动滚动已开启,此时会把视图拉回实时底部。
        int scrollback = Emulator.Screen.ScrollbackCount;
        int offsetBefore = _scrollOffset;
        if (ScrollOnOutput && _scrollOffset > 0 && scrollback > _lastScrollbackCount)
        {
            _scrollOffset = 0;
        }
        else
        {
            _scrollOffset = PinScrollOffset(_scrollOffset, _lastScrollbackCount, scrollback);
        }
        bool scrollStateChanged = scrollback != _lastScrollbackCount || _scrollOffset != offsetBefore;
        _lastScrollbackCount = scrollback;
        InvalidateTerminal();
        // 滚动几何没变(如 htop 原地重绘、进度条刷新)就不惊动滚动条等订阅者:
        // 稳态输出下这是每次 Feed 一趟的下游刷新,省掉是纯赚。
        if (scrollStateChanged)
        {
            ScrollChanged?.Invoke();
        }
        _imeClient?.NotifyCursorMoved();
    }

    /// <summary>
    /// 为操作系统输入法提供一个锚定在终端光标处的客户端,使 IME
    /// 候选窗口(中文/日文/韩文组字)在文本将要落下的位置旁打开,
    /// 而非窗口角落(#14b)。
    /// </summary>
    private void OnTextInputMethodClientRequested(
        object? sender,
        TextInputMethodClientRequestedEventArgs e
    )
    {
        if (!ImeEnabled)
        {
            return;
        }
        _imeClient ??= new(this);
        e.Client = _imeClient;
    }

    /// <summary>光标单元格在控件坐标系中的矩形(命令补全弹层锚点,与 IME 光标同一套计算)。</summary>
    public Rect GetCursorRect() => GetImeCursorRect();

    /// <summary>
    /// 光标单元格左移 <paramref name="columnsBack" /> 列后的矩形(不越过行首/装订线)。
    /// 补全弹层锚定在输入起点而非光标处,避免面板随键入逐列漂移。列数按字符数近似,
    /// CJK 宽字符会略有偏差——锚点仅供定位,可接受。
    /// </summary>
    public Rect GetCursorRect(int columnsBack)
    {
        Rect rect = GetImeCursorRect();
        double x = Math.Max(
            ContentPadding + GutterWidth(),
            rect.X - columnsBack * CellWidthForTest
        );
        return new(x, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>备用屏(DECSET 1047/1049)是否激活。全屏程序(vim/htop)内宿主不启用命令补全。</summary>
    public bool IsAlternateScreenActive => Emulator.IsAlternateScreen;

    // ---- 幽灵文本(fish/Warp 式补全叠画)---------------------------------------
    //
    // 纯视觉覆盖层,不进屏幕缓冲;宿主(补全逻辑)只负责设置/清除"完整候选",
    // 剩余部分不由宿主逐键推送,而是在每次重绘时按屏上"光标左侧已回显文本"现算。
    //
    // 为何这样做:若逐键推送剩余文本,内容(键入时钟,按键即刻)与位置(回显时钟,
    // 需 PTY 往返)各走一套时钟——键入领先回显时,缩短/增长后的幽灵先画到尚未随回显
    // 前移的旧光标处、回显到达再 snap 回来,连打即成抖动,退格同理反向抖动,SSH 高延迟
    // 下尤甚。这里改为:取完整候选中"其前缀恰是光标左侧已回显文本之后缀"的最长重叠,
    // 剩余部分即候选去掉该重叠。此值纯由屏幕真实状态(已回显文本 + 光标)决定,与回显
    // 延迟完全无关,故键入/空格/退格都恒贴光标、永不失步(fish/zsh 把建议写进缓冲与
    // 光标原子刷新,本质相同的不变式:所见即缓冲)。

    private string? _ghostFull;

    /// <summary>当前是否有生效的幽灵候选。</summary>
    public bool HasGhost => _ghostFull is not null;

    /// <summary>设置完整补全候选(fish 语义:必为当前输入的严格更长同前缀候选)。</summary>
    public void SetGhostSuggestion(string full)
    {
        if (_ghostFull == full)
        {
            return;
        }
        _ghostFull = full;
        InvalidateTerminal();
    }

    /// <summary>清除幽灵候选(立即重绘;移除叠画不会引发位置抖动)。</summary>
    public void ClearGhostSuggestion()
    {
        if (_ghostFull is null)
        {
            return;
        }
        _ghostFull = null;
        InvalidateTerminal();
    }

    /// <summary>
    /// 按屏上真实状态现算当前应显示的幽灵剩余文本:取候选中其前缀恰为光标左侧已回显
    /// 文本之后缀的最长重叠,剩余即候选去掉该重叠;无候选/无重叠/已键满时为 null。
    /// 完全由已回显文本与光标决定,与回显延迟无关。供渲染与"→/End 接受"共用,
    /// 确保接受写入的正是屏上所见。
    /// </summary>
    public string? CurrentGhostRemainder()
    {
        if (_ghostFull is not { Length: > 0 } full)
        {
            return null;
        }
        int col = Emulator.CursorX;
        if (col <= 0)
        {
            return null;
        }
        string line = Emulator.Screen.ActiveLine(Emulator.CursorY).GetText();

        // 光标右侧同一行还有非空白内容时一律不显示:幽灵是叠画层,画上去会与既有文字
        // 重影,且"补全在行尾"的语义此时并不成立。典型场景是 zsh-autosuggestions ——
        // shell 自己把建议写进了光标右侧的缓冲(#115),还有行中编辑与右提示符 RPROMPT。
        // 判定必须落在渲染时钟上:宿主侧只在输入变化那一刻判过一次(HasTextRightOfCursor),
        // 而 shell 自带的建议要等 PTY 往返之后才回显,那时幽灵早已设好、宿主不会再复核,
        // 只有每帧按屏上真实状态现判才拦得住。GetText 已裁掉尾部空格,故行尾留白不误判。
        if (line.Length > col && !string.IsNullOrWhiteSpace(line[col..]))
        {
            return null;
        }

        // 光标左侧已回显文本(1 单元格≈1 字符,与 HasTextRightOfCursor 等既有假设一致)。
        int before = Math.Min(col, line.Length);
        // 最长 k:line 的末 k 字符 == full 的前 k 字符。先求最长(含 k==full.Length),
        // 再判定:k==full.Length 表示候选已被完整键入 → 无剩余(返回 null),绝不回退到
        // 更短的偶合后缀(否则 "abcabc" 键满后会误显 "abc")。
        int max = Math.Min(full.Length, before);
        for (int k = max; k >= 1; k--)
        {
            if (string.CompareOrdinal(line, before - k, full, 0, k) == 0)
            {
                return k < full.Length ? full[k..] : null;
            }
        }
        return null;
    }

    /// <summary>光标单元在控件坐标系中的矩形(与 RenderCursor 同一套计算)。</summary>
    private Rect GetImeCursorRect()
    {
        TerminalScreen screen = Emulator.Screen;
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = ScreenRowForAbsolute(cursorAbsolute);
        if (screenRow < 0)
        {
            screenRow = Math.Max(0, screen.Rows - 1 - _scrollOffset);
        }
        return new(
            ContentPadding + GutterWidth() + screen.CursorX * CellWidthForTest,
            ContentPadding + screenRow * CellHeightForTest,
            CellWidthForTest,
            CellHeightForTest
        );
    }

    // ---- Palette ------------------------------------------------------------

    /// <summary>
    /// 为给定主题变体初始化调色板(跟随应用主题的默认配色):
    /// dark = Dracula(官方 Windows Terminal 方案),
    /// light = Solarized Light(与设置 → 外观 内置方案同一套色值)。
    /// </summary>
    private static void ApplyDesignPalette(TerminalPalette palette, bool light = false)
    {
        if (light)
        {
            palette.DefaultForeground = Rgba.FromRgb(0x65, 0x7B, 0x83); // base00
            palette.DefaultBackground = Rgba.FromRgb(0xFD, 0xF6, 0xE3); // base3
            palette.CursorColor = Rgba.FromRgb(0x65, 0x7B, 0x83);
            palette.SelectionBackground = new(0x40, 0x58, 0x6E, 0x75); // base01 @25%(方案原生选区 base2 与背景过近,取更可辨的半透明灰蓝)
            palette.SetAnsi(0, Rgba.FromRgb(0x07, 0x36, 0x42)); // black  = base02
            palette.SetAnsi(1, Rgba.FromRgb(0xDC, 0x32, 0x2F)); // red
            palette.SetAnsi(2, Rgba.FromRgb(0x85, 0x99, 0x00)); // green
            palette.SetAnsi(3, Rgba.FromRgb(0xB5, 0x89, 0x00)); // yellow
            palette.SetAnsi(4, Rgba.FromRgb(0x26, 0x8B, 0xD2)); // blue
            palette.SetAnsi(5, Rgba.FromRgb(0xD3, 0x36, 0x82)); // magenta
            palette.SetAnsi(6, Rgba.FromRgb(0x2A, 0xA1, 0x98)); // cyan
            palette.SetAnsi(7, Rgba.FromRgb(0xEE, 0xE8, 0xD5)); // white  = base2
            palette.SetAnsi(8, Rgba.FromRgb(0x58, 0x6E, 0x75)); // bright black = base01
            palette.SetAnsi(9, Rgba.FromRgb(0xCB, 0x4B, 0x16)); // bright red (orange)
            palette.SetAnsi(10, Rgba.FromRgb(0x85, 0x99, 0x00));
            palette.SetAnsi(11, Rgba.FromRgb(0xB5, 0x89, 0x00));
            palette.SetAnsi(12, Rgba.FromRgb(0x26, 0x8B, 0xD2));
            palette.SetAnsi(13, Rgba.FromRgb(0x6C, 0x71, 0xC4)); // bright magenta (violet)
            palette.SetAnsi(14, Rgba.FromRgb(0x93, 0xA1, 0xA1)); // bright cyan = base1
            palette.SetAnsi(15, Rgba.FromRgb(0xFD, 0xF6, 0xE3)); // bright white = base3
            return;
        }
        palette.DefaultForeground = Rgba.FromRgb(0xF8, 0xF8, 0xF2);
        palette.DefaultBackground = Rgba.FromRgb(0x28, 0x2A, 0x36);
        palette.CursorColor = Rgba.FromRgb(0xF8, 0xF8, 0xF2);
        palette.SelectionBackground = new(0x99, 0x44, 0x47, 0x5A); // dracula selection
        palette.SetAnsi(0, Rgba.FromRgb(0x21, 0x22, 0x2C)); // black
        palette.SetAnsi(1, Rgba.FromRgb(0xFF, 0x55, 0x55)); // red
        palette.SetAnsi(2, Rgba.FromRgb(0x50, 0xFA, 0x7B)); // green
        palette.SetAnsi(3, Rgba.FromRgb(0xF1, 0xFA, 0x8C)); // yellow
        palette.SetAnsi(4, Rgba.FromRgb(0xBD, 0x93, 0xF9)); // blue (dracula purple)
        palette.SetAnsi(5, Rgba.FromRgb(0xFF, 0x79, 0xC6)); // magenta (dracula pink)
        palette.SetAnsi(6, Rgba.FromRgb(0x8B, 0xE9, 0xFD)); // cyan
        palette.SetAnsi(7, Rgba.FromRgb(0xF8, 0xF8, 0xF2)); // white
        palette.SetAnsi(8, Rgba.FromRgb(0x62, 0x72, 0xA4)); // bright black (comment)
        palette.SetAnsi(9, Rgba.FromRgb(0xFF, 0x6E, 0x6E));
        palette.SetAnsi(10, Rgba.FromRgb(0x69, 0xFF, 0x94));
        palette.SetAnsi(11, Rgba.FromRgb(0xFF, 0xFF, 0xA5));
        palette.SetAnsi(12, Rgba.FromRgb(0xD6, 0xAC, 0xFF));
        palette.SetAnsi(13, Rgba.FromRgb(0xFF, 0x92, 0xDF));
        palette.SetAnsi(14, Rgba.FromRgb(0xA4, 0xFF, 0xFF));
        palette.SetAnsi(15, Rgba.FromRgb(0xFF, 0xFF, 0xFF));
    }

    /// <summary>
    /// 终端「默认背景」整屏填充的不透明度(0..1,默认 1=不透明,行为不变)。低于 1 时整屏默认背景变半透明,
    /// 透出其后的应用背景图(设置→外观→背景图片)。只作用于整屏默认背景填充,不影响单元格自身着色的背景
    /// (选区、彩色底等仍不透明),因此不牺牲文本可读性。仅在设置了背景图时由 MainWindowViewModel 下调。
    /// </summary>
    public double BackgroundOpacity
    {
        get;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(clamped - field) < 0.001)
            {
                return;
            }
            field = clamped;
            InvalidateTerminal();
        }
    } = 1.0;

    private ImmutableSolidColorBrush? _bgFillBrush;
    private uint _bgFillPacked;
    private int _bgFillOp = -1;

    /// <summary>整屏默认背景填充画刷:不透明时走共享缓存,半透明时按(颜色×不透明度)缓存一支专用画刷。</summary>
    private ImmutableSolidColorBrush DefaultBackgroundBrush(Rgba bg)
    {
        if (BackgroundOpacity >= 0.999)
        {
            return BrushFor(bg);
        }
        int op = (int)Math.Round(BackgroundOpacity * 1000);
        if (_bgFillBrush is null || _bgFillPacked != bg.Packed || _bgFillOp != op)
        {
            byte a = (byte)Math.Round(bg.A * BackgroundOpacity);
            _bgFillBrush = new(Color.FromArgb(a, bg.R, bg.G, bg.B));
            _bgFillPacked = bg.Packed;
            _bgFillOp = op;
        }
        return _bgFillBrush;
    }

    private ImmutableSolidColorBrush BrushFor(Rgba c)
    {
        if (_brushCache.TryGetValue(c.Packed, out ImmutableSolidColorBrush? brush))
        {
            return brush;
        }
        // 同 PenFor:truecolor 下颜色空间无界,封顶防止字典长期膨胀。
        if (_brushCache.Count > 4096)
        {
            _brushCache.Clear();
        }
        brush = new(Color.FromArgb(c.A, c.R, c.G, c.B));
        _brushCache[c.Packed] = brush;
        return brush;
    }

    /// <summary>
    /// 按颜色缓存的 1px 画笔:下划线/删除线/语义下划线每个 cell 画一次线,
    /// 逐格 new Pen 在满行 URL/下划线文本时是每帧 O(cols) 的堆分配。
    /// 缓存上界 = 用过的前景色数,与 <see cref="_brushCache" /> 同量级。
    /// </summary>
    private ImmutablePen PenFor(Rgba c)
    {
        if (_penCache.TryGetValue(c.Packed, out ImmutablePen? pen))
        {
            return pen;
        }
        // Truecolor 输出(渐变进度条等)每 cell 一色,不设上界会无限增长;
        // 全清后一两帧内按需回填,成本可忽略。
        if (_penCache.Count > 4096)
        {
            _penCache.Clear();
        }
        pen = new(BrushFor(c));
        _penCache[c.Packed] = pen;
        return pen;
    }

    /// <summary>
    /// 返回单个单元字形对应的缓存 <see cref="FormattedText" />。每个字形仍由调用方
    /// 绘制在各自的网格位置,因此宽字符(CJK)单元与等宽对齐被精确保留;
    /// 只是把昂贵的塑形开销摊薄了。
    /// </summary>
    private FormattedText GlyphFor(in TerminalCell cell, Rgba fg, bool bold, bool italic)
    {
        int style = (bold ? 1 : 0) | (italic ? 2 : 0);
        var key = new GlyphKey(cell.Rune, cell.Combining, fg.Packed, style);
        if (_glyphCache.TryGetValue(key, out FormattedText? cached))
        {
            return cached;
        }

        // 限制缓存大小;整体清空也无妨,因为它会在一两帧内重新填满。
        if (_glyphCache.Count > 8192)
        {
            _glyphCache.Clear();
        }
        var typeface = new Typeface(
            FontFamily,
            italic ? FontStyle.Italic : FontStyle.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal
        );
        var ft = new FormattedText(
            cell.GetText(),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            BrushFor(fg)
        );
        _glyphCache[key] = ft;
        return ft;
    }

    // ---- Metrics & layout ---------------------------------------------------

    private void RecomputeMetrics()
    {
        var typeface = new Typeface(FontFamily);
        var probe = new FormattedText(
            "0",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brushes.White
        );
        CellWidthForTest = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        // 行高倍数(设置 → 终端 → 行高):多出的空间上下均分,字形垂直居中。
        CellHeightForTest = Math.Max(1, Math.Ceiling(probe.Height * LineHeight));
        _glyphYOffset = Math.Max(0, (CellHeightForTest - probe.Height) / 2);
        _baselineOffset = probe.Baseline + _glyphYOffset;

        // 缓存的字形绑定在旧的字体/字号上;任何度量变化都应丢弃它们。
        _glyphCache.Clear();
        _gutterTextCache.Clear();
        _gutterTextCacheBrush = null;
        _ghostFormatted = null;
        _styleTypefacesReady = false;
    }

    /// <summary>
    /// 解析(并缓存)加粗/斜体风格组合下的主 <see cref="GlyphTypeface" />,
    /// 供批量化字形运行路径使用。平台无法提供时为 null,此时调用方回退到逐单元
    /// FormattedText 路径。
    /// </summary>
    private GlyphTypeface? StyleTypeface(int style)
    {
        if (!_styleTypefacesReady)
        {
            for (int s = 0; s < 4; s++)
            {
                var tf = new Typeface(
                    FontFamily,
                    (s & 2) != 0 ? FontStyle.Italic : FontStyle.Normal,
                    (s & 1) != 0 ? FontWeight.Bold : FontWeight.Normal
                );
                try
                {
                    _styleTypefaces[s] = tf.GlyphTypeface;
                }
                catch
                {
                    _styleTypefaces[s] = null;
                }
            }
            _styleTypefacesReady = true;
        }
        return _styleTypefaces[style];
    }

    /// <summary>
    /// 向待处理运行追加一个字形;每当风格或前景色变化时,先冲刷当前运行再开启新运行。
    /// 自上一字形起跳过的列(空格、空白)被并入上一字形的步进中,以保持对齐精确。
    /// </summary>
    // ---- 背景矩形合批 ----
    // 与字形合批同构的状态机:相邻同色的非默认背景合成一个 FillRectangle。
    // 全屏 TUI(htop 的占用条、带主题的 vim)与大段选区原先是 O(行 × 列) 个矩形指令,
    // 200×50 的窗口最坏一万次;合批后大片同色区域只发一次。
    // -1 表示当前没有待发的 run。
    private int _bgRunStart = -1;
    private int _bgRunEnd;
    private uint _bgRunPacked;
    private IBrush? _bgRunBrush;

    /// <summary>整帧发出的背景矩形数量(合批守门用例读它)。</summary>
    internal int BackgroundRectCountForTest { get; private set; }

    /// <summary>
    /// 把一格的背景并入当前 run:颜色变了、列不连续、或退回默认背景,就先把上一段发出去。
    /// 默认背景不画(那是画布本身的颜色)。
    /// </summary>
    private void AppendBackground(DrawingContext context, double y, Rgba bg, int col, int width, Rgba defaultBackground)
    {
        if (bg.Equals(defaultBackground))
        {
            FlushBackgroundRun(context, y);
            return;
        }
        if (_bgRunStart >= 0 && (bg.Packed != _bgRunPacked || col != _bgRunEnd))
        {
            FlushBackgroundRun(context, y);
        }
        if (_bgRunStart < 0)
        {
            _bgRunStart = col;
            _bgRunPacked = bg.Packed;
            _bgRunBrush = BrushFor(bg);
        }
        _bgRunEnd = col + width;
    }

    /// <summary>
    /// 发出当前背景 run(若有)。
    /// </summary>
    /// <remarks>
    /// **不变量:背景必须画在同一格的任何前景之前。** 字形是攒批后延迟画的,而下划线、
    /// 删除线、以及主字体覆盖不到时的 <c>FormattedText</c> 回退都是即时画的;背景一旦
    /// 也变成延迟画,就必须在这些绘制发生之前冲刷,否则「A 红底样式 X,B 红底样式 Y」
    /// 这种序列会在 B 处先画出 A 的字形、再画整段红底把它盖掉。
    /// 因此 <see cref="FlushGlyphRun" /> 在真的要画字形时先调用本方法,而下划线、删除线、
    /// <c>FormattedText</c> 回退三处即时绘制各自显式再调一次 —— 光靠 <c>FlushGlyphRun</c>
    /// 挡不住:批次为空时它直接返回,背景就留到行尾才发,反过来盖住刚画的字
    /// (整行 CJK 走的正是这条空批次 + 即时绘制的路径)。
    /// <para>
    /// 代价:背景 run 会在每处字形 run 断裂点(样式或前景色变化)被迫断开。所以合并收益
    /// 集中在**大片同色区域**(选区、搜索高亮、占用条、彩色分隔行),而同底色但前景色频繁
    /// 变化的行(vim 状态行)仍接近逐格 —— 这是次序正确性的必然代价,不是缺陷。
    /// </para>
    /// </remarks>
    private void FlushBackgroundRun(DrawingContext context, double y)
    {
        if (_bgRunStart < 0)
        {
            return;
        }
        context.FillRectangle(_bgRunBrush!, CellRect(_bgRunStart, _bgRunEnd - _bgRunStart, y));
        BackgroundRectCountForTest++;
        _bgRunStart = -1;
        _bgRunBrush = null;
    }

    private void AppendGlyph(
        DrawingContext context,
        double y,
        int style,
        Rgba fg,
        int col,
        int width,
        ushort glyphId,
        char ch
    )
    {
        if (_runGlyphs.Count > 0 && (style != _runStyle || fg.Packed != _runFg))
        {
            FlushGlyphRun(context, y);
        }
        if (_runGlyphs.Count == 0)
        {
            _runStyle = style;
            _runFg = fg.Packed;
            _runBrush = BrushFor(fg);
            _runStartCol = col;
        }
        else
        {
            int gapCells = col - (_runPrevCol + _runPrevWidth);
            if (gapCells > 0)
            {
                GlyphInfo last = _runGlyphs[^1];
                _runGlyphs[^1] = new(
                    last.GlyphIndex,
                    last.GlyphCluster,
                    last.GlyphAdvance + gapCells * CellWidthForTest,
                    last.GlyphOffset
                );
            }
        }
        _runGlyphs.Add(new(glyphId, _runCharCount, width * CellWidthForTest));
        if (_runCharCount == _runChars.Length)
        {
            Array.Resize(ref _runChars, _runChars.Length * 2);
        }
        _runChars[_runCharCount++] = ch;
        _runPrevCol = col;
        _runPrevWidth = width;
    }

    /// <summary>将待处理的字形运行(若有)作为单次 DrawGlyphRun 发出,并重置缓冲区。</summary>
    /// <remarks>
    /// <b>为什么可以把复用缓冲直接交给 GlyphRun</b>:此处原先是
    /// <c>_runChars.ToArray().AsMemory()</c> + <c>_runGlyphs.ToArray()</c> —— 每个 run、每帧两个
    /// 定长数组,而彩色输出(ls --color、提示符)一行会拆成好几个 run,是正文重绘里最大的一笔分配。
    /// <para>
    /// 之所以能安全复用:Avalonia 的延迟渲染在 <c>DrawGlyphRun</c> 里就地取用
    /// <c>GlyphRun.PlatformImpl</c>(把字形塑形成平台的不可变文本 blob),而落进渲染数据的
    /// <c>DrawGlyphRunPayload</c> 只存一个 <c>Int32</c> 资源索引 —— 不持有任何托管数组。
    /// 也就是说 <c>DrawGlyphRun</c> 返回之后,这两个缓冲就与已记录的绘制无关了。
    /// </para>
    /// <para>
    /// <b>GlyphRun 必须释放</b>:它实现 <see cref="IDisposable" />,内部持有引用计数的原生
    /// 文本 blob(<c>IRef&lt;IGlyphRunImpl&gt;</c>),而 <c>GlyphRun</c> 自身<b>没有终结器</b> ——
    /// 兜底只剩 <c>RefCountable.Ref&lt;T&gt;</c> 的终结器。原生内存不计入 GC 压力,GC 因此没有
    /// 理由及时跑,漏掉的引用会一路堆到某次 gen2 才释放:每帧每个 run 泄漏一个文本 blob。
    /// 释放是安全的 —— 上面说的 <c>Clone()</c> 已经让渲染数据自持一份引用,这里放掉的只是我们自己那份。
    /// </para>
    /// <para>
    /// 以上两点(缓冲复用、画完即释放)都只有真实光栅化才验得到,由
    /// <c>VelaShell.Terminal.RenderTests.GlyphRenderingTests</c> 按像素把守。
    /// </para>
    /// </remarks>
    private void FlushGlyphRun(DrawingContext context, double y)
    {
        if (_runGlyphs.Count == 0)
        {
            return;
        }
        // 字形要画在背景之上:先把待发的背景 run 落地,否则它会盖掉这批字形。
        FlushBackgroundRun(context, y);
        GlyphTypeface? gtf = _runStyle >= 0 ? _styleTypefaces[_runStyle] : null;
        if (gtf is not null && _runBrush is not null)
        {
            try
            {
                // List<GlyphInfo> 已经是长度精确的 IReadOnlyList<GlyphInfo>,直接交出去;
                // 字符缓冲按实际长度切片。两者都不再复制。
                using var run = new GlyphRun(
                    gtf,
                    FontSize,
                    _runChars.AsMemory(0, _runCharCount),
                    _runGlyphs,
                    new Point(_runStartCol * CellWidthForTest, y + _baselineOffset)
                );
                context.DrawGlyphRun(_runBrush, run);
            }
            catch
            {
                // 本不应发生,但若平台拒绝我们的字形运行,就停止批处理,
                // 并在会话余下时间改为重绘,使一切经由逐单元 FormattedText 路径重新渲染
                // (结果正确,只是更慢)。
                _glyphRunUnsupported = true;
                Dispatcher.UIThread.Post(InvalidateTerminal);
            }
        }
        _runGlyphs.Clear();
        _runCharCount = 0;
        _runStyle = -1;
    }

    /// <summary>布局控件并把网格 reflow 到最终布局尺寸。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);

        // 叠加层铺满整个控件:它内部再套与正文相同的平移,坐标系因此完全一致。
        // 手工挂在 VisualChildren 上的子元素不参与默认的测量/排布,必须自己走这两步,
        // 否则它拿不到 Bounds、渲染器直接跳过 —— 表现为光标彻底消失。
        _overlay.Measure(finalSize);
        _overlay.Arrange(new Rect(finalSize));
        ApplyLayoutSize(finalSize);
        return result;
    }

    private void RelayoutFromBounds() => ApplyLayoutSize(Bounds.Size);

    /// <summary>
    /// 自愈闸:模拟器网格若与宿主最后下发的尺寸对不上,说明有东西绕过布局改了几何。
    /// 此时按当前布局重新下发一次网格,顺带把新尺寸通知 PTY。
    /// <para>
    /// 为什么需要:<see cref="ApplyLayoutSize" /> 只在 arrange 时跑,而 arrange 只在布局失效时发生。
    /// 网格被悄悄改小后控件尺寸并没变,于是没有任何 arrange 会来纠正 —— 用户看到的就是
    /// 「打开 screen 后可选中的区域变小,切一次标签才恢复」(issue #253:切标签重挂控件才触发了 arrange)。
    /// 这里每次输出更新做两次整型比较,代价可忽略,却让这一类「悄悄分家」自己收敛。
    /// </para>
    /// <para>
    /// <c>_appliedColumns == 0</c> 表示还没拿到真实布局(单测里的裸控件即如此),此时不介入,
    /// 免得在退化尺寸上空转。
    /// </para>
    /// </summary>
    private void ReconcileGridWithLayout()
    {
        if (_appliedColumns <= 0)
        {
            return;
        }
        if (Emulator.Columns == _appliedColumns && Emulator.Rows == _appliedRows)
        {
            return;
        }
        RelayoutFromBounds();
    }

    private void ApplyLayoutSize(Size size)
    {
        if (CellWidthForTest <= 0 || CellHeightForTest <= 0)
        {
            return;
        }
        // 用户内边距在四边各扣一份(右侧再叠加滚动条留白带),剩下的才是网格可用区。
        double pad = ContentPadding;
        int cols = (int)(
            (size.Width - pad * 2 - GutterWidth() - RightPadding) / CellWidthForTest
        );
        int rows = (int)((size.Height - pad * 2) / CellHeightForTest);

        // 忽略过早/退化的布局过程(尺寸为零或不足一格)。在这里把网格压缩成
        // 单列,正是过去登录横幅每行只渲染一个字符的元凶:后续每个字符都自动换行。
        // 在真实尺寸到来之前,保持当前(或默认 120x32)网格。
        if (cols < 2 || rows < 2)
        {
            return;
        }
        if (cols == Emulator.Columns && rows == Emulator.Rows)
        {
            // 已经是这个网格了,但仍要记账:自愈闸靠 _applied* 判断"模拟器是否被绕过宿主改过",
            // 首帧若不记账,它会把每次输出都当成分家、反复空跑重排。
            _appliedColumns = cols;
            _appliedRows = rows;
            return;
        }

        // 本地网格立即 reflow,使拖拽感觉实时,并且也立刻通知 PTY —— 这是主流做法。
        // 本地与远端必须保持同步:早期带防抖的通知让远端的认知(readline 的提示符行数学)
        // 落后许多次 reflow,导致其相对光标移动与擦除落在错误的行上,逐步破坏缓冲内容。
        // 传输层按顺序串行化发送,将突发合并为最新尺寸。
        Emulator.Resize(cols, rows);
        _appliedColumns = cols;
        _appliedRows = rows;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Emulator.Screen.ScrollbackCount);
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        ClearFolds(); // reflow 会重建行对象,折叠引用失效。
        // reflow 会移动绝对行;陈旧的选区会标记(并复制)错误的文本。
        ClearSelection();
        InvalidateTerminal();
        ScrollChanged?.Invoke();
        PtySizeChanged?.Invoke(cols, rows);
    }

    /// <summary>
    /// 主机流(转义序列)改变了模拟器网格。事件来自 feed 线程,先编组回 UI 线程再接纳。
    /// </summary>
    private void OnHostGeometryChanged(int cols, int rows)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AdoptHostGeometry(cols, rows);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AdoptHostGeometry(cols, rows));
        }
    }

    /// <summary>
    /// 接纳一次主机端发起的网格改动:把宿主的记账、滚动/折叠/选区状态与新几何对齐,并把新尺寸
    /// 转告 PTY。关键在最后一步 —— 远端若不知道宽度变了,它的换行与光标数学会继续按旧宽度算,
    /// 缓冲区会被一点点写花。
    /// </summary>
    private void AdoptHostGeometry(int cols, int rows)
    {
        if (cols == _appliedColumns && rows == _appliedRows)
        {
            return;
        }
        _appliedColumns = cols;
        _appliedRows = rows;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Emulator.Screen.ScrollbackCount);
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        ClearFolds();
        ClearSelection();
        InvalidateTerminal();
        ScrollChanged?.Invoke();
        PtySizeChanged?.Invoke(cols, rows);
    }

    // ---- Rendering ----------------------------------------------------------

    // 本帧的设备像素栅格。整数 DIP 的格子尺寸在 125%/150% 这类分数缩放下不落在整数设备像素上,
    // 相邻格子的背景各自抗锯齿、叠加后凑不回满覆盖,于是每条格线上留下一道浅缝(issue #245)。
    private DevicePixelGrid _pixels = new(0, 0, 1);

    /// <summary>
    /// 刷新本帧的设备像素栅格。原点取「控件在渲染根中的位置 + 正文平移(内边距 + 侧栏)」:
    /// 吸附必须以渲染根的像素格为准,控件本身若停在半个像素上,只按控件内坐标取整照样错位。
    /// </summary>
    private void RefreshPixelGrid()
    {
        var top = TopLevel.GetTopLevel(this);
        double scale = RenderScalingOverrideForTest > 0 ? RenderScalingOverrideForTest : top?.RenderScaling ?? 1;
        Point offset = top is null ? default : this.TranslatePoint(default, top) ?? default;
        _pixels = new(offset.X + ContentPadding + GutterWidth(), offset.Y + ContentPadding, scale);
    }

    /// <summary>
    /// 单元格背景矩形(正文坐标系),四边已吸附到设备像素 —— 相邻格子因此严丝合缝。
    /// 字形位置<b>不</b>吸附:那会让字距忽宽忽窄,而背景带宽度浮动 ±1 设备像素肉眼不可见。
    /// </summary>
    private Rect CellRect(int col, int width, double y) =>
        _pixels.Snap(
            new(col * CellWidthForTest, y, CellWidthForTest * width, CellHeightForTest)
        );

    /// <summary>
    /// 视觉 BEL:整个终端上的一次短暂半透明闪烁(§终端 → 视觉闪烁)
    /// </summary>
    /// <param name="context"></param>
    public override void Render(DrawingContext context)
    {
        BodyRenderCountForTest++;
        BackgroundRectCountForTest = 0;
        TerminalScreen screen = Emulator.Screen;
        TerminalPalette palette = Emulator.Palette;
        RefreshPixelGrid();
        context.FillRectangle(DefaultBackgroundBrush(palette.DefaultBackground), new(Bounds.Size));
        int rows = screen.Rows;
        int cols = screen.Columns;

        // 计算本帧「屏幕行 → 绝对缓冲行」映射(_screenToAbs):无折叠时即连续 topAbsolute+sr(与原行为一致),
        // 有折叠时跳过被隐藏的行。侧栏、正文、光标、命中测试全部复用该映射,确保三者对齐。
        BuildScreenRowMap(screen, rows);
        List<SelectionSpan> spans = SelectionSpans();

        // 行号/时间侧栏在正文左侧:先画侧栏,再把正文(含光标、选区)整体右移一个侧栏宽度绘制,
        // 这样所有 col*_cellWidth 的坐标计算保持不变,只在命中测试处减去侧栏宽度即可。
        // 用户内边距是整体平移:底色已铺满全控件,平移只挪侧栏与正文,故留白处自然是终端底色。
        double pad = ContentPadding;
        if (GutterEnabled)
        {
            using (context.PushTransform(Matrix.CreateTranslation(pad, pad)))
            {
                RenderGutter(context, screen, palette, rows);
            }
        }
        using (context.PushTransform(Matrix.CreateTranslation(pad + GutterWidth(), pad)))
        {
            for (int screenRow = 0; screenRow < rows; screenRow++)
            {
                int absoluteRow = _screenToAbs[screenRow];
                if (absoluteRow < 0)
                {
                    continue;
                }
                TerminalRow line = screen.ViewLine(absoluteRow);
                double y = screenRow * CellHeightForTest;
                RenderLine(context, palette, line, cols, y, absoluteRow, spans);
            }
        }

        // 视觉 BEL:整个终端上的一次短暂半透明闪烁(§终端 → 视觉闪烁)。
        if (_bellFlashUntil > DateTime.UtcNow)
        {
            context.FillRectangle(BellFlashBrush, new(Bounds.Size));
        }

        // 光标与幽灵文本不在这里画,由 _overlay 这个独立可视子元素承担 —— 见 CursorOverlay。
    }

    /// <summary>
    /// 光标 + 幽灵文本的叠加层:一个独立的可视子元素,画在正文之上。
    /// </summary>
    /// <remarks>
    /// <b>为什么要单独一层</b>:光标闪烁每 530ms 翻一次相位,而 Avalonia 没有"局部失效"——
    /// 在同一个控件里就意味着为了一个格子重新记录整屏(200×50 = 1 万格的解析色 + 逐行语义扫描
    /// + 全部 GlyphRun)。拆成子元素后,闪烁只失效这一层,正文的绘制记录原样复用。
    /// <para>
    /// 顺序上光标先画、幽灵后画(与拆分前一致):块状光标落在 CursorX 上,幽灵文本正是从 CursorX
    /// 起向右铺,幽灵必须压在光标块之上,否则补全的第一个字符会被光标盖住。两者同层才能保住这个次序,
    /// 所以幽灵一并搬了过来。
    /// </para>
    /// <para>
    /// <b>失效必须成对</b>:本层的内容(光标位置、幽灵剩余)由屏幕状态现算,正文一变它就过期。
    /// 因此正文的每一次失效都要连带失效本层 —— 全走 <see cref="InvalidateTerminal" />,
    /// 不要在本类里直接写 <c>InvalidateVisual()</c>(唯一的例外是闪烁计时器,它只失效本层)。
    /// </para>
    /// </remarks>
    private sealed class CursorOverlay(VelaTerminalControl owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            owner.OverlayRenderCountForTest++;
            if (owner._scrollOffset != 0)
            {
                return; // 回滚查看历史时不画光标/幽灵(与拆分前的可见性条件一致)。
            }
            TerminalScreen screen = owner.Emulator.Screen;
            TerminalPalette palette = owner.Emulator.Palette;

            // 与正文同一套平移,使 col*_cellWidth 的坐标计算保持不变。
            using (context.PushTransform(
                       Matrix.CreateTranslation(owner.ContentPadding + owner.GutterWidth(), owner.ContentPadding)))
            {
                owner.RenderCursor(context, screen, palette);
                owner.RenderGhostText(context, screen, palette, screen.Columns);
            }
        }
    }

    /// <summary>
    /// 在光标处以约 40% 透明度的前景色绘制幽灵文本,裁剪到当前行行尾。
    /// 只在未回滚(_scrollOffset==0)时绘制,与光标同一可见性条件。
    /// </summary>
    private void RenderGhostText(
        DrawingContext context,
        TerminalScreen screen,
        TerminalPalette palette,
        int cols
    )
    {
        // 剩余部分按真实光标现切(而非宿主逐键推送),与已回显文本同源于本帧光标,恒不失步。
        string? ghost = CurrentGhostRemainder();
        if (string.IsNullOrEmpty(ghost) || screen.CursorX >= cols)
        {
            return;
        }
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = ScreenRowForAbsolute(cursorAbsolute);
        if (screenRow < 0)
        {
            return;
        }
        double x = screen.CursorX * CellWidthForTest;
        double y = screenRow * CellHeightForTest;

        // 幽灵可见期间光标闪烁每 ~530ms 重绘一帧;FormattedText 塑形较贵,
        // 按 (文本, 颜色) 缓存,仅在幽灵内容/主题/字体度量变化时重建。
        Rgba fg = palette.DefaultForeground with { A = 0x66 };
        if (_ghostFormatted is null || _ghostFormattedText != ghost || _ghostFormattedColor != fg.Packed)
        {
            _ghostFormatted = new(
                ghost,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily),
                FontSize,
                BrushFor(fg)
            );
            _ghostFormattedText = ghost;
            _ghostFormattedColor = fg.Packed;
        }
        using (
            context.PushClip(
                new Rect(x, y, (cols - screen.CursorX) * CellWidthForTest, CellHeightForTest)
            )
        )
        {
            context.DrawText(_ghostFormatted, new(x, y + _glyphYOffset));
        }
    }

    private FormattedText? _ghostFormatted;
    private string? _ghostFormattedText;
    private uint _ghostFormattedColor;

    // ---- Line gutter(时间/行号/折叠侧栏,WindTerm 式) ---------------------

    private const string GutterTimeFormat = "HH:mm:ss";

    /// <summary>当前侧栏几何(各部件宽度/偏移/命中区间,见 <see cref="GutterLayout" />)。按当前单元格宽与开关计算。</summary>
    private GutterLayout Gutter =>
        new(CellWidthForTest, ShowLineTimestamp, ShowLineNumber, ShowFoldMarker, GutterBlank);

    /// <summary>任一侧栏部件开启即绘制侧栏。</summary>
    private bool GutterEnabled => Gutter.Enabled;

    /// <summary>侧栏总像素宽度(全部部件关时为 0)。</summary>
    private double GutterWidth() => Gutter.TotalWidth;

    // ---- 测试专用只读探针(headless UI 测试用,见 GutterFoldUiTests)----------
    internal int FoldCountForTest => _foldModel.Count;
    internal double CellWidthForTest { get; private set; } = 8;
    internal double CellHeightForTest { get; private set; } = 16;
    internal GutterLayout GutterForTest => Gutter;

    /// <summary>
    /// 测试用的 <c>RenderScaling</c> 覆盖(&gt;0 生效)。headless 平台的窗口恒为 1.0 缩放,
    /// 而"方块网格"只在分数缩放下出现,故只能由测试注入(见 TerminalSeamSnapUiTests)。
    /// </summary>
    internal double RenderScalingOverrideForTest { get; set; }

    /// <summary>
    /// 测试钩子(唯一一个会写状态的):绕过宿主直接改模拟器网格,<b>不</b>更新 <c>_applied*</c> 记账 ——
    /// 也就是造出「有东西改了几何却没通知任何人」的分家状态,用来验证 <see cref="ReconcileGridWithLayout" />
    /// 的自愈闸(issue #253)。生产代码一律走 <see cref="Resize" /> 或 <see cref="AdoptHostGeometry" />。
    /// </summary>
    internal void DesyncGridForTest(int cols, int rows) => Emulator.Resize(cols, rows);

    /// <summary>
    /// 按当前设备像素栅格算出某个单元格的背景矩形(坐标系与正文绘制一致:已减去内边距与侧栏平移)。
    /// 与 <see cref="RenderLine" /> 走同一个 <see cref="CellRect" />,测试因此锁住的是真实绘制路径。
    /// </summary>
    internal Rect CellRectForTest(int col, int screenRow, int width = 1)
    {
        RefreshPixelGrid();
        return CellRect(col, width, screenRow * CellHeightForTest);
    }

    /// <summary>
    /// 该行是否显示侧栏(行号/时间戳),并计入折叠导引线的下端。
    /// </summary>
    /// <remarks>
    /// 两条规矩:
    /// · 有真实内容的行一律显示 —— 满屏重绘型程序(vim 等)光标下方也有内容,不能按光标位置砍掉。
    /// · 只有时间戳、没有内容的空行,仅在光标位置及之上显示。换行会给经过的行盖上时间戳(哪怕
    ///   没写入任何字符,见 LineTimestampTests),重绘型 shell 又常把提示符下方来回涂改;若不设
    ///   这道界,时间线会一直拖到提示符下方的空白区,折叠导引线随之画过光标把光标盖住
    ///   (PowerShell + oh-my-posh + PSReadLine 的历史列表撤销后)。
    /// internal 供测试直接验证这条判定,不必去驱动整个渲染。
    /// </remarks>
    internal static bool ShowsGutterFor(TerminalRow line, int absoluteRow, int cursorAbsoluteRow) =>
        line.LastNonBlank() >= 0 || (line.Timestamp is not null && absoluteRow <= cursorAbsoluteRow);

    /// <summary>
    /// 侧栏文本的缓存取用:命中直接复用已塑形的 <see cref="FormattedText" />。
    /// 画刷实例变化(主题切换/画刷缓存重建)时整体失效;字体/字号变化时随
    /// <c>_glyphCache</c> 一起清空。上限防长会话滚动把历史行号无界积累。
    /// </summary>
    /// <remarks>
    /// 取 span 而非 string:调用方在栈上拼好 "[HH:mm:ss] " / 右对齐行号,命中缓存时
    /// (滚动稳定期几乎恒命中)连缓存键都不必物化。只有 miss 才 <c>ToString()</c> 建键。
    /// </remarks>
    private FormattedText GutterText(ReadOnlySpan<char> text, Typeface typeface, ImmutableSolidColorBrush brush)
    {
        if (!ReferenceEquals(_gutterTextCacheBrush, brush))
        {
            _gutterTextCache.Clear();
            _gutterTextCacheBrush = brush;
        }
        if (_gutterTextCacheBySpan.TryGetValue(text, out FormattedText? cached))
        {
            return cached;
        }
        if (_gutterTextCache.Count > 512)
        {
            _gutterTextCache.Clear();
        }
        string key = new(text);
        var formatted = new FormattedText(
            key, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
        _gutterTextCache[key] = formatted;
        return formatted;
    }

    /// <summary>
    /// 把行时间戳写成 <c>"[HH:mm:ss] "</c>,返回 <paramref name="buffer" /> 中写好的那一段。
    /// 缓冲至少需要 <c>GutterTimeFormat.Length + 3</c> 个字符。
    /// </summary>
    /// <remarks>
    /// 这些文本只是 <see cref="GutterText" /> 的缓存键,却在每个可见行、每一帧上重算
    /// (光标闪烁也会重绘)。写进调用方的栈缓冲,把原先每行 2 个中间 string 降为零。
    /// internal 是为了让 <c>GutterTextFormattingTests</c> 直接锁住与原 <c>PadLeft</c> 写法的逐字等价。
    /// </remarks>
    internal static ReadOnlySpan<char> FormatGutterTimestamp(DateTime timestamp, Span<char> buffer)
    {
        buffer[0] = '[';
        timestamp.TryFormat(buffer[1..], out int length, GutterTimeFormat, CultureInfo.InvariantCulture);
        buffer[++length] = ']';
        buffer[++length] = ' ';
        return buffer[..++length];
    }

    /// <summary>
    /// 把绝对缓冲行号写成右对齐到 <see cref="GutterLayout.NumberDigits" /> 位、尾随一个空格的形式
    /// (与原先的 <c>(row + 1).ToString().PadLeft(NumberDigits) + " "</c> 逐字等价),
    /// 返回 <paramref name="buffer" /> 中写好的那一段。位数超出时不截断,自然变宽。
    /// </summary>
    /// <inheritdoc cref="FormatGutterTimestamp" path="/remarks" />
    internal static ReadOnlySpan<char> FormatGutterLineNumber(int absoluteRow, Span<char> buffer)
    {
        buffer.Fill(' '); // 缓冲跨行复用,先抹掉上一行的残留。
        (absoluteRow + 1).TryFormat(buffer, out int digits, provider: CultureInfo.InvariantCulture);
        int width = Math.Max(digits, GutterLayout.NumberDigits);
        if (digits < width)
        {
            // TryFormat 从头写起,右移到该宽度的末尾;CopyTo 是 memmove 语义,区间重叠安全。
            buffer[..digits].CopyTo(buffer[(width - digits)..]);
            buffer[..(width - digits)].Fill(' ');
        }
        buffer[width] = ' ';
        return buffer[..(width + 1)];
    }

    private void RenderGutter(DrawingContext context, TerminalScreen screen, TerminalPalette palette, int rows)
    {
        // 侧栏底色刻意保持与终端背景一致(不再叠色):正文区域整体右移,侧栏落在开头那次全局底色填充上,
        // 因此无需单独填底 —— 空白处与终端浑然一体(WindTerm 观感),仅靠暗色文本/分隔线区分。
        Rgba dim = Blend(palette.DefaultForeground, palette.DefaultBackground, 0.45);
        ImmutableSolidColorBrush dimBrush = BrushFor(dim);
        var typeface = new Typeface(FontFamily);
        double numberLeft = Gutter.NumberLeft;
        int lastContentRow = -1; // 最后一行有内容的屏幕行:分隔线/折叠线只画到这里,空屏不画侧栏。
        int cursorAbsoluteRow = screen.ScrollbackCount + screen.CursorY;

        // 时间戳/行号的栈缓冲在循环外开一次(stackalloc 不随迭代释放,CA2014):
        // 逐行复用即可,内容每行现写。
        Span<char> stampBuffer = stackalloc char[GutterTimeFormat.Length + 3];
        Span<char> numberBuffer = stackalloc char[GutterLayout.NumberDigits + 16];
        for (int screenRow = 0; screenRow < rows; screenRow++)
        {
            int absoluteRow = _screenToAbs[screenRow];
            if (absoluteRow < 0)
            {
                continue;
            }
            if (!ShowsGutterFor(screen.ViewLine(absoluteRow), absoluteRow, cursorAbsoluteRow))
            {
                continue;
            }
            TerminalRow line = screen.ViewLine(absoluteRow);
            lastContentRow = screenRow;
            double y = screenRow * CellHeightForTest + _glyphYOffset;
            // 时间戳与行号都在栈上拼:两者原先各拼 2~3 个中间 string,而它们只是缓存键 ——
            // 每个可见行、每一帧(光标闪烁也算)白扔一轮。栈缓冲足够容下最长形态。
            if (ShowLineTimestamp && line.Timestamp is { } ts)
            {
                context.DrawText(GutterText(FormatGutterTimestamp(ts, stampBuffer), typeface, dimBrush), new Point(0, y));
            }
            if (ShowLineNumber)
            {
                context.DrawText(
                    GutterText(FormatGutterLineNumber(absoluteRow, numberBuffer), typeface, dimBrush),
                    new Point(numberLeft, y));
            }
        }
        if (lastContentRow < 0)
        {
            return; // 空屏:不画分隔线/折叠列,侧栏完全隐形。
        }
        double contentBottom = (lastContentRow + 1) * CellHeightForTest;
        // 唯一的竖线由折叠列绘制,只保留折叠标记这一条。
        if (ShowFoldMarker)
        {
            RenderFoldColumn(context, screen, palette, rows, dim, contentBottom);
        }
    }

    /// <summary>
    /// 折叠列(Notepad++ 风格):竖直导引线 + 方框标记 + 底部 └ 转角收尾。
    /// ⊞(方框内 +)= 已折叠区域的锚点,点击展开;⊟(方框内 −)= 悬停在可折叠行,
    /// 点击把上方内容折叠到该行。标记用矢量线条绘制而非字体字形——任何字体/字号/DPI 下
    /// 形状一致且边缘锐利;方框以终端底色填充,天然打断背后的导引线(N++ 的断线观感)。
    /// 交互不变,折叠点击经指针命中 <see cref="GutterLayout" /> 折叠列区域触发。
    /// </summary>
    private void RenderFoldColumn(
        DrawingContext context,
        TerminalScreen screen,
        TerminalPalette palette,
        int rows,
        Rgba dim,
        double contentBottom
    )
    {
        GutterLayout g = Gutter;
        // 像素对齐:1px 笔画的中心必须落在 x.5/y.5 上,否则反锯齿会把它糊成 2px 灰线。
        double cx = Math.Floor(g.FoldLeft + g.FoldWidth / 2) + 0.5;
        ImmutablePen linePen = PenFor(Blend(dim, palette.DefaultBackground, 0.4));
        ImmutablePen markPen = PenFor(dim);
        ImmutableSolidColorBrush boxFill = BrushFor(palette.DefaultBackground);

        // 导引线通到最后内容行,并以 └ 转角收尾(指向正文,暗示"折叠作用于上方内容")。
        double bottomY = Math.Floor(contentBottom - CellHeightForTest / 2) + 0.5;
        context.DrawLine(linePen, new Point(cx, 0), new Point(cx, bottomY));
        context.DrawLine(linePen, new Point(cx, bottomY), new Point(cx + Math.Floor(g.FoldWidth / 2) - 1, bottomY));

        // 方框边长:随行高缩放,夹在 7–11px 且取奇数,保证 ± 符号有精确的单像素中心。
        int box = (int)Math.Clamp(Math.Floor(CellHeightForTest * 0.55), 7, 11);
        if (box % 2 == 0)
        {
            box--;
        }
        int half = (box - 1) / 2;

        for (int screenRow = 0; screenRow < rows; screenRow++)
        {
            int absoluteRow = _screenToAbs[screenRow];
            if (absoluteRow < 0)
            {
                continue;
            }
            bool anchor = _foldModel.IsAnchor(screen, absoluteRow);
            bool hover = !anchor && absoluteRow == _foldHoverAbs;
            if (!anchor && !hover)
            {
                continue;
            }

            double cy = Math.Floor(screenRow * CellHeightForTest + CellHeightForTest / 2) + 0.5;
            var rect = new Rect(cx - half, cy - half, box - 1, box - 1);

            // 底色填充盖住背后的导引线段,再描方框边。
            context.DrawRectangle(boxFill, markPen, rect);

            // −:两种标记都有的水平笔画;+:锚点(已折叠)再加竖直笔画。
            context.DrawLine(markPen, new Point(rect.Left + 2, cy), new Point(rect.Right - 2, cy));
            if (anchor)
            {
                context.DrawLine(markPen, new Point(cx, rect.Top + 2), new Point(cx, rect.Bottom - 2));
            }
        }
    }

    // ---- Folding(折叠区域)-------------------------------------------------
    // 折叠逻辑抽到 UI 无关的 GutterFoldModel(可单测,见 GutterFoldTests);默认无折叠时渲染/滚动
    // 走连续快路径,零影响。列宽 reflow 会重建行对象使引用失效,由 ClearFolds() 在 resize 时清空。

    private readonly GutterFoldModel _foldModel = new();
    private int[] _screenToAbs = []; // 本帧 screenRow → 绝对缓冲行(-1=空),侧栏/正文/光标/命中测试共用
    private int _foldHoverAbs = -1; // 折叠列上鼠标悬停的绝对行(显示 ▾ 折叠提示),-1=无

    /// <summary>构建本帧屏幕行映射;无折叠走连续快路径,有折叠跳过隐藏行。同时把 _scrollOffset 夹到可见范围。</summary>
    private void BuildScreenRowMap(TerminalScreen screen, int rows)
    {
        if (_screenToAbs.Length != rows)
        {
            _screenToAbs = new int[rows];
        }
        List<int>? visible = _foldModel.VisibleRowsOrNull(screen);
        GutterFoldModel.FillScreenRowMap(
            _screenToAbs,
            visible,
            screen.TotalRows,
            rows,
            ref _scrollOffset
        );
    }

    /// <summary>清空所有折叠(列宽 reflow 会重建行对象使引用失效,resize 时调用)。</summary>
    private void ClearFolds() => _foldModel.Clear();

    /// <summary>
    /// 该绝对行是否可作为折叠交互目标(悬停显示 ▾、点击折叠)。
    /// 只放行"有内容的行"与既有折叠头:最后一行输出之下的空白屏幕行也是合法的活动屏行,
    /// 若不拦截,悬停会在空白区凭空冒出 ▾,误点一下就把上方内容整段折叠——
    /// 看起来就是"终端内容凭空消失"(2026-07-23 实证,用户误点空白区折叠所致)。
    /// 折叠头无条件放行,保证已折叠的区域永远能展开。
    /// </summary>
    internal bool IsFoldTargetRow(int abs)
    {
        TerminalScreen screen = Emulator.Screen;
        if (abs < 0 || abs >= screen.TotalRows)
        {
            return false;
        }
        if (_foldModel.IsAnchor(screen, abs))
        {
            return true;
        }
        return ShowsGutterFor(screen.ViewLine(abs), abs, screen.ScrollbackCount + screen.CursorY);
    }

    /// <summary>折叠交互:点击折叠列某屏幕行 —— 折叠头则展开,否则把上方内容折叠到该行(见 <see cref="GutterFoldModel" />)。</summary>
    private void ToggleFoldAt(int screenRow)
    {
        int abs = AbsoluteForScreenRow(screenRow);
        if (IsFoldTargetRow(abs) && _foldModel.Toggle(Emulator.Screen, abs))
        {
            AfterFoldChange();
        }
    }

    private void AfterFoldChange()
    {
        InvalidateTerminal();
        ScrollChanged?.Invoke();
    }

    /// <summary>上一次弹出的侧栏菜单。每次右键都新建实例,旧实例必须显式关闭,否则会叠着不消失。</summary>
    private ContextMenu? _gutterMenu;

    /// <summary>侧栏右键菜单:四个部件(行号/时间戳/折叠标记/空白)的可勾选开关。</summary>
    private void ShowGutterContextMenu()
    {
        _gutterMenu?.Close();
        _gutterMenu = BuildGutterContextMenu();
        _gutterMenu.Open(this);
    }

    /// <summary>构建侧栏右键菜单(不弹出)。internal 供 headless 测试直接检视内容与开关接线,避免打开弹层。</summary>
    internal ContextMenu BuildGutterContextMenu()
    {
        GutterMenuLabels labels = GutterMenu;
        var menu = new ContextMenu();
        AddGutterMenuItem(menu, labels.LineNumber, ShowLineNumber, v => ShowLineNumber = v);
        AddGutterMenuItem(menu, labels.Timestamp, ShowLineTimestamp, v => ShowLineTimestamp = v);
        AddGutterMenuItem(menu, labels.FoldMarker, ShowFoldMarker, v => ShowFoldMarker = v);
        AddGutterMenuItem(menu, labels.Blank, GutterBlank, v => GutterBlank = v);
        return menu;
    }

    /// <summary>
    /// 用 <see cref="MenuItemToggleType.CheckBox" /> 而非在 Header 里拼勾号字符:勾号交给模板
    /// 固定宽度的勾选列渲染,开关时文字不再左右跳,且勾号是矢量图形、跟随主题前景色 —— 拼字符
    /// 时 JB Mono 没有 U+2714,回退字体的字宽与颜色都不受控。与文件浏览器的列开关菜单同款。
    /// </summary>
    private void AddGutterMenuItem(ContextMenu menu, string label, bool on, Action<bool> set)
    {
        var item = new MenuItem
        {
            Header = label,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = on,
            // 四个部件可一次性调完,不必每改一个都重新右键。
            StaysOpenOnClick = true
        };
        // 读 item.IsChecked(点击时已由模板翻转)而非取反捕获的 on:菜单不关,同一项可被连点多次。
        item.Click += (_, _) =>
        {
            set(item.IsChecked);
            GutterOptionsChanged?.Invoke(ShowLineTimestamp, ShowLineNumber, ShowFoldMarker, GutterBlank);
        };
        menu.Items.Add(item);
    }

    /// <summary>侧栏右键菜单四个部件的本地化标签。</summary>
    public sealed record GutterMenuLabels(
        string LineNumber,
        string Timestamp,
        string FoldMarker,
        string Blank
    );

    /// <summary>本帧「绝对行 → 屏幕行」反查(命中测试/光标定位用),未在可见窗口内返回 -1。</summary>
    private int ScreenRowForAbsolute(int abs)
    {
        for (int sr = 0; sr < _screenToAbs.Length; sr++)
        {
            if (_screenToAbs[sr] == abs)
            {
                return sr;
            }
        }
        return -1;
    }

    /// <summary>本帧屏幕行 <paramref name="screenRow" /> 对应的绝对缓冲行(越界/空行返回 -1)。</summary>
    private int AbsoluteForScreenRow(int screenRow) =>
        screenRow >= 0 && screenRow < _screenToAbs.Length ? _screenToAbs[screenRow] : -1;

    /// <summary>按比例 <paramref name="t" /> 在两色间线性插值(0=a,1=b),用于混出侧栏暗色。</summary>
    private static Rgba Blend(Rgba a, Rgba b, double t)
    {
        static byte Lerp(byte x, byte y, double f) => (byte)Math.Round(x + (y - x) * f);
        return new Rgba(0xFF, Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
    }

    private void RenderLine(
        DrawingContext context,
        TerminalPalette palette,
        TerminalRow line,
        int cols,
        double y,
        int absoluteRow,
        List<SelectionSpan> spans
    )
    {
        // 备用屏(vim/htop/less 等全屏程序)自带配色且行内容每帧都在变:语义扫描
        // 既无视觉价值又必然缓存 MISS(整行文本为键),是全屏 TUI 卡顿的头号来源,直接跳过。
        SemanticKind?[]? semantic = SemanticHighlightingEnabled && !Emulator.IsAlternateScreen
            ? ComputeSemanticColumns(line, cols)
            : null;
        // 本行落在各选区段内的列区间:逐行算一次,格子循环里只做区间比较 ——
        // 多段选区不会把渲染热路径变成 O(格 × 段)。
        int rowSpans = CollectRowSpans(spans, absoluteRow, cols);
        // 本行的搜索命中区间同样逐行取一次:原先每一格都要在字典里 TryGetValue 一遍。
        List<(int Start, int End, bool Current)>? rowSearchSpans = null;
        _ = _searchHighlights?.TryGetValue(absoluteRow, out rowSearchSpans);
        int col = 0;
        while (col < cols)
        {
            TerminalCell cell = line[col];
            if (cell.IsWideTrailing)
            {
                col++;
                continue;
            }
            int width = cell.Rune == 0 ? 1 : Math.Max(1, CharWidth.Of(cell.Rune));
            bool inverse = (cell.Flags & CellFlags.Inverse) != 0 ^ Emulator.Modes.ReverseVideo;
            bool bold = (cell.Flags & CellFlags.Bold) != 0;
            Rgba fg = palette.Resolve(cell.Foreground, false, bold);
            Rgba bg = palette.Resolve(cell.Background, true, false);
            if (inverse)
            {
                (fg, bg) = (bg, fg);
            }
            if (IsSelectedColumn(rowSpans, col))
            {
                bg = palette.SelectionBackground;
            }
            if (rowSearchSpans is not null)
            {
                foreach ((int Start, int End, bool Current) in rowSearchSpans)
                {
                    if (col >= Start && col < End)
                    {
                        bg = Current ? SearchCurrentBg : SearchMatchBg;
                        break;
                    }
                }
            }

            // 只对程序留在默认颜色下的文本重新着色,因此显式 SGR 颜色
            // (ls --color、git、提示符)绝不会被覆盖。URL 与 IP 还会加下划线,
            // 表示它们可 Ctrl+ 点击。
            bool semanticUnderline = false;
            if (
                semantic is not null
                && !inverse
                && cell.Foreground.IsDefault
                && semantic[col] is { } kind
            )
            {
                fg = SemanticColor(palette, kind);
                semanticUnderline = kind is SemanticKind.Url or SemanticKind.IpAddress;
            }
            AppendBackground(context, y, bg, col, width, palette.DefaultBackground);

            // 空白/空格/不可见单元不绘制字形;它只留出一段空隙由下一运行的
            // 步进吸收。其余内容在主要字体能覆盖时批量并入 GlyphRun,
            // 否则回退到逐单元 FormattedText 绘制(CJK、符号、组合字符)。
            if (cell.Rune != 0 && cell.Rune != ' ' && (cell.Flags & CellFlags.Invisible) == 0)
            {
                bool italic = (cell.Flags & CellFlags.Italic) != 0;
                int style = (bold ? 1 : 0) | (italic ? 2 : 0);
                if (
                    !_glyphRunUnsupported
                    && cell.Combining is null
                    && cell.Rune <= 0xFFFF
                    && StyleTypeface(style) is { } gtf
                    && gtf.CharacterToGlyphMap.TryGetGlyph(cell.Rune, out ushort glyphId)
                )
                {
                    AppendGlyph(context, y, style, fg, col, width, glyphId, (char)cell.Rune);
                }
                else
                {
                    // 逐单元 FormattedText 回退(CJK、组合字符、主字体缺字形)是**即时**绘制。
                    // 只调 FlushGlyphRun 不够:批次为空时它直接返回,背景 run 就留到行尾才发,
                    // 结果是背景矩形盖住已经画好的字 —— 整行 CJK 必然触发。显式冲一次背景。
                    FlushGlyphRun(context, y);
                    FlushBackgroundRun(context, y);
                    FormattedText ft = GlyphFor(cell, fg, bold, italic);
                    context.DrawText(ft, new(col * CellWidthForTest, y + _glyphYOffset));
                }
            }
            if (
                (cell.Flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0
                || semanticUnderline
            )
            {
                // 下划线/删除线是**即时**绘制,不像字形那样攒批;后发的背景矩形会把它盖掉,
                // 所以画之前必须先把待发的背景 run 冲出去(见 FlushBackgroundRun 的不变量说明)。
                FlushBackgroundRun(context, y);
                double uy = y + CellHeightForTest - 1.5;
                context.DrawLine(
                    PenFor(fg),
                    new(col * CellWidthForTest, uy),
                    new((col + width) * CellWidthForTest, uy)
                );
            }
            if ((cell.Flags & CellFlags.Strikethrough) != 0)
            {
                FlushBackgroundRun(context, y);
                double sy = y + CellHeightForTest / 2;
                context.DrawLine(
                    PenFor(fg),
                    new(col * CellWidthForTest, sy),
                    new((col + width) * CellWidthForTest, sy)
                );
            }
            col += width;
        }

        // 把本行中仍被批量缓存的剩余字形全部发出(运行从不跨越行边界)。
        // FlushGlyphRun 会先冲背景 run,所以行尾的最后一段背景也在这里落地。
        FlushGlyphRun(context, y);
        FlushBackgroundRun(context, y);
    }

    /// <summary>
    /// 为一行构建逐列的语义类别映射:重建行文本(把每个字符映射回其源列,
    /// 使宽字符对齐),对其进行匹配,并标记每个区间覆盖的列。
    /// 当该行无可高亮内容时返回 null。
    /// </summary>
    private SemanticKind?[]? ComputeSemanticColumns(TerminalRow line, int cols)
    {
        int lastNonBlank = -1;
        for (int i = 0; i < cols; i++)
        {
            if (line[i].Rune != 0)
            {
                lastNonBlank = i;
            }
        }
        if (lastNonBlank < 0)
        {
            return null;
        }
        List<int> colByChar = _semanticColByChar;
        colByChar.Clear();
        int length = 0;
        for (int i = 0; i <= lastNonBlank; i++)
        {
            TerminalCell cell = line[i];
            if (cell.IsWideTrailing)
            {
                continue;
            }

            // 缓冲不足时扩容重试同一格。单元格最多贡献 2(代理对)+ 组合标记长度个字符,
            // 上界无法预先精确算出,故按"写失败即翻倍"处理。
            int written = cell.AppendTo(_semanticLineChars.AsSpan(length));
            if (written < 0)
            {
                Array.Resize(ref _semanticLineChars, Math.Max(_semanticLineChars.Length * 2, length + 64));
                i--;
                continue;
            }
            for (int k = 0; k < written; k++)
            {
                colByChar.Add(i);
            }
            length += written;
        }
        IReadOnlyList<SemanticSpan> spans = SemanticSpansFor(_semanticLineChars.AsSpan(0, length));
        if (spans.Count == 0)
        {
            return null;
        }

        // 复用成员缓冲(按需扩容):调用方只在渲染完当前行前读取,下一行覆写安全。
        if (_semanticByColumn.Length < cols)
        {
            _semanticByColumn = new SemanticKind?[cols];
        }
        SemanticKind?[] byColumn = _semanticByColumn;
        Array.Clear(byColumn, 0, cols);
        foreach (SemanticSpan span in spans)
        {
            int end = Math.Min(span.End, colByChar.Count);
            for (int ci = span.Start; ci < end; ci++)
            {
                int c = colByChar[ci];
                if (c >= 0 && c < cols)
                {
                    byColumn[c] = span.Kind;
                }
            }
        }
        return byColumn;
    }

    /// <remarks>
    /// 查表走 span 备用查找:命中时(可见行帧间基本不变,这是绝大多数情况)一个字符串都不分配。
    /// 只有 miss 才 <c>ToString()</c> 建缓存键 —— 从"每行每帧一个 string"降到"每行内容变化一次"。
    /// </remarks>
    private IReadOnlyList<SemanticSpan> SemanticSpansFor(ReadOnlySpan<char> text)
    {
        if (_semanticSpanCacheBySpan.TryGetValue(text, out IReadOnlyList<SemanticSpan>? cached))
        {
            return cached;
        }

        // 限制缓存大小;终端输出行的变化极多,因此增长时直接重置即可。
        if (_semanticSpanCache.Count > 1024)
        {
            _semanticSpanCache.Clear();
        }
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(text);
        _semanticSpanCache[new(text)] = spans;
        return spans;
    }

    /// <summary>将语义类别映射到可主题化的 ANSI 颜色(遵循当前 .pen 调色板)。</summary>
    private static Rgba SemanticColor(TerminalPalette palette, SemanticKind kind) =>
        kind switch
        {
            SemanticKind.Error => palette[9], // bright red
            SemanticKind.Warning => palette[11], // bright yellow
            SemanticKind.Success => palette[10], // bright green
            SemanticKind.Url => palette[12], // bright blue
            SemanticKind.IpAddress => palette[14], // bright cyan
            SemanticKind.Option => palette[13], // bright magenta
            SemanticKind.Number => palette[6], // cyan
            _ => palette.DefaultForeground,
        };

    private void RenderCursor(
        DrawingContext context,
        TerminalScreen screen,
        TerminalPalette palette
    )
    {
        if (!Emulator.Modes.CursorVisible)
        {
            return;
        }
        int cursorAbsolute = screen.TotalRows - screen.Rows + screen.CursorY;
        int screenRow = ScreenRowForAbsolute(cursorAbsolute);
        if (screenRow < 0)
        {
            return;
        }
        double x = screen.CursorX * CellWidthForTest;
        double y = screenRow * CellHeightForTest;
        // 光标块与格子背景共用吸附后的矩形,否则分数缩放下它会比背景带错开半个像素。
        Rect rect = CellRect(screen.CursorX, 1, y);
        ImmutableSolidColorBrush cursorBrush = BrushFor(palette.CursorColor);
        if (!_hasFocus)
        {
            // 未聚焦:无论何种风格都画空心轮廓,使光标位置保持可见。
            context.DrawRectangle(new Pen(cursorBrush), rect);
            return;
        }

        // 闪烁相位:"熄灭"的那半周期直接跳过绘制(仅聚焦时;未聚焦轮廓从不闪烁)。
        if ((CursorBlink || Emulator.Modes.CursorBlink) && !_cursorBlinkVisible)
        {
            return;
        }
        switch (CursorStyle)
        {
            case "bar":
                context.FillRectangle(
                    cursorBrush,
                    _pixels.Snap(rect.WithWidth(Math.Max(1.5, CellWidthForTest * 0.15)))
                );
                break;
            case "underline":
                context.FillRectangle(
                    cursorBrush,
                    _pixels.Snap(new(rect.X, rect.Bottom - 2, rect.Width, 2))
                );
                break;
            default: // block
                context.FillRectangle(cursorBrush, rect);
                // 用背景色重绘光标下的字形以增强对比。
                TerminalCell cell = screen.GetCell(screen.CursorX, screen.CursorY);
                if (cell.Rune != 0)
                {
                    FormattedText ft = GlyphFor(cell, palette.DefaultBackground, false, false);
                    context.DrawText(ft, new(x, y + _glyphYOffset));
                }
                break;
        }
    }

    // ---- Selection ----------------------------------------------------------

    private ((int Row, int Col) Start, (int Row, int Col) End)? NormalizedSelection() =>
        _selectionAnchor is { } a && _selectionCaret is { } c
            ? TerminalSelectionMath.Normalize(a, c, _blockSelection)
            : null;

    /// <summary>进行中(或最近一次)那段选区是否为 Alt+拖拽的矩形块选(测试与宿主诊断用)。</summary>
    public bool IsBlockSelection => _blockSelection && NormalizedSelection() is not null;

    /// <summary>进行中(或最近一次)拖拽出的那一段选区;没有锚点时为 null。</summary>
    private SelectionSpan? LiveSelection() =>
        _selectionAnchor is { } a && _selectionCaret is { } c
            ? SelectionSpan.FromDrag(a, c, _blockSelection)
            : null;

    /// <summary>
    /// 当前所有<b>非空</b>选区段(已定稿的附加段 + 进行中那段),按文档顺序(行、列)排好。
    /// 返回的是复用的缓冲列表:调用方用完即弃,下一次调用会就地重建它。
    /// </summary>
    private List<SelectionSpan> SelectionSpans()
    {
        _spanBuffer.Clear();
        foreach (SelectionSpan span in _extraSelections)
        {
            if (!span.IsEmpty)
            {
                _spanBuffer.Add(span);
            }
        }
        if (LiveSelection() is { IsEmpty: false } live)
        {
            _spanBuffer.Add(live);
        }
        // 复制出来的文本必须自上而下读,与用户选取各段的先后无关。
        _spanBuffer.Sort(static (x, y) =>
            x.Start.Row != y.Start.Row ? x.Start.Row - y.Start.Row : x.Start.Col - y.Start.Col);
        return _spanBuffer;
    }

    /// <summary>把进行中那段定稿进附加段列表(空段丢弃),之后锚点可以另起一段。</summary>
    private void CommitLiveSelection()
    {
        if (LiveSelection() is { IsEmpty: false } live)
        {
            _extraSelections.Add(live);
        }
    }

    /// <summary>
    /// 是否存在选区(含"单击不拖"的空段)。Ctrl+C 的复制闸门只看有没有锚点 ——
    /// 与多段选区之前的行为保持一致,不在这里顺手改语义。
    /// </summary>
    private bool HasSelectionAnchor =>
        _extraSelections.Count > 0 || NormalizedSelection() is not null;

    /// <summary>把各选区段在某一绝对行上的列区间收进 _rowSpanBuffer,返回收到的段数。</summary>
    private int CollectRowSpans(List<SelectionSpan> spans, int absoluteRow, int columns)
    {
        int count = 0;
        foreach (SelectionSpan span in spans)
        {
            (int from, int to) = TerminalSelectionMath.RowSpan(
                (span.Start, span.End),
                span.Block,
                absoluteRow,
                columns
            );
            if (to <= from)
            {
                continue;
            }
            if (count == _rowSpanBuffer.Length)
            {
                Array.Resize(ref _rowSpanBuffer, count * 2);
            }
            _rowSpanBuffer[count++] = (from, to);
        }
        return count;
    }

    /// <summary>列 <paramref name="col" /> 是否落在本行任一选中区间内。</summary>
    private bool IsSelectedColumn(int count, int col)
    {
        for (int i = 0; i < count; i++)
        {
            if (col >= _rowSpanBuffer[i].From && col < _rowSpanBuffer[i].To)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>搜索整个缓冲区(回滚区 + 屏幕),不区分大小写(规范 §5.3)。</summary>
    public IReadOnlyList<BufferSearchHit> SearchBuffer(string query) =>
        BufferSearch.FindAll(Emulator.Screen, query);

    /// <summary>
    /// 导出整个缓冲区(scrollback + 当前屏幕)为纯文本:逐行去尾空格,
    /// 末尾的空白行不输出(“保存输出到文件”,§12.4)。
    /// </summary>
    public string GetBufferText()
    {
        TerminalScreen screen = Emulator.Screen;
        var sb = new StringBuilder();
        int lastNonEmpty = -1;
        for (int row = 0; row < screen.TotalRows; row++)
        {
            string text = screen.ViewLine(row).GetText().TrimEnd();
            sb.AppendLine(text);
            if (text.Length > 0)
            {
                lastNonEmpty = sb.Length;
            }
        }
        if (lastNonEmpty < 0)
        {
            return string.Empty;
        }
        sb.Length = lastNonEmpty;
        return sb.ToString();
    }

    /// <summary>
    /// 把所有搜索命中(琥珀色)绘制出来,当前命中项用强调色。行为绝对缓冲行,
    /// 因此高亮在滚动时始终跟随。
    /// </summary>
    public void SetSearchHighlights(IReadOnlyList<BufferSearchHit> hits, int currentIndex)
    {
        if (hits.Count == 0)
        {
            ClearSearchHighlights();
            return;
        }
        var map = new Dictionary<int, List<(int, int, bool)>>();
        for (int i = 0; i < hits.Count; i++)
        {
            BufferSearchHit hit = hits[i];
            if (!map.TryGetValue(hit.Row, out List<(int, int, bool)>? spans))
            {
                map[hit.Row] = spans = [];
            }
            spans.Add((hit.StartCol, hit.StartCol + hit.Length, i == currentIndex));
        }
        _searchHighlights = map;
        InvalidateTerminal();
    }

    /// <summary>移除所有搜索命中高亮并重新绘制。</summary>
    public void ClearSearchHighlights()
    {
        if (_searchHighlights is null)
        {
            return;
        }
        _searchHighlights = null;
        InvalidateTerminal();
    }

    /// <summary>
    /// 将搜索命中滚动到可见区域(大致居中)并选中它,
    /// 使既有选区高亮标记出匹配位置。
    /// </summary>
    public void ShowHit(BufferSearchHit hit)
    {
        _extraSelections.Clear(); // 命中高亮是"整个选区换成这一处",不与既有多段并存
        _selectionAnchor = (hit.Row, hit.StartCol);
        _selectionCaret = (hit.Row, hit.StartCol + hit.Length);
        _blockSelection = false;
        int totalRows = Emulator.Screen.TotalRows;
        int rows = Emulator.Rows;
        int desiredTop = Math.Max(0, hit.Row - rows / 2);
        int maxTop = Math.Max(0, totalRows - rows);
        ScrollOffset = maxTop - Math.Min(desiredTop, maxTop);
        InvalidateTerminal();
    }

    /// <summary>
    /// 以文本形式返回当前选区(可逐行去除行尾空白)。有多段不连续选区时,各段按文档顺序
    /// 自上而下拼接、段间断一行 —— 「选第 1 行、再 Ctrl+Shift 选第 3 行」复制出来就是两行。
    /// </summary>
    public string GetSelectedText()
    {
        List<SelectionSpan> spans = SelectionSpans();
        if (spans.Count == 0)
        {
            return string.Empty;
        }
        TerminalScreen screen = Emulator.Screen;
        var sb = new StringBuilder();
        for (int i = 0; i < spans.Count; i++)
        {
            if (i > 0)
            {
                // 段与段本就不相连,粘贴时不能糊成一行。
                sb.Append('\n');
            }
            AppendSpanText(sb, screen, spans[i]);
        }
        return TrimTrailingWhitespaceOnCopy ? sb.ToString().TrimEnd() : sb.ToString();
    }

    /// <summary>把一段选区的文本追加进 <paramref name="sb" />(不含段与段之间的分隔)。</summary>
    private void AppendSpanText(StringBuilder sb, TerminalScreen screen, SelectionSpan span)
    {
        for (int row = span.Start.Row; row <= span.End.Row && row < screen.TotalRows; row++)
        {
            TerminalRow line = screen.ViewLine(row);
            // 块选时每行取同一段列区间(矩形),线性选区则首行从起点、末行到终点、中间整行。
            (int from, int to) = TerminalSelectionMath.RowSpan(
                (span.Start, span.End),
                span.Block,
                row,
                line.Columns
            );
            int lineStart = sb.Length;
            for (int col = from; col < to; col++)
            {
                TerminalCell cell = line[col];
                if (!cell.IsWideTrailing)
                {
                    sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                }
            }
            // 复制时去除每行尾部空格(设置 → 终端 → 选择与复制)。
            if (TrimTrailingWhitespaceOnCopy)
            {
                while (sb.Length > lineStart && sb[^1] == ' ')
                {
                    sb.Length--;
                }
            }
            if (row != span.End.Row)
            {
                sb.Append('\n');
            }
        }
    }

    private (int Row, int Col) PointToCell(Point p)
    {
        // 传入的是控件坐标;正文被内边距整体平移过,先换算回正文坐标系再分格。
        p = ToContent(p);
        int col = (int)((p.X - GutterWidth()) / CellWidthForTest);
        // 夹取行号:捕获指针期间,拖拽可能把指针拖出控件(负 p.Y),
        // 而负的绝对行曾导致选区复制崩溃。
        // 通过本帧屏幕行映射解析绝对行,折叠时命中被折叠后实际可见的那一行。
        int maxRow = Math.Max(0, _screenToAbs.Length - 1);
        int screenRow = Math.Clamp((int)(p.Y / CellHeightForTest), 0, maxRow);
        int row = AbsoluteForScreenRow(screenRow);
        if (row < 0)
        {
            // 点在内容下方的空白/折叠占位处:退回缓冲区末行。
            row = Math.Max(0, Emulator.Screen.TotalRows - 1);
        }
        return (row, Math.Clamp(col, 0, Emulator.Columns));
    }

    // ---- Input --------------------------------------------------------------

    /// <summary>标记控件已聚焦并(重新)启动光标闪烁计时器。</summary>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        UpdateCursorBlinkTimer();
        InvalidateTerminal();
    }

    /// <summary>标记控件未聚焦,停止闪烁并绘制空心光标。</summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        // 焦点走了,修饰键状态就不再可信 —— 撤掉链接悬停的手型与地址提示。
        ClearLinkHover();
        UpdateCursorBlinkTimer();
        InvalidateTerminal();
    }

    /// <summary>对提交的文本输入进行编码并发送往 PTY。</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            byte[] bytes = InputEncoder.EncodeText(e.Text);
            if (bytes.Length > 0)
            {
                SendTypedInput(bytes);
                if (ScrollOnKeystroke)
                {
                    _scrollOffset = 0;
                }
                ClearSelection();
                ResetCursorBlink();
                e.Handled = true;
            }
        }
        base.OnTextInput(e);
    }

    /// <summary>
    /// 处理剪贴板/滚动快捷键,并把按键编码为主机字节序列。
    /// 分类决策(快捷键优先级、修饰键改写、编码)全在 <see cref="TerminalKeyRouter" />,
    /// 这里只执行动作 —— 改键位行为去改路由器,别在这里加分支。
    /// </summary>
    /// <remarks>
    /// <b>本方法必须保持同步,<c>e.Handled</c> 必须在任何 await 之前置位。</b>
    /// 路由事件的处理器一旦 <c>await</c> 就地返回,事件随即继续冒泡 —— 此后再置 Handled 已经晚了。
    /// 回归 #265:这里原本是 <c>async void</c> 且在 <c>await PasteAsync()</c> <b>之后</b>才置位,
    /// 于是 Ctrl+Shift+V 冒泡到 <c>TerminalTabView.OnKeyDown</c> 又粘贴一次 ——
    /// 用户看到两个多行确认框,点两次确定、粘贴两遍。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        TerminalKeyAction action = TerminalKeyRouter.Classify(
            e.Key,
            e.KeyModifiers,
            Emulator.Modes,
            Emulator.Type,
            canScrollHistory: Emulator.Screen.MaxScrollback > 0,
            ctrlCCopiesSelection: CtrlCCopiesWhenSelected && HasSelectionAnchor);
        switch (action.Kind)
        {
            case TerminalKeyActionKind.ImePassthrough:
                break; // 已提交文本会经 OnTextInput 单独送达。

            case TerminalKeyActionKind.CopySelection:
                e.Handled = true;
                // Ctrl+C 变体带"复制后清选区"的既有语义;Ctrl+Shift+C 保留选区。
                if (e.KeyModifiers == KeyModifiers.Control)
                {
                    TryCopyOnCtrlC();
                }
                else
                {
                    _ = CopyAsync();
                }
                return;

            case TerminalKeyActionKind.PasteClipboard:
                _ = PasteAsync();
                e.Handled = true;
                return;

            case TerminalKeyActionKind.ScrollHistory:
                ScrollOffset += action.ScrollPageDirection * Math.Max(1, Emulator.Rows - 1);
                e.Handled = true;
                return;

            case TerminalKeyActionKind.SendBytes:
                SendTypedInput(action.Bytes!);
                if (ScrollOnKeystroke)
                {
                    _scrollOffset = 0;
                }
                ClearSelection();
                ResetCursorBlink();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 处理侧栏点击、URL 的 Ctrl+点击、应用鼠标上报,以及文本选区的起点、Shift 扩展
    /// 与 Ctrl+Shift 追加(不连续多段选区)。
    /// </summary>

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point point = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        // 侧栏(时间/行号/折叠)区域的交互:右键弹设置菜单;折叠列左键折叠/展开;其余左键吞掉不选文本。
        // 侧栏几何以正文坐标系表达,故先扣掉内边距再命中。
        Point contentPoint = ToContent(point);
        GutterLayout gutter = Gutter;
        if (contentPoint.X >= 0 && gutter.ContainsX(contentPoint.X))
        {
            if (props.IsRightButtonPressed)
            {
                ShowGutterContextMenu();
                e.Handled = true;
                return;
            }
            if (props.IsLeftButtonPressed && gutter.IsFoldColumnHit(contentPoint.X))
            {
                ToggleFoldAt((int)(contentPoint.Y / CellHeightForTest));
                e.Handled = true;
                return;
            }
            if (props.IsLeftButtonPressed)
            {
                e.Handled = true;
                return;
            }
        }

        // 在检测到的 URL 上 Ctrl+点击会用默认浏览器打开它(#9)。
        // 排除 Ctrl+Shift:那是"追加一段不连续选区",落在 URL 上也不该跳浏览器。
        if (
            props.IsLeftButtonPressed
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
        )
        {
            (int row, int col) = PointToCell(point);
            string lineText =
                row < Emulator.Screen.TotalRows
                    ? Emulator.Screen.ViewLine(row).GetText()
                    : string.Empty;
            string? url = SemanticMatcher.UrlAt(lineText, col);
            if (url is not null)
            {
                OpenLink(url);
                e.Handled = true;
                return;
            }
        }

        // 当应用启用了鼠标追踪时,把点击转发给它(htop 标签页/按钮、btop、vim、tmux)。
        // 按住 Shift 可绕过上报,以便用户仍能选择文本。上报只在实时屏幕上才有意义,
        // 滚动到历史区时则不然。
        if (
            Emulator.Modes.Mouse != MouseTracking.None
            && _scrollOffset == 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
        )
        {
            TerminalMouseButton? button =
                props.IsLeftButtonPressed ? TerminalMouseButton.Left
                : props.IsRightButtonPressed ? TerminalMouseButton.Right
                : props.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                : null;
            if (
                button is { } b
                && SendMouse(TerminalMouseEventType.Press, b, point, e.KeyModifiers)
            )
            {
                _mouseButtonDown = b;
                _lastMouseReportCell = ScreenCell(point);
                e.Handled = true;
                return;
            }
        }
        if (props.IsLeftButtonPressed)
        {
            // 双击选择整个单词(设置 → 终端 → 选择与复制)。
            // 按住 Ctrl+Shift 双击则是"再添一个词",已有各段留着。
            if (e.ClickCount == 2 && DoubleClickSelectsWord)
            {
                SelectWordAt(
                    PointToCell(point),
                    append: e.KeyModifiers.HasFlag(KeyModifiers.Control)
                        && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                );
                e.Handled = true;
                return;
            }
            // Ctrl+Shift+左键拖拽 = 再添一段不连续选区:把进行中那段定稿,然后另起一段。
            // 复制时各段按文档顺序拼接、段间断行,于是"选中第 1 行,再 Ctrl+Shift 选中第 3 行,
            // 一次复制得到这两行"成立。终端里没有先例(WT/iTerm2/xterm 都只有单段选区),
            // 键位因此自定:Shift 单独按仍是扩展、Alt 仍是块选,三者可叠加 ——
            // Ctrl+Shift+Alt+拖拽 = 追加一段矩形块选。
            if (
                e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            )
            {
                CommitLiveSelection();
                _blockSelection = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                _selecting = true;
                _selectionAnchor = PointToCell(point);
                _selectionCaret = _selectionAnchor;
                InvalidateTerminal();
                e.Handled = true;
                return;
            }
            // Shift+左键 = 把已有选区从锚点扩展到点击处(#266,对齐 Windows Terminal / xterm):

            // 锚点不动、只挪游标,并照常置 _selecting,故按住不放还能继续拖拽微调,松手也照常触发选中即复制。
            // 块选与否沿用上次按下时定下的模式,Shift 不改写它。没有锚点时不走这里 ——
            // 退回"新建选区",保住"按 Shift 绕过应用鼠标上报以便选文本"的既有语义。
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionAnchor is not null)
            {
                _selectionCaret = PointToCell(point);
                _selecting = true;
                InvalidateTerminal();
                e.Handled = true;
                return;
            }
            // Alt+左键拖拽 = 矩形块选(#128,对齐 Windows Terminal):模式在按下这一刻定下,
            // 拖拽途中松开 Alt 不会退回线性选区。应用开启鼠标追踪时,鼠标事件已在上面转发给应用,
            // 此时需按住 Shift 绕过上报,即 Shift+Alt+拖拽仍可块选。
            _extraSelections.Clear(); // 不带 Ctrl+Shift 的拖拽 = 从头选起
            _blockSelection = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            _selecting = true;
            _selectionAnchor = PointToCell(point);
            _selectionCaret = _selectionAnchor;
            InvalidateTerminal();
        }
        else if (props.IsRightButtonPressed && RightClickPaste)
        {
            // 右键粘贴,符合常见终端行为(可在设置中关闭)。
            _ = PasteAsync();
        }
        e.Handled = true;
    }

    /// <summary>
    /// 选中给定单元周围连续的单词(字母/数字及常见路径字符);
    /// 在开启「选中即复制」时,该单词会立即进入剪贴板。
    /// </summary>
    /// <param name="cell">双击命中的单元。</param>
    /// <param name="append">true = 作为新的一段追加(已有各段留着);false = 换掉整个选区。</param>
    private void SelectWordAt((int Row, int Col) cell, bool append = false)
    {
        TerminalScreen screen = Emulator.Screen;
        if (cell.Row >= screen.TotalRows)
        {
            return;
        }
        TerminalRow line = screen.ViewLine(cell.Row);
        if (line.Columns <= 0)
        {
            return;
        }
        int col = Math.Clamp(cell.Col, 0, line.Columns - 1);
        if (!IsWordCell(line, col))
        {
            return;
        }
        int start = col;
        while (start > 0 && IsWordCell(line, start - 1))
        {
            start--;
        }
        int end = col + 1;
        while (end < line.Columns && IsWordCell(line, end))
        {
            end++;
        }
        if (append)
        {
            CommitLiveSelection();
        }
        else
        {
            _extraSelections.Clear();
        }
        _selectionAnchor = (cell.Row, start);
        _selectionCaret = (cell.Row, end);
        _selecting = false;
        _blockSelection = false;
        InvalidateTerminal();
        if (CopyOnSelect)
        {
            _ = CopyAsync();
        }
    }

    private static bool IsWordCell(TerminalRow line, int col)
    {
        TerminalCell cell = line[col];
        if (cell.IsWideTrailing)
        {
            return true; // 属于其前置宽字符的一部分
        }
        if (cell.Rune is 0 or ' ')
        {
            return false;
        }
        return (Rune.TryCreate(cell.Rune, out Rune rune) && Rune.IsLetterOrDigit(rune))
            || cell.Rune is '_' or '-' or '.' or '/' or '~' or '+' or '@' or ':';
    }

    /// <summary>跟踪折叠列悬停、应用移动上报以及选区拖拽。</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateLinkHover(e);

        // 折叠列悬停提示:指针在折叠列上时记住其绝对行(用于画 ▾ 折叠手柄),移出则清除。
        // 空白行(最后一行输出之下)不给提示——那里折叠无意义且极易误点(内容消失事故)。
        if (ShowFoldMarker && !_selecting)
        {
            Point gp = ToContent(e.GetPosition(this));
            int hover = gp.X >= 0 && Gutter.IsFoldColumnHit(gp.X)
                ? AbsoluteForScreenRow((int)(Math.Max(0, gp.Y) / CellHeightForTest))
                : -1;
            if (hover != -1 && !IsFoldTargetRow(hover))
            {
                hover = -1;
            }
            if (hover != _foldHoverAbs)
            {
                _foldHoverAbs = hover;
                InvalidateTerminal();
            }
        }

        // 在按钮事件模式(?1002,仅按住按钮时)与任意事件模式(?1003,始终)下向应用上报移动,
        // 但仅在跨越单元格边界时才上报。
        MouseTracking tracking = Emulator.Modes.Mouse;
        switch (_selecting)
        {
            case false
                when _scrollOffset == 0
                    && tracking is MouseTracking.ButtonEvent or MouseTracking.AnyEvent:
                {
                    bool held = _mouseButtonDown is not null;
                    if (tracking != MouseTracking.AnyEvent && !held)
                    {
                        return;
                    }
                    Point position = e.GetPosition(this);
                    (int Col, int Row) cell = ScreenCell(position);
                    if (cell == _lastMouseReportCell)
                    {
                        return;
                    }
                    _lastMouseReportCell = cell;
                    TerminalMouseButton button = _mouseButtonDown ?? TerminalMouseButton.None;
                    SendMouse(TerminalMouseEventType.Move, button, position, e.KeyModifiers);
                    return;
                }
            case true:
                _selectionCaret = PointToCell(e.GetPosition(this));
                InvalidateTerminal();
                break;
        }
    }

    // ---- 链接悬停反馈 ----
    //
    // URL 与 IP 一直画着下划线,但"能不能点、点了去哪"全靠猜:Ctrl 按下时光标不变手型,
    // 也没有任何提示告诉你完整地址(终端里的长 URL 经常被折行截断)。
    // 只在 Ctrl 按下时才做匹配 —— 否则每一次鼠标移动都要跑一遍正则。

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>当前悬停命中的链接文本;没有命中时为 null。</summary>
    private string? _hoveredLink;

    /// <summary>
    /// 按住 Ctrl 悬停在 URL 上时把光标变成手型,并用提示气泡给出完整地址。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="SemanticMatcher.UrlAt" /> —— 与 Ctrl+点击是同一个判定函数,
    /// 于是"看起来能点"和"真的能点"永远一致,不会出现指了手型却点不开的情况。
    /// </remarks>
    private void UpdateLinkHover(PointerEventArgs e)
    {
        if (_selecting || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ClearLinkHover();
            return;
        }
        (int row, int col) = PointToCell(e.GetPosition(this));
        string lineText = row >= 0 && row < Emulator.Screen.TotalRows
            ? Emulator.Screen.ViewLine(row).GetText()
            : string.Empty;
        string? url = SemanticMatcher.UrlAt(lineText, col);
        if (url == _hoveredLink)
        {
            return;
        }
        if (url is null)
        {
            // 先清再置位:ClearLinkHover 以 _hoveredLink 判断"有没有东西要清",
            // 顺序反了它会当场早退,手型与提示就留在屏幕上撤不掉。
            ClearLinkHover();
            return;
        }
        _hoveredLink = url;
        Cursor = HandCursor;
        ToolTip.SetTip(this, url);
        ToolTip.SetIsOpen(this, true);
    }

    private void ClearLinkHover()
    {
        if (_hoveredLink is null)
        {
            return;
        }
        _hoveredLink = null;
        Cursor = Cursor.Default;
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    /// <summary>松开 Ctrl 就撤掉手型与地址提示 —— 此刻已经点不开了,再指着手型是在骗人。</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            ClearLinkHover();
        }
    }

    /// <summary>指针离开控件时清除折叠列悬停标记。</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearLinkHover();
        if (_foldHoverAbs != -1)
        {
            _foldHoverAbs = -1;
            InvalidateTerminal();
        }
    }

    /// <summary>完成应用上报的拖拽,或结束一次选区;在「选中即复制」开启时复制该选区。</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // 用一次释放事件结束应用上报的拖拽/点击。
        if (_mouseButtonDown is { } down)
        {
            SendMouse(TerminalMouseEventType.Release, down, e.GetPosition(this), e.KeyModifiers);
            _mouseButtonDown = null;
            _lastMouseReportCell = (-1, -1);
            e.Handled = true;
            return;
        }
        if (!_selecting)
        {
            return;
        }
        _selecting = false;
        // 选中即复制:松开非空选区即复制它,因此用户永远不需要复制快捷键(设计 §8)。
        // 普通点击选区为空,相当于无操作。
        if (CopyOnSelect)
        {
            _ = CopyAsync();
        }
    }

    /// <summary>字体大小经 Ctrl+滚轮缩放改变时触发,便于宿主持久化。</summary>
    public event Action<double>? FontSizeChanged;

    /// <summary>
    /// 用户设置里的「备用屏滚轮转方向键」总开关(设置 → 终端 → 滚动)。
    /// 与应用侧的 <c>DECSET ?1007</c> 是与的关系:两边都开才转。
    /// </summary>
    public bool AlternateScrollEnabled { get; set; } = true;

    /// <summary>
    /// 读屏器读到的控件名字,由宿主填成标签标题(<c>用户名@主机</c>)。
    /// </summary>
    /// <remarks>
    /// 终端完全自绘,屏幕上那一屏字对读屏器来说什么都不是 —— 没有这个名字,
    /// 用户只会听到一个匿名控件,连"我在哪台机器上"都无从得知。
    /// </remarks>
    public string? AccessibleName { get; set; }

    /// <inheritdoc />
    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new TerminalAutomationPeer(this);

    /// <summary>字号可调范围的下限。</summary>
    public const double MinFontSize = 6;

    /// <summary>字号可调范围的上限。</summary>
    public const double MaxFontSize = 40;

    /// <summary>
    /// 按步长调整字号并触发 <see cref="FontSizeChanged" />(Ctrl+滚轮与 Ctrl+加/减 共用)。
    /// </summary>
    /// <remarks>
    /// 改变 FontSize 会重算单元格度量、reflow 网格并调整 PTY 尺寸,所以夹在
    /// [<see cref="MinFontSize" />, <see cref="MaxFontSize" />] 内;夹住不动时不发事件,
    /// 避免在边界上反复触发持久化写盘。
    /// </remarks>
    /// <param name="delta">步长(正数放大)。</param>
    public void AdjustFontSize(int delta) => ApplyFontSize(FontSize + delta);

    /// <summary>把字号重置到给定值(Ctrl+0:回到设置里的字号)。</summary>
    /// <param name="size">目标字号。</param>
    public void ResetFontSize(double size) => ApplyFontSize(size);

    private void ApplyFontSize(double size)
    {
        double next = Math.Clamp(size, MinFontSize, MaxFontSize);
        if (Math.Abs(next - FontSize) <= 0.01)
        {
            return;
        }
        FontSize = next;
        FontSizeChanged?.Invoke(next);
    }

    /// <summary>
    /// Ctrl+滚轮缩放字体;否则转发给应用鼠标追踪或滚动回滚区。
    /// 本地回滚区中按住 Alt 使用五倍步长快速滚动。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+滚轮缩放字体而非滚动(#21)。改变 FontSize 会重算单元格度量、
        // reflow 网格并调整 PTY 尺寸。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            AdjustFontSize(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        // 在启用鼠标追踪的实时屏幕上,滚轮滚动的是应用(htop/btop 列表、less、vim),
        // 而非本地回滚区。
        if (Emulator.Modes.Mouse != MouseTracking.None && _scrollOffset == 0 && e.Delta.Y != 0)
        {
            TerminalMouseButton button =
                e.Delta.Y > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            if (
                SendMouse(TerminalMouseEventType.Press, button, e.GetPosition(this), e.KeyModifiers)
            )
            {
                e.Handled = true;
                return;
            }
        }
        // 备用屏滚轮转方向键(xterm alternateScroll / DECSET ?1007)。备用屏没有回滚区,
        // 所以未开鼠标追踪时滚轮原本什么都不做 —— less / man / 未开 mouse 的 vim 里
        // 滚轮"没反应"就是这么来的。转成光标上下键发给应用,与 xterm / Windows Terminal /
        // iTerm2 的默认行为一致。走 InputEncoder 而不是手写 ESC [ A:DECCKM 开着时
        // 应用要的是 SS3 A,编码器已经会判。
        if (
            AlternateScrollEnabled
            && Emulator.Modes.AlternateScroll
            && Emulator.IsAlternateScreen
            && Emulator.Modes.Mouse == MouseTracking.None
            && e.Delta.Y != 0
        )
        {
            int lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * WheelScrollLines));
            byte[]? one = InputEncoder.Encode(
                e.Delta.Y > 0 ? Key.Up : Key.Down,
                KeyModifiers.None,
                Emulator.Modes,
                Emulator.Type
            );
            if (one is { Length: > 0 })
            {
                byte[] payload = new byte[one.Length * lines];
                for (int i = 0; i < lines; i++)
                {
                    one.CopyTo(payload, i * one.Length);
                }
                SendTypedInput(payload);
                e.Handled = true;
                return;
            }
        }
        int multiplier = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            ? FastWheelScrollMultiplier
            : 1;
        int delta = (int)(e.Delta.Y * WheelScrollLines * multiplier);
        int maxOffset = Emulator.Screen.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        InvalidateTerminal();
        ScrollChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>将指针位置映射到可见屏幕内从 0 起始的单元。</summary>
    private (int Col, int Row) ScreenCell(Point p)
    {
        p = ToContent(p);
        int col = Math.Clamp(
            (int)((p.X - GutterWidth()) / CellWidthForTest),
            0,
            Math.Max(0, Emulator.Columns - 1)
        );
        int row = Math.Clamp((int)(p.Y / CellHeightForTest), 0, Math.Max(0, Emulator.Rows - 1));
        return (col, row);
    }

    /// <summary>
    /// 在当前的追踪模式下编码一次鼠标事件并发送往 PTY。
    /// 当当前模式不报告此事件时返回 false。
    /// </summary>
    private bool SendMouse(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        Point p,
        KeyModifiers mods
    )
    {
        (int col, int row) = ScreenCell(p);
        byte[]? bytes = MouseEncoder.Encode(
            type,
            button,
            col,
            row,
            mods.HasFlag(KeyModifiers.Shift),
            mods.HasFlag(KeyModifiers.Alt),
            mods.HasFlag(KeyModifiers.Control),
            Emulator.Modes
        );
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }
        SendTypedInput(bytes);
        return true;
    }

    private void ClearSelection()
    {
        _extraSelections.Clear();
        _selectionAnchor = null;
        _selectionCaret = null;
        _blockSelection = false;
    }

    /// <summary>
    /// 在系统浏览器里打开一条链接(Ctrl+点击 URL)。
    /// </summary>
    /// <remarks>
    /// 这里的 <c>async void</c> 自带 try/catch 兜住全部异常。终端项目在宿主项目之下,
    /// 用不了 <c>VelaShell.Services.FireAndForget</c>;而没有这道兜底的话,一个打不开的
    /// 协议处理器(用户把 http 关联到一个已卸载的浏览器)就会以未处理异常掀翻整个进程。
    /// </remarks>
    private async void OpenLink(string url)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null)
            {
                return;
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                await top.Launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaTerminalControl] 打开链接失败 {url}:{ex}");
        }
    }

    /// <summary>
    /// Ctrl+C 的复制分支:仅当「选中时 Ctrl+C 复制」开启且当前有选区时,复制选中
    /// 内容并清除选区,返回 true(调用方不再发送 ^C);否则返回 false,Ctrl+C 照常作为中断
    /// 信号发往 PTY。TerminalTabView 的快捷键回退层也调用这里,保证两层行为一致跟随设置。
    /// </summary>
    public bool TryCopyOnCtrlC()
    {
        if (!CtrlCCopiesWhenSelected || !HasSelectionAnchor)
        {
            return false;
        }
        _ = CopyAsync();
        ClearSelection();
        InvalidateTerminal();
        return true;
    }

    /// <summary>将当前选区复制到系统剪贴板(为空时无操作)。</summary>
    public async Task CopyAsync()
    {
        string text = GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>将剪贴板文本粘贴进终端,遵循括号粘贴与多行确认。</summary>
    public async Task PasteAsync()
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        string? text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 粘贴多行内容前确认(设置 → 终端 → 输入),防止误执行整段脚本。
        if (
            ConfirmMultilinePaste
            && MultilinePasteConfirmation is { } confirm
            && text.IndexOfAny(['\r', '\n']) >= 0
            && text.TrimEnd('\r', '\n').IndexOfAny(['\r', '\n']) >= 0
        )
        {
            bool approved = await confirm(text);

            // 确认框是模态窗口:它关闭后 Avalonia 只把焦点还给宿主窗口,不会回到弹窗前的焦点
            // 控件,于是粘贴完终端是失焦的 —— 用户必须先点一下才能敲回车执行。这里主动把焦点
            // 收回来(取消粘贴时同样收回,否则一样敲不了键)。用 Post 而不是直接 Focus():
            // 关窗时的焦点还原发生在本延续之后,同步抢焦点会被它覆盖掉。
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
            if (!approved)
            {
                return;
            }
        }
        WritePasteInput(text);
    }

    private readonly record struct GlyphKey(
        int Rune,
        string? Combining,
        uint Foreground,
        int Style
    );

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MessageBeep(uint type);
    }

    /// <summary>
    /// 最小 IME 客户端:无缓冲区内的预编辑、无环绕文本 —— 终端并非可编辑文档;
    /// 已提交的文本通过 OnTextInput 作为主机字节到达。只有光标矩形有意义,
    /// 用于定位候选窗口。
    /// </summary>
    private sealed class TerminalImeClient(VelaTerminalControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => owner.GetImeCursorRect();

        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
    }
}

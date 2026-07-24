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
    private readonly List<char> _runChars = [];
    private readonly List<GlyphInfo> _runGlyphs = [];

    // 客户端语义着色(URL、IP、错误/警告/成功词、选项标志、数字),针对
    // 远端程序留在默认颜色下的文本,使普通日志/MOTD 也能被高亮,
    // 且绝不破坏显式 SGR 颜色(ls --color、git 等)。正则结果按行文本缓存,
    // 因为可见行每一帧都会被重新扫描(光标闪烁、输出)。
    private readonly Dictionary<string, IReadOnlyList<SemanticSpan>> _semanticSpanCache = [];

    // 侧栏文本(时间戳/行号/折叠标记)的 FormattedText 缓存:这些文本帧间高度重复
    // (行号在滚动稳定时完全不变),不缓存则每行每帧做一次文本塑形。
    // 键为文本本身;暗色画刷随主题变化时整体失效(见 GutterText)。
    private readonly Dictionary<string, FormattedText> _gutterTextCache = [];
    private ImmutableSolidColorBrush? _gutterTextCacheBrush;

    // ComputeSemanticColumns 的复用缓冲:该方法对每个可见行、每一帧都会执行,
    // 若每次都 new StringBuilder/List/数组,全屏 TUI 下就是每帧几百次堆分配直喂 GC。
    // 三者都只在 UI 线程的渲染路径内短暂使用,跨行复用安全。
    private readonly StringBuilder _semanticLineBuilder = new();
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

    // 选区(线性),位于绝对行空间。
    private (int Row, int Col)? _selectionAnchor;
    private (int Row, int Col)? _selectionCaret;
    private bool _styleTypefacesReady;

    /// <summary>创建一个使用默认 120×32 网格的终端控件。</summary>
    public VelaTerminalControl()
        : this(new(120, 32)) { }

    private VelaTerminalControl(TerminalEmulator emulator)
    {
        Emulator = emulator;
        Focusable = true;
        ClipToBounds = true;
        ApplyDesignPalette(Emulator.Palette);
        RecomputeMetrics();
        Emulator.Updated += OnEmulatorUpdated;
        Emulator.Response += bytes => UserInput?.Invoke(bytes); // 协议自动应答:发往 PTY 但不算用户键入(不进 TypedInput)。
        Emulator.Bell += OnBell;
        Emulator.ClipboardWriteRequested += OnRemoteClipboardWrite;

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
                InvalidateVisual();
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
            InvalidateVisual();
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
        InvalidateVisual();
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
            InvalidateVisual();
            ScrollChanged?.Invoke();
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
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateVisual();
        }
    } = new("fonts:VelaShell#Cascadia Mono, Cascadia Mono, JetBrains Mono, Consolas, Microsoft YaHei, Segoe UI, monospace");

    /// <summary>终端字号(磅);修改后重算单元格度量、重排网格并重绘。</summary>
    public double FontSize
    {
        get;
        set
        {
            field = value;
            RecomputeMetrics();
            RelayoutFromBounds();
            InvalidateVisual();
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
        _scrollOffset = 0;
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        ClearFolds(); // reflow 会重建行对象,折叠引用失效。
        // 选区的行索引是绝对的,会在调整大小时偏移;与其让陈旧的范围
        // 标记(或复制)错误的文本,不如直接丢弃它。
        ClearSelection();
        InvalidateVisual();
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

    // 遗留接口成员:为与既有绑定的源码兼容性而保留。
    /// <summary>遗留回滚缓冲区,仅为与既有绑定的源码兼容性而保留。</summary>
    public ScrollbackBuffer ScrollbackBuffer { get; } = new(1);

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
        ApplyDesignPalette(Emulator.Palette, ActualThemeVariant == ThemeVariant.Light);
        ApplyPaletteOverrides(Emulator.Palette);
        InvalidateVisual();
    }

    private void ApplyPaletteOverrides(TerminalPalette palette)
    {
        if (PaletteOverrides is not { } o)
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
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
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
            InvalidateVisual();
            DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(140));
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
                (_, _) =>
                {
                    _cursorBlinkVisible = !_cursorBlinkVisible;
                    InvalidateVisual();
                }
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
            InvalidateVisual();
        }
    }

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
            InvalidateVisual();
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
        InvalidateVisual();
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
        double x = Math.Max(GutterWidth(), rect.X - columnsBack * CellWidthForTest);
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
        InvalidateVisual();
    }

    /// <summary>清除幽灵候选(立即重绘;移除叠画不会引发位置抖动)。</summary>
    public void ClearGhostSuggestion()
    {
        if (_ghostFull is null)
        {
            return;
        }
        _ghostFull = null;
        InvalidateVisual();
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
            screen.CursorX * CellWidthForTest + GutterWidth(),
            screenRow * CellHeightForTest,
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
        get => _backgroundOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(clamped - _backgroundOpacity) < 0.001)
            {
                return;
            }
            _backgroundOpacity = clamped;
            InvalidateVisual();
        }
    }

    private double _backgroundOpacity = 1.0;
    private ImmutableSolidColorBrush? _bgFillBrush;
    private uint _bgFillPacked;
    private int _bgFillOp = -1;

    /// <summary>整屏默认背景填充画刷:不透明时走共享缓存,半透明时按(颜色×不透明度)缓存一支专用画刷。</summary>
    private ImmutableSolidColorBrush DefaultBackgroundBrush(Rgba bg)
    {
        if (_backgroundOpacity >= 0.999)
        {
            return BrushFor(bg);
        }
        int op = (int)Math.Round(_backgroundOpacity * 1000);
        if (_bgFillBrush is null || _bgFillPacked != bg.Packed || _bgFillOp != op)
        {
            byte a = (byte)Math.Round(bg.A * _backgroundOpacity);
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
        _runGlyphs.Add(new(glyphId, _runChars.Count, width * CellWidthForTest));
        _runChars.Add(ch);
        _runPrevCol = col;
        _runPrevWidth = width;
    }

    /// <summary>将待处理的字形运行(若有)作为单次 DrawGlyphRun 发出,并重置缓冲区。</summary>
    private void FlushGlyphRun(DrawingContext context, double y)
    {
        if (_runGlyphs.Count == 0)
        {
            return;
        }
        GlyphTypeface? gtf = _runStyle >= 0 ? _styleTypefaces[_runStyle] : null;
        if (gtf is not null && _runBrush is not null)
        {
            try
            {
                var run = new GlyphRun(
                    gtf,
                    FontSize,
                    _runChars.ToArray().AsMemory(),
                    _runGlyphs.ToArray(),
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
                Dispatcher.UIThread.Post(InvalidateVisual);
            }
        }
        _runGlyphs.Clear();
        _runChars.Clear();
        _runStyle = -1;
    }

    /// <summary>布局控件并把网格 reflow 到最终布局尺寸。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);
        ApplyLayoutSize(finalSize);
        return result;
    }

    private void RelayoutFromBounds() => ApplyLayoutSize(Bounds.Size);

    private void ApplyLayoutSize(Size size)
    {
        if (CellWidthForTest <= 0 || CellHeightForTest <= 0)
        {
            return;
        }
        int cols = (int)((size.Width - GutterWidth()) / CellWidthForTest);
        int rows = (int)(size.Height / CellHeightForTest);

        // 忽略过早/退化的布局过程(尺寸为零或不足一格)。在这里把网格压缩成
        // 单列,正是过去登录横幅每行只渲染一个字符的元凶:后续每个字符都自动换行。
        // 在真实尺寸到来之前,保持当前(或默认 120x32)网格。
        if (cols < 2 || rows < 2)
        {
            return;
        }
        if (cols == Emulator.Columns && rows == Emulator.Rows)
        {
            return;
        }

        // 本地网格立即 reflow,使拖拽感觉实时,并且也立刻通知 PTY —— 这是主流做法。
        // 本地与远端必须保持同步:早期带防抖的通知让远端的认知(readline 的提示符行数学)
        // 落后许多次 reflow,导致其相对光标移动与擦除落在错误的行上,逐步破坏缓冲内容。
        // 传输层按顺序串行化发送,将突发合并为最新尺寸。
        Emulator.Resize(cols, rows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Emulator.Screen.ScrollbackCount);
        _lastScrollbackCount = Emulator.Screen.ScrollbackCount;
        ClearFolds(); // reflow 会重建行对象,折叠引用失效。
        // reflow 会移动绝对行;陈旧的选区会标记(并复制)错误的文本。
        ClearSelection();
        InvalidateVisual();
        ScrollChanged?.Invoke();
        PtySizeChanged?.Invoke(cols, rows);
    }

    // ---- Rendering ----------------------------------------------------------

    /// <summary>
    /// 视觉 BEL:整个终端上的一次短暂半透明闪烁(§终端 → 视觉闪烁)
    /// </summary>
    /// <param name="context"></param>
    public override void Render(DrawingContext context)
    {
        TerminalScreen screen = Emulator.Screen;
        TerminalPalette palette = Emulator.Palette;
        context.FillRectangle(DefaultBackgroundBrush(palette.DefaultBackground), new(Bounds.Size));
        int rows = screen.Rows;
        int cols = screen.Columns;

        // 计算本帧「屏幕行 → 绝对缓冲行」映射(_screenToAbs):无折叠时即连续 topAbsolute+sr(与原行为一致),
        // 有折叠时跳过被隐藏的行。侧栏、正文、光标、命中测试全部复用该映射,确保三者对齐。
        BuildScreenRowMap(screen, rows);
        ((int Row, int Col) Start, (int Row, int Col) End)? sel = NormalizedSelection();

        // 行号/时间侧栏在正文左侧:先画侧栏,再把正文(含光标、选区)整体右移一个侧栏宽度绘制,
        // 这样所有 col*_cellWidth 的坐标计算保持不变,只在命中测试处减去侧栏宽度即可。
        if (GutterEnabled)
        {
            RenderGutter(context, screen, palette, rows);
        }
        using (context.PushTransform(Matrix.CreateTranslation(GutterWidth(), 0)))
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
                RenderLine(context, palette, line, cols, y, absoluteRow, sel);
            }
            if (_scrollOffset == 0)
            {
                RenderCursor(context, screen, palette);
                RenderGhostText(context, screen, palette, cols);
            }
        }

        // 视觉 BEL:整个终端上的一次短暂半透明闪烁(§终端 → 视觉闪烁)。
        if (_bellFlashUntil > DateTime.UtcNow)
        {
            context.FillRectangle(BellFlashBrush, new(Bounds.Size));
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
    private FormattedText GutterText(string text, Typeface typeface, ImmutableSolidColorBrush brush)
    {
        if (!ReferenceEquals(_gutterTextCacheBrush, brush))
        {
            _gutterTextCache.Clear();
            _gutterTextCacheBrush = brush;
        }
        if (_gutterTextCache.TryGetValue(text, out FormattedText? cached))
        {
            return cached;
        }
        if (_gutterTextCache.Count > 512)
        {
            _gutterTextCache.Clear();
        }
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
        _gutterTextCache[text] = formatted;
        return formatted;
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
            if (ShowLineTimestamp && line.Timestamp is { } ts)
            {
                string stamp =
                    "[" + ts.ToString(GutterTimeFormat, CultureInfo.InvariantCulture) + "] ";
                context.DrawText(GutterText(stamp, typeface, dimBrush), new Point(0, y));
            }
            if (ShowLineNumber)
            {
                string number =
                    (absoluteRow + 1)
                        .ToString(CultureInfo.InvariantCulture)
                        .PadLeft(GutterLayout.NumberDigits) + " ";
                context.DrawText(GutterText(number, typeface, dimBrush), new Point(numberLeft, y));
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
        InvalidateVisual();
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
        ((int Row, int Col) Start, (int Row, int Col) End)? sel
    )
    {
        // 备用屏(vim/htop/less 等全屏程序)自带配色且行内容每帧都在变:语义扫描
        // 既无视觉价值又必然缓存 MISS(整行文本为键),是全屏 TUI 卡顿的头号来源,直接跳过。
        SemanticKind?[]? semantic = SemanticHighlightingEnabled && !Emulator.IsAlternateScreen
            ? ComputeSemanticColumns(line, cols)
            : null;
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
            if (IsSelected(sel, absoluteRow, col))
            {
                bg = palette.SelectionBackground;
            }
            if (
                _searchHighlights is not null
                && _searchHighlights.TryGetValue(
                    absoluteRow,
                    out List<(int Start, int End, bool Current)>? searchSpans
                )
            )
            {
                foreach ((int Start, int End, bool Current) in searchSpans)
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
            var cellRect = new Rect(
                col * CellWidthForTest,
                y,
                CellWidthForTest * width,
                CellHeightForTest
            );
            if (!bg.Equals(palette.DefaultBackground))
            {
                context.FillRectangle(BrushFor(bg), cellRect);
            }

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
                    FlushGlyphRun(context, y);
                    FormattedText ft = GlyphFor(cell, fg, bold, italic);
                    context.DrawText(ft, new(col * CellWidthForTest, y + _glyphYOffset));
                }
            }
            if (
                (cell.Flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0
                || semanticUnderline
            )
            {
                double uy = y + CellHeightForTest - 1.5;
                context.DrawLine(
                    PenFor(fg),
                    new(col * CellWidthForTest, uy),
                    new((col + width) * CellWidthForTest, uy)
                );
            }
            if ((cell.Flags & CellFlags.Strikethrough) != 0)
            {
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
        FlushGlyphRun(context, y);
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
        StringBuilder sb = _semanticLineBuilder.Clear();
        List<int> colByChar = _semanticColByChar;
        colByChar.Clear();
        for (int i = 0; i <= lastNonBlank; i++)
        {
            TerminalCell cell = line[i];
            if (cell.IsWideTrailing)
            {
                continue;
            }
            int before = sb.Length;
            cell.AppendText(sb);
            for (int k = before; k < sb.Length; k++)
            {
                colByChar.Add(i);
            }
        }
        IReadOnlyList<SemanticSpan> spans = SemanticSpansFor(sb.ToString());
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

    private IReadOnlyList<SemanticSpan> SemanticSpansFor(string text)
    {
        if (_semanticSpanCache.TryGetValue(text, out IReadOnlyList<SemanticSpan>? cached))
        {
            return cached;
        }

        // 限制缓存大小;终端输出行的变化极多,因此增长时直接重置即可。
        if (_semanticSpanCache.Count > 1024)
        {
            _semanticSpanCache.Clear();
        }
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(text);
        _semanticSpanCache[text] = spans;
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
        var rect = new Rect(x, y, CellWidthForTest, CellHeightForTest);
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
                    new(x, y, Math.Max(1.5, CellWidthForTest * 0.15), CellHeightForTest)
                );
                break;
            case "underline":
                context.FillRectangle(
                    cursorBrush,
                    new(x, y + CellHeightForTest - 2, CellWidthForTest, 2)
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

    private ((int Row, int Col) Start, (int Row, int Col) End)? NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionCaret is not { } c)
        {
            return null;
        }
        if (a.Row < c.Row || (a.Row == c.Row && a.Col <= c.Col))
        {
            return (a, c);
        }
        return (c, a);
    }

    private static bool IsSelected(
        ((int Row, int Col) Start, (int Row, int Col) End)? sel,
        int row,
        int col
    )
    {
        if (sel is not { } s)
        {
            return false;
        }
        if (row < s.Start.Row || row > s.End.Row)
        {
            return false;
        }
        if (row == s.Start.Row && col < s.Start.Col)
        {
            return false;
        }
        if (row == s.End.Row && col >= s.End.Col)
        {
            return false;
        }
        return true;
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
        InvalidateVisual();
    }

    /// <summary>移除所有搜索命中高亮并重新绘制。</summary>
    public void ClearSearchHighlights()
    {
        if (_searchHighlights is null)
        {
            return;
        }
        _searchHighlights = null;
        InvalidateVisual();
    }

    /// <summary>
    /// 将搜索命中滚动到可见区域(大致居中)并选中它,
    /// 使既有选区高亮标记出匹配位置。
    /// </summary>
    public void ShowHit(BufferSearchHit hit)
    {
        _selectionAnchor = (hit.Row, hit.StartCol);
        _selectionCaret = (hit.Row, hit.StartCol + hit.Length);
        int totalRows = Emulator.Screen.TotalRows;
        int rows = Emulator.Rows;
        int desiredTop = Math.Max(0, hit.Row - rows / 2);
        int maxTop = Math.Max(0, totalRows - rows);
        ScrollOffset = maxTop - Math.Min(desiredTop, maxTop);
        InvalidateVisual();
    }

    /// <summary>以文本形式返回当前选区(可逐行去除行尾空白)。</summary>
    public string GetSelectedText()
    {
        ((int Row, int Col) Start, (int Row, int Col) End)? sel = NormalizedSelection();
        if (sel is not { } s)
        {
            return string.Empty;
        }
        TerminalScreen screen = Emulator.Screen;
        var sb = new StringBuilder();
        for (int row = s.Start.Row; row <= s.End.Row && row < screen.TotalRows; row++)
        {
            TerminalRow line = screen.ViewLine(row);
            int from = row == s.Start.Row ? s.Start.Col : 0;
            int to = row == s.End.Row ? s.End.Col : line.Columns;
            int lineStart = sb.Length;
            for (int col = Math.Max(0, from); col < Math.Min(line.Columns, to); col++)
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
            if (row != s.End.Row)
            {
                sb.Append('\n');
            }
        }
        return TrimTrailingWhitespaceOnCopy ? sb.ToString().TrimEnd() : sb.ToString();
    }

    private (int Row, int Col) PointToCell(Point p)
    {
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
        InvalidateVisual();
    }

    /// <summary>标记控件未聚焦,停止闪烁并绘制空心光标。</summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        UpdateCursorBlinkTimer();
        InvalidateVisual();
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
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        TerminalKeyAction action = TerminalKeyRouter.Classify(
            e.Key,
            e.KeyModifiers,
            Emulator.Modes,
            Emulator.Type,
            canScrollHistory: Emulator.Screen.MaxScrollback > 0,
            ctrlCCopiesSelection: CtrlCCopiesWhenSelected && NormalizedSelection() is not null);
        switch (action.Kind)
        {
            case TerminalKeyActionKind.ImePassthrough:
                break; // 已提交文本会经 OnTextInput 单独送达。

            case TerminalKeyActionKind.CopySelection:
                // Ctrl+C 变体带"复制后清选区"的既有语义;Ctrl+Shift+C 保留选区。
                if (e.KeyModifiers == KeyModifiers.Control)
                {
                    TryCopyOnCtrlC();
                }
                else
                {
                    await CopyAsync();
                }
                e.Handled = true;
                return;

            case TerminalKeyActionKind.PasteClipboard:
                await PasteAsync();
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

    /// <summary>处理侧栏点击、URL 的 Ctrl+点击、应用鼠标上报以及文本选区起点。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point point = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        // 侧栏(时间/行号/折叠)区域的交互:右键弹设置菜单;折叠列左键折叠/展开;其余左键吞掉不选文本。
        GutterLayout gutter = Gutter;
        if (gutter.ContainsX(point.X))
        {
            if (props.IsRightButtonPressed)
            {
                ShowGutterContextMenu();
                e.Handled = true;
                return;
            }
            if (props.IsLeftButtonPressed && gutter.IsFoldColumnHit(point.X))
            {
                ToggleFoldAt((int)(point.Y / CellHeightForTest));
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
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
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
            if (e.ClickCount == 2 && DoubleClickSelectsWord)
            {
                SelectWordAt(PointToCell(point));
                e.Handled = true;
                return;
            }
            _selecting = true;
            _selectionAnchor = PointToCell(point);
            _selectionCaret = _selectionAnchor;
            InvalidateVisual();
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
    private void SelectWordAt((int Row, int Col) cell)
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
        _selectionAnchor = (cell.Row, start);
        _selectionCaret = (cell.Row, end);
        _selecting = false;
        InvalidateVisual();
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

        // 折叠列悬停提示:指针在折叠列上时记住其绝对行(用于画 ▾ 折叠手柄),移出则清除。
        // 空白行(最后一行输出之下)不给提示——那里折叠无意义且极易误点(内容消失事故)。
        if (ShowFoldMarker && !_selecting)
        {
            Point gp = e.GetPosition(this);
            int hover = Gutter.IsFoldColumnHit(gp.X)
                ? AbsoluteForScreenRow((int)(gp.Y / CellHeightForTest))
                : -1;
            if (hover != -1 && !IsFoldTargetRow(hover))
            {
                hover = -1;
            }
            if (hover != _foldHoverAbs)
            {
                _foldHoverAbs = hover;
                InvalidateVisual();
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
                InvalidateVisual();
                break;
        }
    }

    /// <summary>指针离开控件时清除折叠列悬停标记。</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_foldHoverAbs != -1)
        {
            _foldHoverAbs = -1;
            InvalidateVisual();
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

    /// <summary>Ctrl+滚轮缩放字体;否则转发给应用鼠标追踪或滚动回滚区。</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+滚轮缩放字体而非滚动(#21)。改变 FontSize 会重算单元格度量、
        // reflow 网格并调整 PTY 尺寸。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double next = Math.Clamp(FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 40);
            if (Math.Abs(next - FontSize) > 0.01)
            {
                FontSize = next;
                FontSizeChanged?.Invoke(next);
            }
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
        int delta = (int)(e.Delta.Y * 3);
        int maxOffset = Emulator.Screen.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        InvalidateVisual();
        ScrollChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>将指针位置映射到可见屏幕内从 0 起始的单元。</summary>
    private (int Col, int Row) ScreenCell(Point p)
    {
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
        _selectionAnchor = null;
        _selectionCaret = null;
    }

    private async void OpenLink(string url)
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

    /// <summary>
    /// Ctrl+C 的复制分支:仅当「选中时 Ctrl+C 复制」开启且当前有选区时,复制选中
    /// 内容并清除选区,返回 true(调用方不再发送 ^C);否则返回 false,Ctrl+C 照常作为中断
    /// 信号发往 PTY。TerminalTabView 的快捷键回退层也调用这里,保证两层行为一致跟随设置。
    /// </summary>
    public bool TryCopyOnCtrlC()
    {
        if (!CtrlCCopiesWhenSelected || NormalizedSelection() is null)
        {
            return false;
        }
        _ = CopyAsync();
        ClearSelection();
        InvalidateVisual();
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
            && !await confirm(text)
        )
        {
            return;
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

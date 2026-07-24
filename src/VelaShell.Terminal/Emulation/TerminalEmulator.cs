using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端的"大脑":消费来自 <see cref="VtParser" /> 的已解析转义序列事件,并将其施加到
/// <see cref="TerminalScreen" /> 上。持有当前图形显示属性(画笔)、字符集、终端模式、制表位
/// 以及保存的光标状态,并通过 <see cref="Response" /> 产生发往宿主的应答
/// (设备属性、光标报告等)。行为受当前生效的 <see cref="TerminalType" /> 约束,
/// 因此同一引擎可仿真从 VT52 到 xterm-256color 的任何终端。
/// </summary>
public sealed class TerminalEmulator : IVtActions
{
    /// <summary>OSC 52 载荷上限(base64 解码后),防远端滥发撑爆剪贴板。</summary>
    private const int MaxOsc52Bytes = 1024 * 1024;

    // 字符集:G0..G3 指定,GL/GR 调用,单字符移位。
    private readonly bool[] _decGraphics = new bool[4]; // true => DEC 特殊图形
    private readonly TerminalScreen _mainScreen;
    private readonly VtParser _parser;
    private readonly Utf8Sink _utf8 = new();

    // 备用屏切换(DECSET 1049)专用的独立保存槽。与 _saved 区分开来正是 xterm 的做法:
    // 在备用屏(如 nano)中运行的应用可自由使用 DECSC/DECRC,
    // 而不会破坏退出时必须恢复的、主屏的光标位置(#14b)。
    private SavedCursor? _altSaved;
    private TerminalScreen? _altScreen; // 备用缓冲区(无回滚)
    private TerminalColor _bg = TerminalColor.Default;

    // 当前画笔
    private TerminalColor _fg = TerminalColor.Default;
    private CellFlags _flags = CellFlags.None;
    private int _gl; // 当前生效的 GL 字符集索引

    private bool _pendingWrap; // 行尾的延迟自动换行
    private DateTime _feedTimestamp = DateTime.Now; // 当前 Feed 到达时刻,用于给写入的行盖时间戳(行号侧栏)

    // Saved cursor (DECSC / DECRC, CSI s/u, DECSET 1048)
    private SavedCursor? _saved;

    private int _singleShift = -1;
    private bool[] _tabStops;

    /// <summary>以给定的屏幕尺寸、终端类型与回滚容量创建仿真器。</summary>
    public TerminalEmulator(int columns = 80, int rows = 24, TerminalType type = TerminalType.XtermColor256, int scrollback = 10_000)
    {
        Type = type;
        Palette = new();
        Modes = new();
        Screen = new(columns, rows, scrollback);
        _mainScreen = Screen;
        _tabStops = BuildDefaultTabs(columns);
        _parser = new(this) { Vt52Mode = type == TerminalType.Vt52 };
    }

    /// <summary>当前正在仿真的终端类型;约束功能行为。</summary>
    public TerminalType Type { get; private set; }

    /// <summary>当前生效的终端模式(自动换行、原点模式、鼠标跟踪等)。</summary>
    public TerminalModes Modes { get; }

    /// <summary>用于把索引色解析为具体 RGB 值的调色板。</summary>
    public TerminalPalette Palette { get; }

    /// <summary>当前生效的屏幕缓冲区(主屏或备用屏)。</summary>
    public TerminalScreen Screen { get; private set; }

    /// <summary>当前屏幕的列数。</summary>
    public int Columns => Screen.Columns;

    /// <summary>当前屏幕的行数。</summary>
    public int Rows => Screen.Rows;

    /// <summary>当前光标列(从 0 开始)。</summary>
    public int CursorX => Screen.CursorX;

    /// <summary>当前光标行(从 0 开始)。</summary>
    public int CursorY => Screen.CursorY;

    /// <summary>备用屏缓冲区处于活动状态(DECSET 1047/1049)时为 true。</summary>
    public bool IsAlternateScreen { get; private set; }

    // ---- IVtActions:打印 ----------------------------------------------

    /// <summary>在光标处写入可打印字符,处理字符集翻译、宽/组合字形与自动换行。</summary>
    public void Print(int rune)
    {
        // 应用当前生效的字符集翻译。
        int setIndex = _singleShift >= 0 ? _singleShift : _gl;
        _singleShift = -1;
        if (_decGraphics[setIndex])
        {
            rune = Charsets.MapDecSpecial(rune);
        }
        int width = CharWidth.Of(rune);

        // 组合标记附加到前一个单元格,且不移动光标。
        if (width == 0)
        {
            AttachCombining(rune);
            return;
        }
        if (_pendingWrap)
        {
            Screen.ActiveLine(Screen.CursorY).Wrapped = true;
            CarriageReturnLineFeed();
            _pendingWrap = false;
        }

        // 对放不下的宽字符做自动换行检查。
        if (width == 2 && Screen.CursorX == Screen.Columns - 1)
        {
            if (Modes.AutoWrap)
            {
                Screen.ActiveLine(Screen.CursorY).Wrapped = true;
                CarriageReturnLineFeed();
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 2);
            }
        }
        if (Modes.InsertMode)
        {
            Screen.InsertChars(width, Blank());
        }
        var cell = new TerminalCell
        {
            Rune = rune,
            Foreground = _fg,
            Background = _bg,
            Flags = _flags
        };
        Screen.SetCell(Screen.CursorX, Screen.CursorY, cell);
        // 行时间戳取「本次 Feed 到达时刻」——按 chunk 取一次,避免逐字符 DateTime.Now;
        // 同一行被多次写入时以最后一次为准(= 该行最后收到输出的时间)。
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
        if (width == 2)
        {
            TerminalCell trailing = cell;
            trailing.Rune = 0;
            trailing.Flags |= CellFlags.WideTrailing;
            Screen.SetCell(Screen.CursorX + 1, Screen.CursorY, trailing);
        }
        if (Screen.CursorX + width >= Screen.Columns)
        {
            if (Modes.AutoWrap)
            {
                Screen.SetCursorX(Screen.Columns - 1);
                _pendingWrap = true;
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 1);
            }
        }
        else
        {
            Screen.SetCursorX(Screen.CursorX + width);
        }
    }

    // ---- IVtActions:C0 控制字符 -------------------------------------------

    /// <summary>执行一个 C0 控制字符(BEL、BS、HT、LF/VT/FF、CR、SO/SI)。</summary>
    public void Execute(char control)
    {
        switch (control)
        {
            case '\a': // BEL
                Bell?.Invoke();
                break;
            case '\b': // BS
                if (_pendingWrap)
                {
                    _pendingWrap = false;
                }
                else if (Screen.CursorX > 0)
                {
                    Screen.SetCursorX(Screen.CursorX - 1);
                }
                break;
            case '\t': // HT
                HorizontalTab();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                _pendingWrap = false;
                IndexAndStamp();
                if (Modes.NewLineMode)
                {
                    Screen.SetCursorX(0);
                }
                break;
            case '\r': // CR
                _pendingWrap = false;
                Screen.SetCursorX(0);
                break;
            case '\x0E': // SO -> 把 G1 调用进 GL
                _gl = 1;
                break;
            case '\x0F': // SI -> 把 G0 调用进 GL
                _gl = 0;
                break;
        }
    }

    // ---- IVtActions:ESC ----------------------------------------------------

    /// <summary>分发 ESC 序列(字符集指定、IND/RI/NEL、DECSC/DECRC、RIS 等)。</summary>
    public void EscDispatch(string intermediates, char final)
    {
        if (Type == TerminalType.Vt52 && intermediates.Length == 0)
        {
            EscDispatchVt52(final);
            return;
        }
        if (intermediates.Length > 0)
        {
            char inter = intermediates[0];
            switch (inter)
            {
                case '(' or ')' or '*' or '+': // designate G0..G3
                    int g = inter switch { '(' => 0, ')' => 1, '*' => 2, _ => 3 };
                    _decGraphics[g] = final == '0';
                    return;
                case '#':
                    if (final == '8')
                    {
                        FillScreenWithE(); // DECALN
                    }
                    return;
            }
            return;
        }
        switch (final)
        {
            case 'D':
                IndexAndStamp();
                break; // IND
            case 'M':
                Screen.ReverseIndex(Blank());
                break; // RI
            case 'E':
                Screen.SetCursorX(0);
                IndexAndStamp();
                break; // NEL
            case 'H':
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = true;
                break; // HTS
            case '7':
                SaveCursor();
                break; // DECSC
            case '8':
                RestoreCursor();
                break; // DECRC
            case '=':
                Modes.ApplicationKeypad = true;
                break; // DECCKPAM
            case '>':
                Modes.ApplicationKeypad = false;
                break; // DECKPNM
            case 'c':
                FullReset();
                break; // RIS
            case '\\':
                break; // ST(字符串终结符)
            case 'n':
                _gl = 2;
                break; // LS2
            case 'o':
                _gl = 3;
                break; // LS3
        }
    }

    // ---- IVtActions:CSI ----------------------------------------------------

    /// <summary>分发 CSI 序列(光标移动、擦除、插入/删除、SGR、模式设置/重置、报告等)。</summary>
    public void CsiDispatch(char prefix, IReadOnlyList<int> p, string intermediates, char final)
    {
        if (prefix == '?')
        {
            HandlePrivateMode(p, final);
            return;
        }
        switch (intermediates)
        {
            // 中间字节 '!' + 'p' => DECSTR 软复位。
            case "!" when final == 'p':
                SoftReset();
                return;
            case " " when final == 'q':
                return; // DECSCUSR 光标样式(已接受,样式在 UI 中处理)
        }
        switch (final)
        {
            case '@':
                Screen.InsertChars(P(0), Blank());
                break; // ICH
            case 'A':
                MoveCursor(0, -P(0));
                break; // CUU
            case 'B':
                MoveCursor(0, P(0));
                break; // CUD
            case 'C':
                MoveCursor(P(0), 0);
                break; // CUF
            case 'D':
                MoveCursor(-P(0), 0);
                break; // CUB
            case 'E':
                Screen.SetCursorX(0);
                MoveCursor(0, P(0));
                break; // CNL
            case 'F':
                Screen.SetCursorX(0);
                MoveCursor(0, -P(0));
                break; // CPL
            case '`':
            case 'G':
                SetCursorColumn(P(0) - 1);
                break; // CHA / HPA
            case 'd':
                SetCursorRow(P(0) - 1);
                break; // VPA
            case 'H':
            case 'f':
                CursorPosition(P(0) - 1, P(1) - 1);
                break; // CUP / HVP
            case 'I':
                TabForward(P(0));
                break; // CHT
            case 'Z':
                TabBackward(P(0));
                break; // CBT
            case 'J':
                Screen.EraseInDisplay(P0(0), Blank());
                _pendingWrap = false;
                break; // ED
            case 'K':
                Screen.EraseInLine(P0(0), Blank());
                _pendingWrap = false;
                break; // EL
            case 'L':
                Screen.InsertLines(P(0), Blank());
                break; // IL
            case 'M':
                Screen.DeleteLines(P(0), Blank());
                break; // DL
            case 'P':
                Screen.DeleteChars(P(0), Blank());
                break; // DCH
            case 'X':
                Screen.EraseChars(P(0), Blank());
                break; // ECH
            case 'S':
                Screen.ScrollUp(P(0), Blank());
                break; // SU
            case 'T':
                Screen.ScrollDown(P(0), Blank());
                break; // SD
            case 'm':
                ApplySgr(p);
                break; // SGR
            case 'r':
                SetScrollRegion(p);
                break; // DECSTBM
            case 'h':
                SetAnsiMode(p, true);
                break;
            case 'l':
                SetAnsiMode(p, false);
                break;
            case 'g':
                ClearTabs(P0(0));
                break; // TBC
            case 'c':
                DeviceAttributes(prefix);
                break; // DA
            case 'n':
                DeviceStatusReport(P0(0));
                break; // DSR
            case 's':
                SaveCursor();
                break; // ANSI.SYS save
            case 'u':
                RestoreCursor();
                break; // ANSI.SYS restore
            case 't':
                break; // 窗口操作(已忽略)
        }
        return;

        int P(int index, int def = 1)
        {
            if (index >= p.Count)
            {
                return def;
            }
            int v = p[index];
            return v == 0 ? def : v;
        }

        int P0(int index) => index < p.Count ? p[index] : 0;
    }

    /// <summary>分发 OSC 命令(窗口标题变更、OSC 52 剪贴板写入)。</summary>
    public void OscDispatch(IReadOnlyList<string> p)
    {
        if (p.Count == 0)
        {
            return;
        }
        if (!int.TryParse(p[0], out int cmd))
        {
            return;
        }
        switch (cmd)
        {
            case 0:
            case 2:
                if (p.Count > 1)
                {
                    TitleChanged?.Invoke(p[1]);
                }
                break;
            case 52:
                // 形如 52;c;<base64>(c/p/s… 选区种类一律当系统剪贴板处理)。
                if (p.Count > 2 && p[2] is { Length: > 0 } payload && payload != "?" && payload.Length <= MaxOsc52Bytes / 3 * 4 + 4)
                {
                    try
                    {
                        string text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        if (text.Length > 0)
                        {
                            ClipboardWriteRequested?.Invoke(text);
                        }
                    }
                    catch (FormatException)
                    {
                        // 非法 base64:按规范静默忽略。
                    }
                }
                break;
            case 7:
                // OSC 7:shell 上报当前工作目录(file://host/path)。用于「文件浏览器跟随终端目录」。
                // 由 VelaShell 注入的 bash 提示符脚本发出(见 MainWindowViewModel.PromptNewlineFix)。
                if (p.Count > 1 && ParseOsc7Path(p[1]) is { Length: > 0 } dir)
                {
                    WorkingDirectoryChanged?.Invoke(dir);
                }
                break;
                // 4(调色板)、8(超链接)目前有意接受并忽略。
        }
    }

    /// <summary>从 OSC 7 载荷 file://host/path 提取绝对路径(百分号编码按需解码);非法/非绝对路径返回 null。</summary>
    private static string? ParseOsc7Path(string payload)
    {
        const string scheme = "file://";
        if (!payload.StartsWith(scheme, StringComparison.Ordinal))
        {
            return null;
        }
        int slash = payload.IndexOf('/', scheme.Length); // 跳过 host,定位路径起点(file:///abs 时即第三个斜杠)
        if (slash < 0)
        {
            return null;
        }
        string path = payload[slash..];
        try
        {
            path = Uri.UnescapeDataString(path); // VTE 等会百分号编码;我们注入的脚本发原始路径,解码对纯 ASCII 无副作用
        }
        catch (Exception)
        {
            // 解码失败:用原始路径。
        }
        return path.StartsWith('/') ? path : null;
    }

    /// <summary>分发 DCS 序列;目前处理 DECRQSS 状态请求,其余静默消费。</summary>
    public void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data)
    {
        // DECRQSS(DCS $ q Pt ST):按 xterm 惯例应答 DCS 1 $ r <设定> ST(1=有效,0=无效)。
        // sixel 仍未实现,静默消费。
        if (final != 'q' || intermediates != "$")
        {
            return;
        }
        switch (data)
        {
            case "m": // SGR:回报当前画笔属性
                Send($"\eP1$r{BuildSgrReport()}m\e\\");
                break;
            case "r": // DECSTBM:回报当前滚动区域(1 基)
                Send($"\eP1$r{Screen.ScrollTop + 1};{Screen.ScrollBottom + 1}r\e\\");
                break;
            default:
                Send("\eP0$r\e\\");
                break;
        }
    }

    /// <summary>终端需要发回宿主的字节(DA/DSR 等)。</summary>
    public event Action<byte[]>? Response;

    /// <summary>OSC 0/2 窗口标题变更。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>OSC 7 当前工作目录变更(绝对路径)。事件来自 feed 线程,消费者需自行编组到 UI 线程。</summary>
    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>收到 BEL(0x07)。</summary>
    public event Action? Bell;

    /// <summary>在一块输入被应用后触发,以便 UI 重绘。</summary>
    public event Action? Updated;

    /// <summary>切换所仿真的终端类型,并相应更新 VT52 解析。</summary>
    public void SetTerminalType(TerminalType type)
    {
        Type = type;
        _parser.Vt52Mode = type == TerminalType.Vt52;
    }

    /// <summary>改变字节解码所用字符集(默认为 UTF-8)。挂起的字节会被丢弃。</summary>
    public void SetEncoding(Encoding encoding) => _utf8.SetEncoding(encoding);

    // ---- 输入 --------------------------------------------------------------

    /// <summary>从宿主喂入原始字节。UTF-8 在解析前解码。</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _feedTimestamp = DateTime.Now;
        // DecodeSpan 复用 sink 的内部缓冲,parser 只读遍历不留引用——全程零 string 物化。
        ReadOnlySpan<char> decoded = _utf8.DecodeSpan(bytes);
        if (decoded.Length > 0)
        {
            _parser.Parse(decoded);
        }
        Updated?.Invoke();
    }

    /// <summary>从宿主喂入原始字节。UTF-8 在解析前解码。</summary>
    public void Feed(byte[] bytes) => Feed(bytes.AsSpan());

    /// <summary>把主屏与备用屏(以及制表位)都调整为给定几何尺寸。</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        _mainScreen.Resize(columns, rows, Blank());
        _altScreen?.Resize(columns, rows, Blank());
        _tabStops = ResizeTabs(_tabStops, columns);
        _pendingWrap = false;
    }

    // ---- 辅助方法 ------------------------------------------------------------

    private TerminalCell Blank() => TerminalCell.Blank(_bg, _flags);

    private static bool[] BuildDefaultTabs(int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i < old.Length ? old[i] : i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private void AttachCombining(int rune)
    {
        int x = Screen.CursorX - 1;
        int y = Screen.CursorY;
        if (x < 0)
        {
            return;
        }
        ref TerminalCell cell = ref Screen.CellRef(Math.Clamp(x, 0, Screen.Columns - 1), y);
        if (cell.IsWideTrailing && x - 1 >= 0)
        {
            x -= 1;
            cell = ref Screen.CellRef(x, y);
        }
        cell.Combining = (cell.Combining ?? string.Empty) + char.ConvertFromUtf32(rune);
    }

    private void CarriageReturnLineFeed()
    {
        Screen.SetCursorX(0);
        IndexAndStamp();
    }

    /// <summary>
    /// Line feed 并给落到的行盖上本次 Feed 的时间戳。这样即使是输出里的空行(仅 \r\n,无可打印字符),
    /// 也被视为「该次输出产生的真实行」,侧栏据此显示其行号/时间;而光标从未到过的屏幕底部空行不会被盖章。
    /// </summary>
    private void IndexAndStamp()
    {
        Screen.Index(Blank());
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
    }

    private void HorizontalTab()
    {
        int x = Screen.CursorX;
        for (int i = x + 1; i < Screen.Columns; i++)
        {
            if (!_tabStops[i])
            {
                continue;
            }
            Screen.SetCursorX(i);
            return;
        }
        Screen.SetCursorX(Screen.Columns - 1);
    }

    private void EscDispatchVt52(char final)
    {
        switch (final)
        {
            case 'A':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY - 1);
                break;
            case 'B':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY + 1);
                break;
            case 'C':
                Screen.SetCursor(Screen.CursorX + 1, Screen.CursorY);
                break;
            case 'D':
                Screen.SetCursor(Screen.CursorX - 1, Screen.CursorY);
                break;
            case 'H':
                Screen.SetCursor(0, 0);
                break;
            case 'I':
                Screen.ReverseIndex(Blank());
                break;
            case 'J':
                Screen.EraseInDisplay(0, Blank());
                break;
            case 'K':
                Screen.EraseInLine(0, Blank());
                break;
            case 'Z':
                Send("\e/Z");
                break; // VT52 identify
            case '<':
                SetTerminalType(TerminalType.Vt100);
                break; // exit VT52 mode
            case '=':
                Modes.ApplicationKeypad = true;
                break;
            case '>':
                Modes.ApplicationKeypad = false;
                break;
            case 'F':
                _decGraphics[0] = true;
                break; // 进入图形模式
            case 'G':
                _decGraphics[0] = false;
                break; // 退出图形模式
        }
    }

    private void HandlePrivateMode(IReadOnlyList<int> p, char final)
    {
        if (final == 'c')
        {
            // 某些宿主发送 "CSI ? ... c" 风格;若 final 为 c 则在别处当作 DA 处理。
            return;
        }
        bool set = final == 'h';
        if (final is not 'h' and not 'l')
        {
            return;
        }
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 1:
                    Modes.ApplicationCursorKeys = set;
                    break; // DECCKM
                case 2:
                    if (!set)
                    {
                        SetTerminalType(TerminalType.Vt52);
                    }
                    break; // DECANM (reset -> VT52)
                case 3:
                    ColumnMode(set);
                    break; // DECCOLM 132/80
                case 5:
                    Modes.ReverseVideo = set;
                    break; // DECSCNM
                case 6:
                    Modes.OriginMode = set;
                    HomeCursor();
                    break; // DECOM
                case 7:
                    Modes.AutoWrap = set;
                    break; // DECAWM
                case 9:
                    Modes.Mouse = set ? MouseTracking.X10 : MouseTracking.None;
                    break;
                case 12:
                    Modes.CursorBlink = set;
                    break;
                case 25:
                    Modes.CursorVisible = set;
                    break; // DECTCEM
                case 1000:
                    Modes.Mouse = set ? MouseTracking.Normal : MouseTracking.None;
                    break;
                case 1002:
                    Modes.Mouse = set ? MouseTracking.ButtonEvent : MouseTracking.None;
                    break;
                case 1003:
                    Modes.Mouse = set ? MouseTracking.AnyEvent : MouseTracking.None;
                    break;
                case 1004:
                    break; // 焦点报告(已接受)
                case 1006:
                    Modes.MouseEncoding = set ? MouseEncoding.Sgr : MouseEncoding.Default;
                    break;
                case 1015:
                    Modes.MouseEncoding = set ? MouseEncoding.Urxvt : MouseEncoding.Default;
                    break;
                case 1047:
                    SwitchAlternate(set);
                    break;
                case 1048:
                    if (set)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }
                    break;
                case 1049:
                    SwitchAlternate(set, true);
                    break;
                case 2004:
                    Modes.BracketedPaste = set;
                    break;
            }
        }
    }

    private void SetAnsiMode(IReadOnlyList<int> p, bool set)
    {
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 4:
                    Modes.InsertMode = set;
                    break; // IRM
                case 12:
                    // SRM。语义是反的:置位(12h)= 本地回显关,复位(12l)= 本地回显开。
                    Modes.SendReceive = set;
                    break; // SRM
                case 20:
                    Modes.NewLineMode = set;
                    break; // LNM
            }
        }
    }

    // ---- 光标操作 --------------------------------------------------

    private void MoveCursor(int dx, int dy)
    {
        _pendingWrap = false;
        int y = Screen.CursorY + dy;
        if (Modes.OriginMode)
        {
            y = Math.Clamp(y, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(Screen.CursorX + dx, y);
    }

    private void SetCursorColumn(int col)
    {
        _pendingWrap = false;
        Screen.SetCursorX(col);
    }

    private void SetCursorRow(int row)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
        }
        Screen.SetCursorY(Modes.OriginMode ? Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom) : row);
    }

    private void CursorPosition(int row, int col)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
            row = Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(col, row);
    }

    private void HomeCursor()
    {
        if (Modes.OriginMode)
        {
            Screen.SetCursor(0, Screen.ScrollTop);
        }
        else
        {
            Screen.SetCursor(0, 0);
        }
    }

    private void TabForward(int count)
    {
        for (int i = 0; i < count; i++)
        {
            HorizontalTab();
        }
    }

    private void TabBackward(int count)
    {
        for (int c = 0; c < count; c++)
        {
            int x = Screen.CursorX;
            int target = 0;
            for (int i = x - 1; i > 0; i--)
            {
                if (!_tabStops[i])
                {
                    continue;
                }
                target = i;
                break;
            }
            Screen.SetCursorX(target);
        }
    }

    private void ClearTabs(int mode)
    {
        switch (mode)
        {
            case 0:
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = false;
                break;
            case 3:
                Array.Clear(_tabStops, 0, _tabStops.Length);
                break;
        }
    }

    private void SetScrollRegion(IReadOnlyList<int> p)
    {
        int top = (p.Count > 0 && p[0] > 0 ? p[0] : 1) - 1;
        int bottom = (p.Count > 1 && p[1] > 0 ? p[1] : Screen.Rows) - 1;
        Screen.SetMargins(top, bottom);
        HomeCursor();
    }

    // ---- SGR ---------------------------------------------------------------

    private void ApplySgr(IReadOnlyList<int> p)
    {
        if (p.Count == 0)
        {
            ResetPen();
            return;
        }
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    ResetPen();
                    break;
                case 1:
                    _flags |= CellFlags.Bold;
                    break;
                case 2:
                    _flags |= CellFlags.Dim;
                    break;
                case 3:
                    _flags |= CellFlags.Italic;
                    break;
                case 4:
                    _flags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _flags |= CellFlags.Blink;
                    break;
                case 7:
                    _flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _flags |= CellFlags.Invisible;
                    break;
                case 9:
                    _flags |= CellFlags.Strikethrough;
                    break;
                case 21:
                    _flags |= CellFlags.DoubleUnderline;
                    break;
                case 22:
                    _flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    break;
                case 25:
                    _flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _flags &= ~CellFlags.Invisible;
                    break;
                case 29:
                    _flags &= ~CellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _fg = TerminalColor.FromIndex(code - 30);
                    break;
                case 38:
                    i = ParseExtendedColor(p, i, ref _fg);
                    break;
                case 39:
                    _fg = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _bg = TerminalColor.FromIndex(code - 40);
                    break;
                case 48:
                    i = ParseExtendedColor(p, i, ref _bg);
                    break;
                case 49:
                    _bg = TerminalColor.Default;
                    break;
                case >= 90 and <= 97:
                    _fg = TerminalColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _bg = TerminalColor.FromIndex(code - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>解析 <c>38;5;n</c> / <c>48;5;n</c>(256 色)与 <c>38;2;r;g;b</c>(真彩色)。</summary>
    private int ParseExtendedColor(IReadOnlyList<int> p, int i, ref TerminalColor target)
    {
        if (i + 1 >= p.Count)
        {
            return i;
        }
        int kind = p[i + 1];
        switch (kind)
        {
            case 5 when i + 2 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromIndex(p[i + 2]);
                    }
                    return i + 2;
                }
            case 2 when i + 4 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromRgb((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]);
                    }
                    return i + 4;
                }
            default:
                return i + 1;
        }
    }

    private void ResetPen()
    {
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _flags = CellFlags.None;
    }

    // ---- 模式 / 复位 ------------------------------------------------------

    private void ColumnMode(bool set)
    {
        // DECCOLM:切换 132/80 列,并清空屏幕。
        int cols = set ? 132 : 80;
        Screen.EraseInDisplay(2, Blank());
        Screen.SetCursor(0, 0);
        Resize(cols, Screen.Rows);
    }

    private void SwitchAlternate(bool enable, bool saveCursor = false)
    {
        if (enable == IsAlternateScreen)
        {
            return;
        }
        // 诊断:记录每次备用屏切换(DECSET 1047/1049)。ZMODEM 传输期间本不该发生此切换,
        // 若日志显示在 sz/rz 取消前后出现 enter=true,即坐实"杂散协议字节污染终端 → 整屏消失"。
        Core.ZModem.Diagnostics.ZModemTrace.Log($"ALT-SCREEN switch enable={enable} (was {IsAlternateScreen})");
        if (enable)
        {
            // 切换前把主屏光标存入专用的备用屏槽。
            if (saveCursor)
            {
                _altSaved = CaptureCursor();
            }
            _altScreen = new(_mainScreen.Columns, _mainScreen.Rows, 0);
            _altScreen.ResetToBlank(Blank());
            IsAlternateScreen = true;
            Screen = _altScreen;
            Screen.SetCursor(0, 0);
        }
        else
        {
            _altScreen = null;
            IsAlternateScreen = false;
            Screen = _mainScreen;
            // 从专用备用屏槽恢复主屏光标——绝不从 _saved 恢复,因为
            // 备用屏应用可能已经通过 DECSC 覆盖过它。
            if (saveCursor)
            {
                ApplyCursor(_altSaved);
            }
        }
        _pendingWrap = false;
    }

    private SavedCursor CaptureCursor() =>
        new()
        {
            X = Screen.CursorX,
            Y = Screen.CursorY,
            Fg = _fg,
            Bg = _bg,
            Flags = _flags,
            Gl = _gl,
            DecGraphics = (bool[])_decGraphics.Clone(),
            OriginMode = Modes.OriginMode
        };

    private void ApplyCursor(SavedCursor? saved)
    {
        if (saved is not { } s)
        {
            Screen.SetCursor(0, 0);
            return;
        }
        Screen.SetCursor(s.X, s.Y);
        _fg = s.Fg;
        _bg = s.Bg;
        _flags = s.Flags;
        _gl = s.Gl;
        Array.Copy(s.DecGraphics, _decGraphics, _decGraphics.Length);
        Modes.OriginMode = s.OriginMode;
        _pendingWrap = false;
    }

    private void SaveCursor() => _saved = CaptureCursor();

    private void RestoreCursor() => ApplyCursor(_saved);

    private void FillScreenWithE()
    {
        var cell = new TerminalCell { Rune = 'E', Foreground = _fg, Background = _bg, Flags = _flags };
        for (int y = 0; y < Screen.Rows; y++)
            for (int x = 0; x < Screen.Columns; x++)
            {
                Screen.SetCell(x, y, cell);
            }
    }

    private void SoftReset()
    {
        Modes.Reset();
        Screen.ResetMargins();
        ResetPen();
        _gl = 0;
        Array.Clear(_decGraphics, 0, _decGraphics.Length);
        _saved = null;
        _altSaved = null;
        _pendingWrap = false;
    }

    private void FullReset()
    {
        SoftReset();
        _tabStops = BuildDefaultTabs(Screen.Columns);
        if (IsAlternateScreen)
        {
            SwitchAlternate(false);
        }
        Screen.ResetToBlank(Blank());
        Screen.ClearScrollback();
        _utf8.Reset();
        _parser.Reset();
    }

    // ---- 报告 ------------------------------------------------------------

    private void DeviceAttributes(char prefix)
    {
        switch (prefix)
        {
            case '>':
                Send(Type.SecondaryDeviceAttributes());
                break;
            case '\0':
                Send(Type.PrimaryDeviceAttributes());
                break;
        }
    }

    private void DeviceStatusReport(int p)
    {
        switch (p)
        {
            case 5:
                Send("\e[0n");
                break; // OK
            case 6:
                int row = Screen.CursorY + 1;
                int col = Screen.CursorX + 1;
                if (Modes.OriginMode)
                {
                    row = Screen.CursorY - Screen.ScrollTop + 1;
                }
                Send($"\e[{row};{col}R");
                break;
        }
    }

    private void Send(string ascii) => Response?.Invoke(Encoding.ASCII.GetBytes(ascii));

    // ---- IVtActions:OSC / DCS ---------------------------------------------

    /// <summary>
    /// OSC 52:远端程序(tmux/vim 的 yank)请求写系统剪贴板。只支持写方向;
    /// 查询("?")一律不应答,防止远端读取本地剪贴板内容(安全)。宿主控件订阅后落剪贴板。
    /// </summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>把当前画笔状态编码为 SGR 参数串(DECRQSS "m" 应答用),始终以 0 开头。</summary>
    private string BuildSgrReport()
    {
        var sb = new StringBuilder("0");
        if ((_flags & CellFlags.Bold) != 0)
        {
            sb.Append(";1");
        }
        if ((_flags & CellFlags.Dim) != 0)
        {
            sb.Append(";2");
        }
        if ((_flags & CellFlags.Italic) != 0)
        {
            sb.Append(";3");
        }
        if ((_flags & CellFlags.Underline) != 0)
        {
            sb.Append(";4");
        }
        if ((_flags & CellFlags.Blink) != 0)
        {
            sb.Append(";5");
        }
        if ((_flags & CellFlags.Inverse) != 0)
        {
            sb.Append(";7");
        }
        if ((_flags & CellFlags.Invisible) != 0)
        {
            sb.Append(";8");
        }
        if ((_flags & CellFlags.Strikethrough) != 0)
        {
            sb.Append(";9");
        }
        AppendSgrColor(sb, _fg, true);
        AppendSgrColor(sb, _bg, false);
        return sb.ToString();
    }

    private static void AppendSgrColor(StringBuilder sb, TerminalColor color, bool isForeground)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (color.Kind)
        {
            case TerminalColorKind.Indexed when color.Index < 8:
                sb.Append(';').Append((isForeground ? 30 : 40) + color.Index);
                break;
            case TerminalColorKind.Indexed when color.Index < 16:
                sb.Append(';').Append((isForeground ? 90 : 100) + color.Index - 8);
                break;
            case TerminalColorKind.Indexed:
                sb.Append(isForeground ? ";38;5;" : ";48;5;").Append(color.Index);
                break;
            case TerminalColorKind.Rgb:
                sb.Append(isForeground ? ";38;2;" : ";48;2;")
                  .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B);
                break;
                // Default:SGR 0 已覆盖,无需追加。
        }
    }

    private struct SavedCursor
    {
        public int X, Y;
        public TerminalColor Fg, Bg;
        public CellFlags Flags;
        public int Gl;
        public bool[] DecGraphics;
        public bool OriginMode;
    }
}

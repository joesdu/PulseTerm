namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端运行模式(ANSI 与 DEC 私有模式)。默认值与刚复位的 xterm 一致:
/// 自动换行开启、光标可见,其余均关闭。
/// </summary>
public sealed class TerminalModes
{
    // DEC 私有模式
    /// <summary>光标键应用模式(DECCKM ?1):方向键发送应用序列而非普通序列。</summary>
    public bool ApplicationCursorKeys; // DECCKM  ?1
    /// <summary>小键盘应用模式(DECKPAM / DECKPNM):小键盘发送应用序列。</summary>
    public bool ApplicationKeypad;     // DECKPAM / DECKPNM
    /// <summary>自动换行模式(DECAWM ?7):行尾字符自动折行,默认开启。</summary>
    public bool AutoWrap = true;       // DECAWM  ?7
    /// <summary>括号粘贴模式(?2004):粘贴内容以转义序列包裹。</summary>
    public bool BracketedPaste;        // ?2004
    /// <summary>光标闪烁(?12):是否让光标闪烁,默认开启。</summary>
    public bool CursorBlink = true;    // ?12
    /// <summary>
    /// 光标形状(DECSCUSR <c>CSI Ps SP q</c>):0/1=闪烁块、2=稳定块、3=闪烁下划线、
    /// 4=稳定下划线、5=闪烁竖线、6=稳定竖线。渲染仍按用户设置(见 VelaTerminalControl.CursorStyle),
    /// 这里只保留远端设过的值,以便 DECRQSS " q" 能如实回报 —— vim 会用它决定退出时恢复成什么形状。
    /// </summary>
    public int CursorStyle;            // DECSCUSR (CSI Ps SP q)
    /// <summary>光标可见(DECTCEM ?25):是否显示光标,默认可见。</summary>
    public bool CursorVisible = true;  // DECTCEM ?25

    // ANSI 模式
    /// <summary>插入模式(IRM 4):新字符插入而非覆盖现有字符。</summary>
    public bool InsertMode; // IRM  (4)

    // 鼠标跟踪(?1000/1002/1003)与编码(?1006 SGR / ?1015 urxvt)
    /// <summary>鼠标事件跟踪模式(?1000/1002/1003)。</summary>
    public MouseTracking Mouse = MouseTracking.None;
    /// <summary>鼠标坐标编码方式(?1006 SGR / ?1015 urxvt)。</summary>
    public MouseEncoding MouseEncoding = MouseEncoding.Default;
    /// <summary>换行模式(LNM 20):回车同时执行换行。</summary>
    public bool NewLineMode;  // LNM  (20)
    /// <summary>原点模式(DECOM ?6):光标定位相对于滚动区域顶部。</summary>
    public bool OriginMode;   // DECOM   ?6
    /// <summary>反显模式(DECSCNM ?5):整屏前景色与背景色反转。</summary>
    public bool ReverseVideo; // DECSCNM ?5

    /// <summary>
    /// 发送/接收模式(SRM 12):**置位(<c>CSI 12 h</c>,默认)表示本地回显关闭** ——
    /// 终端只把键入发往主机,由主机回显。复位(<c>CSI 12 l</c>)则要求终端本地回显。
    /// 注意语义是反的:<c>SendReceive == true</c> 意味着**不**本地回显。
    /// 默认 true 与 SSH 场景一致(远端 shell 自己回显,本地再回显会出现双字符)。
    /// </summary>
    public bool SendReceive = true; // SRM (12)

    /// <summary>将所有模式恢复为终端复位后的默认状态。</summary>
    public void Reset()
    {
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        OriginMode = false;
        AutoWrap = true;
        ReverseVideo = false;
        CursorVisible = true;
        BracketedPaste = false;
        CursorBlink = true;
        CursorStyle = 0;
        Mouse = MouseTracking.None;
        MouseEncoding = MouseEncoding.Default;
        InsertMode = false;
        NewLineMode = false;
        SendReceive = true;
    }
}

/// <summary>鼠标跟踪模式,决定终端上报哪些鼠标事件。</summary>
public enum MouseTracking
{
    /// <summary>不跟踪鼠标事件。</summary>
    None,
    /// <summary>X10 兼容模式(?9):仅上报按键按下。</summary>
    X10,         // ?9
    /// <summary>普通跟踪模式(?1000):上报按键按下与释放。</summary>
    Normal,      // ?1000
    /// <summary>按键事件跟踪(?1002):按住并移动时上报拖动事件。</summary>
    ButtonEvent, // ?1002
    /// <summary>任意事件跟踪(?1003):上报所有鼠标移动。</summary>
    AnyEvent     // ?1003
}

/// <summary>鼠标事件坐标的编码格式。</summary>
public enum MouseEncoding
{
    /// <summary>默认 X10 编码:坐标以单字节偏移量表示。</summary>
    Default,
    /// <summary>SGR 编码(?1006):以十进制文本上报坐标与按键状态。</summary>
    Sgr,  // ?1006
    /// <summary>urxvt 编码(?1015):以十进制文本上报坐标。</summary>
    Urxvt // ?1015
}

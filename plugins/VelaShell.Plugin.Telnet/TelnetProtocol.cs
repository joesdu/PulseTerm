namespace VelaShell.Plugin.Telnet;

/// <summary>
/// RFC 854 及其配套 RFC 的协议常量。数值逐条对照 RFC 原文确认,**不要凭记忆改动**。
/// </summary>
internal static class TelnetProtocol
{
    /// <summary>命令引导字节(Interpret As Command)。数据流里字面的 0xFF 必须双写。</summary>
    public const byte Iac = 255;

    /// <summary>子协商结束。</summary>
    public const byte Se = 240;

    /// <summary>子协商开始。</summary>
    public const byte Sb = 250;

    /// <summary>"我要启用"。</summary>
    public const byte Will = 251;

    /// <summary>"我不启用 / 我停用"。</summary>
    public const byte Wont = 252;

    /// <summary>"请你启用"。</summary>
    public const byte Do = 253;

    /// <summary>"请你别启用 / 请你停用"。</summary>
    public const byte Dont = 254;

    // ---- 选项码 ----

    /// <summary>RFC 856 BINARY:8 位透明传输(按方向分别协商)。</summary>
    public const byte OptionBinary = 0;

    /// <summary>RFC 857 ECHO:由对端回显。</summary>
    public const byte OptionEcho = 1;

    /// <summary>RFC 858 SUPPRESS-GO-AHEAD:抑制 GA,与 ECHO 同时生效才是逐字符模式。</summary>
    public const byte OptionSuppressGoAhead = 3;

    /// <summary>RFC 1091 TERMINAL-TYPE:上报 TERM。</summary>
    public const byte OptionTerminalType = 24;

    /// <summary>RFC 1073 NAWS:上报窗口行列。</summary>
    public const byte OptionNaws = 31;

    /// <summary>TERMINAL-TYPE 子协商:IS。</summary>
    public const byte TerminalTypeIs = 0;

    /// <summary>TERMINAL-TYPE 子协商:SEND。</summary>
    public const byte TerminalTypeSend = 1;
}

/// <summary>用户按下回车时线上发什么(RFC 854 的 NVT 里裸 CR 非法)。</summary>
internal enum TelnetEnterMode
{
    /// <summary>CR LF —— RFC 1123 §3.3.1 建议的默认。</summary>
    CrLf,

    /// <summary>CR NUL —— NVT 里"只回车不换行"的合法写法,少数老设备要这个。</summary>
    CrNul,

    /// <summary>裸 CR —— 不改写,交给对端自己处置(与 BINARY 模式等价的行为)。</summary>
    Cr
}

/// <summary>本地回显策略。</summary>
internal enum TelnetLocalEcho
{
    /// <summary>自动:对端启用了 ECHO 就不回显,否则本地回显。</summary>
    Auto,

    /// <summary>始终本地回显。</summary>
    On,

    /// <summary>从不本地回显。</summary>
    Off
}

/// <summary>一条 Telnet 会话的协商与改写策略(来自连接表单)。</summary>
/// <param name="TerminalType">上报给对端的 TERM。</param>
/// <param name="EnterMode">回车改写方式。</param>
/// <param name="RequestBinary">是否请求 BINARY(8 位透明);关掉它 UTF-8 与 ZMODEM 都可能被打断。</param>
/// <param name="LocalEcho">本地回显策略。</param>
internal readonly record struct TelnetConfig(
    string TerminalType,
    TelnetEnterMode EnterMode,
    bool RequestBinary,
    TelnetLocalEcho LocalEcho);

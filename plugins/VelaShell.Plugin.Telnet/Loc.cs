namespace VelaShell.Plugin.Telnet;

/// <summary>
/// 插件自带的文案表(理由同 S3 插件:领域词汇随插件走,宿主不替一个自己不认识的协议背词典)。
/// 只带英文与简体中文两套,其余语言回落英文。
/// </summary>
/// <param name="locale">宿主当前语言(如 <c>zh-Hans</c>、<c>en</c>)。</param>
internal sealed class Loc(string locale)
{
    private readonly bool _chinese = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取一条文案;未收录的键原样返回(方便一眼看出漏了哪条)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string this[string key] =>
        (_chinese ? Chinese : English).TryGetValue(key, out string? value) ? value : key;

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Telnet_Host"] = "Host / IP",
        ["Telnet_HostPlaceholder"] = "192.168.1.100",
        ["Telnet_TerminalType"] = "Terminal type (TERM)",
        ["Telnet_TerminalTypeHint"] = "Reported through TERMINAL-TYPE negotiation. Leave empty to follow the app's terminal setting; old gear may need vt100.",
        ["Telnet_EnterMode"] = "Enter key sends",
        ["Telnet_EnterModeHint"] = "RFC 854 forbids a bare CR outside binary mode. Ignored once 8-bit (BINARY) mode is negotiated — bytes then go out untouched.",
        ["Telnet_EnterCrLf"] = "CR LF (recommended)",
        ["Telnet_EnterCrNul"] = "CR NUL",
        ["Telnet_EnterCr"] = "CR (no rewrite)",
        ["Telnet_Binary"] = "Request 8-bit (BINARY) mode",
        ["Telnet_BinaryHint"] = "Needed for UTF-8 and ZMODEM to survive intact. Turn it off only for gear that refuses the option.",
        ["Telnet_LocalEcho"] = "Local echo",
        ["Telnet_LocalEchoHint"] = "Auto echoes locally only while the server has not taken over echoing (ECHO option), so typing is never invisible.",
        ["Telnet_EchoAuto"] = "Auto",
        ["Telnet_EchoOn"] = "Always on",
        ["Telnet_EchoOff"] = "Always off",
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["Telnet_Host"] = "主机名 / IP",
        ["Telnet_HostPlaceholder"] = "192.168.1.100",
        ["Telnet_TerminalType"] = "终端类型(TERM)",
        ["Telnet_TerminalTypeHint"] = "经 TERMINAL-TYPE 协商上报。留空跟随应用的终端类型设置;老设备可能只认 vt100。",
        ["Telnet_EnterMode"] = "回车键发送",
        ["Telnet_EnterModeHint"] = "RFC 854 规定非二进制模式下裸 CR 非法。谈成 8 位(BINARY)后此项不生效 —— 那时字节原样发出。",
        ["Telnet_EnterCrLf"] = "CR LF(推荐)",
        ["Telnet_EnterCrNul"] = "CR NUL",
        ["Telnet_EnterCr"] = "CR(不改写)",
        ["Telnet_Binary"] = "请求 8 位(BINARY)模式",
        ["Telnet_BinaryHint"] = "UTF-8 与 ZMODEM 要完整传输就靠它;只有对端拒绝该选项时才关掉。",
        ["Telnet_LocalEcho"] = "本地回显",
        ["Telnet_LocalEchoHint"] = "自动:仅在对端未接管回显(ECHO 选项)时本地回显,保证不会出现「打字看不见」。",
        ["Telnet_EchoAuto"] = "自动",
        ["Telnet_EchoOn"] = "始终开启",
        ["Telnet_EchoOff"] = "始终关闭",
    };
}

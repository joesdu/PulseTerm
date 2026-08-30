using System.Text;

namespace VelaShell.Terminal.Input;

/// <summary>
/// 旁路跟踪用户正在终端里键入的命令行(命令补全/历史建议的数据来源,plan.md #16)。
/// 终端本身不知道 shell 的行缓冲,这里以"监听发往 PTY 的用户输入字节"重建它:
/// 可打印字符追加、退格回删、Enter/换行提交、Ctrl+C/Ctrl+U 清空。
/// 凡是本地无法推演结果的编辑(方向键召回历史、Tab 补全、F 键等 ESC 序列)进入
/// <b>未知态</b>(<see cref="CurrentInput" /> = null):整行内容不可知,不允许按整行
/// 建议、也不允许把提交计入历史。但未知态不"粘死"——期间继续键入的连续字符段以
/// <see cref="TentativeRun" /> 暴露,供降级为"追加式词补全"(只回删/追加自己看着
/// 键入的字符,永远安全),直到 Enter/Ctrl+C/Ctrl+U 把行重置为确定的空。
/// </summary>
public sealed class TerminalInputTracker
{
    private enum EscState
    {
        None,

        /// <summary>刚收到 ESC,等待判别序列类型。</summary>
        Esc,

        /// <summary>CSI 序列(ESC [ ...),吞到终结字节 0x40-0x7E 为止。</summary>
        Csi,

        /// <summary>SS3 序列(ESC O X),再吞一个字节。</summary>
        Ss3
    }

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _tentative = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private EscState _esc;
    private bool _unknown;

    /// <summary>当前推演出的完整行内容;null = 未知态(发生过本地无法跟踪的编辑)。</summary>
    public string? CurrentInput => _unknown ? null : _buffer.ToString();

    /// <summary>
    /// 未知态下自最后一个控制键以来连续键入的字符段(确定态下为空串)。
    /// 这些字符必然紧贴光标之前,可安全地做追加式补全与等量回删。
    /// </summary>
    public string TentativeRun => _unknown ? _tentative.ToString() : string.Empty;

    /// <summary>
    /// 行内容(或未知态/试探段)变化时触发,在输入线程(UI 线程)上同步回调。
    /// Ctrl+C / Ctrl+U 这类「取消当前行」无条件触发,即使行本来就是确定的空行 ——
    /// 消费方(补全弹层)要靠它收口,只比较字面内容会漏掉空行上的取消。
    /// </summary>
    public event Action? InputChanged;

    /// <summary>
    /// 用户按 Enter 提交了一行(仅确定态且非空时触发,参数为提交内容)。
    /// 消费方仍应做回显校验后再入历史(密码输入无回显,不得记录)。
    /// </summary>
    public event Action<string>? CommandSubmitted;

    /// <summary>
    /// 未知态下按了 Enter:行内容本地不可知,但消费方可改从屏幕的光标行提取命令
    /// (提示符之后的文本),否则"按过一次方向键的命令"永远进不了历史。
    /// </summary>
    public event Action? UnknownLineSubmitted;

    /// <summary>处理一段发往 PTY 的用户输入字节(与 TypedInput 事件的粒度一致)。</summary>
    public void Process(byte[] data)
    {
        bool changed = false;
        foreach (byte b in data)
        {
            // ESC 序列(方向键/F 键/括号粘贴标记)整条吞掉:序列尾部是可打印字节
            // (如 F10 的 "[21~"),绝不能漏进试探段。
            if (_esc != EscState.None)
            {
                ConsumeEscapeByte(b);
                continue;
            }
            switch (b)
            {
                case 0x0D or 0x0A: // Enter(\r)/换行(\n,注入与粘贴):提交并回到确定的空行。
                    if (!_unknown && _buffer.Length > 0)
                    {
                        CommandSubmitted?.Invoke(_buffer.ToString());
                    }
                    else if (_unknown)
                    {
                        UnknownLineSubmitted?.Invoke();
                    }
                    changed |= ResetToKnownEmpty();
                    break;
                case 0x7F or 0x08: // Backspace / DEL:从当前活动缓冲回删一个字符。
                    StringBuilder target = _unknown ? _tentative : _buffer;
                    if (target.Length > 0)
                    {
                        RemoveLastCharacter(target);
                        changed = true;
                    }
                    break;
                case 0x03 or 0x15: // Ctrl+C / Ctrl+U:shell 把行清掉,回到确定的空行。
                    ResetToKnownEmpty();
                    // 无条件记为「变化」:行本就是确定的空行时字面内容确实没变,但
                    // 「取消当前行」是消费方必须收到的一拍 —— 补全弹层靠 InputChanged
                    // 收口,吞掉这一拍就会出现「空行 Alt+Enter 召出全量面板后 Ctrl+C
                    // 关不掉」(#315)。
                    changed = true;
                    break;
                case 0x1B: // ESC:进入序列吞噬模式,整行不可知。
                    changed |= MarkUnknown();
                    _esc = EscState.Esc;
                    break;
                default:
                    if (b < 0x20)
                    {
                        // 其余控制字节(Tab 补全、Ctrl+W/A/E...):整行不可知;
                        // 已在未知态时也要重置试探段(新一轮编辑开始)。
                        changed |= MarkUnknown();
                    }
                    else
                    {
                        changed |= AppendByte(b);
                    }
                    break;
            }
        }
        // 按键编码器产生的转义序列永远完整地落在同一个输入包里;包结束仍悬置的
        // 序列状态只可能是用户孤立按下的 Esc 键(vim 等场景),复位以免吞掉下一键。
        _esc = EscState.None;
        if (changed)
        {
            InputChanged?.Invoke();
        }
    }

    private void ConsumeEscapeByte(byte b)
    {
        switch (_esc)
        {
            case EscState.Esc:
                _esc = b switch
                {
                    (byte)'[' => EscState.Csi,
                    (byte)'O' => EscState.Ss3,
                    0x1B => EscState.Esc, // ESC ESC:继续等待判别。
                    _ => EscState.None    // ESC+ch(Alt 修饰字符):连同该字符一起吞掉。
                };
                break;
            case EscState.Csi:
                if (b is >= 0x40 and <= 0x7E)
                {
                    _esc = EscState.None; // CSI 终结字节。
                }
                break;
            case EscState.Ss3:
            default:
                _esc = EscState.None;
                break;
        }
    }

    private bool ResetToKnownEmpty()
    {
        bool changed = _unknown || _buffer.Length > 0;
        _unknown = false;
        _buffer.Clear();
        _tentative.Clear();
        _decoder.Reset();
        return changed;
    }

    private bool MarkUnknown()
    {
        bool changed = !_unknown || _tentative.Length > 0;
        _unknown = true;
        _buffer.Clear();
        _tentative.Clear();
        _decoder.Reset();
        return changed;
    }

    private bool AppendByte(byte b)
    {
        Span<char> chars = stackalloc char[2];
        int produced = _decoder.GetChars([b], chars, false);
        StringBuilder target = _unknown ? _tentative : _buffer;
        for (int i = 0; i < produced; i++)
        {
            target.Append(chars[i]);
        }
        return produced > 0;
    }

    private static void RemoveLastCharacter(StringBuilder buffer)
    {
        // shell 的一次 DEL 按"字符"回删;代理对(emoji 等)按整字删除。
        int remove = buffer.Length >= 2 && char.IsLowSurrogate(buffer[^1]) && char.IsHighSurrogate(buffer[^2]) ? 2 : 1;
        buffer.Length -= remove;
    }
}

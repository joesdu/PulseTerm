using System.Diagnostics;
using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 一个兼容 DEC 的转义序列解析器,实现了 Paul Williams
/// (https://vt100.net/emu/dec_ansi_parser)所描述的那个状态机。它消费一串 Unicode 标量值
/// (UTF-8 在上游已解码,使多字节文本不会与 7 位控制字符集冲突),并把语义事件分发给
/// <see cref="IVtActions" /> 接收端。另提供一条专用的 VT52 兼容路径,因为 VT52 使用的是
/// 一套不同的、非 CSI 的转义文法。
/// </summary>
public sealed class VtParser(IVtActions actions)
{
    private const int MaxParams = 32;

    /// <summary>
    /// OSC / DCS 字符串载荷的字符数上限。越限即判定为「没有终结符的坏序列」:丢弃载荷、
    /// 回到 ground。
    /// <para>
    /// 没有上限时,一条不带终结符的 OSC(坏掉的提示符脚本忘了发 BEL/ST,或 <c>cat</c> 到
    /// 二进制里的 <c>1B 5D</c>)会把其后<b>全部输出</b>吸进这个 StringBuilder —— 内存无限
    /// 增长,屏幕一直死着,而终结符可能永远不来。
    /// </para>
    /// <para>
    /// 取 128 KiB:合法 OSC 远低于此,含最大的 OSC 52 剪贴板写入
    /// (<c>TerminalEmulator.MaxOsc52Bytes</c> = 64 KiB 解码后,base64 约 87 KiB)。
    /// </para>
    /// </summary>
    private const int MaxStringPayload = 128 * 1024;

    /// <summary>
    /// 中间字节个数上限(与 xterm 一致)。合法序列最多 1 个(<c>CSI ! p</c>、<c>CSI SP q</c>
    /// 之类),超出即为畸形;不设限时 <c>ESC [</c> 后跟一长串空格(0x20 也是中间字节)
    /// 同样能撑爆缓冲。
    /// </summary>
    private const int MaxIntermediates = 2;

    private readonly StringBuilder _intermediates = new(4);
    private readonly StringBuilder _oscOrDcs = new(64);

    private readonly List<int> _params = [with(MaxParams)];
    private int _currentParam;

    private char _dcsFinal;
    private bool _hasCurrentParam;
    private char _prefix;
    private State _state = State.Ground;
    private int _vt52Row;

    /// <summary>为 true 时,解析器使用 VT52 转义文法来解释输入。</summary>
    public bool Vt52Mode { get; set; }

    /// <summary>把解析器重置为 ground 状态,丢弃任何已部分收集到的序列。</summary>
    public void Reset()
    {
        _state = State.Ground;
        ClearParams();
        _intermediates.Clear();
        _oscOrDcs.Clear();
        _prefix = '\0';
    }

    /// <summary>
    /// 把一段已解码的终端输出喂入状态机,分发对应动作。
    /// Span 重载是输出热路径(<c>TerminalEmulator.Feed</c> 每帧调用),避免中间 string 物化。
    /// </summary>
    public void Parse(ReadOnlySpan<char> text)
    {
        // 逐 Unicode 标量值遍历,使代理对作为一个 rune 整体提交。
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                Consume(char.ConvertToUtf32(c, text[i + 1]));
                i++;
            }
            else
            {
                Consume(c);
            }
        }
    }

    /// <summary>把一段已解码的终端输出喂入状态机,分发对应动作。</summary>
    public void Parse(string text) => Parse(text.AsSpan());

    private void Consume(int rune)
    {
        if (Vt52Mode && _state is State.Ground or State.Vt52Escape or State.Vt52CursorRow or State.Vt52CursorCol)
        {
            ConsumeVt52(rune);
            return;
        }
        switch (rune)
        {
            // CAN 与 SUB 会中止任何正在进行的序列。
            case 0x18 or 0x1A:
                _state = State.Ground;
                ClearParams();
                _intermediates.Clear();
                return;
            // ESC 会从大多数状态重新开始一个序列。OSC/DCS 是例外:它们的终结符 ST 就是 ESC \,
            // 因此收集到的载荷必须在此处分发——否则以 ST(而非 BEL)结尾的字符串会被静默丢弃。
            case 0x1B:
                {
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (_state == State.OscString)
                    {
                        DispatchOsc();
                    }
                    else if (_state == State.DcsPassthrough)
                    {
                        DispatchDcs();
                    }
                    EnterEscape();
                    return;
                }
            default:
                switch (_state)
                {
                    case State.Ground:
                        Ground(rune);
                        break;
                    case State.Escape:
                        Escape(rune);
                        break;
                    case State.EscapeIntermediate:
                        EscapeIntermediate(rune);
                        break;
                    case State.CsiEntry:
                        CsiEntry(rune);
                        break;
                    case State.CsiParam:
                        CsiParam(rune);
                        break;
                    case State.CsiIntermediate:
                        CsiIntermediate(rune);
                        break;
                    case State.CsiIgnore:
                        CsiIgnore(rune);
                        break;
                    case State.OscString:
                        OscString(rune);
                        break;
                    case State.DcsEntry:
                        DcsEntry(rune);
                        break;
                    case State.DcsParam:
                        DcsParam(rune);
                        break;
                    case State.DcsIntermediate:
                        DcsIntermediate(rune);
                        break;
                    case State.DcsPassthrough:
                        DcsPassthrough(rune);
                        break;
                    case State.DcsIgnore:
                        DcsIgnore(rune);
                        break;
                    case State.SosPmApcString:
                        SosPmApcString(rune);
                        break;
                    case State.Vt52Escape:
                    case State.Vt52CursorRow:
                    case State.Vt52CursorCol:
                        break;
                    default:
                        throw new UnreachableException($"Unhandled VT parser state {_state}.");
                }
                break;
        }
    }

    // ---- Ground -------------------------------------------------------------

    private void Ground(int rune)
    {
        if (IsC0(rune) || rune == 0x7F)
        {
            actions.Execute((char)rune);
            return;
        }
        actions.Print(rune);
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        ClearParams();
        _intermediates.Clear();
        _prefix = '\0';
        _oscOrDcs.Clear();
    }

    private void Escape(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        if (rune is >= 0x20 and <= 0x2F) // 中间字节
        {
            _intermediates.Append((char)rune);
            _state = State.EscapeIntermediate;
            return;
        }
        switch (rune)
        {
            case '[':
                _state = State.CsiEntry;
                return;
            case ']':
                _state = State.OscString;
                _oscOrDcs.Clear();
                return;
            case 'P':
                _state = State.DcsEntry;
                return;
            case 'X' or '^' or '_': // SOS / PM / APC 字符串
                _state = State.SosPmApcString;
                return;
        }
        if (rune is >= 0x30 and <= 0x7E)
        {
            actions.EscDispatch(_intermediates.ToString(), (char)rune);
        }
        _state = State.Ground;
    }

    private void EscapeIntermediate(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                // ESC 没有 ignore 态,超限就只是不再收集(EscDispatch 只看 intermediates[0],
                // 而带 3 个以上中间字节的 ESC 序列现实中不存在):缓冲有界即可。
                _ = CollectIntermediate(rune);
                return;
            case >= 0x30 and <= 0x7E:
                actions.EscDispatch(_intermediates.ToString(), (char)rune);
                break;
        }
        _state = State.Ground;
    }

    // ---- CSI ----------------------------------------------------------------

    private void CsiEntry(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            // < = > ?
            case >= 0x3C and <= 0x3F:
                _prefix = (char)rune;
                _state = State.CsiParam;
                return;
            case >= 0x30 and <= 0x39:
            case ';':
            case ':':
                HandleParamDigit(rune);
                _state = State.CsiParam;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.CsiIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiParam(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x30 and <= 0x39 or ';' or ':':
                HandleParamDigit(rune);
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.CsiIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiIntermediate(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                if (!CollectIntermediate(rune))
                {
                    _state = State.CsiIgnore;
                }
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiIgnore(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        if (rune is >= 0x40 and <= 0x7E)
        {
            _state = State.Ground;
        }
    }

    // ---- OSC ----------------------------------------------------------------

    private void OscString(int rune)
    {
        switch (rune)
        {
            // 此处以 BEL(0x07)结尾,或在 Consume 的全局 ESC 分支中以 ST(ESC \) 结尾——
            // ESC 永远不会到达本处理函数。
            case 0x07:
                DispatchOsc();
                _state = State.Ground;
                return;
            case >= 0x20:
                AppendStringPayload(rune);
                break;
        }
    }

    /// <summary>
    /// 往 OSC/DCS 载荷追加一个字符;越过 <see cref="MaxStringPayload" /> 即中止整条序列
    /// (丢弃载荷并回到 ground,理由见该常量注释)。
    /// </summary>
    private void AppendStringPayload(int rune)
    {
        if (_oscOrDcs.Length >= MaxStringPayload)
        {
            Reset(); // 丢弃载荷、回 ground:其后的字节照常显示,终端自己缓过来。
            return;
        }
        _oscOrDcs.Append(char.ConvertFromUtf32(rune));
    }

    /// <summary>
    /// 收集一个中间字节;已达 <see cref="MaxIntermediates" /> 时返回 false —— 调用方据此把
    /// 整条序列判为畸形,转入对应的 ignore 态(继续吞到终结字节,但不分发)。
    /// </summary>
    private bool CollectIntermediate(int rune)
    {
        if (_intermediates.Length >= MaxIntermediates)
        {
            return false;
        }
        _intermediates.Append((char)rune);
        return true;
    }

    private void DispatchOsc()
    {
        string[] parts = _oscOrDcs.ToString().Split(';');
        actions.OscDispatch(parts);
        _oscOrDcs.Clear();
    }

    // ---- DCS ----------------------------------------------------------------

    private void DcsEntry(int rune)
    {
        switch (rune)
        {
            case >= 0x3C and <= 0x3F:
                _prefix = (char)rune;
                _state = State.DcsParam;
                return;
            case >= 0x30 and <= 0x39:
            case ';':
            case ':':
                HandleParamDigit(rune);
                _state = State.DcsParam;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.DcsIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsParam(int rune)
    {
        switch (rune)
        {
            case >= 0x30 and <= 0x39 or ';' or ':':
                HandleParamDigit(rune);
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.DcsIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsIntermediate(int rune)
    {
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                if (!CollectIntermediate(rune))
                {
                    _state = State.DcsIgnore;
                }
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsPassthrough(int rune)
    {
        switch (rune)
        {
            // ST(ESC \) 由 Consume 的全局 ESC 分支处理——ESC 永远不会到达此处。
            case 0x07:
                DispatchDcs();
                _state = State.Ground;
                return;
            case >= 0x20:
            case 0x09:
            case 0x0A:
            case 0x0D:
                AppendStringPayload(rune);
                break;
        }
    }

    private void DcsIgnore(int rune)
    {
        if (rune == 0x1B)
        {
            EnterEscape();
        }
    }

    private void DispatchDcs()
    {
        actions.DcsDispatch(_prefix, _params, _intermediates.ToString(), _dcsFinal, _oscOrDcs.ToString());
        _oscOrDcs.Clear();
    }

    private void SosPmApcString(int rune)
    {
        switch (rune)
        {
            // 一直消费到 ST/BEL 为止;内容被忽略。
            case 0x1B:
                EnterEscape();
                return;
            case 0x07:
                _state = State.Ground;
                break;
        }
    }

    // ---- VT52 ---------------------------------------------------------------

    private void ConsumeVt52(int rune)
    {
        switch (_state)
        {
            case State.Ground:
                if (rune == 0x1B)
                {
                    _state = State.Vt52Escape;
                    return;
                }
                if (IsC0(rune) || rune == 0x7F)
                {
                    actions.Execute((char)rune);
                    return;
                }
                actions.Print(rune);
                return;
            case State.Vt52Escape:
                if (rune == 'Y')
                {
                    _state = State.Vt52CursorRow;
                    return;
                }
                // 将其余所有 VT52 命令作为一个无中间字节的 ESC 分发交付。
                actions.EscDispatch(string.Empty, (char)rune);
                _state = State.Ground;
                return;
            case State.Vt52CursorRow:
                _vt52Row = rune - 0x20;
                _state = State.Vt52CursorCol;
                return;
            case State.Vt52CursorCol:
                _params.Clear();
                _params.Add(_vt52Row + 1);
                _params.Add(rune - 0x20 + 1);
                actions.CsiDispatch('\0', _params, string.Empty, 'H');
                _state = State.Ground;
                return;
            case State.Escape:
            case State.EscapeIntermediate:
            case State.CsiEntry:
            case State.CsiParam:
            case State.CsiIntermediate:
            case State.CsiIgnore:
            case State.OscString:
            case State.DcsEntry:
            case State.DcsParam:
            case State.DcsIntermediate:
            case State.DcsPassthrough:
            case State.DcsIgnore:
            case State.SosPmApcString:
                break;
            default:
                throw new UnreachableException($"Unhandled VT parser state {_state}.");
        }
    }

    // ---- 参数辅助方法 ------------------------------------------------------

    private void HandleParamDigit(int rune)
    {
        if (rune is ';' or ':')
        {
            FinishParam();
            return;
        }
        if (_params.Count >= MaxParams)
        {
            return;
        }
        _hasCurrentParam = true;
        _currentParam = _currentParam * 10 + (rune - '0');
        if (_currentParam > 65535)
        {
            _currentParam = 65535;
        }
    }

    private void FinishParam()
    {
        _params.Add(_hasCurrentParam ? _currentParam : 0);
        _currentParam = 0;
        _hasCurrentParam = false;
    }

    private void ClearParams()
    {
        _params.Clear();
        _currentParam = 0;
        _hasCurrentParam = false;
    }

    private static bool IsC0(int rune) => rune is >= 0x00 and <= 0x1F;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,

        // VT52 子状态
        Vt52Escape,
        Vt52CursorRow,
        Vt52CursorCol
    }
}

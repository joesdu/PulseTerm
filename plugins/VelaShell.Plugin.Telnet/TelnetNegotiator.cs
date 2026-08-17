using System.Text;

namespace VelaShell.Plugin.Telnet;

/// <summary>
/// Telnet 选项协商与 IAC 编解码的纯状态机(不碰套接字,因此可以逐字节单测)。
/// <para>
/// 三件事必须一处做全,漏一处的表现都是"平时都好,偶尔莫名其妙":
/// </para>
/// <list type="number">
///   <item><b>IAC 双写/还原覆盖所有字节路径</b> —— 出方向的字面 0xFF 发成 <c>IAC IAC</c>,
///     入方向还原。漏了它,输出里恰好出现 0xFF 时(二进制、UTF-8 尾字节、ZMODEM 帧)
///     后续字节会被当成命令吃掉。</item>
///   <item><b>子协商载荷里的 0xFF 同样要双写</b> —— NAWS 只在窗口宽或高恰为 255
///     (或 65280+)时才踩到,几乎必然被漏掉。</item>
///   <item><b>只在状态真的变化时回应</b> —— RFC 854 的要求。对已生效的选项再回一次
///     WILL/DO 就是协商回环,两端能一直对着刷。</item>
/// </list>
/// </summary>
/// <param name="config">协商策略(TERM、回车模式、是否要 BINARY)。</param>
internal sealed class TelnetNegotiator(TelnetConfig config)
{
    /// <summary>入方向解析状态。</summary>
    private enum State
    {
        Data,
        Iac,
        Negotiate,
        SubnegotiationStart,
        Subnegotiation,
        SubnegotiationIac
    }

    // 本端(我们 WILL 了什么)与对端(我们 DO 了什么)的启用状态。
    private readonly HashSet<byte> _localEnabled = [];
    private readonly HashSet<byte> _remoteEnabled = [];
    private readonly List<byte> _subnegotiation = [];

    private State _state = State.Data;
    private byte _command;
    private bool _lastWasCarriageReturn;

    /// <summary>当前窗口列数(NAWS 生效后每次变化都会重发)。</summary>
    private int _columns = 80;

    /// <summary>当前窗口行数。</summary>
    private int _rows = 24;

    /// <summary>对端是否已启用 ECHO(它负责回显我们的输入)。</summary>
    public bool RemoteEcho => _remoteEnabled.Contains(TelnetProtocol.OptionEcho);

    /// <summary>对端是否已启用 SUPPRESS-GO-AHEAD。</summary>
    public bool RemoteSuppressGoAhead => _remoteEnabled.Contains(TelnetProtocol.OptionSuppressGoAhead);

    /// <summary>出方向是否已进入 BINARY(8 位透明:不再改写回车)。</summary>
    public bool BinaryOutbound => _localEnabled.Contains(TelnetProtocol.OptionBinary);

    /// <summary>入方向是否已进入 BINARY(不再做 CR NUL → CR 归一)。</summary>
    public bool BinaryInbound => _remoteEnabled.Contains(TelnetProtocol.OptionBinary);

    /// <summary>NAWS 是否已启用(对端 DO 过)。</summary>
    public bool NawsEnabled => _localEnabled.Contains(TelnetProtocol.OptionNaws);

    /// <summary>
    /// 连接建立后主动发出的第一批协商。
    /// <para>
    /// 只发**我们真正想要**的四项(+ 可选 BINARY):TERMINAL-TYPE 与 NAWS 由我们提供,
    /// ECHO 与 SGA 要对端提供 —— RFC 858 明确这两项同时生效才是"逐字符回显",
    /// 也就是 vim/htop 能工作的前提。
    /// </para>
    /// </summary>
    /// <param name="columns">初始列数。</param>
    /// <param name="rows">初始行数。</param>
    /// <returns>要发给对端的字节。</returns>
    public byte[] BuildInitialRequests(int columns, int rows)
    {
        _columns = Normalize(columns, 80);
        _rows = Normalize(rows, 24);
        List<byte> output = [];
        Command(output, TelnetProtocol.Will, TelnetProtocol.OptionTerminalType);
        Command(output, TelnetProtocol.Will, TelnetProtocol.OptionNaws);
        Command(output, TelnetProtocol.Will, TelnetProtocol.OptionSuppressGoAhead);
        Command(output, TelnetProtocol.Do, TelnetProtocol.OptionSuppressGoAhead);
        Command(output, TelnetProtocol.Do, TelnetProtocol.OptionEcho);
        if (config.RequestBinary)
        {
            // 按方向分别协商:我们要发 8 位透明(WILL),也要能收 8 位透明(DO)。
            Command(output, TelnetProtocol.Will, TelnetProtocol.OptionBinary);
            Command(output, TelnetProtocol.Do, TelnetProtocol.OptionBinary);
        }
        return [.. output];
    }

    /// <summary>
    /// 处理一段收到的原始字节:命令与子协商就地消化(应答写进 <paramref name="responses" />),
    /// 剩下的净数据写进 <paramref name="data" /> 交给终端引擎。
    /// </summary>
    /// <param name="input">收到的原始字节。</param>
    /// <param name="data">输出:去掉协议字节后的净数据。</param>
    /// <param name="responses">输出:要回给对端的协商字节。</param>
    public void Process(ReadOnlySpan<byte> input, List<byte> data, List<byte> responses)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(responses);
        foreach (byte current in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (current == TelnetProtocol.Iac)
                    {
                        _state = State.Iac;
                        break;
                    }
                    Emit(data, current);
                    break;

                case State.Iac:
                    if (current == TelnetProtocol.Iac)
                    {
                        // IAC IAC = 一个字面的 0xFF。
                        _state = State.Data;
                        Emit(data, TelnetProtocol.Iac);
                        break;
                    }
                    if (current is TelnetProtocol.Will or TelnetProtocol.Wont
                        or TelnetProtocol.Do or TelnetProtocol.Dont)
                    {
                        _command = current;
                        _state = State.Negotiate;
                        break;
                    }
                    if (current == TelnetProtocol.Sb)
                    {
                        _state = State.SubnegotiationStart;
                        break;
                    }
                    // 其余单字节命令(GA / NOP / DM…)对全屏终端没有意义,吃掉即可。
                    _state = State.Data;
                    break;

                case State.Negotiate:
                    Negotiate(_command, current, responses);
                    _state = State.Data;
                    break;

                case State.SubnegotiationStart:
                    _subnegotiation.Clear();
                    _subnegotiation.Add(current); // 选项码
                    _state = State.Subnegotiation;
                    break;

                case State.Subnegotiation:
                    if (current == TelnetProtocol.Iac)
                    {
                        _state = State.SubnegotiationIac;
                        break;
                    }
                    _subnegotiation.Add(current);
                    break;

                case State.SubnegotiationIac:
                    if (current == TelnetProtocol.Iac)
                    {
                        // 载荷里的字面 0xFF(对端按规范双写过)。
                        _subnegotiation.Add(TelnetProtocol.Iac);
                        _state = State.Subnegotiation;
                        break;
                    }
                    if (current == TelnetProtocol.Se)
                    {
                        HandleSubnegotiation(responses);
                        _state = State.Data;
                        break;
                    }
                    // 规范外的写法:当作子协商被打断,回到数据态重新同步。
                    _state = State.Data;
                    break;
            }
        }
    }

    /// <summary>
    /// 把要发给对端的用户数据编码成线上字节:0xFF 双写,并按需改写回车。
    /// <para>
    /// **BINARY 生效后一律不改写**。这是刻意的:CR 改写只对"用户敲的回车"成立,
    /// 而这个方法看到的还有粘贴内容与 ZMODEM 帧 —— 对整条出方向流改写会把它们打坏。
    /// BINARY 谈成时按 8 位透明原样发;没谈成才退回 NVT 的 CR 规则(那种对端本来也跑不了 ZMODEM)。
    /// </para>
    /// </summary>
    /// <param name="data">用户数据。</param>
    /// <returns>线上字节。</returns>
    public byte[] EncodeOutbound(ReadOnlySpan<byte> data)
    {
        List<byte> output = [with(data.Length + 8)];
        bool rewriteCarriageReturn = !BinaryOutbound && config.EnterMode != TelnetEnterMode.Cr;
        for (int index = 0; index < data.Length; index++)
        {
            byte current = data[index];
            if (current == TelnetProtocol.Iac)
            {
                output.Add(TelnetProtocol.Iac);
                output.Add(TelnetProtocol.Iac);
                continue;
            }
            output.Add(current);
            if (!rewriteCarriageReturn || current != 0x0D)
            {
                continue;
            }
            // 已经是 CR LF 就别再补:补了会变成两行。
            if (index + 1 < data.Length && data[index + 1] is 0x0A or 0x00)
            {
                continue;
            }
            output.Add(config.EnterMode == TelnetEnterMode.CrLf ? (byte)0x0A : (byte)0x00);
        }
        return [.. output];
    }

    /// <summary>
    /// 构造 NAWS 子协商帧;NAWS 未启用或尺寸没变时返回 <see langword="null" />。
    /// 载荷里等于 0xFF 的字节按 RFC 1073 双写(宽或高恰为 255 时才会遇到)。
    /// </summary>
    /// <param name="columns">列数。</param>
    /// <param name="rows">行数。</param>
    /// <returns>要发送的字节;无需发送时为 null。</returns>
    public byte[]? BuildWindowSize(int columns, int rows)
    {
        int normalizedColumns = Normalize(columns, _columns);
        int normalizedRows = Normalize(rows, _rows);
        bool unchanged = normalizedColumns == _columns && normalizedRows == _rows;
        _columns = normalizedColumns;
        _rows = normalizedRows;
        if (!NawsEnabled || unchanged)
        {
            return null;
        }
        return BuildWindowSizeFrame(normalizedColumns, normalizedRows);
    }

    private static byte[] BuildWindowSizeFrame(int columns, int rows)
    {
        List<byte> frame =
        [
            TelnetProtocol.Iac,
            TelnetProtocol.Sb,
            TelnetProtocol.OptionNaws
        ];
        AppendEscaped(frame, (byte)(columns >> 8));
        AppendEscaped(frame, (byte)(columns & 0xFF));
        AppendEscaped(frame, (byte)(rows >> 8));
        AppendEscaped(frame, (byte)(rows & 0xFF));
        frame.Add(TelnetProtocol.Iac);
        frame.Add(TelnetProtocol.Se);
        return [.. frame];
    }

    private void Negotiate(byte command, byte option, List<byte> responses)
    {
        switch (command)
        {
            // 对端说"我要启用 x":我们想要就 DO,不想要就 DONT。
            case TelnetProtocol.Will:
                if (WantRemote(option))
                {
                    if (_remoteEnabled.Add(option))
                    {
                        Command(responses, TelnetProtocol.Do, option);
                    }
                    // 已启用:不再回应(RFC 854:状态没变就不回,否则两端对刷)。
                    break;
                }
                Command(responses, TelnetProtocol.Dont, option);
                break;

            // 对端说"我不启用 x":状态变了才回 DONT。
            case TelnetProtocol.Wont:
                if (_remoteEnabled.Remove(option))
                {
                    Command(responses, TelnetProtocol.Dont, option);
                }
                break;

            // 对端说"请你启用 x":我们能做就 WILL。
            case TelnetProtocol.Do:
                if (WantLocal(option))
                {
                    if (_localEnabled.Add(option))
                    {
                        Command(responses, TelnetProtocol.Will, option);
                    }
                    if (option == TelnetProtocol.OptionNaws)
                    {
                        // NAWS 一旦启用必须立刻上报一次当前尺寸,否则对端一直按 80x24 画,
                        // 直到用户碰巧拉一下窗口才对上(htop/vim 在此之前全是错位的)。
                        responses.AddRange(BuildWindowSizeFrame(_columns, _rows));
                    }
                    break;
                }
                Command(responses, TelnetProtocol.Wont, option);
                break;

            // 对端说"请你别启用 x"。
            case TelnetProtocol.Dont:
                if (_localEnabled.Remove(option))
                {
                    Command(responses, TelnetProtocol.Wont, option);
                }
                break;
        }
    }

    private void HandleSubnegotiation(List<byte> responses)
    {
        if (_subnegotiation.Count == 0)
        {
            return;
        }
        byte option = _subnegotiation[0];
        if (option != TelnetProtocol.OptionTerminalType
            || _subnegotiation.Count < 2
            || _subnegotiation[1] != TelnetProtocol.TerminalTypeSend)
        {
            return;
        }
        // IAC SB 24 IS <term> IAC SE。对端可能反复 SEND 以枚举我们支持的类型;
        // 我们只有一个,按惯例每次都回同一个(枚举方看到重复即停止)。
        responses.Add(TelnetProtocol.Iac);
        responses.Add(TelnetProtocol.Sb);
        responses.Add(TelnetProtocol.OptionTerminalType);
        responses.Add(TelnetProtocol.TerminalTypeIs);
        foreach (byte value in Encoding.ASCII.GetBytes(TerminalTypeName()))
        {
            AppendEscaped(responses, value);
        }
        responses.Add(TelnetProtocol.Iac);
        responses.Add(TelnetProtocol.Se);
    }

    /// <summary>TERM 名:表单留空时退回 <c>xterm-256color</c>,并裁到 40 字符以内。</summary>
    private string TerminalTypeName()
    {
        string name = string.IsNullOrWhiteSpace(config.TerminalType) ? "xterm-256color" : config.TerminalType.Trim();
        return name.Length > 40 ? name[..40] : name;
    }

    /// <summary>我们希望对端启用的选项。其余一律 DONT —— 不认识的选项开着只会带来意外。</summary>
    private bool WantRemote(byte option) => option switch
    {
        TelnetProtocol.OptionEcho => true,
        TelnetProtocol.OptionSuppressGoAhead => true,
        TelnetProtocol.OptionBinary => config.RequestBinary,
        _ => false
    };

    /// <summary>我们自己能提供的选项。</summary>
    private bool WantLocal(byte option) => option switch
    {
        TelnetProtocol.OptionTerminalType => true,
        TelnetProtocol.OptionNaws => true,
        TelnetProtocol.OptionSuppressGoAhead => true,
        TelnetProtocol.OptionBinary => config.RequestBinary,
        // ECHO 由对端做。我们回显自己的输入是"本地回显",那是显示层的事,
        // 不该让对端以为我们会把它的输出再回给它。
        _ => false
    };

    /// <summary>
    /// 写一个净数据字节。非 BINARY 入方向时按 RFC 854 把 <c>CR NUL</c> 归一成裸 CR:
    /// NUL 喂进 VT 引擎虽然无害,但会污染回滚缓冲与"导出缓冲区"的结果。
    /// </summary>
    private void Emit(List<byte> data, byte value)
    {
        if (!BinaryInbound && _lastWasCarriageReturn && value == 0x00)
        {
            _lastWasCarriageReturn = false;
            return;
        }
        _lastWasCarriageReturn = value == 0x0D;
        data.Add(value);
    }

    private static void AppendEscaped(List<byte> output, byte value)
    {
        output.Add(value);
        if (value == TelnetProtocol.Iac)
        {
            output.Add(TelnetProtocol.Iac);
        }
    }

    private static void Command(List<byte> output, byte command, byte option)
    {
        output.Add(TelnetProtocol.Iac);
        output.Add(command);
        output.Add(option);
    }

    /// <summary>行列钳到 1–65535(NAWS 是两个 16 位值);非法值退回上一次的有效值。</summary>
    private static int Normalize(int value, int fallback) => value is > 0 and <= 65535 ? value : fallback;
}

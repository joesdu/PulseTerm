using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Terminal.FileTransfer;

/// <summary>检测到的 ZMODEM 自启动类型(决定本地扮演哪一侧)。</summary>
public enum ZModemTrigger
{
    /// <summary>未命中任何引导。</summary>
    None,

    /// <summary>命中 ZRQINIT:远端跑了 <c>sz</c> 要发文件,本地应「接收」(下载)。</summary>
    Receive,

    /// <summary>命中 ZRINIT:远端跑了 <c>rz</c> 要收文件,本地应「发送」(上传)。</summary>
    Send
}

/// <summary>一次检测扫描的结果:应喂给终端的字节,以及是否命中 ZMODEM 启动。</summary>
/// <param name="TerminalBytes">应正常喂入 VT 终端的字节(命中时为引导前、且尚未喂过的部分)。</param>
/// <param name="Trigger">命中的引导类型;<see cref="ZModemTrigger.None" /> 表示未命中。</param>
/// <param name="ProtocolBytes">命中时,从引导序列起、应交给 ZMODEM 引擎的字节。</param>
public readonly record struct ZModemDetectResult(
    byte[] TerminalBytes,
    ZModemTrigger Trigger,
    byte[] ProtocolBytes)
{
    /// <summary>是否命中了 ZMODEM 引导。</summary>
    public bool Detected => Trigger != ZModemTrigger.None;
}

/// <summary>
/// 在终端输出字节流中检测 ZMODEM 自动启动:远端 <c>sz</c> 注入的 <c>ZRQINIT</c> 引导
/// (十六进制帧头 <c>** ZDLE 'B' "00" …</c>,本地转接收),或远端 <c>rz</c> 注入的 <c>ZRINIT</c>
/// 引导(<c>** ZDLE 'B' "01" …</c>,本地转发送)。
/// <para>
/// <b>零扣留</b>:字节一律照常喂终端,检测只靠一份 ≤<see cref="MaxCarry" /> 字节的尾部副本
/// (<see cref="_carry" />)做跨分片匹配。扣留用户数据等下一分片是 #291 的根因 —— 引导头两字节
/// 正是 <c>'*' '*'</c>,SSH 下逐字回显的星号会被无限期扣住,表现为「敲 <c>*</c> 不回显、光标不动」。
/// 代价只是引导被分片切开时屏幕上多出一两个已经画出的 <c>'*'</c>。zmodem.js 的 Sentry 亦是此策略
/// (「If there is no active or pending ZMODEM session, the text is all output」)。
/// </para>
/// <para>
/// <b>判据是完整帧头而非 6 字节引导</b>:必须是一个格式良好的十六进制帧头(<see cref="HexHeaderLength" />
/// 字节,后 14 位全为十六进制数字),且默认还要求它<b>锚在分片末尾</b>(其后只允许 CR/LF/XON)——
/// <c>sz</c>/<c>rz</c> 写完帧头就阻塞等应答,真引导天然满足;而 <c>cat</c> 到二进制里恰好凑出这
/// 六个字节的误报则几乎不可能同时满足。尾锚定可由 <see cref="AcceptUnanchoredHeader" /> 放宽。
/// </para>
/// </summary>
public sealed class ZModemDetector
{
    /// <summary>
    /// 十六进制帧头长度:<c>ZPAD ZPAD ZDLE ZHEX</c> 4 字节 + (帧类型 + 4 个参数 + CRC16)
    /// 每字节两位十六进制共 14 字节 = 18(见 <c>ZModemFrameWriter.WriteHex</c>)。
    /// </summary>
    private const int HexHeaderLength = 18;

    /// <summary>帧头之后允许出现的收尾字节:CR、LF、XON(lrzsz 补 XON 释放流控)。</summary>
    private const int MaxTrailerLength = 3;

    /// <summary>
    /// 跨分片保留的尾部副本上限 = 完整帧头 + 收尾。与 zmodem.js 的
    /// <c>MAX_ZM_HEX_START_LENGTH = 21</c> 同值。
    /// </summary>
    private const int MaxCarry = HexHeaderLength + MaxTrailerLength;

    private static readonly byte[] ReceiveSignature = ZModemConstants.ReceiveInitSignature.ToArray();
    private static readonly byte[] SendSignature = ZModemConstants.SendInitSignature.ToArray();

    // 两个引导等长(都是 ZPAD ZPAD ZDLE ZHEX + 两位十六进制类型)。
    private static readonly int SignatureLength = ReceiveSignature.Length;

    // 两个引导只在最后一字节不同('0' = ZRQINIT / '1' = ZRINIT),前 5 字节完全一致。
    // 因此整块只需扫一遍公共前缀,命中后看第 6 字节定方向 —— 比逐个签名各扫一遍省一半。
    private static readonly byte[] CommonPrefix = ReceiveSignature[..^1];

    /// <summary>
    /// 已经喂过终端、仅留作跨分片匹配的尾部副本(疑似被切断的帧头,或其前缀)。
    /// 长度恒 ≤ <see cref="MaxCarry" />。
    /// </summary>
    private readonly List<byte> _carry = [];

    /// <summary>
    /// 放宽尾锚定:为 true 时,格式良好的帧头出现在分片任意位置都算命中。
    /// 由路由器在用户刚敲过 <c>sz</c>/<c>rz</c> 的时间窗内置位(见
    /// <see cref="TerminalTransferRouter.NoteCommandSubmitted" />)—— 此时误报代价远低于漏检。
    /// </summary>
    public bool AcceptUnanchoredHeader { get; set; }

    /// <summary>
    /// 常态零拷贝快路径判定:无待续尾部、且块内既无引导前缀、块尾也不与前缀重叠时为 true——
    /// 调用方可把原始块原样喂终端,跳过 <see cref="Process" /> 的窗口拼接与切片拷贝。
    /// 该检测器挂在所有会话的输出链路上,绝大多数输出块都应走此路径。
    /// </summary>
    public bool CanPassThrough(ReadOnlySpan<byte> incoming) =>
        _carry.Count == 0
        && incoming.IndexOf(CommonPrefix) < 0
        && LongestPrefixSuffix(incoming) == 0;

    /// <summary>处理一段新到达的输出字节,判断是否命中 ZMODEM 引导。</summary>
    /// <param name="incoming">本次到达的原始输出字节。</param>
    /// <returns>检测结果:待喂终端字节、命中的引导类型、以及命中后交给引擎的协议字节。</returns>
    public ZModemDetectResult Process(ReadOnlySpan<byte> incoming)
    {
        // 窗口 = 已喂过的尾部副本 + 新数据。窗口开头 fedOffset 字节已经进过终端,
        // 只参与匹配,绝不能再喂第二遍。
        int fedOffset = _carry.Count;
        byte[] window = new byte[fedOffset + incoming.Length];
        _carry.CopyTo(window);
        incoming.CopyTo(window.AsSpan(fedOffset));
        _carry.Clear();

        (int idx, ZModemTrigger trigger, int pendingFrom) = Scan(window);
        if (idx >= 0)
        {
            // 命中:引导之前「尚未喂过」的部分照常进终端,引导及其后全部交给引擎。
            return new(idx > fedOffset ? window[fedOffset..idx] : [], trigger, window[idx..]);
        }

        // 未命中:字节全部照喂,只留一份尾部副本等下一分片续判。
        // 续判起点取「不完整候选帧头的起点」,没有候选时退化为「与引导前缀重叠的块尾」。
        int carryFrom = pendingFrom >= 0
            ? pendingFrom
            : window.Length - LongestPrefixSuffix(window);
        carryFrom = Math.Max(carryFrom, window.Length - MaxCarry);
        if (carryFrom < window.Length)
        {
            _carry.AddRange(window.AsSpan(carryFrom));
        }
        return new(fedOffset == 0 ? window : window[fedOffset..], ZModemTrigger.None, []);
    }

    /// <summary>
    /// 会话开始 / 结束或路由复位时丢弃跨分片匹配状态。
    /// <para>
    /// 不返回任何字节:本检测器从不扣留 —— 副本里的字节早已喂过终端,再交还一次就是重影。
    /// </para>
    /// </summary>
    public void Reset() => _carry.Clear();

    /// <summary>
    /// 单遍扫描定位最靠前的、合格的十六进制引导帧头。
    /// 这条路径挂在所有会话的输出链路上,每个输出块都要走一次,故按热路径写。
    /// </summary>
    /// <param name="window">待扫描的窗口。</param>
    /// <returns>
    /// <c>Index</c> ≥ 0 表示命中;否则 <c>PendingFrom</c> ≥ 0 表示窗口尾部有个「引导已现、帧头未完」
    /// 的候选,其起点须留作下一分片续判(两者都为负则窗口里什么都没有)。
    /// </returns>
    private (int Index, ZModemTrigger Trigger, int PendingFrom) Scan(ReadOnlySpan<byte> window)
    {
        int offset = 0;
        while (offset <= window.Length - CommonPrefix.Length)
        {
            int hit = window[offset..].IndexOf(CommonPrefix);
            if (hit < 0)
            {
                break;
            }
            int abs = offset + hit;

            // 第 6 字节定方向;帧头还没到齐就留到下一分片续判(它已经喂过终端了,不影响显示)。
            if (abs + SignatureLength > window.Length)
            {
                return (-1, ZModemTrigger.None, abs);
            }
            ZModemTrigger trigger = window[abs + CommonPrefix.Length] switch
            {
                var d when d == ReceiveSignature[^1] => ZModemTrigger.Receive,
                var d when d == SendSignature[^1] => ZModemTrigger.Send,
                _ => ZModemTrigger.None
            };
            if (trigger == ZModemTrigger.None)
            {
                offset = abs + 1;
                continue;
            }
            if (abs + HexHeaderLength > window.Length)
            {
                return (-1, ZModemTrigger.None, abs);
            }

            // 帧头剩下的 12 位必须全是十六进制数字,否则这六个字节只是巧合。
            if (!IsHexRun(window[(abs + SignatureLength)..(abs + HexHeaderLength)]))
            {
                offset = abs + 1;
                continue;
            }

            // 尾锚定:帧头之后只允许 CR/LF/XON,且到此为止或紧接着另一个 ZMODEM 帧。
            if (!AcceptUnanchoredHeader && !IsFrameBoundary(window[(abs + HexHeaderLength)..]))
            {
                offset = abs + 1;
                continue;
            }
            return (abs, trigger, -1);
        }
        return (-1, ZModemTrigger.None, -1);
    }

    /// <summary>整段是否都是 ASCII 十六进制数字。</summary>
    private static bool IsHexRun(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            bool hex = b is >= (byte)'0' and <= (byte)'9'
                or >= (byte)'a' and <= (byte)'f'
                or >= (byte)'A' and <= (byte)'F';
            if (!hex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 帧头之后是不是一个干净的帧边界:跳过收尾字节(CR / LF / XON)后,要么到此为止
    /// (<c>sz</c>/<c>rz</c> 写完引导就阻塞等应答,常态如此),要么紧接着另一个 ZMODEM 帧
    /// (所有帧都以 ZPAD 起头 —— 对端把引导和后续帧塞进同一次写时如此)。
    /// <para>
    /// 落在这两者之外的(帧头后面跟着普通 shell 输出),判为巧合而非真引导:典型如
    /// <c>cat</c> 一份 ZMODEM 抓包文件,不该把终端交出去。
    /// </para>
    /// </summary>
    private static bool IsFrameBoundary(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && span[i] is 0x0D or 0x0A or ZModemConstants.XON)
        {
            i++;
        }
        return i == span.Length || span[i] == ZModemConstants.ZPAD;
    }

    /// <summary>
    /// 求 <paramref name="window" /> 的末尾与引导前缀的最长重叠长度(0..SignatureLength-1),
    /// 即需要留作下一分片续判的尾部长度。两个引导只在最后一字节不同,故按公共前缀判断即可。
    /// </summary>
    private static int LongestPrefixSuffix(ReadOnlySpan<byte> window)
    {
        int max = Math.Min(SignatureLength - 1, window.Length);
        for (int len = max; len > 0; len--)
        {
            if (window[^len..].SequenceEqual(CommonPrefix.AsSpan(0, len)))
            {
                return len;
            }
        }
        return 0;
    }
}

namespace VelaShell.Plugin.Telnet.Tests;

/// <summary>
/// RFC 854 / 857 / 858 / 1073 / 1091 的协商与编解码。
/// <para>
/// 期望值全部按 RFC 原文的字节序列手写(不是"跑一遍看输出是什么就写什么"):
/// 这一层错了的表现都是"平时都好、偶尔莫名其妙",自证式测试在这里等于没测
/// —— 参见仓库里 ZMODEM CRC 双重增广那次教训。
/// </para>
/// </summary>
[TestClass]
public sealed class TelnetNegotiatorTests
{
    private const byte Iac = 255;
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    private static TelnetNegotiator Create(
        TelnetEnterMode enterMode = TelnetEnterMode.CrLf,
        bool binary = true,
        string term = "xterm-256color") =>
        new(new(term, enterMode, binary, TelnetLocalEcho.Auto));

    private static (byte[] Data, byte[] Responses) Process(TelnetNegotiator negotiator, params byte[] input)
    {
        List<byte> data = [];
        List<byte> responses = [];
        negotiator.Process(input, data, responses);
        return ([.. data], [.. responses]);
    }

    [TestMethod]
    public void InitialRequests_AskForTheFiveOptionsAFullScreenTerminalNeeds()
    {
        // RFC 858:ECHO 与 SGA 必须同时生效才是逐字符远端回显 —— vim/htop 能跑的前提。
        // TERMINAL-TYPE 与 NAWS 由我们提供(不给对端就按 unknown 80x24 画)。
        byte[] hello = Create().BuildInitialRequests(120, 32);
        Assert.AreSequenceEqual(
            new byte[]
            {
                Iac, Will, 24,  // TERMINAL-TYPE
                Iac, Will, 31,  // NAWS
                Iac, Will, 3,   // SGA(我方)
                Iac, Do, 3,     // SGA(对端)
                Iac, Do, 1,     // ECHO(对端)
                Iac, Will, 0,   // BINARY 出方向
                Iac, Do, 0      // BINARY 入方向
            }, hello);
    }

    [TestMethod]
    public void InitialRequests_WithoutBinary_OmitsBothDirections()
    {
        byte[] hello = Create(binary: false).BuildInitialRequests(80, 24);
        Assert.DoesNotContain(command => command[2] == 0, hello.Chunk(3), "关掉 BINARY 后不该再请求它。");
    }

    [TestMethod]
    public void Iac_DoubledInStream_IsRestoredToASingleByte()
    {
        // 数据里字面的 0xFF 由对端双写;还原漏了会把后续字节当命令吃掉 ——
        // 这正是"传大文件或输出含 0xFF 时随机损坏、极难定位"那一类。
        (byte[] data, byte[] responses) = Process(Create(), (byte)'a', Iac, Iac, (byte)'b');
        Assert.AreSequenceEqual(new byte[] { (byte)'a', 0xFF, (byte)'b' }, data);
        Assert.IsEmpty(responses);
    }

    [TestMethod]
    public void Negotiation_SplitAcrossReads_IsStillParsed()
    {
        // 网络会在任意字节切开:状态机必须跨调用保持,否则 IAC 与选项码分到两次读里就漏判。
        TelnetNegotiator negotiator = Create();
        (byte[] first, byte[] noResponse) = Process(negotiator, (byte)'x', Iac);
        Assert.AreSequenceEqual("x"u8.ToArray(), first);
        Assert.IsEmpty(noResponse);

        (byte[] second, byte[] responses) = Process(negotiator, Will, 1);
        Assert.IsEmpty(second);
        Assert.AreSequenceEqual(new byte[] { Iac, Do, 1 }, responses);
        Assert.IsTrue(negotiator.RemoteEcho);
    }

    [TestMethod]
    public void RepeatedWill_IsNotAnsweredTwice()
    {
        // RFC 854:状态没变就不回应。回了就是协商回环,两端能一直对着刷。
        TelnetNegotiator negotiator = Create();
        (_, byte[] first) = Process(negotiator, Iac, Will, 1);
        Assert.AreSequenceEqual(new byte[] { Iac, Do, 1 }, first);
        (_, byte[] second) = Process(negotiator, Iac, Will, 1);
        Assert.IsEmpty(second, "对已生效的选项再回一次 DO 就是回环。");
    }

    [TestMethod]
    public void UnwantedOption_IsRefused()
    {
        // 不认识的选项一律拒绝:开着只会带来意外的语义(如 LINEMODE 把逐字符模式关掉)。
        (_, byte[] responses) = Process(Create(), Iac, Will, 34); // LINEMODE
        Assert.AreSequenceEqual(new byte[] { Iac, Dont, 34 }, responses);
    }

    [TestMethod]
    public void DoEcho_IsRefused_BecauseEchoingIsTheServersJob()
    {
        // 客户端答应 ECHO 意味着"我会把你发来的东西回显给你",不是本地回显。
        (_, byte[] responses) = Process(Create(), Iac, Do, 1);
        Assert.AreSequenceEqual(new byte[] { Iac, Wont, 1 }, responses);
    }

    [TestMethod]
    public void DoNaws_AnswersWillAndImmediatelyReportsTheCurrentSize()
    {
        // NAWS 启用后不立刻上报,对端会一直按 80x24 画,直到用户碰巧拉一下窗口。
        TelnetNegotiator negotiator = Create();
        negotiator.BuildInitialRequests(120, 32);
        (_, byte[] responses) = Process(negotiator, Iac, Do, 31);
        Assert.AreSequenceEqual(
            new byte[] { Iac, Will, 31, Iac, Sb, 31, 0, 120, 0, 32, Iac, Se }, responses);
        Assert.IsTrue(negotiator.NawsEnabled);
    }

    [TestMethod]
    public void Naws_DoublesPayloadBytesThatEqual255()
    {
        // RFC 1073:"any occurrence of 255 in the subnegotiation must be doubled"。
        // 只在宽或高恰为 255(或 65280+)时才踩到 —— 教科书级的潜伏 bug。
        TelnetNegotiator negotiator = Create();
        negotiator.BuildInitialRequests(80, 24);
        Process(negotiator, Iac, Do, 31);
        byte[]? frame = negotiator.BuildWindowSize(255, 24);
        Assert.IsNotNull(frame);
        Assert.AreSequenceEqual(
            new byte[] { Iac, Sb, 31, 0, 255, 255, 0, 24, Iac, Se }, frame);
    }

    [TestMethod]
    public void Naws_IsSilentWhenTheSizeDidNotChange_OrWhenNotNegotiated()
    {
        TelnetNegotiator negotiator = Create();
        negotiator.BuildInitialRequests(80, 24);
        Assert.IsNull(negotiator.BuildWindowSize(100, 40), "对端没 DO 过 NAWS 时不该发。");
        Process(negotiator, Iac, Do, 31);
        Assert.IsNull(negotiator.BuildWindowSize(100, 40), "尺寸没变(上一步已记住)时不该重发。");
        Assert.IsNotNull(negotiator.BuildWindowSize(100, 41));
    }

    [TestMethod]
    public void TerminalTypeSend_IsAnsweredWithIsAndTheConfiguredName()
    {
        // RFC 1091:SEND=1、IS=0。回错方向的话对端拿不到 TERM,curses 程序全按 dumb 走。
        TelnetNegotiator negotiator = Create(term: "vt100");
        Process(negotiator, Iac, Do, 24);
        (_, byte[] responses) = Process(negotiator, Iac, Sb, 24, 1, Iac, Se);
        Assert.AreSequenceEqual(
            new byte[] { Iac, Sb, 24, 0, (byte)'v', (byte)'t', (byte)'1', (byte)'0', (byte)'0', Iac, Se }, responses);
    }

    [TestMethod]
    public void Subnegotiation_WithEscapedPayload_IsParsedAndDoesNotLeakIntoData()
    {
        // 子协商载荷里的 IAC IAC 是一个字面 0xFF,不是帧结束;错判会把后面的数据整段吃掉。
        TelnetNegotiator negotiator = Create();
        (byte[] data, _) = Process(negotiator, Iac, Sb, 31, 0, Iac, Iac, 0, 24, Iac, Se, (byte)'k');
        Assert.AreSequenceEqual("k"u8.ToArray(), data);
    }

    [TestMethod]
    public void EncodeOutbound_EscapesIac_AndAppendsLineFeedAfterBareCarriageReturn()
    {
        // 没谈成 BINARY 时按 NVT 规则:裸 CR 非法,按设置补 LF(RFC 1123 §3.3.1 的默认)。
        TelnetNegotiator negotiator = Create(binary: false);
        byte[] wire = negotiator.EncodeOutbound([(byte)'a', 0xFF, 0x0D]);
        Assert.AreSequenceEqual(new byte[] { (byte)'a', 0xFF, 0xFF, 0x0D, 0x0A }, wire);
    }

    [TestMethod]
    public void EncodeOutbound_CrNulMode_SendsNul_AndNeverDoublesAnExistingCrLf()
    {
        TelnetNegotiator negotiator = Create(TelnetEnterMode.CrNul, binary: false);
        Assert.AreSequenceEqual(new byte[] { 0x0D, 0x00 }, negotiator.EncodeOutbound([0x0D]));
        Assert.AreSequenceEqual("\r\n"u8.ToArray(), negotiator.EncodeOutbound([0x0D, 0x0A]));
    }

    [TestMethod]
    public void EncodeOutbound_AfterBinaryIsNegotiated_LeavesCarriageReturnAlone()
    {
        // 这条是 ZMODEM 与粘贴内容不被打坏的保证:出方向一旦 8 位透明就不再改写,
        // 只保留 IAC 双写(那是协议层面绕不开的)。
        TelnetNegotiator negotiator = Create();
        Process(negotiator, Iac, Do, 0); // 对端同意我方 BINARY
        Assert.IsTrue(negotiator.BinaryOutbound);
        Assert.AreSequenceEqual(new byte[] { 0x0D, 0xFF, 0xFF }, negotiator.EncodeOutbound([0x0D, 0xFF]));
    }

    [TestMethod]
    public void Inbound_CrNul_IsNormalizedToBareCr_UntilBinaryIsNegotiated()
    {
        // RFC 854:入方向 CR NUL 表示"只回车"。NUL 喂进 VT 引擎虽无害,
        // 但会污染回滚缓冲与"导出缓冲区"的结果。
        TelnetNegotiator negotiator = Create();
        (byte[] data, _) = Process(negotiator, (byte)'a', 0x0D, 0x00, (byte)'b');
        Assert.AreSequenceEqual("a\rb"u8.ToArray(), data);

        // 谈成入方向 BINARY 之后就不能再动:那时 0x00 是真实数据。
        Process(negotiator, Iac, Will, 0);
        Assert.IsTrue(negotiator.BinaryInbound);
        (byte[] binaryData, _) = Process(negotiator, 0x0D, 0x00);
        Assert.AreSequenceEqual(new byte[] { 0x0D, 0x00 }, binaryData);
    }

    [TestMethod]
    public void SingleByteCommands_AreSwallowedWithoutTouchingData()
    {
        // GA / NOP / DM 之类对全屏终端没有意义,但绝不能漏进数据流(会画出乱码)。
        (byte[] data, byte[] responses) = Process(Create(), (byte)'a', Iac, 249 /* GA */, (byte)'b');
        Assert.AreSequenceEqual("ab"u8.ToArray(), data);
        Assert.IsEmpty(responses);
    }

    [TestMethod]
    public void Wont_AfterWill_TurnsTheOptionBackOff()
    {
        TelnetNegotiator negotiator = Create();
        Process(negotiator, Iac, Will, 1);
        Assert.IsTrue(negotiator.RemoteEcho);
        (_, byte[] responses) = Process(negotiator, Iac, Wont, 1);
        Assert.AreSequenceEqual(new byte[] { Iac, Dont, 1 }, responses);
        Assert.IsFalse(negotiator.RemoteEcho, "对端撤回 ECHO 后必须回到本地回显,否则用户打字看不见。");
    }
}

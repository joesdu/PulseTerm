using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// VT 解析器的缓冲上限:没有终结符的 OSC/DCS 字符串,以及畸形的超长中间字节串,
/// 都不允许把缓冲撑到无限大、也不允许让屏幕永久死住。
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class VtParserLimitTests
{
    /// <summary>与 <c>VtParser.MaxStringPayload</c> 一致(该常量为 private,这里按契约取同值)。</summary>
    private const int MaxStringPayload = 128 * 1024;

    /// <summary>与 <c>VtParser.MaxIntermediates</c> 一致。</summary>
    private const int MaxIntermediates = 2;

    private static TerminalEmulator New(int cols = 40, int rows = 6) => new(cols, rows);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(TerminalEmulator e, int row) => e.Screen.ActiveLine(row).GetText().TrimEnd();

    /// <summary>正常长度的 OSC 仍须原样吞掉并分发,不能被上限逻辑误伤。</summary>
    [TestMethod]
    public void TerminatedOsc_IsStillConsumedAndDispatched()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b]0;window title\avisible");
        Assert.AreEqual("visible", Line(e, 0));

        var rec = new RecordingActions();
        new VtParser(rec).Parse("\x1b]0;window title\a");
        Assert.AreSequenceEqual(["0", "window title"], rec.Osc[0]);
    }

    /// <summary>
    /// 没有终结符的 OSC:超过上限后必须丢弃载荷回到 ground,其后的字节照常显示。
    /// 回归:不设限时坏掉的提示符脚本(<c>printf '\e]0;'</c> 忘了发 BEL)会把之后
    /// 全部输出吸进 StringBuilder —— 内存无限涨,屏幕一直是死的。
    /// </summary>
    [TestMethod]
    public void UnterminatedOsc_RecoversAfterPayloadLimit()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b]0;");
        Feed(e, new string('x', MaxStringPayload + 16));

        // 越限那一刻回 ground,后续字节照常进屏幕。
        Feed(e, "\r\nrecovered");
        Assert.AreEqual("recovered", Line(e, e.CursorY));
    }

    /// <summary>越限的 OSC 载荷绝不能被分发出去(半截的 OSC 52 会往剪贴板写垃圾)。</summary>
    [TestMethod]
    public void OverlongOsc_IsDiscardedNotDispatched()
    {
        var rec = new RecordingActions();
        var parser = new VtParser(rec);
        parser.Parse("\x1b]52;c;" + new string('A', MaxStringPayload + 16) + "\a");

        Assert.IsEmpty(rec.Osc);
    }

    /// <summary>DCS 直通载荷同样受限,恢复方式一致。</summary>
    [TestMethod]
    public void UnterminatedDcs_RecoversAfterPayloadLimit()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1bP");
        Feed(e, new string('y', MaxStringPayload + 16));

        Feed(e, "\r\nrecovered");
        Assert.AreEqual("recovered", Line(e, e.CursorY));
    }

    /// <summary>
    /// <c>ESC [</c> 后跟一长串空格:0x20 也是中间字节,不设限就会一直往中间字节缓冲里堆
    /// (终结字节一到照样分发,屏幕看不出异常 —— 纯粹是内存在涨,所以这里必须直接量
    /// 中间字节串本身,量屏幕是量不出来的)。超限即把整条序列判为畸形,不再分发。
    /// </summary>
    [TestMethod]
    public void OverlongCsiIntermediates_AreCappedAndSequenceIgnored()
    {
        var rec = new RecordingActions();
        new VtParser(rec).Parse("\x1b[" + new string(' ', 5000) + "q");

        Assert.IsEmpty(rec.Csi); // 转 CsiIgnore,不分发。
    }

    /// <summary>DCS 的中间字节同样有帽子。</summary>
    [TestMethod]
    public void OverlongDcsIntermediates_AreCappedAndSequenceIgnored()
    {
        var rec = new RecordingActions();
        new VtParser(rec).Parse("\x1bP" + new string(' ', 5000) + "q payload\x1b\\");

        Assert.IsEmpty(rec.Dcs);
    }

    /// <summary>畸形序列被忽略后,流要能自己缓过来,后续输出照常显示。</summary>
    [TestMethod]
    public void AfterOverlongCsiIntermediates_StreamRecovers()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[" + new string(' ', 5000) + "q");
        Feed(e, "recovered");

        Assert.AreEqual("recovered", Line(e, 0));
    }

    /// <summary>合法的中间字节序列(DECSCUSR <c>CSI SP q</c>)不受影响,照常分发。</summary>
    [TestMethod]
    public void SingleIntermediateSequence_StillDispatches()
    {
        var rec = new RecordingActions();
        new VtParser(rec).Parse("\x1b[2 q");

        Assert.HasCount(1, rec.Csi);
        Assert.AreEqual(" ", rec.Csi[0].Intermediates);
        Assert.AreEqual('q', rec.Csi[0].Final);
        Assert.IsLessThanOrEqualTo(MaxIntermediates, rec.Csi[0].Intermediates.Length);
    }

    /// <summary>只记录分发事件的假动作接收端。</summary>
    private sealed class RecordingActions : IVtActions
    {
        public List<(string Intermediates, char Final)> Csi { get; } = [];
        public List<(string Intermediates, char Final, string Data)> Dcs { get; } = [];
        public List<IReadOnlyList<string>> Osc { get; } = [];

        public void Print(int rune) { }

        public void PrintRun(ReadOnlySpan<char> text) { }

        public void Execute(char control) { }

        public void EscDispatch(string intermediates, char final) { }

        public void CsiDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final) =>
            Csi.Add((intermediates, final));

        public void OscDispatch(IReadOnlyList<string> parameters) => Osc.Add(parameters);

        public void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data) =>
            Dcs.Add((intermediates, final, data));
    }
}

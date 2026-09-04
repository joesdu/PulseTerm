using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 本地回显策略。重点守两件事:
/// ① 含 ESC 的载荷绝不回显 —— 方向键编码是 <c>ESC [ A</c>,喂回终端会被**执行**(光标真的动),
///    而不是显示出来,这是这个功能最容易做错的地方;
/// ② 开关语义 —— SRM 是反的(置位 = 不回显),写反了 SSH 下每个字符都会显示两遍。
/// </summary>
[TestClass]
[TestCategory("LocalEcho")]
public class LocalEchoTests
{
    private static string Echo(string input, bool newLineMode = false) =>
        Encoding.ASCII.GetString(LocalEcho.Compute(Encoding.ASCII.GetBytes(input), newLineMode));

    [TestMethod]
    public void PrintableText_IsEchoedVerbatim() => Assert.AreEqual("ls -la", Echo("ls -la"));

    [TestMethod]
    public void Utf8MultiByte_SurvivesRoundTrip()
    {
        byte[] input = Encoding.UTF8.GetBytes("中文");
        byte[] echo = LocalEcho.Compute(input, newLineMode: false);
        Assert.AreEqual("中文", Encoding.UTF8.GetString(echo), "多字节字符的后续字节(≥0x80)不能被当成控制字符丢掉。");
    }

    /// <summary>这条是本类的核心:方向键回显出去会移动光标而不是显示字符。</summary>
    [TestMethod]
    public void EscapeSequences_AreNeverEchoed()
    {
        Assert.AreEqual("", Echo("\e[A"), "方向键序列不能回显 —— 回显即被执行。");
        Assert.AreEqual("", Echo("\e[200~pasted\e[201~"), "括号粘贴的包裹序列同理。");
    }

    /// <summary>含 ESC 时整段丢弃,不做"挑出可打印部分"的小聪明 —— 那会把序列拆碎得更难看。</summary>
    [TestMethod]
    public void PayloadContainingEscape_IsDroppedEntirely() => Assert.AreEqual("", Echo("abc\e[Adef"));

    [TestMethod]
    public void Backspace_EchoesVisibleErase()
    {
        Assert.AreEqual("\b \b", Echo("\b"), "退格要真正擦掉:退一格、盖空格、再退回。");
        Assert.AreEqual("\b \b", Echo("\u007F"), "DEL 与 BS 同样处理。");
    }

    [TestMethod]
    public void CarriageReturn_FollowsNewLineMode()
    {
        Assert.AreEqual("\r", Echo("\r"), "LNM 关:只回显 CR。");
        Assert.AreEqual("\r\n", Echo("\r", newLineMode: true), "LNM 开:CR 要带上 LF,与终端处理主机输出的语义一致。");
    }

    [TestMethod]
    public void ControlCharacters_AreNotEchoed()
    {
        Assert.AreEqual("", Echo("\u0003"), "Ctrl+C 的屏幕效果(^C)该由主机决定。");
        Assert.AreEqual("", Echo("\t"), "Tab 的制表位展开该由主机决定。");
    }

    [TestMethod]
    public void EmptyInput_ProducesNothing() => Assert.AreEqual("", Echo(""));

    // ---- 开关语义 ----------------------------------------------------

    [TestMethod]
    public void Disabled_WhenUserOffAndSrmSet() =>
        // SRM 置位(默认)= 主机负责回显 → 不本地回显。SSH 的常态。
        Assert.IsFalse(LocalEcho.IsEnabled(userEnabled: false, sendReceiveMode: true));

    [TestMethod]
    public void Enabled_WhenUserTurnsItOn() =>
        // Telnet/串口:主机不回显也不会发 SRM,只能靠用户设置。
        Assert.IsTrue(LocalEcho.IsEnabled(userEnabled: true, sendReceiveMode: true));

    [TestMethod]
    public void Enabled_WhenHostResetsSrm() =>
        // CSI 12 l:主机显式要求终端自行回显。
        Assert.IsTrue(LocalEcho.IsEnabled(userEnabled: false, sendReceiveMode: false));

    // ---- 对端自己回显时强制关闭(SSH / 本地 ConPTY)------------------

    /// <summary>
    /// 这条是防"用户为串口打开开关后,SSH 与本地标签全部双字符"。
    /// 判据是对端行为而非连接类型 —— 现有两种传输(SSH 远端 PTY、本地 ConPTY 的 shell)都自己回显。
    /// </summary>
    [TestMethod]
    public void UserSetting_IsIgnored_WhenPeerEchoes()
    {
        Assert.IsFalse(
            LocalEcho.IsEnabled(userEnabled: true, sendReceiveMode: true, peerEchoes: true),
            "对端自己回显时必须忽略用户开关,否则每个字符会显示两遍。");
    }

    [TestMethod]
    public void UserSetting_Applies_WhenPeerDoesNotEcho()
    {
        Assert.IsTrue(
            LocalEcho.IsEnabled(userEnabled: true, sendReceiveMode: true, peerEchoes: false),
            "Telnet 半双工 / 串口等不回显的链路上,用户开关照常生效。");
    }

    /// <summary>
    /// 主机显式复位 SRM 时,即便对端平时自己回显也要照做 —— 远端程序主动要求终端接管回显
    /// (它自己会相应停止),无视它会让用户打字完全看不见。
    /// </summary>
    [TestMethod]
    public void ExplicitSrmReset_StillWins_OverPeerEchoes() => Assert.IsTrue(LocalEcho.IsEnabled(userEnabled: false, sendReceiveMode: false, peerEchoes: true));

    // ---- SRM 在引擎里的解析 ------------------------------------------

    [TestMethod]
    public void Srm_DefaultsToHostEcho() => Assert.IsTrue(new TerminalModes().SendReceive, "默认必须是 12h(不本地回显),否则 SSH 会话会双字符。");

    [TestMethod]
    public void Srm_IsParsedFromAnsiMode12()
    {
        var emulator = new TerminalEmulator(20, 5);

        emulator.Feed(Encoding.ASCII.GetBytes("\e[12l"));
        Assert.IsFalse(emulator.Modes.SendReceive, "CSI 12 l 复位 SRM = 要求本地回显。");

        emulator.Feed(Encoding.ASCII.GetBytes("\e[12h"));
        Assert.IsTrue(emulator.Modes.SendReceive, "CSI 12 h 置位 SRM = 主机负责回显。");
    }

    [TestMethod]
    public void Srm_IsRestoredByReset()
    {
        var modes = new TerminalModes { SendReceive = false };
        modes.Reset();
        Assert.IsTrue(modes.SendReceive, "复位后应回到默认的 12h。");
    }
}

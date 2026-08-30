using System.Text;
using VelaShell.Terminal.Input;

namespace VelaShell.Terminal.Tests.Input;

[TestClass]
[TestCategory("InputTracker")]
public class TerminalInputTrackerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void PrintableInput_BuildsCurrentLine()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("docker ps"));
        Assert.AreEqual("docker ps", tracker.CurrentInput);
    }

    [TestMethod]
    public void Backspace_RemovesLastCharacter()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("lss"));
        tracker.Process([0x7F]);
        Assert.AreEqual("ls", tracker.CurrentInput);
    }

    [TestMethod]
    public void Backspace_RemovesWholeMultiByteCharacter()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("ls 中"));
        tracker.Process([0x7F]);
        Assert.AreEqual("ls ", tracker.CurrentInput);
    }

    [TestMethod]
    public void Enter_SubmitsAndResetsToEmpty()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process(Bytes("htop\r"));
        Assert.AreEqual("htop", submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void CtrlC_ClearsLineWithoutSubmit()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process(Bytes("rm -rf /"));
        tracker.Process([0x03]);
        Assert.IsNull(submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void CtrlC_OnAlreadyEmptyLine_StillRaisesInputChanged()
    {
        // #315:空行上 Alt+Enter 召出全量补全面板后按 Ctrl+C。行内容前后都是空串,
        // 若只按「字面内容变了才通知」,消费方收不到任何一拍,面板就永远关不掉。
        var tracker = new TerminalInputTracker();
        int changes = 0;
        tracker.InputChanged += () => changes++;
        tracker.Process([0x03]);
        Assert.AreEqual(1, changes, "Ctrl+C 取消空行也必须通知消费方");
        Assert.AreEqual(string.Empty, tracker.CurrentInput);

        tracker.Process([0x15]); // Ctrl+U(kill line)同理。
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void ArrowKey_EscSequence_MarksUnknownUntilReset()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("ls"));
        tracker.Process([0x1B, (byte)'[', (byte)'A']); // ↑:shell 召回历史,本地不可知。
        Assert.IsNull(tracker.CurrentInput);

        // 未知态下继续键入也不能恢复跟踪(行内容仍不可知)。
        tracker.Process(Bytes("x"));
        Assert.IsNull(tracker.CurrentInput);

        // Enter 把行交给 shell,回到确定的空行,且不提交未知内容。
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process([0x0D]);
        Assert.IsNull(submitted);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void TabCompletion_MarksUnknown()
    {
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("sys"));
        tracker.Process([0x09]);
        Assert.IsNull(tracker.CurrentInput);
    }

    [TestMethod]
    public void CtrlBackspace_KillWord_MarksUnknownButKeepsTrackingNewTyping()
    {
        // Ctrl+Backspace 发的 ESC+DEL(#127)删掉多少字符由 shell 的词边界规则决定
        // (readline 只认字母数字,zsh 的 WORDCHARS 还含 /-_. 等),本地推演必然会与
        // 真实行内容分叉 —— 分叉的缓冲会喂出错误的整行建议。故一律进未知态,
        // 之后键入的字符仍以试探段暴露,降级为追加式词补全。
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("git checkout"));
        tracker.Process([0x1B, 0x7F]);
        Assert.IsNull(tracker.CurrentInput);
        Assert.AreEqual(string.Empty, tracker.TentativeRun, "ESC 序列的尾字节 DEL 不得漏进试探段");

        tracker.Process(Bytes("st"));
        Assert.AreEqual("st", tracker.TentativeRun);

        // 回车把行交给 shell,状态复位为确定的空行(未知态不粘死)。
        tracker.Process([0x0D]);
        Assert.AreEqual(string.Empty, tracker.CurrentInput);
    }

    [TestMethod]
    public void EnterOnUnknownState_DoesNotSubmit()
    {
        var tracker = new TerminalInputTracker();
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.Process([0x1B]);
        tracker.Process([0x0D]);
        Assert.IsNull(submitted);
    }

    [TestMethod]
    public void InjectedInitCommand_WithEscAndTrailingNewline_RecoversToKnownEmpty()
    {
        // 连接初始化注入(补行脚本)含 ESC 字节、以 \n 结尾;ESC 使行不可知,
        // 但 \n 必须把状态复位为确定的空行,否则 SSH 标签的建议从连接起全灭。
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes(" prompt_nl() { read -p $'[6n' -d R -rs _ _ c; }; PROMPT_COMMAND=prompt_nl\n"));
        Assert.AreEqual(string.Empty, tracker.CurrentInput);

        tracker.Process(Bytes("ht"));
        Assert.AreEqual("ht", tracker.CurrentInput);
    }

    [TestMethod]
    public void FunctionKey_ThenTyping_RecoversViaTentativeRun()
    {
        // F10(ESC[21~)后继续键入:整行不可知,但试探段必须干净地拿到 "ht"
        // (序列尾部的可打印字节 "[21~" 不得漏入),降级建议才能继续工作。
        var tracker = new TerminalInputTracker();
        tracker.Process(Bytes("h"));
        tracker.Process([0x1B, (byte)'[', (byte)'2', (byte)'1', (byte)'~']);
        Assert.IsNull(tracker.CurrentInput);
        Assert.AreEqual(string.Empty, tracker.TentativeRun);

        tracker.Process(Bytes("ht"));
        Assert.IsNull(tracker.CurrentInput);
        Assert.AreEqual("ht", tracker.TentativeRun);

        // 退格只回删试探段;再来一个控制键则重置试探段(新一轮编辑)。
        tracker.Process([0x7F]);
        Assert.AreEqual("h", tracker.TentativeRun);
        tracker.Process([0x1B, (byte)'[', (byte)'A']);
        Assert.AreEqual(string.Empty, tracker.TentativeRun);
    }

    [TestMethod]
    public void EnterOnUnknownState_RaisesUnknownLineSubmitted()
    {
        var tracker = new TerminalInputTracker();
        int unknownSubmits = 0;
        string? submitted = null;
        tracker.CommandSubmitted += cmd => submitted = cmd;
        tracker.UnknownLineSubmitted += () => unknownSubmits++;

        tracker.Process([0x1B, (byte)'[', (byte)'A']); // ↑ 召回历史 → 未知态。
        tracker.Process(Bytes("x"));
        tracker.Process([0x0D]);

        Assert.IsNull(submitted, "未知态不得按本地缓冲提交");
        Assert.AreEqual(1, unknownSubmits, "未知态回车应上报,由消费方从屏幕提取命令");
        Assert.AreEqual(string.Empty, tracker.CurrentInput, "回车后回到确定的空行");
    }

    [TestMethod]
    public void InputChanged_FiresOnEdits()
    {
        var tracker = new TerminalInputTracker();
        int fired = 0;
        tracker.InputChanged += () => fired++;
        tracker.Process(Bytes("ab"));
        tracker.Process([0x7F]);
        Assert.AreEqual(2, fired);
    }
}

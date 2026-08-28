using VelaShell.Core.FileTransfer.Model;
using VelaShell.Terminal.FileTransfer;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 命令行传输意图解析:X/YMODEM 在链路上没有引导序列,自动触发只能靠「用户敲了什么」。
/// 这一路信号必须既灵(常见敲法都认)又稳(绝不能把普通命令误判成传输)。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class TransferCommandParserTests
{
    private static void AssertIntent(
        string commandLine,
        TerminalTransferProtocol protocol,
        FileTransferDirection direction)
    {
        TransferCommandIntent? intent = TransferCommandParser.Parse(commandLine);

        Assert.IsNotNull(intent, $"\"{commandLine}\" should parse as a transfer command");
        Assert.AreEqual(protocol, intent.Value.Protocol, commandLine);
        Assert.AreEqual(direction, intent.Value.Direction, commandLine);
    }

    /// <summary>远端 <c>s*</c> 发文件 = 本地接收;<c>r*</c> 收文件 = 本地发送。</summary>
    [TestMethod]
    public void BareTools_MapToProtocolAndDirection()
    {
        AssertIntent("sz report.pdf", TerminalTransferProtocol.ZModem, FileTransferDirection.Receive);
        AssertIntent("rz", TerminalTransferProtocol.ZModem, FileTransferDirection.Send);
        AssertIntent("sx firmware.bin", TerminalTransferProtocol.XModem, FileTransferDirection.Receive);
        AssertIntent("rx out.bin", TerminalTransferProtocol.XModem, FileTransferDirection.Send);
        AssertIntent("sb a.log b.log", TerminalTransferProtocol.YModem, FileTransferDirection.Receive);
        AssertIntent("rb", TerminalTransferProtocol.YModem, FileTransferDirection.Send);
    }

    /// <summary>lrzsz 的 <c>sz</c>/<c>rz</c> 可用开关切到 X/YMODEM(WindTerm 文档里的敲法)。</summary>
    [TestMethod]
    public void VariantFlags_OverrideProtocol()
    {
        AssertIntent("sz -X kernel.img", TerminalTransferProtocol.XModem, FileTransferDirection.Receive);
        AssertIntent("sz --xmodem kernel.img", TerminalTransferProtocol.XModem, FileTransferDirection.Receive);
        AssertIntent("rz --ymodem", TerminalTransferProtocol.YModem, FileTransferDirection.Send);
        AssertIntent("sz --ymodem a b", TerminalTransferProtocol.YModem, FileTransferDirection.Receive);
        AssertIntent("sz -Z x", TerminalTransferProtocol.ZModem, FileTransferDirection.Receive);
    }

    /// <summary>带路径调用、前置环境变量、<c>sudo</c> 都是常见敲法,必须认得。</summary>
    [TestMethod]
    public void PathQualifiedAndPrefixedInvocations_AreRecognized()
    {
        AssertIntent("/usr/bin/sz file", TerminalTransferProtocol.ZModem, FileTransferDirection.Receive);
        AssertIntent("sudo rz", TerminalTransferProtocol.ZModem, FileTransferDirection.Send);
        AssertIntent("LC_ALL=C sz file", TerminalTransferProtocol.ZModem, FileTransferDirection.Receive);
        AssertIntent("  sb   spaced.log ", TerminalTransferProtocol.YModem, FileTransferDirection.Receive);
    }

    /// <summary>
    /// <c>--</c> 之后是文件名而不是开关:名为 <c>-X</c> 的文件不得把协议改掉。
    /// </summary>
    [TestMethod]
    public void FlagsAfterDoubleDash_AreNotParsed()
    {
        AssertIntent("sz -- -X", TerminalTransferProtocol.ZModem, FileTransferDirection.Receive);
    }

    /// <summary>
    /// 误判的代价是把终端交给引擎最多 30 秒,所以宁可漏认也不能错认:
    /// 工具名必须<b>精确</b>匹配,不能是子串或参数。
    /// </summary>
    [TestMethod]
    public void NonTransferCommands_AreRejected()
    {
        foreach (string line in (string[])
                 [
                     "", "   ", "ls -la", "echo sz", "cat rz.txt", "szechuan --spicy",
                     "myrz", "vim sx.c", "git rebase", "grep -r sb ."
                 ])
        {
            Assert.IsNull(TransferCommandParser.Parse(line), $"\"{line}\" must not parse as a transfer command");
        }
    }

    [TestMethod]
    public void NullInput_IsRejected() => Assert.IsNull(TransferCommandParser.Parse(null));
}

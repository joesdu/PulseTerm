using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("CommandSuggestions")]
public class PromptCommandExtractionTests
{
    [TestMethod]
    public void BashPrompt_ExtractsCommand()
    {
        Assert.AreEqual("sudo apt update",
            TerminalTabViewModel.ExtractCommandAfterPrompt("pi@NanoPi-R2S:~$ sudo apt update"));
    }

    [TestMethod]
    public void RootPrompt_ExtractsCommand()
    {
        Assert.AreEqual("systemctl restart nginx",
            TerminalTabViewModel.ExtractCommandAfterPrompt("root@server:/etc# systemctl restart nginx"));
    }

    [TestMethod]
    public void ZshArrowPrompt_ExtractsCommand()
    {
        Assert.AreEqual("ls -la",
            TerminalTabViewModel.ExtractCommandAfterPrompt("❯ ls -la"));
    }

    [TestMethod]
    public void PasswordPromptLine_ReturnsNull()
    {
        // 密码行没有提示符结尾标记 → 不提取,口令不进历史。
        Assert.IsNull(TerminalTabViewModel.ExtractCommandAfterPrompt("[sudo] password for pi:"));
        Assert.IsNull(TerminalTabViewModel.ExtractCommandAfterPrompt("Password:"));
    }

    [TestMethod]
    public void EmptyCommand_ReturnsNull()
    {
        Assert.IsNull(TerminalTabViewModel.ExtractCommandAfterPrompt("pi@NanoPi-R2S:~$ "));
    }

    [TestMethod]
    public void DollarInsideCommand_UsesEarliestPromptMarker()
    {
        // 命令里自己的 "$ " 不应劫持提取:取最早出现的标记(提示符在行首)。
        Assert.AreEqual("echo \"$ x\"",
            TerminalTabViewModel.ExtractCommandAfterPrompt("pi@host:~$ echo \"$ x\""));
    }
}

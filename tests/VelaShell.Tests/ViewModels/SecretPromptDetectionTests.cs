using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 密码类提示行判定(命令补全在口令输入时不得弹出):
/// 冒号结尾 + 密码关键词 双条件;已回显的输入先剥掉再判提示部分。
/// </summary>
[TestClass]
[TestCategory("CommandSuggestions")]
public class SecretPromptDetectionTests
{
    [TestMethod]
    public void SudoPasswordPrompt_IsSecret()
    {
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("[sudo] password for pi: ", ""));
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("[sudo] password for pi:", "abc")); // 无回显,typed 不在屏上
    }

    [TestMethod]
    public void CommonSecretPrompts_AreSecret()
    {
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("root@192.168.1.1's password:", ""));
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("Enter passphrase for key '/home/pi/.ssh/id_ed25519':", ""));
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("Password:", ""));
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("密码：", ""));
        Assert.IsTrue(TerminalTabView.IsSecretPrompt("Verification code:", ""));
    }

    [TestMethod]
    public void ShellPromptWhileTyping_IsNotSecret()
    {
        // 正常提示符,输入已回显:剥掉输入后提示部分不以冒号结尾。
        Assert.IsFalse(TerminalTabView.IsSecretPrompt("pi@NanoPi-R2S:~$ sudo apt", "sudo apt"));
        // 回显尚未到达(SSH 延迟):提示符行原样,同样不以冒号结尾。
        Assert.IsFalse(TerminalTabView.IsSecretPrompt("pi@NanoPi-R2S:~$ ", "sud"));
    }

    [TestMethod]
    public void CommandContainingPasswordWord_IsNotSecret()
    {
        // 命令文本里出现 password 不算:剥掉回显输入后提示是 "$" 结尾。
        Assert.IsFalse(TerminalTabView.IsSecretPrompt("pi@host:~$ grep password /etc/x", "grep password /etc/x"));
    }

    [TestMethod]
    public void ColonWithoutKeyword_IsNotSecret()
    {
        Assert.IsFalse(TerminalTabView.IsSecretPrompt("Downloading:", "abc"));
        Assert.IsFalse(TerminalTabView.IsSecretPrompt("", "abc"));
    }
}

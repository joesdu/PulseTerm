using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 交互提示行判定(命令补全在"回答程序提问"时不得弹出,例如 apt 的 [Y/n]
/// 确认行、覆盖配置文件的 y/N 下按键仍弹智能提示)。覆盖是否类确认与编号选单,
/// 并复用密码类判定;已回显的输入先剥掉再判提示部分。
/// </summary>
[TestClass]
[TestCategory("CommandSuggestions")]
public class InteractivePromptDetectionTests
{
    [TestMethod]
    public void AptContinuePrompt_IsInteractive()
    {
        // apt 安装的确认行,输入 y 时仍弹出补全。
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Do you want to continue? [Y/n] y", "y"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Do you want to continue? [Y/n] ", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Do you want to continue? [Y/n] yes", "yes"));
    }

    [TestMethod]
    public void CommonYesNoPrompts_AreInteractive()
    {
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Continue anyway? [y/N]", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Are you sure? (yes/no)", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Are you sure you want to continue connecting (yes/no/[fingerprint])?", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("是否继续? [Y/n]", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("确定要删除吗? [是/否]", ""));
    }

    [TestMethod]
    public void DpkgConfigOverwritePrompt_IsInteractive()
    {
        // 覆盖配置文件(dpkg conffile),选项字母多。
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("*** config: What do you want to do about it? [Y/I/N/O/D/Z]", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Replace it with the version in the package? [default=N] ?", ""));
    }

    [TestMethod]
    public void OverwriteWithoutShownChoices_IsInteractive()
    {
        // cp -i / rm -i:只问号结尾,未列出选项。
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("cp: overwrite 'config.json'? ", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("rm: remove regular file 'a.txt'? y", "y"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("是否覆盖已存在的文件?", ""));
    }

    [TestMethod]
    public void NumberedSelectionMenu_IsInteractive()
    {
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Press <enter> to keep the current choice, or type selection number: 2", "2"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("Please select a number: ", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("请选择时区,请输入编号:", ""));
    }

    [TestMethod]
    public void SecretPrompts_StillInteractive()
    {
        // 合并判定必须仍拦住密码类。
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("[sudo] password for pi: ", ""));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("密码：", ""));
    }

    [TestMethod]
    public void ReplPrompts_AreInteractive()
    {
        // 具名/高辨识度 REPL 提示符:此时补全给 shell 快捷命令是错的。
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt(">>> print('hi')", "print('hi')")); // Python 主提示
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("... ", ""));                        // Python 续行
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("mysql> select 1", "select 1"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("sqlite> .tables", ".tables"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("MariaDB [test]> show tables", "show tables"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("postgres=# select", "select"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("mydb=> select", "select"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("In [1]: x = 1", "x = 1"));           // IPython
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("(gdb) break main", "break main"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("ipdb> n", "n"));
        Assert.IsTrue(InteractivePromptDetector.IsInteractivePrompt("irb(main):001:0> puts 1", "puts 1"));
    }

    [TestMethod]
    public void BareAngleAndCustomShellPrompts_AreNotInteractive()
    {
        // 裸 ">"(node/R/mongosh)与自定义 shell 提示符无法区分,刻意不拦,以免误伤补全。
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("> require('fs')", "require('fs')")); // node 裸提示
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("> ls", "ls"));                        // PS1='> '
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("pi@host:/tmp/build-# ls", "ls"));     // root shell,cwd 以 - 结尾
    }

    [TestMethod]
    public void NormalShellCommand_IsNotInteractive()
    {
        // 普通 shell 提示符下键入命令:剥掉回显后是 $ 结尾,不拦。
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("pi@NanoPi-R2S:~$ yarn", "yarn"));
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("pi@host:~$ ", ""));
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("", "y"));
    }

    [TestMethod]
    public void GitBranchInPrompt_IsNotInteractive()
    {
        // 主题里的 (feature/xxx) 分支括号不得被当成 (y/n) 选项:段长 >3,且末尾还有 $。
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("~/repo (feature/login)$ git status", "git status"));
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("~/repo (main)$ ls", "ls"));
    }

    [TestMethod]
    public void CommandOutputWithSlashPath_IsNotInteractive()
    {
        // 输出行里出现斜杠路径,但无括号选项、不以问号/冒号+关键词结尾。
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("Cloning into '/home/pi/app'...", ""));
        Assert.IsFalse(InteractivePromptDetector.IsInteractivePrompt("Downloading:", "abc"));
    }
}

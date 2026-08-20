using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 手动输入路径的规整规则(#226)。路径栏能直接敲以后,输入什么样的怪东西都可能发生:
/// 相对路径、<c>..</c> 一路往上、重复斜杠、粘贴时带上的引号。这里把每一条钉住。
/// </summary>
[TestClass]
public class RemotePathInputTests
{
    [TestMethod]
    public void AbsolutePath_PassesThroughNormalized()
    {
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("/var/log", "/home/u", null));
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("/var/log/", "/home/u", null));
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("//var//log//", "/home/u", null));
        Assert.AreEqual("/", RemotePathInput.Normalize("/", "/home/u", null));
    }

    [TestMethod]
    public void RelativePath_ResolvesAgainstCurrentDirectory()
    {
        Assert.AreEqual("/home/u/logs", RemotePathInput.Normalize("logs", "/home/u", null));
        Assert.AreEqual("/home/u/a/b", RemotePathInput.Normalize("a/b", "/home/u/", null));
        Assert.AreEqual("/etc", RemotePathInput.Normalize("etc", "/", null));
    }

    [TestMethod]
    public void DotSegments_Collapse_AndDoNotEscapeRoot()
    {
        Assert.AreEqual("/home", RemotePathInput.Normalize("..", "/home/u", null));
        Assert.AreEqual("/home/u", RemotePathInput.Normalize("./", "/home/u", null));
        Assert.AreEqual("/var", RemotePathInput.Normalize("/home/u/../../var", "/", null));

        // 越过根的 .. 在根处停住,不会拼出 "/.." 这种发不出去的路径。
        Assert.AreEqual("/", RemotePathInput.Normalize("../../../..", "/home", null));
    }

    [TestMethod]
    public void Tilde_ExpandsWhenHomeKnown_AndSurvivesWhenNot()
    {
        Assert.AreEqual("/home/u", RemotePathInput.Normalize("~", "/", "/home/u"));
        Assert.AreEqual("/home/u/logs", RemotePathInput.Normalize("~/logs", "/", "/home/u"));
        Assert.AreEqual("/home/u/logs", RemotePathInput.Normalize("~/logs", "/", "/home/u/"));

        // 家目录未知时不本地瞎猜:~ 当作普通相对段透传,由服务端报出真实错误。
        Assert.AreEqual("/tmp/~/logs", RemotePathInput.Normalize("~/logs", "/tmp", null));

        // ~user 形式无法在本地解析(要读远端 passwd),同样原样透传。
        Assert.AreEqual("/~root", RemotePathInput.Normalize("~root", "/", "/home/u"));
    }

    [TestMethod]
    public void PastedQuotes_AreStripped()
    {
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("\"/var/log\"", "/", null));
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("'/var/log'", "/", null));

        // 只脱整体包裹的一层;单边引号是文件名的一部分,不能动。
        Assert.AreEqual("/var/\"log", RemotePathInput.Normalize("/var/\"log", "/", null));
    }

    [TestMethod]
    public void Backslash_IsAFileNameChar_NotASeparator()
    {
        // POSIX 下 \ 是合法文件名字符;若当成分隔符替换,带反斜杠的目录将永远打不开。
        Assert.AreEqual("/tmp/a\\b", RemotePathInput.Normalize("/tmp/a\\b", "/", null));
    }

    [TestMethod]
    public void BlankInput_MeansNoNavigation()
    {
        Assert.IsNull(RemotePathInput.Normalize(null, "/home", null));
        Assert.IsNull(RemotePathInput.Normalize("   ", "/home", null));
        Assert.IsNull(RemotePathInput.Normalize("\"\"", "/home", null));
    }

    [TestMethod]
    public void SurroundingWhitespace_IsTrimmed()
    {
        Assert.AreEqual("/var/log", RemotePathInput.Normalize("  /var/log  ", "/", null));
        Assert.AreEqual("/var/log", RemotePathInput.Normalize(" \" /var/log \" ", "/", null));
    }
}

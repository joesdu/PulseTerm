using System.Text;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>输入框 <c>@</c> 文件引用的语法:补全 token 识别、目录拆分与发送时的路径提取。</summary>
[TestClass]
public sealed class FileReferenceTests
{
    [TestMethod]
    public void TryFindToken_FindsTokenAtCaret_AfterWhitespaceOnly()
    {
        Assert.IsTrue(FileReference.TryFindToken("看看 @/etc/ho", 12, out int start, out bool quoted, out string token));
        Assert.AreEqual(3, start);
        Assert.IsFalse(quoted);
        Assert.AreEqual("/etc/ho", token);

        Assert.IsFalse(FileReference.TryFindToken("mail@example.com", 16, out _, out _, out _),
            "邮箱里的 @ 前面不是空白,不该当成文件引用");
        Assert.IsFalse(FileReference.TryFindToken("@/etc/hosts 然后呢", 16, out _, out _, out _),
            "光标已经离开 token,不该继续补全");
    }

    [TestMethod]
    public void TryFindToken_SupportsQuotedPathsWithSpaces()
    {
        const string text = "读一下 @\"/opt/my app/conf";
        Assert.IsTrue(FileReference.TryFindToken(text, text.Length, out int start, out bool quoted, out string token));
        Assert.IsTrue(quoted);
        Assert.AreEqual(4, start);
        Assert.AreEqual("/opt/my app/conf", token);
    }

    [TestMethod]
    public void TryFindToken_StopsAtNewLineAndForeignQuotes()
    {
        Assert.IsFalse(FileReference.TryFindToken("@/etc\n再看看", 9, out _, out _, out _));
        Assert.IsFalse(FileReference.TryFindToken("他说\"好\" 之后", 8, out _, out _, out _));
    }

    [TestMethod]
    public void Split_ResolvesDirectoryAndFilter()
    {
        Assert.AreEqual(("/home/tester", "log"), FileReference.Split("log", "/home/tester"));
        Assert.AreEqual(("/etc", "ho"), FileReference.Split("/etc/ho", "/home/tester"));
        Assert.AreEqual(("/", "et"), FileReference.Split("/et", "/home/tester"));
        Assert.AreEqual(("/home/tester/logs", ""), FileReference.Split("~/logs/", "/home/tester"));
        Assert.AreEqual(("/home/tester/logs", "a"), FileReference.Split("logs/a", "/home/tester"));
    }

    [TestMethod]
    public void Parse_ExtractsAbsolutePathsOnly_DedupedAndOrdered()
    {
        List<string> paths = FileReference.Parse("对比 @/etc/hosts 和 @\"/opt/my app/a.conf\",别理 @someone 和 @/etc/hosts");

        Assert.AreSequenceEqual(new[] { "/etc/hosts", "/opt/my app/a.conf" }, paths);
    }

    [TestMethod]
    public void Parse_DropsTrailingSentencePunctuation()
    {
        Assert.AreSequenceEqual(new[] { "/var/log/syslog" }, FileReference.Parse("看看 @/var/log/syslog。"));
        Assert.AreSequenceEqual(new[] { "~/notes.md" }, FileReference.Parse("还有 @~/notes.md, 谢谢"));
    }

    [TestMethod]
    public void Parse_StopsAtCjk_SoChinesePunctuationDoesNotSwallowThePath()
    {
        // 中文里逗号后不空格,路径必须在 CJK 处收住;非 ASCII 路径则要求带引号(补全会自动加)
        Assert.AreSequenceEqual(new[] { "/etc/hosts" }, FileReference.Parse("看 @/etc/hosts,再看别的"));
        Assert.AreSequenceEqual(new[] { "/data/报表.xlsx" }, FileReference.Parse("看 @\"/data/报表.xlsx\",谢谢"));
        Assert.IsTrue(FileReference.NeedsQuoting("/data/报表.xlsx"));
        Assert.IsTrue(FileReference.NeedsQuoting("/opt/my app/a.conf"));
        Assert.IsFalse(FileReference.NeedsQuoting("/etc/hosts"));
    }

    [TestMethod]
    public void Parse_IgnoresUnterminatedQuote()
    {
        Assert.IsEmpty(FileReference.Parse("@\"/opt/still typing"));
    }

    [TestMethod]
    public void Expand_ResolvesHome() => Assert.AreEqual("/home/tester/a.txt", FileReference.Expand("~/a.txt", "/home/tester/"));

    [TestMethod]
    public void LooksBinary_DetectsNulBytes()
    {
        Assert.IsFalse(FileReference.LooksBinary(Encoding.UTF8.GetBytes("plain text\n中文")));
        Assert.IsTrue(FileReference.LooksBinary([0x7f, 0x45, 0x4c, 0x46, 0x00, 0x01]));
    }
}

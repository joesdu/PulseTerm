using VelaShell.Plugin.Redis.Ui;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 过滤条文本 → <c>SCAN MATCH</c> 模式。这是用户输入到服务端命令的唯一转换点,
/// 错一点点的表现就是"我明明有这个键,为什么搜不到"—— 界面上最难自证的一类 bug。
/// </summary>
[TestClass]
public sealed class RedisMatchPatternTests
{
    [TestMethod]
    public void Prefix_AppendsStar()
    {
        Assert.AreEqual("user*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "user"));
    }

    [TestMethod]
    public void Contains_WrapsWithStars()
    {
        Assert.AreEqual("*user*", RedisMatchPattern.Build(RedisMatchMode.Contains, "user"));
    }

    [TestMethod]
    public void Glob_PassesTheInputThroughUntouched()
    {
        // 通配模式下用户输入的**就是**模式 —— 那正是他选这个模式的意思,一个字符都不许改。
        Assert.AreEqual("user:*:profile", RedisMatchPattern.Build(RedisMatchMode.Glob, "user:*:profile"));
    }

    [TestMethod]
    public void EmptyInput_MatchesEverything()
    {
        Assert.AreEqual("*", RedisMatchPattern.Build(RedisMatchMode.Prefix, ""));
        Assert.AreEqual("*", RedisMatchPattern.Build(RedisMatchMode.Contains, "   "));
        Assert.AreEqual("*", RedisMatchPattern.Build(RedisMatchMode.Glob, null));
    }

    [TestMethod]
    public void Prefix_EscapesGlobMetacharacters()
    {
        // 想找字面量 a*b 的用户否则会得到一堆无关的键,而他绝不会想到是自己输入里的
        // 星号被当成了通配符。
        Assert.AreEqual(@"a\*b*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "a*b"));
        Assert.AreEqual(@"a\?b*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "a?b"));
        Assert.AreEqual(@"a\[b\]*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "a[b]"));
        Assert.AreEqual(@"a\\b*", RedisMatchPattern.Build(RedisMatchMode.Prefix, @"a\b"));
        Assert.AreEqual(@"a\^b*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "a^b"));
    }

    [TestMethod]
    public void Contains_EscapesGlobMetacharacters()
    {
        Assert.AreEqual(@"*a\*b*", RedisMatchPattern.Build(RedisMatchMode.Contains, "a*b"));
    }

    [TestMethod]
    public void Glob_DoesNotEscape()
    {
        Assert.AreEqual("a*b", RedisMatchPattern.Build(RedisMatchMode.Glob, "a*b"));
    }

    [TestMethod]
    public void Input_IsTrimmed()
    {
        // 从别处粘过来的键名常带首尾空白;不去掉就会得到一个永远匹配不到东西的模式。
        Assert.AreEqual("user*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "  user  "));
    }

    [TestMethod]
    public void ColonsAreNotEscaped()
    {
        // 冒号不是 glob 元字符,转义它会让 user: 变成一个匹配不到任何键的模式。
        Assert.AreEqual("user:10086*", RedisMatchPattern.Build(RedisMatchMode.Prefix, "user:10086"));
    }
}

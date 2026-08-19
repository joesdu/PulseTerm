namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 控制台输入行的分词。错一点点的表现是"我明明带了引号,为什么值被切开了" ——
/// 而参数里带空格的键值在 Redis 里非常常见。
/// </summary>
[TestClass]
public sealed class RedisCommandLineTests
{
    private static IReadOnlyList<string> Split(string line)
    {
        Assert.IsTrue(RedisCommandLine.TrySplit(line, out IReadOnlyList<string> args, out string error), error);
        return args;
    }

    [TestMethod]
    public void Whitespace_SeparatesArguments()
    {
        CollectionAssert.AreEqual(new[] { "SET", "a", "1" }, Split("SET a 1").ToArray());
    }

    [TestMethod]
    public void RepeatedWhitespace_DoesNotProduceEmptyArguments()
    {
        CollectionAssert.AreEqual(new[] { "SET", "a", "1" }, Split("  SET   a \t 1  ").ToArray());
    }

    [TestMethod]
    public void DoubleQuotes_KeepSpacesTogether()
    {
        CollectionAssert.AreEqual(new[] { "SET", "my key", "hello world" }, Split("SET \"my key\" \"hello world\"").ToArray());
    }

    [TestMethod]
    public void SingleQuotes_AreLiteral()
    {
        // 单引号内不认转义 —— 与 redis-cli 一致。
        CollectionAssert.AreEqual(new[] { "SET", "a", @"a\nb" }, Split(@"SET a 'a\nb'").ToArray());
    }

    [TestMethod]
    public void DoubleQuotes_HonorEscapes()
    {
        IReadOnlyList<string> args = Split(@"SET a ""line1\nline2\ttab\\slash\""quote""");

        Assert.AreEqual("line1\nline2\ttab\\slash\"quote", args[2]);
    }

    [TestMethod]
    public void HexEscape_PutsBinaryIntoAnArgument()
    {
        // \xNN 是把二进制值敲进控制台的唯一途径 —— 键与值都是字节串。
        IReadOnlyList<string> args = Split(@"SET k ""\x00\xff""");

        Assert.AreEqual(2, args[2].Length);
        Assert.AreEqual('\x00', args[2][0]);
        Assert.AreEqual('ÿ', args[2][1]);
    }

    [TestMethod]
    public void UnknownEscape_IsTakenLiterally()
    {
        IReadOnlyList<string> args = Split(@"SET a ""\q""");

        Assert.AreEqual("q", args[2]);
    }

    [TestMethod]
    public void QuotesInsideAToken_AreConcatenated()
    {
        // redis-cli 的行为:a"b c"d 是一个参数 abc d。
        IReadOnlyList<string> args = Split("SET a\"b c\"d 1");

        Assert.AreEqual("ab cd", args[1]);
    }

    [TestMethod]
    public void UnbalancedQuotes_AreReported()
    {
        Assert.IsFalse(RedisCommandLine.TrySplit("SET a \"unclosed", out _, out string error));
        Assert.AreEqual("unbalanced-quotes", error);
    }

    [TestMethod]
    public void EmptyLine_YieldsNoArguments()
    {
        Assert.IsTrue(RedisCommandLine.TrySplit("   ", out IReadOnlyList<string> args, out _));
        Assert.IsEmpty(args);
    }

    [TestMethod]
    public void EmptyQuotedArgument_IsPreserved()
    {
        // SET k "" 要真的写一个空字符串,而不是把参数丢掉。
        IReadOnlyList<string> args = Split("SET k \"\"");

        Assert.HasCount(3, args);
        Assert.AreEqual(string.Empty, args[2]);
    }
}

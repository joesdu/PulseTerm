using System.Text;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 键名的二进制安全性。这是多数 Redis 图形客户端**静默改坏用户数据**的地方:
/// 把字节串按 UTF-8 解码成字符串再编回去,非法序列被替换字符顶掉,
/// 于是"重命名"出来的是另一个键。这一组测试就是那条防线。
/// </summary>
[TestClass]
public sealed class RedisKeyNameTests
{
    [TestMethod]
    public void Raw_RoundTripsExactly()
    {
        byte[] raw = [0x75, 0x73, 0x65, 0x72, 0xFF, 0xFE, 0x00, 0x41];

        var key = new RedisKeyName(raw);

        CollectionAssert.AreEqual(raw, key.Raw.ToArray(), "原始字节必须一字不差地保留下来。");
    }

    [TestMethod]
    public void Display_EscapesNonPrintableBytes()
    {
        var key = new RedisKeyName([(byte)'a', 0x00, 0xFF, (byte)'b']);

        Assert.AreEqual("a\\x00\\xffb", key.Display);
    }

    [TestMethod]
    public void Display_EscapesQuotesAndBackslashes()
    {
        var key = new RedisKeyName("a\"b\\c");

        Assert.AreEqual("a\\\"b\\\\c", key.Display);
    }

    [TestMethod]
    public void Display_EscapesWhitespaceControlCharacters()
    {
        var key = new RedisKeyName("a\nb\tc");

        Assert.AreEqual("a\\nb\\tc", key.Display);
    }

    [TestMethod]
    public void Text_ForValidUtf8_IsTheDecodedString()
    {
        var key = new RedisKeyName("用户:10086");

        Assert.IsTrue(key.IsUtf8);
        Assert.AreEqual("用户:10086", key.Text);
    }

    [TestMethod]
    public void Text_ForInvalidUtf8_FallsBackToEscapedForm()
    {
        // 退回转义形式而不是带 U+FFFD 的近似值:一个看起来正常却其实不对的键名
        // 比一串 \xNN 危险得多。
        var key = new RedisKeyName([0xC3, 0x28]);

        Assert.IsFalse(key.IsUtf8);
        Assert.AreEqual(key.Display, key.Text);
    }

    [TestMethod]
    public void Text_ForValidUtf8ContainingControlChars_FallsBackToEscapedForm()
    {
        // 合法 UTF-8 但含控制字符:直接进列表会把行高与对齐搞乱。
        var key = new RedisKeyName([(byte)'a', 0x07, (byte)'b']);

        Assert.IsFalse(key.IsUtf8);
        Assert.AreEqual("a\\ab", key.Text);
    }

    [TestMethod]
    public void Segments_SplitsOnDelimiter()
    {
        var key = new RedisKeyName("user:10086:profile");

        CollectionAssert.AreEqual(new[] { "user", "10086", "profile" }, key.Segments(":"));
    }

    [TestMethod]
    public void Segments_WithoutDelimiter_IsOneSegment()
    {
        var key = new RedisKeyName("lonely");

        CollectionAssert.AreEqual(new[] { "lonely" }, key.Segments(":"));
    }

    [TestMethod]
    public void Segments_EmptyDelimiter_IsOneSegment()
    {
        var key = new RedisKeyName("a:b");

        CollectionAssert.AreEqual(new[] { "a:b" }, key.Segments(string.Empty));
    }

    [TestMethod]
    public void Equality_IsByBytes_NotByDisplayText()
    {
        // 去重靠它:SCAN 在 rehash 期间会返回重复键。而两个不同的字节串可能解出
        // 同一个替换字符串 —— 按文本比就会把两个不同的键判成同一个。
        var first = new RedisKeyName([0xC3, 0x28]);
        var second = new RedisKeyName([0xC3, 0x29]);
        var sameAsFirst = new RedisKeyName([0xC3, 0x28]);

        Assert.AreEqual(first, sameAsFirst);
        Assert.AreEqual(first.GetHashCode(), sameAsFirst.GetHashCode());
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void ToRedisKey_CarriesTheOriginalBytes()
    {
        // 交给库的也必须是原始字节 —— 这一步一旦经过字符串,前面的努力全白费。
        byte[] raw = [0x01, 0x02, 0xFF];

        var redisKey = new RedisKeyName(raw).ToRedisKey();

        CollectionAssert.AreEqual(raw, (byte[]?)redisKey);
    }

    [TestMethod]
    public void StringConstructor_UsesUtf8()
    {
        var key = new RedisKeyName("中");

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("中"), key.Raw.ToArray());
    }
}

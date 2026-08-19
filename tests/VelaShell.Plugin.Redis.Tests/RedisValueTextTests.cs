using System.Text;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 值的字节 ↔ 文本转换。这一层守的是一条硬规矩:**看到什么就存回什么**。
/// <para>
/// 它存在的理由是一个真实的数据损坏路径:值按 UTF-8 解码显示、保存时再按 UTF-8 编回去。
/// 二进制值在解码那一步就已经变形,于是"保存"实际上是"用一段近似值覆盖原值",
/// 而用户全程看不出异常。这里逐条钉住往返。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisValueTextTests
{
    /// <summary>gzip 头:最常见的一种"存在 Redis 里的二进制"。</summary>
    private static readonly byte[] Gzip = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03];

    [TestMethod]
    public void IsTextSafe_PlainText_IsText() =>
        Assert.IsTrue(RedisValueText.IsTextSafe(Encoding.UTF8.GetBytes("hello 世界")));

    /// <summary>
    /// 换行 / 制表要当文本 —— 值编辑器就是个多行输入框。
    /// 键名那边一律转义(带换行的键名会把列表行高搞乱),值这边不能照抄那条规则。
    /// </summary>
    [TestMethod]
    public void IsTextSafe_MultilineJson_IsStillText()
    {
        byte[] raw = Encoding.UTF8.GetBytes("{\n  \"a\": 1,\n\t\"b\": 2\n}");

        Assert.IsTrue(RedisValueText.IsTextSafe(raw));
        Assert.AreEqual("{\n  \"a\": 1,\n\t\"b\": 2\n}", RedisValueText.Render(raw, RedisValueFormat.Text));
        Assert.AreEqual(RedisValueFormat.Text, RedisValueText.Detect(raw));
    }

    [TestMethod]
    public void IsTextSafe_InvalidUtf8_IsNot() =>
        Assert.IsFalse(RedisValueText.IsTextSafe([0xC3, 0x28]));

    /// <summary>NUL / BEL 这类控制字符在文本框里不可见却真实存在,编辑时会被悄悄吃掉。</summary>
    [TestMethod]
    public void IsTextSafe_InvisibleControlBytes_AreNotText() =>
        Assert.IsFalse(RedisValueText.IsTextSafe(Encoding.UTF8.GetBytes("a\0b")));

    [TestMethod]
    public void Detect_BinaryValue_FallsBackToEscapedNotText() =>
        Assert.AreEqual(RedisValueFormat.Escaped, RedisValueText.Detect(Gzip));

    /// <summary>**这条是整个类型存在的理由**:转义 → 解回,必须一个字节不差。</summary>
    [TestMethod]
    public void EscapeThenUnescape_RoundTripsExactly()
    {
        foreach (byte[] raw in new[]
                 {
                     Gzip,
                     [0xC3, 0x28],
                     Encoding.UTF8.GetBytes("plain"),
                     Encoding.UTF8.GetBytes("双引号\" 反斜杠\\ 换行\n 制表\t"),
                     [.. Enumerable.Range(0, 256).Select(b => (byte)b)]
                 })
        {
            string escaped = RedisValueText.Escape(raw);
            Assert.IsTrue(RedisValueText.TryUnescape(escaped, out byte[] back, out string? error), error);
            CollectionAssert.AreEqual(raw, back, $"往返丢字节:{escaped}");
        }
    }

    /// <summary>
    /// 这条复现的是修复前的真实损坏路径:转义文本被当成普通文本按 UTF-8 编码写回去。
    /// 十个字节的 gzip 头会变成四十个字节的 ASCII 字面量 —— 值被彻底毁掉,而界面毫无异常。
    /// </summary>
    [TestMethod]
    public void EscapedText_EncodedAsPlainUtf8_DestroysTheValue()
    {
        string escaped = RedisValueText.Escape(Gzip);
        byte[] naive = Encoding.UTF8.GetBytes(escaped);

        Assert.AreNotEqual(Gzip.Length, naive.Length);
        CollectionAssert.AreNotEqual(Gzip, naive);
        // 正确的那条路必须还原原值。
        Assert.IsTrue(RedisValueText.TryUnescape(escaped, out byte[] correct, out _));
        CollectionAssert.AreEqual(Gzip, correct);
    }

    [TestMethod]
    public void Unescape_UnknownEscape_FailsInsteadOfGuessing()
    {
        Assert.IsFalse(RedisValueText.TryUnescape(@"ok\q", out _, out string? error));
        Assert.Contains("unknown escape", error!);
    }

    [TestMethod]
    public void Unescape_TruncatedHexEscape_Fails()
    {
        Assert.IsFalse(RedisValueText.TryUnescape(@"\x1", out _, out string? error));
        Assert.Contains(@"bad \x", error!);
    }

    [TestMethod]
    public void Unescape_DanglingBackslash_Fails() =>
        Assert.IsFalse(RedisValueText.TryUnescape(@"abc\", out _, out _));

    /// <summary>转义模式里也应该能直接键入非 ASCII,而不必自己敲 \xNN。</summary>
    [TestMethod]
    public void Unescape_LiteralNonAscii_EncodesAsUtf8()
    {
        Assert.IsTrue(RedisValueText.TryUnescape("中", out byte[] bytes, out _));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("中"), bytes);
    }

    [TestMethod]
    public void HexDump_ShowsOffsetHexAndAsciiGutter()
    {
        string dump = RedisValueText.HexDump(Encoding.UTF8.GetBytes("Hello, Redis!"));

        Assert.StartsWith("00000000  ", dump);
        Assert.Contains("48 65 6c 6c 6f", dump);
        Assert.Contains("|Hello, Redis!|", dump);
    }

    [TestMethod]
    public void HexDump_UnprintableBytes_ShowAsDotsInTheGutter()
    {
        string dump = RedisValueText.HexDump(Gzip);

        Assert.Contains("1f 8b 08", dump);
        Assert.Contains("|..........|", dump);
    }

    [TestMethod]
    public void HexDump_Empty_IsEmpty() => Assert.AreEqual(string.Empty, RedisValueText.HexDump([]));
}

using System.Text;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 内置远程编辑器的编码判定 —— 这是"别把用户的文件改坏"那条防线。
/// </summary>
/// <remarks>
/// 修复的是一个**破坏性**缺陷:原先只认 BOM,其余一律按 UTF-8 解;而 UTF-8 解码器
/// 默认静默把非法字节换成 U+FFFD。于是打开一个 GBK 文件时每个中文字都变成 �,
/// 用户随手一存,原文件就永久损坏,全程没有任何提示。
/// </remarks>
[TestClass]
[TestCategory("Editor")]
public sealed class EditorEncodingDetectorTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        // GBK / Big5 / Shift_JIS 都在旧代码页里,不注册取不到。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Gbk => Encoding.GetEncoding("GBK");

    [TestMethod]
    public void Utf8Bom_IsDetectedAndSkipped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("你好")];

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.AreEqual(3, result.PreambleLength);
        Assert.IsFalse(result.FellBackToSessionEncoding);
        Assert.AreEqual("你好", EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void Utf16LeBom_IsDetected()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello")];

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.AreEqual(2, result.PreambleLength);
        Assert.AreEqual("hello", EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void Utf16BeBom_IsDetected()
    {
        byte[] bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("hello")];

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.AreEqual(2, result.PreambleLength);
        Assert.AreEqual("hello", EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void BomlessValidUtf8_StaysUtf8()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("# 配置文件\nkey = value\n");

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.AreEqual(0, result.PreambleLength);
        Assert.IsFalse(result.FellBackToSessionEncoding, "合法 UTF-8 不该被判成会话编码。");
        Assert.AreEqual("# 配置文件\nkey = value\n", EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void PlainAscii_StaysUtf8()
    {
        byte[] bytes = "server { listen 80; }"u8.ToArray();

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.IsFalse(result.FellBackToSessionEncoding);
        Assert.AreEqual("server { listen 80; }", EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void GbkFile_FallsBackToTheSessionEncoding_AndRoundTripsIntact()
    {
        // 这条就是那个数据损坏缺陷的回归:按 UTF-8 解出来会是一串 �,
        // 存回去原文件就毁了。
        const string original = "配置文件:中文注释";
        byte[] bytes = Gbk.GetBytes(original);

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.IsTrue(result.FellBackToSessionEncoding, "非法 UTF-8 应当回落到会话编码。");
        string decoded = EditorEncodingDetector.Decode(bytes, result);
        Assert.AreEqual(original, decoded);
        Assert.DoesNotContain("�", decoded, StringComparison.Ordinal, "解出替换字符就意味着存回去会毁文件。");
        CollectionAssert.AreEqual(bytes, result.Encoding.GetBytes(decoded), "解码再编码必须回到原字节。");
    }

    [TestMethod]
    public void GbkFile_WithNoSessionEncoding_DoesNotClaimAFallback()
    {
        byte[] bytes = Gbk.GetBytes("中文");

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, sessionEncoding: null);

        Assert.IsFalse(result.FellBackToSessionEncoding, "没有会话编码可回落时不该谎报回落。");
        Assert.AreEqual(0, result.PreambleLength);
    }

    [TestMethod]
    public void EmptyFile_IsHandled()
    {
        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect([], Gbk);

        Assert.AreEqual(0, result.PreambleLength);
        Assert.AreEqual(string.Empty, EditorEncodingDetector.Decode([], result));
    }

    [TestMethod]
    public void BomOnlyFile_DecodesToEmpty()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF];

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.AreEqual(string.Empty, EditorEncodingDetector.Decode(bytes, result));
    }

    [TestMethod]
    public void DetectedUtf8_NeverEmitsABomOnSave()
    {
        // 无 BOM 的文件存回去也不该长出一个 BOM —— 那会让 shell 脚本的 shebang 失效。
        byte[] bytes = Encoding.UTF8.GetBytes("#!/bin/sh\necho hi\n");

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.IsEmpty(result.Encoding.GetPreamble());
    }

    [TestMethod]
    public void DetectedUtf8Bom_KeepsTheBomOnSave()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "x"u8.ToArray()];

        EditorEncodingDetector.Result result = EditorEncodingDetector.Detect(bytes, Gbk);

        Assert.HasCount(3, result.Encoding.GetPreamble(), "原来有 BOM 的文件,存回去要保住它。");
    }

    [TestMethod]
    public void Detect_OnNullBytes_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => EditorEncodingDetector.Detect(null!, Gbk));
}

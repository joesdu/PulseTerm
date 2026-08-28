using System.Globalization;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 侧栏时间戳/行号的栈上格式化回归:这两段文本原先用字符串拼接产生
/// (<c>"[" + ts.ToString("HH:mm:ss") + "] "</c>、<c>n.ToString().PadLeft(5) + " "</c>),
/// 为消掉"每个可见行、每一帧两三个中间 string"改成写进调用方的栈缓冲。
/// 这里逐字锁死与原写法的等价性 —— 手写的右对齐逻辑没有断言守着就是隐患。
/// </summary>
[TestClass]
[TestCategory("Gutter")]
public class GutterTextFormattingTests
{
    private const int NumberBufferSize = GutterLayout.NumberDigits + 16;
    private const int StampBufferSize = 8 + 3; // "HH:mm:ss" 8 位 + '[' + ']' + 尾随空格

    [TestMethod]
    public void FormatGutterTimestamp_MatchesLegacyConcatenation()
    {
        DateTime[] samples =
        [
            new(2026, 8, 28, 0, 0, 0),
            new(2026, 8, 28, 9, 5, 3),
            new(2026, 8, 28, 23, 59, 59),
            new(2026, 1, 1, 12, 34, 56)
        ];
        Span<char> buffer = stackalloc char[StampBufferSize];
        foreach (DateTime ts in samples)
        {
            string expected = "[" + ts.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] ";
            string actual = new(VelaTerminalControl.FormatGutterTimestamp(ts, buffer));
            Assert.AreEqual(expected, actual, $"时间戳 {ts:O} 的侧栏文本与原拼接写法不一致。");
        }
    }

    [TestMethod]
    public void FormatGutterLineNumber_MatchesLegacyPadLeft()
    {
        // 覆盖:补空格档(1 位到 5 位)、恰好占满、以及超宽自然变宽(6 位起不截断)。
        int[] rows = [0, 8, 98, 998, 9998, 9999, 99998, 999998, 9999998, int.MaxValue - 1];
        Span<char> buffer = stackalloc char[NumberBufferSize];
        foreach (int row in rows)
        {
            string expected = (row + 1)
                .ToString(CultureInfo.InvariantCulture)
                .PadLeft(GutterLayout.NumberDigits) + " ";
            string actual = new(VelaTerminalControl.FormatGutterLineNumber(row, buffer));
            Assert.AreEqual(expected, actual, $"行号 {row + 1} 的侧栏文本与原 PadLeft 写法不一致。");
        }
    }

    [TestMethod]
    public void FormatGutterLineNumber_ReusedBuffer_LeavesNoResidueFromWiderRow()
    {
        // 缓冲跨行复用:先写一个宽行号,再写一个窄的,窄的绝不能带出上一行的尾巴。
        Span<char> buffer = stackalloc char[NumberBufferSize];
        _ = VelaTerminalControl.FormatGutterLineNumber(9999998, buffer);
        string narrow = new(VelaTerminalControl.FormatGutterLineNumber(0, buffer));
        Assert.AreEqual("    1 ", narrow, "复用缓冲后残留了上一行的数字。");
    }
}

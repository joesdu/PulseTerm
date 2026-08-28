using System.Runtime.CompilerServices;
using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// <see cref="TerminalCell" /> 的内存布局与组合标记驻留池回归:
/// 回滚缓冲可达数百万格,这里锁住"单元格不含托管引用(GC 不逐格扫描)"的优化成果。
/// 组合字符一律用 \u 转义书写,避免源文件编码链偷偷把 e+U+0301 规范化成预组合字符。
/// </summary>
[TestClass]
[TestCategory("CellMemory")]
public class TerminalCellMemoryTests
{
    private const string AcuteAccent = "\u0301"; // 组合尖音符
    private const string GraveAccent = "\u0300"; // 组合重音符
    private const string Diaeresis = "\u0308";   // 组合分音符

    [TestMethod]
    public void TerminalCell_ContainsNoManagedReferences()
    {
        // 这是本结构的内存契约:一旦有人往 cell 里加回引用类型字段,
        // 整个回滚缓冲会重新变成 GC 扫描对象,数百万格的代价悄然回归。
        Assert.IsFalse(
            RuntimeHelpers.IsReferenceOrContainsReferences<TerminalCell>(),
            "TerminalCell 必须保持 blittable:组合标记等引用数据请走 CombiningPool 驻留索引。");
    }

    [TestMethod]
    public void TerminalCell_StaysWithinPackedSize()
    {
        // 另一半内存契约:单元格必须保持 16 字节。回滚缓冲默认每标签页 1 万行 × 上百列,
        // 每涨 4 字节就是每标签页数 MB。当前布局 = Rune(4) + 前景(4) + 背景(4)
        // + CombiningIndex(2) + Flags(2);任何一个字段放宽都会被这条断言挡下。
        // 历史:TerminalColor 曾是 5 个独立字节字段(结构本身 5 字节),两份加对齐把单元格
        // 撑到 20 字节;打包成单个 uint 后降到 16。
        Assert.AreEqual(
            16,
            Unsafe.SizeOf<TerminalCell>(),
            "TerminalCell 超出 16 字节:回滚缓冲的内存开销会按比例上涨,请检查新增/放宽的字段。");
    }

    [TestMethod]
    public void TerminalColor_PackedRoundTrip_PreservesAllKinds()
    {
        // 打包后 Kind/Index/RGB 全走位运算解出,这里锁住三种 Kind 的往返与相等性语义:
        // 各 Kind 下未使用的位必须恒为 0,否则"比较 packed"就不再等价于逐字段比较。
        Assert.IsTrue(TerminalColor.Default.IsDefault);
        Assert.AreEqual(TerminalColorKind.Default, TerminalColor.Default.Kind);
        Assert.AreEqual(0, (int)TerminalColor.Default.Index);

        var indexed = TerminalColor.FromIndex(196);
        Assert.AreEqual(TerminalColorKind.Indexed, indexed.Kind);
        Assert.AreEqual(196, (int)indexed.Index);
        Assert.AreEqual(0, (int)indexed.R, "Indexed 色的 RGB 通道必须读作 0(与打包前语义一致)。");
        Assert.AreEqual(0, (int)indexed.G);
        Assert.AreEqual(0, (int)indexed.B);
        Assert.IsFalse(indexed.IsDefault);

        var rgb = TerminalColor.FromRgb(0x12, 0x34, 0x56);
        Assert.AreEqual(TerminalColorKind.Rgb, rgb.Kind);
        Assert.AreEqual(0x12, (int)rgb.R);
        Assert.AreEqual(0x34, (int)rgb.G);
        Assert.AreEqual(0x56, (int)rgb.B);
        Assert.AreEqual(0, (int)rgb.Index, "Rgb 色的调色板索引必须读作 0。");

        // 索引钳制。
        Assert.AreEqual(255, (int)TerminalColor.FromIndex(999).Index);
        Assert.AreEqual(0, (int)TerminalColor.FromIndex(-5).Index);

        // 跨 Kind 绝不相等,即使低位数值相同(Indexed 5 vs Rgb 0,0,5)。
        Assert.AreNotEqual(TerminalColor.FromIndex(5), TerminalColor.FromRgb(0, 0, 5));
        Assert.AreEqual(TerminalColor.FromIndex(5), TerminalColor.FromIndex(5));
        Assert.AreEqual(TerminalColor.FromRgb(1, 2, 3), TerminalColor.FromRgb(1, 2, 3));
        Assert.AreEqual(TerminalColor.FromIndex(5).GetHashCode(), TerminalColor.FromIndex(5).GetHashCode());
    }

    [TestMethod]
    public void Combining_RoundTripsThroughPool_AndPreservesEqualitySemantics()
    {
        TerminalCell cell = TerminalCell.Empty;
        cell.Rune = 'e';
        Assert.IsNull(cell.Combining);

        cell.Combining = AcuteAccent;
        Assert.AreEqual(AcuteAccent, cell.Combining);

        TerminalCell same = TerminalCell.Empty;
        same.Rune = 'e';
        same.Combining = AcuteAccent;
        Assert.AreEqual(cell, same, "相同组合标记经驻留后必须等值(同串同索引)。");

        TerminalCell different = TerminalCell.Empty;
        different.Rune = 'e';
        different.Combining = GraveAccent;
        Assert.AreNotEqual(cell, different);

        cell.Combining = null;
        Assert.IsNull(cell.Combining);
        Assert.AreEqual(0, (int)cell.CombiningIndex);
    }

    [TestMethod]
    public void AppendTo_MatchesAppendText()
    {
        // AppendTo 是 AppendText 的零分配孪生实现(渲染与搜索热路径用它)。
        // 两套代码并行存在就必须有断言钉住语义一致,否则迟早各走各的。
        (int Rune, string? Combining, CellFlags Flags)[] samples =
        [
            (0, null, CellFlags.None),                       // 空格
            ('a', null, CellFlags.None),                     // ASCII
            ('中', null, CellFlags.None),                    // BMP 宽字符
            (0x1F600, null, CellFlags.None),                 // 补充平面(代理对)
            ('e', AcuteAccent, CellFlags.None),              // 基础字符 + 组合标记
            ('a', AcuteAccent + Diaeresis, CellFlags.None),  // 多个组合标记
            (0x1F600, GraveAccent, CellFlags.None),          // 代理对 + 组合标记
            ('X', null, CellFlags.WideTrailing)              // 宽字符尾格:两者都不产出内容
        ];

        Span<char> buffer = stackalloc char[16];
        foreach ((int rune, string? combining, CellFlags flags) in samples)
        {
            TerminalCell cell = TerminalCell.Empty;
            cell.Rune = rune;
            cell.Combining = combining;
            cell.Flags = flags;

            var sb = new StringBuilder();
            cell.AppendText(sb);
            int written = cell.AppendTo(buffer);
            Assert.IsGreaterThanOrEqualTo(0, written, "样例应当放得进 16 字符缓冲。");
            Assert.AreEqual(
                sb.ToString(),
                new string(buffer[..written]),
                $"AppendTo 与 AppendText 对 rune=0x{rune:X}、combining={combining ?? "<null>"} 的产出不一致。");
        }
    }

    [TestMethod]
    public void AppendTo_ReturnsNegativeOne_WhenBufferTooSmall()
    {
        // 缓冲不足必须报 -1 让调用方扩容重试,而不是截断 —— 截断会让语义匹配/搜索悄悄错位。
        TerminalCell cell = TerminalCell.Empty;
        cell.Rune = 0x1F600; // 需要 2 个字符
        Span<char> tooSmall = stackalloc char[1];
        Assert.AreEqual(-1, cell.AppendTo(tooSmall));

        cell.Combining = AcuteAccent; // 现在需要 3 个
        Span<char> stillTooSmall = stackalloc char[2];
        Assert.AreEqual(-1, cell.AppendTo(stillTooSmall));
    }

    [TestMethod]
    public void Combining_AppendText_EmitsBaseRunePlusMarks()
    {
        TerminalCell cell = TerminalCell.Empty;
        cell.Rune = 'a';
        cell.Combining = Diaeresis;
        var sb = new StringBuilder();
        cell.AppendText(sb);
        Assert.AreEqual("a" + Diaeresis, sb.ToString());
        Assert.AreEqual("a" + Diaeresis, cell.GetText());
    }

    [TestMethod]
    public void Emulator_CombiningMark_FoldsIntoPrecedingCell()
    {
        // 端到端:经 VT 流写入的组合字符仍折叠进基础格(驻留池改造不改变行为)。
        var emulator = new TerminalEmulator(20, 4);
        emulator.Feed(Encoding.UTF8.GetBytes("e" + AcuteAccent));
        TerminalCell cell = emulator.Screen.ViewLine(0)[0];
        Assert.AreEqual('e', cell.Rune);
        Assert.AreEqual(AcuteAccent, cell.Combining);
        Assert.AreEqual("e" + AcuteAccent, cell.GetText());
    }
}

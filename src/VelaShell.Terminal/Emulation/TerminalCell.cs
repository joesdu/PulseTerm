using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端网格中的一个字符单元格。采用值类型,使行可被存为连续数组并被廉价清空。
/// </summary>
/// <remarks>
/// <b>字段声明顺序即内存布局,不要随手调整。</b>本结构含自定义结构体字段
/// (<see cref="TerminalColor" />),此时 CLR 的自动布局不再按大小重排字段、而是沿用声明顺序;
/// 把两个 2 字节字段(<see cref="CombiningIndex" />、<see cref="Flags" />)分开写会各自吃掉
/// 一个 4 字节槽,结构从 16 字节涨到 20。当前顺序 4+4+4+2+2 = 16 字节,恰好塞满。
/// 回滚缓冲默认每标签页 1 万行 × 上百列,这 4 字节按整个缓冲区放大。
/// 由 <c>TerminalCellMemoryTests.TerminalCell_StaysWithinPackedSize</c> 把守。
/// </remarks>
public struct TerminalCell : IEquatable<TerminalCell>
{
    /// <summary>
    /// 该单元格中显示的 Unicode 标量值。0 表示空单元格(渲染为空格)。
    /// 组合字符通过 <see cref="Combining" /> 折叠进基础单元格。
    /// </summary>
    public int Rune;

    /// <summary>单元格的前景(文字)颜色。</summary>
    public TerminalColor Foreground;

    /// <summary>单元格的背景颜色。</summary>
    public TerminalColor Background;

    /// <summary>
    /// 组合标记在 <see cref="CombiningPool" /> 中的索引;0 = 无。存索引而非字符串引用,
    /// 使本结构不含托管引用:回滚缓冲的数百万格从此不被 GC 逐格扫描,每格再省 4 字节。
    /// <para>
    /// 宽度取 <c>ushort</c> 而非 <c>int</c>:与打包成 4 字节的 <see cref="TerminalColor" /> 合力,
    /// 把本结构从 20 字节压到 16 字节(回滚缓冲省 20% 内存)。池上限
    /// <c>CombiningPool.MaxEntries</c> 已相应对齐为 <see cref="ushort.MaxValue" />。
    /// </para>
    /// </summary>
    public ushort CombiningIndex;

    /// <summary>单元格的渲染属性位(加粗、反显、宽字符尾格等)。</summary>
    /// <remarks>紧跟 <see cref="CombiningIndex" /> 声明:两个 2 字节字段合用一个槽,见类型注释。</remarks>
    public CellFlags Flags;

    /// <summary>追加在 <see cref="Rune" /> 之后的可选组合标记。常见情况下为 Null。</summary>
    public string? Combining
    {
        readonly get => CombiningPool.Get(CombiningIndex);
        set => CombiningIndex = CombiningPool.Intern(value);
    }

    /// <summary>一个使用默认颜色、无属性的空单元格。</summary>
    public static TerminalCell Empty => new()
    {
        Rune = 0,
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
        Flags = CellFlags.None
    };

    /// <summary>仍带有给定背景/属性的空白单元格(擦除时使用)。</summary>
    public static TerminalCell Blank(TerminalColor background, CellFlags flags) =>
        new()
        {
            Rune = 0,
            Foreground = TerminalColor.Default,
            Background = background,
            Flags = flags & CellFlags.Inverse // 擦除后的单元格仅保留影响背景的位
        };

    /// <summary>是否为宽字符所占据的第二个(尾随)单元格。</summary>
    public readonly bool IsWideTrailing => (Flags & CellFlags.WideTrailing) != 0;

    /// <summary>物化该单元格的文本(基础字符加任何组合标记)。</summary>
    public readonly string GetText()
    {
        if (Rune == 0)
        {
            return " ";
        }
        if (Combining is null)
        {
            return char.ConvertFromUtf32(Rune);
        }
        return char.ConvertFromUtf32(Rune) + Combining;
    }

    /// <summary>
    /// 把单元格文本(基础字符及组合标记)写入 <paramref name="destination" />,返回写入的字符数;
    /// 宽字符尾格写入 0 个字符。空间不足时不写入任何内容并返回 -1(调用方据此扩容重试)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AppendText(StringBuilder)" /> 语义逐字一致,只是写进调用方自备的缓冲。
    /// 渲染热路径(<c>VelaTerminalControl.ComputeSemanticColumns</c> 逐行重建行文本)用它
    /// 避免为了做一次语义匹配而物化 StringBuilder + string。两者的一致性由
    /// <c>TerminalCellMemoryTests.AppendTo_MatchesAppendText</c> 把守。
    /// </remarks>
    /// <param name="destination">接收单元格文本的目标缓冲。</param>
    /// <returns>写入的字符数;缓冲不足时为 -1。</returns>
    public readonly int AppendTo(Span<char> destination)
    {
        if (IsWideTrailing)
        {
            return 0;
        }
        string? combining = Combining;
        int baseLength = Rune is 0 or <= 0xFFFF ? 1 : 2;
        int needed = baseLength + (combining?.Length ?? 0);
        if (destination.Length < needed)
        {
            return -1;
        }
        int written = 0;
        if (Rune == 0)
        {
            destination[written++] = ' ';
        }
        else if (Rune <= 0xFFFF)
        {
            destination[written++] = (char)Rune;
        }
        else
        {
            // 补充平面:手工拆代理对,免掉 char.ConvertFromUtf32 的 string 分配。
            int offset = Rune - 0x10000;
            destination[written++] = (char)(0xD800 + (offset >> 10));
            destination[written++] = (char)(0xDC00 + (offset & 0x3FF));
        }
        if (combining is not null)
        {
            combining.CopyTo(destination[written..]);
            written += combining.Length;
        }
        return written;
    }

    /// <summary>将单元格文本(基础字符及组合标记)追加到指定的 <see cref="StringBuilder" />;宽字符尾格不追加内容。</summary>
    /// <param name="sb">接收单元格文本的目标缓冲区。</param>
    public readonly void AppendText(StringBuilder sb)
    {
        if (IsWideTrailing)
        {
            return;
        }
        if (Rune == 0)
        {
            sb.Append(' ');
            return;
        }
        sb.Append(char.ConvertFromUtf32(Rune));
        if (Combining is not null)
        {
            sb.Append(Combining);
        }
    }

    /// <summary>判断两个单元格的字符、组合标记、颜色与属性是否全部相等。</summary>
    /// <param name="other">要比较的另一个单元格。</param>
    /// <returns>各字段均相等时返回 true。</returns>
    public readonly bool Equals(TerminalCell other) =>
        Rune == other.Rune &&
        CombiningIndex == other.CombiningIndex && // 池内驻留:同串必同索引。
        Foreground == other.Foreground &&
        Background == other.Background &&
        Flags == other.Flags;

    /// <summary>判断指定对象是否为相等的单元格。</summary>
    /// <param name="obj">要比较的对象。</param>
    /// <returns>对象为等值的 <see cref="TerminalCell" /> 时返回 true。</returns>
    public override readonly bool Equals(object? obj) => obj is TerminalCell other && Equals(other);

    /// <summary>返回基于全部字段的哈希码。</summary>
    /// <returns>单元格的哈希码。</returns>
    public override readonly int GetHashCode() => HashCode.Combine(Rune, CombiningIndex, Foreground, Background, Flags);

    /// <summary>判断两个单元格是否相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>相等时返回 true。</returns>
    public static bool operator ==(TerminalCell left, TerminalCell right)
    {
        return left.Equals(right);
    }

    /// <summary>判断两个单元格是否不相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>不相等时返回 true。</returns>
    public static bool operator !=(TerminalCell left, TerminalCell right)
    {
        return !(left == right);
    }
}

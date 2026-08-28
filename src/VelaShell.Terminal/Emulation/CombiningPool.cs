namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 组合标记字符串的进程级驻留池:<see cref="TerminalCell" /> 只存 <c>int</c> 索引而非
/// <c>string?</c> 引用。这让单元格结构不含任何托管引用(blittable)——整个回滚缓冲
/// (可达数百万格)从此不再被 GC 逐格扫描,每格还省下 4 字节。
/// 真实世界的组合标记来自极小的字符集(重音、变音符),池天然收敛到几十条。
/// </summary>
/// <remarks>
/// 写路径(驻留)只发生在解析到组合字符时,极罕见,加锁无碍;读路径(渲染/取文本)
/// 只按索引读已发布的数组——数组内容一经发布不再变更,扩容以"新数组 + volatile 发布"
/// 完成,无锁读安全。恶意输入理论上可制造大量唯一组合序列,超出上限后停止驻留、
/// 静默丢弃新标记(显示退化为基础字符),不会无界增长。
/// </remarks>
internal static class CombiningPool
{
    /// <summary>
    /// 池上限:正常使用永远到不了;是对抗性输入的无界增长保险丝。
    /// 取 ushort.MaxValue 而非 65536 —— 索引存在 <see cref="TerminalCell.CombiningIndex" /> 的
    /// <c>ushort</c> 里(把单元格从 20 字节压到 16 字节的一半功劳),65536 会溢出成 0。
    /// </summary>
    private const int MaxEntries = ushort.MaxValue;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, int> Lookup = [];
    private static volatile string?[] _byIndex = new string?[64]; // [0] 恒为 null = 无组合标记。
    private static int _count = 1;

    /// <summary>驻留一段组合标记,返回其索引;null/空串返回 0;池满返回 0(丢弃标记)。</summary>
    public static ushort Intern(string? marks)
    {
        if (string.IsNullOrEmpty(marks))
        {
            return 0;
        }
        lock (Gate)
        {
            if (Lookup.TryGetValue(marks, out int existing))
            {
                return (ushort)existing;
            }
            if (_count >= MaxEntries)
            {
                return 0;
            }
            if (_count == _byIndex.Length)
            {
                string?[] grown = new string?[_byIndex.Length * 2];
                Array.Copy(_byIndex, grown, _byIndex.Length);
                _byIndex = grown; // volatile 发布:读者要么看到旧数组(内容仍有效),要么看到新数组。
            }
            int index = _count;
            _byIndex[index] = marks;
            _count = index + 1;
            Lookup[marks] = index;
            return (ushort)index;
        }
    }

    /// <summary>按索引取回组合标记;0 或越界返回 null。</summary>
    public static string? Get(int index)
    {
        if (index <= 0)
        {
            return null;
        }
        string?[] snapshot = _byIndex;
        return index < snapshot.Length ? snapshot[index] : null;
    }
}

using System.Text;
using BenchmarkDotNet.Attributes;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Benchmarks;

/// <summary>
/// 回滚缓冲的内存占用、reflow 与全缓冲搜索。
/// </summary>
/// <remarks>
/// <para>
/// P-07 的账是这么算的:每行固定 <c>new TerminalCell[columns]</c>,16 字节一格,
/// 200 列 × 20 万行 ≈ <b>640 MB / 标签</b> —— 而典型日志行只有几十列非空。
/// <see cref="ScrollbackFootprint" /> 就是把这笔账变成一个可复现的数字:同样一万行日志,
/// 裁剪前后各占多少堆。
/// </para>
/// <para>
/// reflow 与搜索一起放进来,是因为裁剪要动 <c>TerminalRow</c> 的索引器 ——
/// 这两条是最容易被裁剪改坏、也最容易被裁剪拖慢的路径,得盯着。
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ScrollbackBenchmarks
{
    /// <summary>灌进去的行数。一万行是「跑了一会儿的编译任务」的量级。</summary>
    private const int Lines = 10_000;

    private byte[] _log = [];

    /// <summary>
    /// 每行的实际字符数。宽终端里绝大多数日志行远短于列宽 —— 这正是裁剪能省下东西的原因,
    /// 所以要有一个「短行」档和一个「接近满宽」档做对照。
    /// </summary>
    [Params(20, 76)]
    public int LineLength { get; set; } = 20;

    /// <summary>终端列宽。200 列是宽屏最大化后的常见值,也是 P-07 那笔账用的数。</summary>
    [Params(80, 200)]
    public int Columns { get; set; } = 80;

    [GlobalSetup]
    public void Setup() => _log = BuildLog(Lines, LineLength);

    /// <summary>
    /// 灌一万行日志后,整个仿真器占多少堆。
    /// </summary>
    /// <remarks>
    /// 看的是 <c>Allocated</c> 那一列,不是耗时:这条基准的目的是给 P-07 的裁剪一个
    /// 改动前后可比的数字。<c>Columns</c> 与 <c>LineLength</c> 的四种组合摊开看,
    /// 「列宽涨一倍、内存也涨一倍,与行里实际有多少字无关」这件事会直接显形。
    /// </remarks>
    [Benchmark(Description = "回滚占用(灌 1 万行)")]
    public int ScrollbackFootprint()
    {
        TerminalEmulator emulator = Feed();
        return emulator.Screen.ScrollbackCount;
    }

    /// <summary>把窗口拉窄一半再拉回去 —— 两次 reflow,历史行全部重排。</summary>
    [Benchmark(Description = "reflow(窄一半再还原)")]
    public int Reflow()
    {
        TerminalEmulator emulator = Feed();
        emulator.Resize(Columns / 2, 24);
        emulator.Resize(Columns, 24);
        return emulator.Screen.TotalRows;
    }

    /// <summary>全缓冲搜索一个每行都命中的词。</summary>
    [Benchmark(Description = "全缓冲搜索")]
    public int Search()
    {
        TerminalEmulator emulator = Feed();
        return BufferSearch.FindAll(emulator.Screen, "VelaShell").Count;
    }

    private TerminalEmulator Feed()
    {
        TerminalEmulator emulator = new(Columns, 24, TerminalType.XtermColor256, Lines * 2);
        emulator.Feed(_log);
        return emulator;
    }

    /// <summary>每行都含一个可搜索的词,行长补到 <paramref name="length" />。</summary>
    private static byte[] BuildLog(int lines, int length)
    {
        StringBuilder text = new(lines * (length + 2));
        for (int i = 0; i < lines; i++)
        {
            string line = $"VelaShell {i}";
            text.Append(line.Length >= length ? line[..length] : line.PadRight(length, '.'))
                .Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}

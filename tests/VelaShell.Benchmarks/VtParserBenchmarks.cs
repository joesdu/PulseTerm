using System.Text;
using BenchmarkDotNet.Attributes;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Benchmarks;

/// <summary>
/// VT 解析 + 仿真的吞吐与分配。
/// </summary>
/// <remarks>
/// <para>
/// 三种负载对应三条不同的代码路径,不能只测一种:
/// </para>
/// <list type="bullet">
/// <item><b>纯 ASCII</b> —— 编译日志、<c>cat</c> 大文件。全程走 Ground 态逐字符 <c>Print</c>,
/// P-08 的快路径就是冲它去的。</item>
/// <item><b>ANSI 密集</b> —— <c>ls --color</c>、进度条、TUI。每几个字符就一次 CSI,
/// 状态机切换与参数收集占大头,快路径帮不上忙,但不能被它拖慢。</item>
/// <item><b>CJK</b> —— 中文日志。每个字符都要判宽度、占两格,还会触发自动换行。</item>
/// </list>
/// <para>
/// 每条都从字节喂起(<c>Feed</c> 而不是 <c>Parse</c>),因为 UTF-8 解码也在热路径上,
/// 只测 <c>Parse</c> 会把它藏起来。
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class VtParserBenchmarks
{
    /// <summary>每条负载的字节数(约 1 MB),三种负载取同一量级才好横向比。</summary>
    private const int PayloadBytes = 1 << 20;

    /// <summary>
    /// ESC(0x1B)。写成数值而不是源文件里的裸控制字节 —— 后者能跑,但任何编辑器、
    /// 格式化工具或一次编码转换都可能把它悄悄吃掉,那时基准就变成了「测纯文本」。
    /// </summary>
    private const char Esc = (char)0x1B;

    private byte[] _ascii = [];
    private byte[] _ansi = [];
    private byte[] _cjk = [];

    /// <summary>终端几何:80×24 是最常见的默认值。</summary>
    [Params(80)]
    public int Columns { get; set; } = 80;

    /// <summary>
    /// 回滚行数。0 与默认值分开测:行退休进 scrollback 的代价(P-07 的裁剪就作用在这里)
    /// 只有开着回滚才看得见。
    /// </summary>
    [Params(0, 10_000)]
    public int Scrollback { get; set; } = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _ascii = BuildAscii();
        _ansi = BuildAnsi();
        _cjk = BuildCjk();
    }

    [Benchmark(Baseline = true, Description = "纯 ASCII 日志")]
    public int Ascii() => Consume(_ascii);

    [Benchmark(Description = "ANSI 密集(带色)")]
    public int Ansi() => Consume(_ansi);

    [Benchmark(Description = "CJK 文本")]
    public int Cjk() => Consume(_cjk);

    /// <summary>喂完整段并返回一个与结果相关的值,免得整段被优化掉。</summary>
    private int Consume(byte[] payload)
    {
        TerminalEmulator emulator = new(Columns, 24, TerminalType.XtermColor256, Scrollback);
        emulator.Feed(payload);
        return emulator.Screen.CursorY + emulator.Screen.ScrollbackCount;
    }

    /// <summary>典型编译日志:行长不一,全是可打印 ASCII。</summary>
    private static byte[] BuildAscii()
    {
        StringBuilder text = new(PayloadBytes + 128);
        int line = 0;
        while (text.Length < PayloadBytes)
        {
            text.Append("  Building VelaShell.Terminal -> bin/Debug/net11.0/VelaShell.Terminal.dll  [")
                .Append(line++)
                .Append("]\r\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>每几个字符一次 SGR:接近 `ls --color` 与带色日志的形状。</summary>
    private static byte[] BuildAnsi()
    {
        StringBuilder text = new(PayloadBytes + 128);
        int line = 0;
        while (text.Length < PayloadBytes)
        {
            text.Append($"{Esc}[32m[ok]{Esc}[0m ")
                .Append($"{Esc}[1;34m")
                .Append("src/VelaShell.Terminal/Emulation/VtParser.cs")
                .Append($"{Esc}[0m {Esc}[90m(")
                .Append(line++)
                .Append($" ms){Esc}[0m\r\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>中文日志:每个字符三字节 UTF-8、显示占两格。</summary>
    private static byte[] BuildCjk()
    {
        StringBuilder text = new(PayloadBytes + 128);
        int line = 0;
        while (text.Length < PayloadBytes)
        {
            text.Append("  正在连接到远程主机,读取会话配置并校验主机指纹 [")
                .Append(line++)
                .Append("]\r\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}

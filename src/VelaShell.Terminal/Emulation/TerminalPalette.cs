namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 一个 32 位打包颜色(0xAARRGGBB),在解析 <see cref="TerminalColor" /> 时产生。
/// 与 Avalonia 解耦,使引擎保持 UI 无关、可测试。
/// </summary>
/// <param name="A">Alpha 通道。</param>
/// <param name="R">红色通道。</param>
/// <param name="G">绿色通道。</param>
/// <param name="B">蓝色通道。</param>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    /// <summary>打包为单个 0xAARRGGBB 无符号整数的颜色。</summary>
    public uint Packed => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>由红、绿、蓝通道值创建不透明颜色。</summary>
    public static Rgba FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
}

/// <summary>
/// 当前生效的调色板:256 个索引色,外加语义化的默认前景、背景与光标颜色。
/// 索引 0-15 可随主题改变;16-255 遵循标准的 xterm 6x6x6 颜色立方图与灰度渐变。
/// </summary>
public sealed class TerminalPalette
{
    private readonly Rgba[] _colors = new Rgba[256];

    /// <summary>创建预填标准 xterm 256 色默认值的调色板。</summary>
    public TerminalPalette()
    {
        InitializeAnsi16();
        InitializeCube();
        InitializeGrayscale();
    }

    /// <summary>使用默认前景色的单元格所采用的颜色。</summary>
    public Rgba DefaultForeground { get; set; } = Rgba.FromRgb(0xE0, 0xE6, 0xED);

    /// <summary>使用默认背景色的单元格所采用的颜色。</summary>
    public Rgba DefaultBackground { get; set; } = Rgba.FromRgb(0x08, 0x0C, 0x12);

    /// <summary>绘制光标所用的颜色。</summary>
    public Rgba CursorColor { get; set; } = Rgba.FromRgb(0x00, 0xD4, 0xAA);

    /// <summary>选中文本背后所绘制的填充色。</summary>
    public Rgba SelectionBackground { get; set; } = new(0x60, 0x1C, 0x2A, 0x3F);

    /// <summary>终端内搜索:非当前命中项背后的填充色。</summary>
    /// <remarks>
    /// 这是**界面交互色**,与 ANSI 那 256 个"远端程序指定的数据颜色"性质完全不同 ——
    /// 后者必须原样呈现,前者该跟着应用主题走。它和 <see cref="SearchCurrentBackground" />
    /// 曾经是渲染器里的两个 <c>static readonly</c> 常量,亮色主题下压在浅底上几乎看不见。
    /// 现在与 <see cref="SelectionBackground" /> 同一条路径:由 ApplyDesignPalette 按
    /// 明暗变体设定,主题热切换时随之更新。
    /// </remarks>
    public Rgba SearchMatchBackground { get; set; } = new(0x59, 0xFD, 0xCB, 0x6E);

    /// <summary>终端内搜索:当前命中项背后的填充色(须与其余命中项可区分)。</summary>
    public Rgba SearchCurrentBackground { get; set; } = new(0x73, 0x00, 0xD4, 0xAA);

    /// <summary>获取给定调色板索引(0-255,掩码为一个字节)对应的解析后颜色。</summary>
    public Rgba this[int index] => _colors[index & 0xFF];

    /// <summary>覆盖 16 个 ANSI 颜色之一(用于套用设计里的 term-* 令牌)。</summary>
    public void SetAnsi(int index, Rgba color)
    {
        if (index is >= 0 and < 16)
        {
            _colors[index] = color;
        }
    }

    /// <summary>把一个单元格颜色解析为具体 RGBA,遵循反显与加粗加亮规则。</summary>
    public Rgba Resolve(TerminalColor color, bool isBackground, bool bold)
    {
        switch (color.Kind)
        {
            case TerminalColorKind.Rgb:
                return Rgba.FromRgb(color.R, color.G, color.B);
            case TerminalColorKind.Indexed:
                int idx = color.Index;
                // 加粗文本会提亮低 8 个 ANSI 颜色,与 xterm 行为一致。
                if (bold && !isBackground && idx < 8)
                {
                    idx += 8;
                }
                return _colors[idx];
            case TerminalColorKind.Default:
            default:
                return isBackground ? DefaultBackground : DefaultForeground;
        }
    }

    private void InitializeAnsi16()
    {
        // 标准 xterm 默认色;应用会从 .pen 的 term-* 令牌覆盖 0-15。
        var ansi = new (byte, byte, byte)[]
        {
            (0x00, 0x00, 0x00), (0xCD, 0x00, 0x00), (0x00, 0xCD, 0x00), (0xCD, 0xCD, 0x00),
            (0x00, 0x00, 0xEE), (0xCD, 0x00, 0xCD), (0x00, 0xCD, 0xCD), (0xE5, 0xE5, 0xE5),
            (0x7F, 0x7F, 0x7F), (0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
            (0x5C, 0x5C, 0xFF), (0xFF, 0x00, 0xFF), (0x00, 0xFF, 0xFF), (0xFF, 0xFF, 0xFF)
        };
        for (int i = 0; i < 16; i++)
        {
            _colors[i] = Rgba.FromRgb(ansi[i].Item1, ansi[i].Item2, ansi[i].Item3);
        }
    }

    private void InitializeCube()
    {
        // 位于索引 16-231 的 216 色 6x6x6 立方图。
        ReadOnlySpan<byte> steps = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];
        int i = 16;
        foreach (byte r in steps)
        {
            foreach (byte g in steps)
            {
                foreach (byte b in steps)
                {
                    _colors[i++] = Rgba.FromRgb(r, g, b);
                }
            }
        }
    }

    private void InitializeGrayscale()
    {
        // 位于索引 232-255 的 24 级灰度渐变。
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + i * 10);
            _colors[232 + i] = Rgba.FromRgb(v, v, v);
        }
    }
}

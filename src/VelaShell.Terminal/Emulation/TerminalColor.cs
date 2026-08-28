namespace VelaShell.Terminal.Emulation;

/// <summary>
/// <see cref="TerminalColor" /> 解析为屏幕上实际颜色的方式。
/// </summary>
public enum TerminalColorKind : byte
{
    /// <summary>使用终端配置好的默认前景色/背景色。</summary>
    Default = 0,

    /// <summary>256 色调色板索引(0-15 = ANSI,16-231 = 立方图,232-255 = 灰度)。</summary>
    Indexed = 1,

    /// <summary>直接 24 位真彩色。</summary>
    Rgb = 2
}

/// <summary>
/// 与任何具体调色板都无关的颜色。渲染层会针对当前生效的 <see cref="TerminalPalette" />
/// 来解析 <see cref="TerminalColorKind.Default" /> 与 <see cref="TerminalColorKind.Indexed" />。
/// </summary>
/// <remarks>
/// <b>内存布局是本结构的契约</b>:整个字段集打包进单个 <c>uint</c>(0xKKRRGGBB —— 高字节存
/// <see cref="Kind" />,低 24 位按 Kind 解释为 RGB 或调色板索引)。五个独立字节字段时本结构占
/// 5 字节,而 <see cref="TerminalCell" /> 里放两份,受对齐影响会把单元格撑到 20 字节;
/// 打包成 4 字节后单元格降到 16 字节 —— 回滚缓冲(默认每标签页 1 万行)整整省下 20% 内存。
/// 见 <c>TerminalCellMemoryTests.TerminalCell_StaysWithinPackedSize</c>。
/// <para>
/// 各 Kind 下未使用的位恒为 0(Default 全 0;Indexed 只用低 8 位;Rgb 只用低 24 位),
/// 因此"比较 <see cref="_packed" />"与逐字段比较完全等价 —— 相等性由此退化为一次整数比较。
/// </para>
/// </remarks>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private readonly uint _packed;

    private TerminalColor(uint packed) => _packed = packed;

    /// <summary>该颜色解析为屏幕上实际颜色的方式。</summary>
    public TerminalColorKind Kind => (TerminalColorKind)(byte)(_packed >> 24);

    /// <summary>当 <see cref="Kind" /> 为 Indexed 时的调色板索引;其余 Kind 下为 0。</summary>
    public byte Index => Kind == TerminalColorKind.Indexed ? (byte)_packed : (byte)0;

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的红色通道;其余 Kind 下为 0。</summary>
    public byte R => Kind == TerminalColorKind.Rgb ? (byte)(_packed >> 16) : (byte)0;

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的绿色通道;其余 Kind 下为 0。</summary>
    public byte G => Kind == TerminalColorKind.Rgb ? (byte)(_packed >> 8) : (byte)0;

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的蓝色通道;其余 Kind 下为 0。</summary>
    public byte B => Kind == TerminalColorKind.Rgb ? (byte)_packed : (byte)0;

    /// <summary>终端默认前景/背景色哨兵值。</summary>
    public static TerminalColor Default => new(0);

    /// <summary>由 256 色调色板索引创建索引色(钳制到 0-255)。</summary>
    public static TerminalColor FromIndex(int index) =>
        new(((uint)TerminalColorKind.Indexed << 24) | (byte)Math.Clamp(index, 0, 255));

    /// <summary>由给定的红、绿、蓝通道创建 24 位真彩色。</summary>
    public static TerminalColor FromRgb(byte r, byte g, byte b) =>
        new(((uint)TerminalColorKind.Rgb << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    /// <summary>该颜色是否为终端默认哨兵值。</summary>
    public bool IsDefault => _packed == 0; // Default 的打包表示恒为全 0。

    /// <summary>判断该颜色是否与另一个颜色相等。</summary>
    /// <remarks>未使用位恒为 0(见类型注释),故整数比较与逐字段比较等价,且省去 5 次取值。</remarks>
    public bool Equals(TerminalColor other) => _packed == other._packed;

    /// <summary>判断该颜色是否与给定对象相等。</summary>
    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    /// <summary>返回该颜色的哈希码。</summary>
    public override int GetHashCode() => (int)_packed;

    /// <summary>判断两个颜色是否相等。</summary>
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    /// <summary>判断两个颜色是否不相等。</summary>
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

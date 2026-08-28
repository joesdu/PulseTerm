namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 确定某个 Unicode 标量值所占的显示宽度(以终端单元格计)。
/// 遵循 wcwidth 所用的 Unicode 东亚宽度约定:宽字符与全角字符占两个单元格,
/// 组合标记与零宽字符占零个,其余字符占一个。
/// </summary>
public static class CharWidth
{
    // 宽字符(W)与全角字符(F)码点范围。保持紧凑,但覆盖终端实际会渲染的
    // 文字系统:CJK、Hangul、假名、全角形式、emoji。
    private static readonly (int Lo, int Hi)[] Wide =
    [
        (0x1100, 0x115F),   // Hangul 辅音/元音
        (0x2329, 0x232A),   // 尖括号
        (0x2E80, 0x303E),   // CJK 部首/康熙部首/符号
        (0x3041, 0x33FF),   // 平假名..CJK 兼容
        (0x3400, 0x4DBF),   // CJK 扩展 A
        (0x4E00, 0x9FFF),   // CJK 统一汉字
        (0xA000, 0xA4CF),   // 彝文
        (0xAC00, 0xD7A3),   // Hangul 音节
        (0xF900, 0xFAFF),   // CJK 兼容表意文字
        (0xFE10, 0xFE19),   // 垂直形式
        (0xFE30, 0xFE6F),   // CJK 兼容/小形式
        (0xFF00, 0xFF60),   // 全角形式
        (0xFFE0, 0xFFE6),   // 全角符号
        (0x1F300, 0x1F64F), // emoji/象形符号
        (0x1F900, 0x1F9FF), // 补充符号
        (0x20000, 0x3FFFD)  // CJK 扩展 B+/补充表意文字平面
    ];

    // 零宽字符:组合标记、格式字符、变体选择符。
    private static readonly (int Lo, int Hi)[] ZeroWidth =
    [
        (0x0300, 0x036F), // 组合变音符号
        (0x0483, 0x0489),
        (0x0591, 0x05BD),
        (0x0610, 0x061A),
        (0x064B, 0x065F),
        (0x0670, 0x0670),
        (0x06D6, 0x06DC),
        (0x0E31, 0x0E31),
        (0x0E34, 0x0E3A),
        (0x1AB0, 0x1AFF),
        (0x1DC0, 0x1DFF),
        (0x200B, 0x200F), // ZWSP..RLM
        (0x20D0, 0x20FF), // 符号组合标记
        (0xFE00, 0xFE0F), // 变体选择符
        (0xFE20, 0xFE2F)  // 组合半音符号
    ];

    /// <summary>返回给定 Unicode 标量值的终端单元格显示宽度(0、1 或 2)。</summary>
    public static int Of(int rune)
    {
        switch (rune)
        {
            case 0:
            // C0/C1 控制字符没有可打印宽度。
            case < 0x20:
            case >= 0x7F and < 0xA0:
                return 0;
        }
        // ASCII / Latin-1 / 拉丁扩展一律宽度 1:0x0300 以下没有任何零宽或宽字符区间
        // (最低的零宽区间是组合变音符号 U+0300,最低的宽字符区间是 Hangul U+1100)。
        // 这条早退让绝大多数字符只做一次比较,免掉下面两趟二分 —— 本方法在每个打印字符
        // (TerminalEmulator.Print)与每个渲染格子(VelaTerminalControl.RenderLine)上各调一次,
        // 是全仓最热的叶子函数之一。
        if (rune < 0x0300)
        {
            return 1;
        }
        if (InRanges(rune, ZeroWidth))
        {
            return 0;
        }
        if (InRanges(rune, Wide))
        {
            return 2;
        }
        return 1;
    }

    private static bool InRanges(int rune, (int Lo, int Hi)[] ranges)
    {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ref readonly (int Lo, int Hi) r = ref ranges[mid];
            if (rune < r.Lo)
            {
                hi = mid - 1;
            }
            else if (rune > r.Hi)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
}

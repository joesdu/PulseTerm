using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Services;

/// <summary>
/// 把设置 → 外观 的终端配色映射成渲染层的稀疏覆盖集:只有与出厂默认(Dracula)不同的
/// 颜色才会生效覆盖,未改过的槽位保持 null,让终端继续跟随应用主题
/// (暗 = Dracula / 亮 = Solarized Light)。
/// </summary>
public static class TerminalAppearanceMapper
{
    private static readonly AppearanceOptions Defaults = new();

    /// <summary>把外观配色映射为渲染层的稀疏覆盖集;全部与默认一致时返回 null。</summary>
    public static TerminalPaletteOverrides? BuildPaletteOverrides(AppearanceOptions appearance)
    {
        var overrides = new TerminalPaletteOverrides
        {
            Foreground = DiffColor(appearance.TerminalForeground, Defaults.TerminalForeground),
            Background = DiffColor(appearance.TerminalBackground, Defaults.TerminalBackground),
            Cursor = DiffColor(appearance.CursorColor, Defaults.CursorColor),
            Selection = DiffColor(appearance.SelectionColor, Defaults.SelectionColor)
        };
        FillAnsi(overrides, appearance.AnsiNormal, Defaults.AnsiNormal, 0);
        FillAnsi(overrides, appearance.AnsiBright, Defaults.AnsiBright, 8);
        return overrides.IsEmpty ? null : overrides;
    }

    private static void FillAnsi(TerminalPaletteOverrides overrides,
        List<string>? values,
        List<string> defaults,
        int offset)
    {
        if (values is null)
        {
            return;
        }
        for (int i = 0; i < 8 && i < values.Count; i++)
        {
            string def = i < defaults.Count ? defaults[i] : string.Empty;
            overrides.Ansi[offset + i] = DiffColor(values[i], def);
        }
    }

    /// <summary>与默认一致(忽略大小写/空白)→ null;否则解析成 Rgba,解析失败也返回 null。</summary>
    private static Rgba? DiffColor(string? value, string defaultValue)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return TryParseHex(trimmed, out Rgba color) ? color : null;
    }

    private static bool TryParseHex(string hex, out Rgba color)
    {
        color = default;
        string s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, null, out uint rgb))
        {
            return false;
        }
        color = Rgba.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}

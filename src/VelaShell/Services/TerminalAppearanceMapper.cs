using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Services;

/// <summary>
/// 把设置 → 外观 的终端配色映射成渲染层的调色板。两层:
/// <list type="number">
/// <item>主题给的底 —— 当前具名主题配套的那套终端方案(<see cref="UiTheme.TerminalSchemeName" />),
/// 由 <see cref="BuildThemePalette" /> 摊成整套下发。</item>
/// <item>用户的配色 —— 由 <see cref="BuildPaletteOverrides" /> 叠在底上。
/// 处于「跟随主题」时返回 null(一个槽位都不覆盖);用户选了具体方案就整套覆盖。</item>
/// </list>
/// </summary>
public static class TerminalAppearanceMapper
{
    /// <summary>
    /// 把一套终端配色方案摊成**整套**(每个槽位都有值)的调色板,供宿主随主题下发到终端控件。
    /// </summary>
    public static TerminalPaletteOverrides BuildThemePalette(TerminalColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        return BuildPalette(
            scheme.Foreground, scheme.Background, scheme.Cursor, scheme.Selection,
            scheme.AnsiNormal, scheme.AnsiBright);
    }

    /// <summary>
    /// 把用户设置里的终端配色映射为叠在主题配色之上的覆盖集;跟随主题时返回 null。
    /// <para>
    /// <b>为什么是整套覆盖、而不是与出厂默认逐色做差</b>:做差的老实现把「跟随主题」隐式编码成
    /// 「颜色与出厂 Dracula 一致」,于是用户明确选中 Dracula 时算出来的覆盖是空的 ——
    /// 在配套方案不是 Dracula 的主题上,选 Dracula 毫无反应,终端仍是主题自带的那套。
    /// 跟随与否现在由 <see cref="AppearanceOptions.TerminalColorsFollowTheme" /> 明确表态,
    /// 一旦不跟随,用户在设置页上看到的那套颜色就是终端要用的那套,原样下发。
    /// </para>
    /// </summary>
    public static TerminalPaletteOverrides? BuildPaletteOverrides(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (TerminalColorScheme.FollowsTheme(appearance))
        {
            return null;
        }
        TerminalPaletteOverrides overrides = BuildPalette(
            appearance.TerminalForeground, appearance.TerminalBackground,
            appearance.CursorColor, appearance.SelectionColor,
            appearance.AnsiNormal, appearance.AnsiBright);
        // 全空只可能出现在配色被写坏(色值全部解析失败)的配置上:那种情况下退回跟随主题,
        // 总比让终端顶着一屏解析失败的空槽位强。
        return overrides.IsEmpty ? null : overrides;
    }

    private static TerminalPaletteOverrides BuildPalette(
        string? foreground,
        string? background,
        string? cursor,
        string? selection,
        IReadOnlyList<string>? ansiNormal,
        IReadOnlyList<string>? ansiBright)
    {
        var palette = new TerminalPaletteOverrides
        {
            Foreground = ParseOrNull(foreground),
            Background = ParseOrNull(background),
            Cursor = ParseOrNull(cursor),
            Selection = ParseOrNull(selection)
        };
        for (int i = 0; i < 8; i++)
        {
            palette.Ansi[i] = ParseOrNull(ElementOrNull(ansiNormal, i));
            palette.Ansi[8 + i] = ParseOrNull(ElementOrNull(ansiBright, i));
        }
        return palette;
    }

    private static string? ElementOrNull(IReadOnlyList<string>? values, int index) =>
        values is not null && index < values.Count ? values[index] : null;

    /// <summary>解析 <c>#RRGGBB</c>;空值或解析失败返回 null(该槽位继续跟随下层)。</summary>
    private static Rgba? ParseOrNull(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && TryParseHex(hex.Trim(), out Rgba color) ? color : null;

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

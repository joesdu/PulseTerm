using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 用户自定义终端配色的稀疏覆盖集(设置 → 外观):null 槽位表示"未自定义,跟随主题"。
/// 由宿主(App 层)将设置中与出厂默认值不同的颜色填进来,叠加在主题调色板之上,
/// 这样没改过的颜色仍能在暗/亮主题(Dracula/Solarized Light)之间正常切换。
/// </summary>
public sealed class TerminalPaletteOverrides
{
    /// <summary>ANSI 调色板槽位数(前 8 普通 + 后 8 明亮)。</summary>
    public const int AnsiCount = 16;

    /// <summary>前景色覆盖;null 表示未自定义、跟随主题。</summary>
    public Rgba? Foreground { get; set; }

    /// <summary>背景色覆盖;null 表示未自定义、跟随主题。</summary>
    public Rgba? Background { get; set; }

    /// <summary>光标色覆盖;null 表示未自定义、跟随主题。</summary>
    public Rgba? Cursor { get; set; }

    /// <summary>选区底色(不含透明度,渲染层负责按方案叠加 alpha)。</summary>
    public Rgba? Selection { get; set; }

    /// <summary>ANSI 0–15(前 8 普通 + 后 8 明亮)。</summary>
    public Rgba?[] Ansi { get; } = new Rgba?[AnsiCount];

    /// <summary>是否不含任何自定义覆盖(所有颜色均跟随主题)。</summary>
    public bool IsEmpty
    {
        get
        {
            if (Foreground is not null || Background is not null || Cursor is not null || Selection is not null)
            {
                return false;
            }
            // 曾经写的是 Ansi.Length == 0 —— 数组恒为 16 个槽位,于是 IsEmpty 恒为 false,
            // "一色未改"也会被当成有覆盖。叠加一份全 null 的覆盖不改变画面,所以从没露头;
            // 但它使"跟随主题"与"钉死配色"这两种状态在外部看来无从区分。
            return Array.TrueForAll(Ansi, color => color is null);
        }
    }
}

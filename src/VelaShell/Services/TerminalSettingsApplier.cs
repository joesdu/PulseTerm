using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Services;

/// <summary>
/// 把一份设置摊到一个终端控件上:回滚深度、字体、字号、编码、行为开关与整套配色。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>MainWindowViewModel</c> 拆出来的一簇(Q-01)。它是纯粹的「设置 → 控件属性」映射,
/// 无状态、不持有任何东西 —— 混在五千行的视图模型里时,想验一条"会话级编码覆盖了全局"
/// 就得先把整个主窗口构造出来。
/// </para>
/// <para>
/// 标签创建时应用一次,此后每次保存设置都对**所有**已打开标签重新应用一次(#3/#15/#21)——
/// 用户改一个开关,期望的是当场生效,而不是"下次开的标签才有"。
/// </para>
/// </remarks>
public static class TerminalSettingsApplier
{
    /// <summary>
    /// 把设置应用到一个终端。
    /// </summary>
    /// <param name="emulator">目标终端;非 <see cref="VelaTerminalControl" /> 时只设回滚深度。</param>
    /// <param name="settings">全局设置。</param>
    /// <param name="theme">当前生效的界面主题(它自带一套配套的终端配色)。</param>
    /// <param name="forceUtf8">
    /// 本地终端(ConPTY)输出恒为 UTF-8,不套用面向远端主机的编码设置。
    /// </param>
    /// <param name="profile">会话配置;其覆盖项优先于全局(F-06)。null = 没有配置。</param>
    /// <param name="multilinePasteConfirmation">多行粘贴的确认回调;由视图提供。</param>
    public static void Apply(
        ITerminalEmulator emulator,
        AppSettings settings,
        UiTheme theme,
        bool forceUtf8 = false,
        SessionProfile? profile = null,
        Func<string, Task<bool>>? multilinePasteConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);

        emulator.ScrollbackLines = settings.ScrollbackLines;
        if (emulator is not VelaTerminalControl control)
        {
            return;
        }
        // 远端按「会话覆盖优先于全局」解析(F-06):少了 profile 这一路,用户在设置页里
        // 随便改点什么,广播下来就把每个标签的会话级编码冲回全局值 —— 而那正是热更新的必经之路。
        control.SetEncoding(
            forceUtf8 ? Encoding.UTF8 : ResolveEncoding(SessionTerminalSettings.Encoding(profile, settings)));
        if (!string.IsNullOrWhiteSpace(settings.TerminalFont))
        {
            control.FontFamily = new(
                $"{ResolveFontFamily(settings.TerminalFont.Trim())}, fonts:VelaShell#Cascadia Mono, JetBrains Mono, Consolas, monospace"
            );
        }
        if (settings.TerminalFontSize > 0)
        {
            control.FontSize = settings.TerminalFontSize;
        }
        // 背景图开启时,终端控件自绘填充置全透明(不画背景),终端 tint 改由 TerminalHost 边框单层承担
        // (VelaBgTerminal 令牌半透明,MainWindow 负责)。若这里仍按不透明度上色,会与该边框两层叠加、
        // 保存后终端又变得几乎不透明。未开启背景图则恒为不透明(行为不变)。
        bool backgroundImageActive = !string.IsNullOrWhiteSpace(settings.Appearance.BackgroundImagePath);
        control.BackgroundOpacity = backgroundImageActive ? 0.0 : 1.0;
        TerminalBehaviorOptions behavior = settings.TerminalBehavior;
        control.LineHeight = behavior.LineHeight;
        control.ContentPadding = behavior.Padding;
        control.CursorStyle = behavior.CursorStyle;
        control.CursorBlink = behavior.CursorBlink;
        control.BellMode = behavior.BellMode;
        control.AllowRemoteClipboardWrite = behavior.AllowRemoteClipboardWrite;
        control.ScrollOnOutput = behavior.ScrollOnOutput;
        control.AlternateScrollEnabled = behavior.AlternateScroll;
        control.ShowLineTimestamp = behavior.ShowLineTimestamp;
        control.ShowLineNumber = behavior.ShowLineNumber;
        control.ShowFoldMarker = behavior.ShowFoldMarker;
        control.GutterBlank = behavior.GutterBlank;
        control.GutterMenu = new(
            Strings.Get("Gutter_LineNumber"),
            Strings.Get("Gutter_Timestamp"),
            Strings.Get("Gutter_FoldMarker"),
            Strings.Get("Gutter_Blank")
        );
        control.ScrollOnKeystroke = behavior.ScrollOnKeystroke;
        control.CopyOnSelect = behavior.CopyOnSelect;
        control.RightClickPaste = behavior.RightClickPaste;
        control.TrimTrailingWhitespaceOnCopy = behavior.TrimTrailingWhitespaceOnCopy;
        control.DoubleClickSelectsWord = behavior.DoubleClickSelectsWord;
        control.ConfirmMultilinePaste = behavior.ConfirmMultilinePaste;
        control.MultilinePasteConfirmation = multilinePasteConfirmation;
        control.CtrlCCopiesWhenSelected = behavior.CtrlCCopiesWhenSelected;
        control.ImeEnabled = behavior.ImeSupport;
        control.LocalEchoEnabled = behavior.LocalEcho;

        // 现有两种传输的对端都自己回显:SSH 是远端 PTY,本地终端是 ConPTY 里的 shell。
        // 因此这两类标签上强制忽略「本地回显」开关 —— 否则用户为串口设备打开它之后,
        // 所有 SSH 与本地标签都会变成每个字符两遍。
        // 将来接入 Telnet 半双工 / 串口时,在此按传输置 false,让它们走正常逻辑。
        // (主机显式 CSI 12 l 要求终端回显时仍然生效,不受本项影响。)
        control.PeerEchoesInput = true;

        // 当前具名主题配套的整套终端配色(VelaDark→Dracula、Nord→Nord…),
        // 再叠上用户自定义的那几个单色(没改过的颜色一律跟随主题)。
        control.ThemePalette = TerminalAppearanceMapper.BuildThemePalette(theme.Terminal);
        // 会话级配色覆盖(F-06)压在全局之上:整套下发,与用户在设置页里选一个方案是同一条路。
        // 没指定时 ColorScheme 返回 null,行为与改动前逐字相同。
        control.PaletteOverrides =
            SessionTerminalSettings.ColorScheme(profile) is { } sessionScheme
                ? TerminalAppearanceMapper.BuildThemePalette(sessionScheme)
                : TerminalAppearanceMapper.BuildPaletteOverrides(settings.Appearance);
    }

    /// <summary>
    /// 按名字取编码;名字为空或系统不认识时退回 UTF-8。
    /// </summary>
    /// <remarks>
    /// 不能让它抛:名字来自设置文件与会话配置,两者都可以手改,而一个打错的编码名
    /// 不该把连接流程炸掉 —— 退回 UTF-8 至少还能用。
    /// <para>
    /// GBK / Big5 一族在旧代码页里,要 <c>Encoding.RegisterProvider</c> 之后才取得到
    /// (<c>Program.Main</c> 已注册)。
    /// </para>
    /// </remarks>
    /// <param name="name">编码名(如 <c>GBK</c>)。</param>
    /// <returns>对应编码,或 UTF-8。</returns>
    public static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// 把设置里的裸字体族名解析为可寻址的 FontFamily 名。
    /// </summary>
    /// <remarks>
    /// 内置字体(随程序分发,<c>fonts:VelaShell</c> 集合)必须带集合 URI 前缀才能被字体管理器
    /// 命中,系统字体名原样返回。这使设置页的自由文本框既能填 "Cascadia Mono" 这类内置族,
    /// 也能填任意系统字体。
    /// </remarks>
    /// <param name="name">设置里的字体族名。</param>
    /// <returns>可寻址的族名。</returns>
    public static string ResolveFontFamily(string name) =>
        name is "Cascadia Mono" ? $"fonts:VelaShell#{name}" : name;
}

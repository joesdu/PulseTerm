using System.Reflection;
using System.Xml;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace VelaShell.Services.Syntax;

/// <summary>
/// 语法高亮的装配点:注册本项目自带的语法定义,并把所有定义(含 AvaloniaEdit 内置的)
/// 重着色成当前主题的配色。
/// <para>
/// **为什么必须重着色**:AvaloniaEdit 内置的 20 份定义全是给浅色背景调的 ——
/// 关键字 Blue、标点 Black。本应用的终端面是 Dracula 的 #282A36,直接用会出现
/// "标点看不见、关键字发暗"的结果。好在这些定义用的都是**命名颜色**且实例被规则共享,
/// 按名字改写 <see cref="HighlightingColor.Foreground" /> 即可整体换肤。
/// </para>
/// </summary>
public static class SyntaxHighlightingService
{
    /// <summary>本项目自带的 xshd(嵌入资源名 → 该定义认领的扩展名)。</summary>
    private static readonly (string Resource, string[] Extensions)[] BundledDefinitions =
    [
        ("VelaShell.Syntax.Shell.xshd", [".sh", ".bash", ".zsh", ".ksh"]),
        ("VelaShell.Syntax.Yaml.xshd", [".yaml", ".yml"]),
        ("VelaShell.Syntax.Ini.xshd", [".ini", ".conf", ".cfg", ".toml", ".properties", ".env"]),
        ("VelaShell.Syntax.Dockerfile.xshd", [".dockerfile"]),
        ("VelaShell.Syntax.Log.xshd", [".log"]),
    ];

    private static readonly Lock RegistrationGate = new();
    private static bool _registered;

    /// <summary>
    /// 解析文件应使用的语法定义并按主题着色;无法判定类型时返回 <c>null</c>(纯文本)。
    /// </summary>
    /// <param name="fileName">文件名(可带路径)。</param>
    /// <param name="firstLine">文件首行,用于 shebang 判定。</param>
    /// <param name="theme">当前主题变体,决定用 Dracula 还是 Alucard 配色。</param>
    public static IHighlightingDefinition? Resolve(string? fileName, string? firstLine, ThemeVariant? theme)
    {
        EnsureRegistered();
        string? name = FileTypeDetector.Detect(fileName, firstLine);
        if (name is null)
        {
            return null;
        }
        IHighlightingDefinition? definition = HighlightingManager.Instance.GetDefinition(name);
        if (definition is null)
        {
            return null;
        }

        // 每次打开都按当前主题重着色:定义是全局共享的单例,用户中途切换主题后
        // 下次打开就会拿到正确配色。
        Recolor(definition, SyntaxPalette.For(theme));
        return definition;
    }

    /// <summary>把本项目自带的 xshd 注册进 AvaloniaEdit 的全局管理器(只做一次)。</summary>
    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
            Assembly assembly = typeof(SyntaxHighlightingService).Assembly;
            foreach ((string resource, string[] extensions) in BundledDefinitions)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        continue;
                    }
                    using var reader = XmlReader.Create(stream);
                    IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    HighlightingManager.Instance.RegisterHighlighting(definition.Name, extensions, definition);
                }
                catch (Exception)
                {
                    // 单份定义坏掉不该让编辑器打不开 —— 其余定义照常注册,该类型退化为纯文本。
                }
            }
        }
    }

    /// <summary>
    /// 按命名颜色把定义重着色。名字对不上的颜色兜底做对比度修正,
    /// 免得留下 Black 之类在深色背景上等于隐形的值。
    /// </summary>
    private static void Recolor(IHighlightingDefinition definition, SyntaxPalette palette)
    {
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            Color? mapped = palette.ForRole(color.Name);
            if (mapped is { } role)
            {
                color.Foreground = new SimpleHighlightingBrush(role);
                continue;
            }

            // 未收录的角色:只在它与背景过于接近时才动它,尽量保留原定义的表达意图。
            if (color.Foreground?.GetColor(null) is { } current && !HasContrast(current, palette.Background))
            {
                color.Foreground = new SimpleHighlightingBrush(palette.Default);
            }
        }
    }

    /// <summary>
    /// 用相对亮度差做一个粗略的可读性判断。这里不需要严格的 WCAG 对比度 ——
    /// 目的只是抓出"几乎与背景同色"的那几个,阈值取得保守。
    /// </summary>
    private static bool HasContrast(Color foreground, Color background) =>
        Math.Abs(Luminance(foreground) - Luminance(background)) >= 0.25;

    private static double Luminance(Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255d;
}

/// <summary>
/// 语法配色。深色用 Dracula、浅色用 Alucard,与应用主题(VelaShellTokens.axaml)同源,
/// 这样编辑器看起来是应用的一部分而不是贴上去的第三方控件。
/// </summary>
public sealed record SyntaxPalette(
    Color Background,
    Color Default,
    Color Comment,
    Color String,
    Color Keyword,
    Color Number,
    Color Variable,
    Color Function,
    Color Type,
    Color Error)
{
    /// <summary>Dracula(暗)。</summary>
    private static readonly SyntaxPalette Dark = new(
        Background: Color.FromRgb(0x28, 0x2A, 0x36),
        Default: Color.FromRgb(0xF8, 0xF8, 0xF2),
        Comment: Color.FromRgb(0x62, 0x72, 0xA4),
        String: Color.FromRgb(0xF1, 0xFA, 0x8C),
        Keyword: Color.FromRgb(0xFF, 0x79, 0xC6),
        Number: Color.FromRgb(0xBD, 0x93, 0xF9),
        Variable: Color.FromRgb(0xFF, 0xB8, 0x6C),
        Function: Color.FromRgb(0x50, 0xFA, 0x7B),
        Type: Color.FromRgb(0x8B, 0xE9, 0xFD),
        Error: Color.FromRgb(0xFF, 0x55, 0x55));

    /// <summary>Alucard(亮),即 Dracula 官方亮色方案。</summary>
    private static readonly SyntaxPalette Light = new(
        Background: Color.FromRgb(0xFF, 0xFB, 0xEB),
        Default: Color.FromRgb(0x1F, 0x1F, 0x1F),
        Comment: Color.FromRgb(0x6C, 0x66, 0x4B),
        String: Color.FromRgb(0x84, 0x6E, 0x15),
        Keyword: Color.FromRgb(0xA3, 0x14, 0x4D),
        Number: Color.FromRgb(0x64, 0x4A, 0xC9),
        Variable: Color.FromRgb(0xA3, 0x4D, 0x14),
        Function: Color.FromRgb(0x14, 0x71, 0x0A),
        Type: Color.FromRgb(0x03, 0x6A, 0x96),
        Error: Color.FromRgb(0xCB, 0x3A, 0x2A));

    /// <summary>按主题变体取配色;未指定时按深色处理(应用默认是暗色)。</summary>
    public static SyntaxPalette For(ThemeVariant? theme) => theme == ThemeVariant.Light ? Light : Dark;

    /// <summary>
    /// 把定义里的颜色名归到调色板角色。
    /// 名字来自各 xshd 作者,同一角色叫法很多(Keyword/Keywords、Number/NumberLiteral……),
    /// 这里统一收口;没收录的返回 <c>null</c>,由调用方走对比度兜底。
    /// </summary>
    public Color? ForRole(string? name) => name switch
    {
        null => null,
        "Comment" or "Comments" or "XmlDoc" or "DocComment" or "Debug" => Comment,
        "String" or "Strings" or "Char" or "Character" or "StringInterpolation" or "Regex" => String,
        "Keyword" or "Keywords" or "ControlFlow" or "ExceptionKeywords" or "GotoKeywords"
            or "ThisOrBaseReference" or "NullOrValueKeywords" or "ContextKeywords"
            or "ReferenceTypeKeywords" or "ValueTypeKeywords" or "OperatorKeywords"
            or "ParameterModifiers" or "Modifiers" or "Visibility" or "NamespaceKeywords"
            or "TrueFalse" or "SemanticKeywords" => Keyword,
        "Number" or "NumberLiteral" or "Digits" or "Constant" or "Bool" or "Null" => Number,
        "Variable" or "Variables" or "AttributeName" or "FieldName" or "Key" or "Entity" => Variable,
        "Function" or "MethodCall" or "MethodName" or "Preprocessor" or "Section"
            or "Heading" or "Heading1" or "Heading2" or "Heading3" or "Heading4"
            or "Heading5" or "Heading6" or "Info" or "Success" => Function,
        "Type" or "TypeKeywords" or "XmlTag" or "CData" or "DocType" or "XmlDeclaration"
            or "TagName" or "ElementName" => Type,
        "Error" or "Errors" or "Warning" or "Invalid" => Error,
        "Punctuation" or "Operator" or "Operators" or "Text" or "Default" => Default,
        _ => null,
    };
}

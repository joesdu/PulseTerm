using System.Xml;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 聊天输入框(AvaloniaEdit)的接线:Markdown 着色 + 把已落定的 <c>@</c> 引用画成一枚
/// 只写文件名的主题色芯片。
/// </summary>
/// <remarks>
/// 输入框是编辑器而不是 <c>TextBox</c>,就是为了这两件事 —— Avalonia 的 TextBox 是纯文本控件,
/// 既不能让某一段变色,也不能让"显示的字"和"实际的值"不一样。这里两者都要:
/// 用户可能直接在输入框里写 Markdown(该看得出结构),而 <c>@/root/abc.txt</c> 这种长路径
/// 平铺出来又吵又占地方,应当收成 <c>@abc.txt</c>。
///
/// 关键约定:<b>文档里存的始终是全路径</b>。芯片只是渲染层的事,发送、附件展开、历史回溯
/// 全都照旧读 <see cref="AvaloniaEdit.TextEditor.Text" />,一行都不用改。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>本插件自带的 Markdown 着色定义(整个进程加载一次,不注册进全局管理器)。</summary>
    private static IHighlightingDefinition? _markdownDefinition;

    /// <summary>把着色与芯片接到输入框上;主题切换时重上色。</summary>
    private void SetUpInputEditor()
    {
        InputBox.SyntaxHighlighting = LoadMarkdownDefinition();
        ApplyInputHighlightPalette();
        InputBox.TextArea.TextView.ElementGenerators.Add(new FileReferenceChipGenerator());
        InputBox.TextArea.TextView.LineTransformers.Add(new FileReferenceChipColorizer(this));
        ActualThemeVariantChanged += (_, _) => ApplyInputHighlightPalette();
    }

    /// <summary>加载(并缓存)自带的 Markdown 着色定义;失败就不上色,输入框照常能用。</summary>
    private IHighlightingDefinition? LoadMarkdownDefinition()
    {
        if (_markdownDefinition is not null)
        {
            return _markdownDefinition;
        }
        try
        {
            using Stream? stream = typeof(ChatPanelView).Assembly
                .GetManifestResourceStream("VelaShell.Plugin.Ai.Ui.MarkdownInput.xshd");
            if (stream is null)
            {
                return null;
            }
            using var reader = XmlReader.Create(stream);
            return _markdownDefinition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Markdown highlighting for the input box is off: {ex.Message}");
            return null;
        }
    }

    /// <summary>按当前主题令牌给各命名颜色上色(令牌缺席时退到一组中性色,不至于没颜色可用)。</summary>
    private void ApplyInputHighlightPalette()
    {
        if (InputBox.SyntaxHighlighting is not { } definition)
        {
            return;
        }
        Paint(definition, "Heading", "VelaAccent", Color.FromRgb(0xBD, 0x93, 0xF9));
        Paint(definition, "Bold", "VelaTextPrimary", Color.FromRgb(0xF8, 0xF8, 0xF2));
        Paint(definition, "Italic", "VelaTextSecondary", Color.FromRgb(0xB0, 0xB8, 0xD6));
        Paint(definition, "InlineCode", "VelaShellCyan", Color.FromRgb(0x8B, 0xE9, 0xFD));
        Paint(definition, "CodeFence", "VelaShellCyan", Color.FromRgb(0x8B, 0xE9, 0xFD));
        Paint(definition, "Quote", "VelaTextTertiary", Color.FromRgb(0x62, 0x72, 0xA4));
        Paint(definition, "ListMarker", "VelaAccent", Color.FromRgb(0xBD, 0x93, 0xF9));
        Paint(definition, "Link", "VelaShellBlue", Color.FromRgb(0xBD, 0x93, 0xF9));
        // 着色是按行缓存的,换色后要让可视行重建一次,否则要等下一次编辑才生效
        InputBox.TextArea.TextView.Redraw();
    }

    /// <summary>给一个命名颜色刷上主题令牌对应的前景色。</summary>
    private void Paint(IHighlightingDefinition definition, string colorName, string token, Color fallback)
    {
        if (definition.GetNamedColor(colorName) is not { } color)
        {
            return;
        }
        color.Foreground = new SimpleHighlightingBrush(ResolveColor(token, fallback));
    }

    /// <summary>取一个 Vela* 画刷令牌的颜色;宿主没提供令牌时(如裸测试宿主)用兜底色。</summary>
    private Color ResolveColor(string token, Color fallback)
        => ResolveBrush(token, fallback) is ISolidColorBrush brush ? brush.Color : fallback;

    /// <summary>取一个 Vela* 画刷令牌;缺席时退回按兜底色现做一支。</summary>
    private IBrush ResolveBrush(string token, Color fallback)
        => this.TryFindResource(token, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    /// <summary>
    /// 把文档里已落定的 <c>@/root/abc.txt</c> 整段替换成一枚显示 <c>@abc.txt</c> 的芯片。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="FormattedTextElement" />(把这段文档替换成另一段<b>文本</b>去排版),
    /// 而不是内联控件:内联控件是"坐在基线上"的,它的高度整个压在基线之上,于是整行被撑高、
    /// AvaloniaEdit 的光标又按行高画 —— 表现就是一输入内容光标突然变成一根长条
    /// (实测 15.2 → 24.7)。当文本排版就没有这个问题,而且 OpenCode 的观感本来也是
    /// "一段彩色文字"而非胶囊。颜色由 <see cref="FileReferenceChipColorizer" /> 上。
    ///
    /// 它的 VisualColumnLength 是 1:光标不会落进芯片内部,整枚跨过 —— 与退格整块删除对上,
    /// 看到的是一枚,删的也是一枚。未落定(还在敲)的 token 不做芯片:那时要看清每个字符。
    /// </remarks>
    private sealed class FileReferenceChipGenerator : VisualLineElementGenerator
    {
        /// <inheritdoc />
        public override int GetFirstInterestedOffset(int startOffset)
        {
            int endOffset = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            for (int offset = startOffset; offset < endOffset; offset++)
            {
                if (CurrentContext.Document.GetCharAt(offset) != '@')
                {
                    continue;
                }
                if (TryRead(offset, out _, out _))
                {
                    return offset;
                }
            }
            return -1;
        }

        /// <inheritdoc />
        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!TryRead(offset, out int length, out string path))
            {
                return null;
            }
            return new FormattedTextElement("@" + FileReference.DisplayName(path), length);
        }

        /// <summary>读取 <paramref name="offset" /> 处的引用(按整行取文本,规则与发送/删除同源)。</summary>
        private bool TryRead(int offset, out int length, out string path)
        {
            length = 0;
            path = "";
            DocumentLine line = CurrentContext.Document.GetLineByOffset(offset);
            string text = CurrentContext.Document.GetText(line);
            if (!FileReference.TryFindCompletedReferenceAt(text, offset - line.Offset, out int len, out string found))
            {
                return false;
            }
            length = len;
            path = found;
            return true;
        }
    }

    /// <summary>
    /// 给芯片段上色:强调色文字 + 一层淡强调底,与输入框外的其它 <c>@</c> 引用观感一致。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="DocumentColorizingTransformer" /> 而不是在生成元素时定色:着色器在
    /// 元素生成之后跑,主题切换后一次 <c>Redraw()</c> 就能重上色,不必重建元素。
    /// </remarks>
    private sealed class FileReferenceChipColorizer(ChatPanelView owner) : DocumentColorizingTransformer
    {
        /// <inheritdoc />
        protected override void ColorizeLine(DocumentLine line)
        {
            string text = CurrentContext.Document.GetText(line);
            IBrush foreground = owner.ResolveBrush("VelaAccent", Color.FromRgb(0xBD, 0x93, 0xF9));
            IBrush background = owner.ResolveBrush("VelaAccentDim", Color.FromArgb(0x30, 0xBD, 0x93, 0xF9));
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '@'
                    || !FileReference.TryFindCompletedReferenceAt(text, i, out int length, out _))
                {
                    continue;
                }
                ChangeLinePart(line.Offset + i, line.Offset + i + length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                    element.TextRunProperties.SetBackgroundBrush(background);
                });
                i += length - 1;
            }
        }
    }
}

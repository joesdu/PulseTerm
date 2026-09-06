using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VelaShell.Controls;

/// <summary>
/// 一段文本,其中若干字符区间被加粗并着强调色 —— 命令面板用它标出"是哪几个字命中的"。
/// </summary>
/// <remarks>
/// 用 <see cref="InlineCollection" /> 拼而不是叠两个 <c>TextBlock</c>:命中区间可能在
/// 标题中间、也可能是散开的几个单词首字母,叠层对不齐。区间是**字符**偏移
/// (<c>string.IndexOf</c> 的口径),CJK 标题同样正确 —— 不涉及字形宽度。
/// </remarks>
public sealed class HighlightTextBlock : TextBlock
{
    /// <summary>要高亮的字符区间(起点, 长度)。</summary>
    public static readonly StyledProperty<IReadOnlyList<(int Start, int Length)>?> HighlightsProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IReadOnlyList<(int Start, int Length)>?>(nameof(Highlights));

    /// <summary>高亮部分的前景色。</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBrush));

    static HighlightTextBlock()
    {
        // 文本或区间任一变化都要重拼。
        TextProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.Rebuild());
        HighlightsProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.Rebuild());
        HighlightBrushProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.Rebuild());
    }

    /// <inheritdoc cref="HighlightsProperty" />
    public IReadOnlyList<(int Start, int Length)>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    /// <inheritdoc cref="HighlightBrushProperty" />
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private void Rebuild()
    {
        string text = Text ?? string.Empty;
        IReadOnlyList<(int Start, int Length)> spans = Highlights ?? [];
        if (text.Length == 0 || spans.Count == 0)
        {
            // 没有命中区间就退回普通 TextBlock 的渲染路径(Inlines 为空时它直接画 Text)。
            Inlines?.Clear();
            return;
        }

        var runs = new InlineCollection();
        int cursor = 0;
        foreach ((int start, int length) in spans.OrderBy(span => span.Start))
        {
            // 区间越界/重叠时按"跳过"处理,绝不让一个坏区间把整行文字弄丢。
            if (start < cursor || start >= text.Length || length <= 0)
            {
                continue;
            }
            int end = Math.Min(start + length, text.Length);
            if (start > cursor)
            {
                runs.Add(new Run(text[cursor..start]));
            }
            runs.Add(new Run(text[start..end])
            {
                FontWeight = FontWeight.Bold,
                Foreground = HighlightBrush
            });
            cursor = end;
        }
        if (cursor < text.Length)
        {
            runs.Add(new Run(text[cursor..]));
        }
        Inlines = runs;
    }
}

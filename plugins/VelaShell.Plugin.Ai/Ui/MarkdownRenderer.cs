using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using AvaloniaInline = Avalonia.Controls.Documents.Inline;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// Markdown → Avalonia 控件渲染器(Markdig 解析)。支持:标题、段落、行内样式
/// (粗体/斜体/删除线/行内代码/链接)、围栏代码块(带语言标签与复制按钮)、
/// 有序/无序列表(可嵌套)、引用块、分隔线、管道表格。
/// 视觉全部走样式类(在 ChatPanelView.axaml 定义,配 Vela* 令牌,主题切换即时跟随);
/// 未覆盖的语法节点回退渲染原文,保证内容永不丢失。
/// </summary>
internal sealed class MarkdownRenderer(Control resourceHost, Func<string, Task> copyAsync, Loc loc)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    /// <summary>渲染整段 Markdown(输入可以是流式中的半截文本,Markdig 容忍)。</summary>
    public Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        RenderIncremental(panel, markdown, []);
        return panel;
    }

    /// <summary>
    /// 增量渲染:<paramref name="blockCache" /> 记录上次渲染时每个块的源码切片
    /// (与 <paramref name="panel" /> 的子控件一一对应),本次只重建切片有变化的
    /// 后缀块,未变的前缀块控件原样复用。流式追加通常只动最后一个块,于是每次
    /// 节流重渲染的代价从 O(全文控件树) 降到 O(尾块)。最后一个块始终重建
    /// (它仍在增长,半截块的 span 会漂移)。注意:增量不回溯已缓存块,引用式
    /// 链接等"后文影响前文"的语法需在定稿时清空缓存做一次全量渲染纠正。
    /// </summary>
    public void RenderIncremental(StackPanel panel, string markdown, List<string> blockCache)
    {
        // 资源查找(字体/强调色)在一趟渲染内只做一次树查找;逐趟重置以跟随主题切换
        _monoFontCache = null;
        _accentBrushCache = null;
        _accentBrushResolved = false;

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(markdown, Pipeline);
        }
        catch
        {
            panel.Children.Clear();
            blockCache.Clear();
            panel.Children.Add(PlainText(markdown, "body"));
            blockCache.Add(markdown);
            return;
        }

        var blocks = new List<Block>();
        foreach (Block block in document)
        {
            // RenderBlock 对这两类返回 null,不产出控件,排除以维持"块↔子控件"一一对应
            if (block is LinkReferenceDefinitionGroup or BlankLineBlock)
            {
                continue;
            }
            blocks.Add(block);
        }

        // 缓存与子控件失配(外部动过 panel)时退化为全量重建
        if (blockCache.Count != panel.Children.Count)
        {
            panel.Children.Clear();
            blockCache.Clear();
        }

        int keep = 0;
        int stableLimit = blocks.Count - 1; // 尾块强制重建
        while (keep < blockCache.Count && keep < stableLimit
               && string.Equals(blockCache[keep], Slice(markdown, blocks[keep].Span), StringComparison.Ordinal))
        {
            keep++;
        }
        panel.Children.RemoveRange(keep, panel.Children.Count - keep);
        blockCache.RemoveRange(keep, blockCache.Count - keep);
        for (int i = keep; i < blocks.Count; i++)
        {
            if (RenderBlock(blocks[i], markdown) is { } control)
            {
                panel.Children.Add(control);
                blockCache.Add(Slice(markdown, blocks[i].Span));
            }
        }
    }

    // ---------- 块级 ----------

    private Control? RenderBlock(Block block, string source) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderLeafInlines(paragraph, "body"),
        FencedCodeBlock fenced => RenderCode(CodeText(fenced), fenced.Info),
        CodeBlock code => RenderCode(CodeText(code), null),
        QuoteBlock quote => RenderQuote(quote, source),
        ListBlock list => RenderList(list, source),
        ThematicBreakBlock => new Border { Height = 1, Classes = { "mdRule" }, Margin = new Thickness(0, 4) },
        Table table => RenderTable(table, source),
        HtmlBlock html => PlainText(Slice(source, html.Span), "mono"),
        LinkReferenceDefinitionGroup => null,
        BlankLineBlock => null,
        _ => PlainText(Slice(source, block.Span), "body")
    };

    private Control RenderHeading(HeadingBlock heading)
    {
        var text = new SelectableTextBlock { Classes = { $"mdH{Math.Clamp(heading.Level, 1, 4)}" } };
        AppendInlines(text.Inlines!, heading.Inline, default);
        return text;
    }

    private Control RenderLeafInlines(LeafBlock block, string cls)
    {
        var text = new SelectableTextBlock { Classes = { cls } };
        AppendInlines(text.Inlines!, block.Inline, default);
        return text;
    }

    private Control RenderCode(string code, string? language)
    {
        var layout = new StackPanel { Spacing = 4 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim(),
            VerticalAlignment = VerticalAlignment.Center
        });
        var copyButton = new Button { Classes = { "mdCopy" }, Content = loc["Copy"] };
        Grid.SetColumn(copyButton, 1);
        copyButton.Click += async (_, _) =>
        {
            try
            {
                await copyAsync(code);
                copyButton.Content = loc["Copied"];
            }
            catch
            {
                // 剪贴板失败静默(不打断阅读)
            }
        };
        header.Children.Add(copyButton);
        layout.Children.Add(header);
        layout.Children.Add(new SelectableTextBlock { Classes = { "mono" }, Text = code.TrimEnd('\n') });
        return new Border { Classes = { "mdCode" }, Child = layout };
    }

    private Control RenderQuote(QuoteBlock quote, string source)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (Block child in quote)
        {
            if (RenderBlock(child, source) is { } control)
            {
                inner.Children.Add(control);
            }
        }
        return new Border { Classes = { "mdQuote" }, Child = inner };
    }

    private Control RenderList(ListBlock list, string source)
    {
        var panel = new StackPanel { Spacing = 2 };
        int index = int.TryParse(list.OrderedStart, out int start) ? start : 1;
        foreach (Block item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }
            string marker = list.IsOrdered ? $"{index++}." : "•";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*") };
            row.Children.Add(new TextBlock
            {
                Classes = { "mdBullet" },
                Text = marker,
                VerticalAlignment = VerticalAlignment.Top
            });
            var content = new StackPanel { Spacing = 2 };
            foreach (Block child in listItem)
            {
                if (RenderBlock(child, source) is { } control)
                {
                    content.Children.Add(control);
                }
            }
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control RenderTable(Table table, string source)
    {
        int columnCount = 0;
        foreach (Block row in table)
        {
            if (row is TableRow tableRow)
            {
                columnCount = Math.Max(columnCount, tableRow.Count);
            }
        }
        if (columnCount == 0)
        {
            return PlainText(Slice(source, table.Span), "mono");
        }

        var grid = new Grid();
        for (int i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        int rowIndex = 0;
        foreach (Block rowBlock in table)
        {
            if (rowBlock is not TableRow tableRow)
            {
                continue;
            }
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int col = 0; col < columnCount; col++)
            {
                var cellBorder = new Border { Classes = { tableRow.IsHeader ? "mdTableHead" : "mdTableCell" } };
                if (col < tableRow.Count && tableRow[col] is TableCell cell)
                {
                    var cellContent = new StackPanel { Spacing = 2, MaxWidth = 360 };
                    foreach (Block child in cell)
                    {
                        Control? rendered = child is ParagraphBlock p
                            ? RenderLeafInlines(p, tableRow.IsHeader ? "mdTableHeadText" : "mdTableText")
                            : RenderBlock(child, source);
                        if (rendered is not null)
                        {
                            cellContent.Children.Add(rendered);
                        }
                    }
                    cellBorder.Child = cellContent;
                }
                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, col);
                grid.Children.Add(cellBorder);
            }
            rowIndex++;
        }
        // 外框补上表格的上/左边线(单元格只画右/下),横向可滚避免撑破面板
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Classes = { "mdTableFrame" }, Child = grid, HorizontalAlignment = HorizontalAlignment.Left }
        };
    }

    // ---------- 行内 ----------

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike);

    private void AppendInlines(InlineCollection target, ContainerInline? container, InlineStyle style)
    {
        if (container is null)
        {
            return;
        }
        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            AppendInline(target, inline, style);
        }
    }

    private void AppendInline(InlineCollection target, MdInline inline, InlineStyle style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(StyledRun(literal.Content.ToString(), style));
                break;

            case EmphasisInline emphasis:
                {
                    InlineStyle merged = emphasis.DelimiterChar == '~'
                        ? style with { Strike = true }
                        : emphasis.DelimiterCount >= 2
                            ? style with { Bold = true }
                            : style with { Italic = true };
                    AppendInlines(target, emphasis, merged);
                    break;
                }

            case CodeInline code:
                {
                    Run run = StyledRun(code.Content, style);
                    run.FontFamily = MonoFont;
                    run.Foreground = AccentBrush ?? run.Foreground;
                    target.Add(run);
                    break;
                }

            case LinkInline link:
                target.Add(RenderLink(link, style));
                break;

            case AutolinkInline autolink:
                target.Add(LinkContainer(autolink.Url, autolink.Url));
                break;

            case LineBreakInline:
                target.Add(new Run("\n"));
                break;

            case HtmlEntityInline entity:
                target.Add(StyledRun(entity.Transcoded.ToString(), style));
                break;

            case HtmlInline html:
                target.Add(StyledRun(html.Tag, style));
                break;

            case ContainerInline nested:
                AppendInlines(target, nested, style);
                break;

            default:
                target.Add(StyledRun(inline.ToString() ?? "", style));
                break;
        }
    }

    private AvaloniaInline RenderLink(LinkInline link, InlineStyle style)
    {
        string text = InlineText(link);
        if (string.IsNullOrEmpty(text))
        {
            text = link.Url ?? "";
        }
        if (link.IsImage)
        {
            // 终端面板不加载远程图片:降级为可点击的占位链接
            return LinkContainer($"🖼 {text}", link.Url);
        }
        return LinkContainer(text, link.Url, style);
    }

    private AvaloniaInline LinkContainer(string text, string? url, InlineStyle style = default)
    {
        var textBlock = new TextBlock
        {
            Classes = { "mdLink" },
            Text = text,
            FontWeight = style.Bold ? FontWeight.SemiBold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal
        };
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https")
        {
            ToolTip.SetTip(textBlock, url);
            textBlock.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    _ = TopLevel.GetTopLevel(resourceHost)?.Launcher.LaunchUriAsync(uri);
                }
            };
        }
        return new InlineUIContainer(textBlock) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    private static Run StyledRun(string text, InlineStyle style)
    {
        var run = new Run(text);
        if (style.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }
        if (style.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }
        if (style.Strike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }
        return run;
    }

    // ---------- 辅助 ----------

    private static string InlineText(ContainerInline container)
    {
        var sb = new StringBuilder();
        for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline nested:
                    sb.Append(InlineText(nested));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CodeText(CodeBlock block)
    {
        StringLineGroup lines = block.Lines;
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.AppendLine(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }

    private static string Slice(string source, SourceSpan span)
        => span.Start >= 0 && span.End < source.Length && span.Length > 0
            ? source.Substring(span.Start, span.Length)
            : source;

    private static Control PlainText(string text, string cls)
        => new SelectableTextBlock { Classes = { cls }, Text = text };

    // 一趟渲染内的资源缓存(TryFindResource 是逐级向上的树查找,行内代码多时开销可观)
    private FontFamily? _monoFontCache;
    private IBrush? _accentBrushCache;
    private bool _accentBrushResolved;

    private FontFamily MonoFont
        => _monoFontCache ??= resourceHost.TryFindResource("VelaUiMonoFont", out object? value) && value is FontFamily family
            ? family
            : new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");

    private IBrush? AccentBrush
    {
        get
        {
            if (!_accentBrushResolved)
            {
                _accentBrushResolved = true;
                _accentBrushCache = Brush("VelaAccent");
            }
            return _accentBrushCache;
        }
    }

    private IBrush? Brush(string key)
        => resourceHost.TryFindResource(key, out object? value) && value is IBrush brush ? brush : null;
}

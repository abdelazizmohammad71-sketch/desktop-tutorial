using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using ZX0ai.Core.Text;

namespace ZX0ai.Views.Controls;

/// <summary>
/// Turns a reply's Markdown into real WinUI elements.
/// </summary>
/// <remarks>
/// <para>
/// Before this, an assistant message was one <see cref="TextBlock"/> with the model's
/// raw text in it — a reply that opened with <c>### Heading</c> and a table rendered as
/// literal hashes and pipe characters, because nothing ever interpreted them. This walks
/// <see cref="MarkdownParser"/>'s block tree and builds the corresponding element for
/// each one.
/// </para>
/// <para>
/// Every coloured surface here is a <see cref="Border"/>, <see cref="TextBlock"/> or
/// <see cref="RichTextBlock"/> carrying a shared <c>Style</c> — never a brush resolved
/// once in code — for the same reason the rest of the shell does it that way: a style's
/// <c>{ThemeResource}</c> setters are re-evaluated against the element itself, so this
/// keeps following the shell's theme flip without needing to rebuild the transcript on
/// every flip.
/// </para>
/// </remarks>
public static class MarkdownRenderer
{
    private static readonly double[] HeadingSizes = [22, 20, 18, 16, 15, 14];

    /// <summary>Builds the full block tree for one message.</summary>
    public static Panel Render(string? markdown)
    {
        var root = new StackPanel { Spacing = 10 };
        RenderInto(root, markdown);
        return root;
    }

    /// <summary>
    /// Repopulates an existing panel rather than building a new one.
    /// </summary>
    /// <remarks>
    /// What a streaming reply needs: the panel stays the one element already in the
    /// transcript, at its own scroll position, and only its children are swapped for
    /// each re-render as more of the message arrives.
    /// </remarks>
    public static void RenderInto(Panel target, string? markdown)
    {
        target.Children.Clear();

        foreach (var block in MarkdownParser.Parse(markdown))
        {
            target.Children.Add(BuildBlock(block));
        }
    }

    private static UIElement BuildBlock(MarkdownBlock block) => block switch
    {
        HeadingBlock heading => BuildHeading(heading),
        ParagraphBlock paragraph => BuildParagraph(paragraph.Spans),
        CodeBlock code => BuildCodeBlock(code),
        ListBlock list => BuildList(list),
        QuoteBlock quote => BuildQuote(quote),
        TableBlock table => BuildTable(table),
        RuleBlock => new Border { Style = Style("MarkdownRuleStyle"), Margin = new Thickness(0, 4, 0, 4) },
        _ => new Grid(),
    };

    private static TextBlock BuildHeading(HeadingBlock heading)
    {
        var text = new TextBlock
        {
            Style = Style("MarkdownHeadingStyle"),
            FontSize = HeadingSizes[Math.Clamp(heading.Level, 1, 6) - 1],
        };

        text.Inlines.Add(PlainRun(heading.Spans));
        ApplyDirection(text, heading.Spans);
        return text;
    }

    private static RichTextBlock BuildParagraph(IReadOnlyList<InlineSpan> spans)
    {
        var richText = new RichTextBlock { Style = Style("MarkdownParagraphStyle") };
        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, spans);
        richText.Blocks.Add(paragraph);
        ApplyDirection(richText, spans);
        return richText;
    }

    private static Border BuildQuote(QuoteBlock quote)
    {
        var content = BuildParagraph(quote.Spans);
        content.Opacity = 0.85;

        return new Border
        {
            Style = Style("MarkdownQuoteBorderStyle"),
            Child = content,
        };
    }

    private static StackPanel BuildList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };

        for (var i = 0; i < list.Items.Count; i++)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Style = Style("MarkdownBulletStyle"),
                Text = list.Ordered ? $"{i + 1}." : "•",
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = BuildParagraph(list.Items[i]);
            content.Margin = new Thickness(0);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>A fenced block: a language chip, a copy button, and the code — always LTR.</summary>
    private static Border BuildCodeBlock(CodeBlock code)
    {
        var header = new Grid { Margin = new Thickness(12, 8, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var language = new TextBlock
        {
            Style = Style("MarkdownCodeHeaderTextStyle"),
            Text = string.IsNullOrWhiteSpace(code.Language) ? "text" : code.Language,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(language, 0);
        header.Children.Add(language);

        var copy = new Button
        {
            Style = Style("IconButtonStyle"),
            Width = 26,
            Height = 26,
        };
        AutomationProperties.SetName(copy, "Copy code");
        ToolTipService.SetToolTip(copy, "Copy code");
        copy.Content = new Microsoft.UI.Xaml.Shapes.Path
        {
            Style = Style("IconSmallStyle"),
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
                typeof(Geometry),
                "M9 9 H19 V19 H9 Z M5 15 V5 H15"),
        };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(code.Code);
            Clipboard.SetContent(package);
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var codeText = new TextBlock
        {
            Style = Style("MarkdownCodeTextStyle"),
            Text = code.Code,
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            Padding = new Thickness(12, 6, 12, 10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = codeText,
        };

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(scroller);

        return new Border
        {
            Style = Style("MarkdownCodeFrameStyle"),
            Child = body,
        };
    }

    /// <summary>
    /// A table, as a Grid: header row bottom-bordered, body cells hairline-bordered,
    /// wrapped in a horizontal scroller so a wide table never forces the transcript wider.
    /// </summary>
    private static Border BuildTable(TableBlock table)
    {
        var grid = new Grid();

        for (var c = 0; c < table.Alignments.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var r = 0; r <= table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var c = 0; c < table.Headers.Count; c++)
        {
            var cell = new Border
            {
                Style = Style("MarkdownTableHeaderCellStyle"),
                BorderThickness = BorderFor(c, table.Alignments.Count, isLastRow: table.Rows.Count == 0, isHeader: true),
            };
            var text = new TextBlock
            {
                Style = Style("MarkdownTableHeaderTextStyle"),
                HorizontalAlignment = AlignmentFor(table.Alignments[c]),
                TextAlignment = TextAlignmentFor(table.Alignments[c]),
            };
            text.Inlines.Add(PlainRun(table.Headers[c]));
            cell.Child = text;

            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                var cell = new Border
                {
                    Style = Style("MarkdownTableCellStyle"),
                    BorderThickness = BorderFor(c, table.Alignments.Count, isLastRow: r == table.Rows.Count - 1, isHeader: false),
                };
                var text = new TextBlock
                {
                    Style = Style("MarkdownTableCellTextStyle"),
                    HorizontalAlignment = AlignmentFor(table.Alignments[c]),
                    TextAlignment = TextAlignmentFor(table.Alignments[c]),
                };
                text.Inlines.Add(PlainRun(table.Rows[r][c]));
                cell.Child = text;

                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        // The table's own reading direction follows its header, so an Arabic table
        // keeps its first column on the right and an English one on the left — mixed
        // Arabic/English cells inside either still shape correctly on their own.
        grid.FlowDirection = DirectionOf(table.Headers.SelectMany(h => h));

        return new Border
        {
            Style = Style("MarkdownTableFrameStyle"),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = grid,
            },
        };
    }

    /// <summary>Only the outer edge of the table draws a border; cells share the one between them.</summary>
    private static Thickness BorderFor(int column, int columnCount, bool isLastRow, bool isHeader)
    {
        var right = column < columnCount - 1 ? 1 : 0;
        var bottom = isHeader || !isLastRow ? 1 : 0;
        return new Thickness(0, 0, right, bottom);
    }

    private static HorizontalAlignment AlignmentFor(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Center => HorizontalAlignment.Center,
        ColumnAlignment.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Stretch,
    };

    private static TextAlignment TextAlignmentFor(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Center => TextAlignment.Center,
        ColumnAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    // ============================== Inline ==============================

    /// <summary>Flattens spans into one Run, for places that cannot host an InlineUIContainer.</summary>
    private static Run PlainRun(IReadOnlyList<InlineSpan> spans) =>
        new() { Text = string.Concat(spans.Select(s => s.Text)) };

    private static void AppendInlines(InlineCollection inlines, IReadOnlyList<InlineSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Style.HasFlag(InlineStyle.Code))
            {
                inlines.Add(BuildInlineCode(span.Text));
                continue;
            }

            if (span.Style.HasFlag(InlineStyle.Link))
            {
                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = span.Text });

                if (Uri.TryCreate(span.Href, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https")
                {
                    link.Click += async (_, _) => await Launcher.LaunchUriAsync(uri);
                }

                inlines.Add(link);
                continue;
            }

            var run = new Run { Text = span.Text };
            if (span.Style.HasFlag(InlineStyle.Bold))
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if (span.Style.HasFlag(InlineStyle.Italic))
            {
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }

            inlines.Add(run);
        }
    }

    /// <summary>
    /// Inline code as a small pill via <see cref="InlineUIContainer"/> rather than a
    /// plain <see cref="Run"/> — a <c>Run</c> has no background to give it a code chip's
    /// look, and forcing <c>LeftToRight</c> here is what keeps a file name from
    /// reversing inside an Arabic sentence.
    /// </summary>
    private static InlineUIContainer BuildInlineCode(string text)
    {
        var label = new TextBlock
        {
            Style = Style("MarkdownInlineCodeTextStyle"),
            Text = text,
        };

        return new InlineUIContainer
        {
            Child = new Border
            {
                Style = Style("MarkdownInlineCodeStyle"),
                Child = label,
            },
        };
    }

    // ============================= Direction =============================

    private static void ApplyDirection(FrameworkElement element, IReadOnlyList<InlineSpan> spans) =>
        element.FlowDirection = DirectionOf(spans);

    /// <summary>
    /// The block's reading direction, from its first strongly-directional letter.
    /// </summary>
    /// <remarks>
    /// Per block, not once for the whole message: a reply can open in English and
    /// explain a result in Arabic a few lines later, and each side should read the way
    /// its own text does. Code spans stay <c>LeftToRight</c> regardless, forced in their
    /// own style rather than here.
    /// </remarks>
    private static FlowDirection DirectionOf(IEnumerable<InlineSpan> spans) =>
        BidiText.DetectParagraphDirection(string.Concat(spans.Select(s => s.Text)))
            == ParagraphDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}

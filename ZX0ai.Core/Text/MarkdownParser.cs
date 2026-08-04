using System.Linq;
using System.Text;

namespace ZX0ai.Core.Text;

/// <summary>Inline emphasis applied to a run of text.</summary>
[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Link = 8,
}

/// <summary>A styled run inside a block.</summary>
/// <param name="Text">Literal text, already unescaped.</param>
/// <param name="Style">Emphasis flags.</param>
/// <param name="Href">Target for <see cref="InlineStyle.Link"/>.</param>
public readonly record struct InlineSpan(string Text, InlineStyle Style = InlineStyle.None, string? Href = null);

/// <summary>Base of the block tree a message renders as.</summary>
public abstract record MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

/// <param name="Language">Fence info string, or empty when unfenced.</param>
public sealed record CodeBlock(string Language, string Code) : MarkdownBlock;

public sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<InlineSpan>> Items) : MarkdownBlock;

public sealed record QuoteBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

public sealed record RuleBlock : MarkdownBlock;

/// <summary>How a table column's text is set, from the separator row's colon placement.</summary>
public enum ColumnAlignment
{
    Default,
    Left,
    Center,
    Right,
}

/// <param name="Alignments">One entry per column, taken from the separator row.</param>
/// <param name="Headers">Cells of the header row.</param>
/// <param name="Rows">Body rows. Short rows are padded, long rows truncated, to <see cref="Headers"/>' width.</param>
public sealed record TableBlock(
    IReadOnlyList<ColumnAlignment> Alignments,
    IReadOnlyList<IReadOnlyList<InlineSpan>> Headers,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineSpan>>> Rows) : MarkdownBlock;

/// <summary>
/// A deliberately small CommonMark subset: what assistant messages actually contain.
/// </summary>
/// <remarks>
/// <para>
/// Supports fenced code (with language), ATX headings, unordered and ordered lists,
/// blockquotes, thematic breaks, and inline bold, italic, code and links. Anything
/// else is passed through as literal text rather than dropped.
/// </para>
/// <para>
/// Lives in Core, not the view, so it can be unit-tested and reused by the backend.
/// It is also written to tolerate a half-finished document: messages are parsed while
/// they stream, so an unclosed code fence must still render as a code block.
/// </para>
/// </remarks>
public static class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();

        if (string.IsNullOrEmpty(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(blocks, paragraph);
                index = ReadFencedCode(lines, index, blocks);
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph(blocks, paragraph);
                index++;
                continue;
            }

            if (IsThematicBreak(trimmed))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new RuleBlock());
                index++;
                continue;
            }

            if (TryReadHeading(trimmed, out var heading))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(heading);
                index++;
                continue;
            }

            if (IsListItem(trimmed, out _))
            {
                FlushParagraph(blocks, paragraph);
                index = ReadList(lines, index, blocks);
                continue;
            }

            // A table needs both lines to be sure: a stray '|' inside ordinary prose
            // ("a | b", a shell pipe) is common, but a delimiter row right under it —
            // only dashes, colons and pipes — is not something prose ever produces.
            if (IsTableRow(trimmed) &&
                index + 1 < lines.Length &&
                IsTableDelimiterRow(lines[index + 1].TrimStart()))
            {
                FlushParagraph(blocks, paragraph);
                index = ReadTable(lines, index, blocks);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(blocks, paragraph);
                index = ReadQuote(lines, index, blocks);
                continue;
            }

            paragraph.Add(trimmed);
            index++;
        }

        FlushParagraph(blocks, paragraph);
        return blocks;
    }

    // ------------------------------------------------------------------ //
    // Block readers
    // ------------------------------------------------------------------ //

    private static int ReadFencedCode(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var language = lines[start].TrimStart()[3..].Trim();
        var code = new StringBuilder();
        var index = start + 1;

        while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            // Explicit '\n', not AppendLine: the input was normalised to LF and
            // AppendLine would put the platform's CRLF back into the code text.
            code.Append(lines[index]).Append('\n');
            index++;
        }

        // Skip the closing fence when there is one. An unterminated fence still
        // renders, because messages are parsed mid-stream.
        if (index < lines.Length)
        {
            index++;
        }

        blocks.Add(new CodeBlock(language, code.ToString().TrimEnd('\n')));
        return index;
    }

    private static int ReadList(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        IsListItem(lines[start].TrimStart(), out var ordered);

        var items = new List<IReadOnlyList<InlineSpan>>();
        var index = start;

        while (index < lines.Length)
        {
            var trimmed = lines[index].TrimStart();
            if (!IsListItem(trimmed, out var itemOrdered) || itemOrdered != ordered)
            {
                break;
            }

            items.Add(ParseInline(StripListMarker(trimmed)));
            index++;
        }

        blocks.Add(new ListBlock(ordered, items));
        return index;
    }

    private static int ReadQuote(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var text = new List<string>();
        var index = start;

        while (index < lines.Length)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith('>'))
            {
                break;
            }

            text.Add(trimmed[1..].TrimStart());
            index++;
        }

        blocks.Add(new QuoteBlock(ParseInline(string.Join(' ', text))));
        return index;
    }

    private static int ReadTable(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var headerCells = SplitTableRow(lines[start]);
        var alignments = SplitTableRow(lines[start + 1]).Select(ParseAlignment).ToList();

        // The delimiter row is the source of truth for column count: a header written
        // with a ragged number of cells is still a table, and the model streaming it
        // one line at a time will very briefly produce exactly that.
        while (alignments.Count > headerCells.Count)
        {
            headerCells.Add(string.Empty);
        }

        var headers = headerCells.Take(alignments.Count).Select(ParseInline).ToList();

        var rows = new List<IReadOnlyList<IReadOnlyList<InlineSpan>>>();
        var index = start + 2;

        while (index < lines.Length && IsTableRow(lines[index].TrimStart()))
        {
            var cells = SplitTableRow(lines[index]);

            while (cells.Count < alignments.Count)
            {
                cells.Add(string.Empty);
            }

            rows.Add(cells.Take(alignments.Count).Select(ParseInline).ToList());
            index++;
        }

        blocks.Add(new TableBlock(alignments, headers, rows));
        return index;
    }

    /// <summary>
    /// A row is any line built from pipe-separated cells — the delimiter row that must
    /// follow is what actually distinguishes a table from a line of prose with a pipe
    /// in it, so this stays permissive on purpose.
    /// </summary>
    private static bool IsTableRow(string trimmed) =>
        trimmed.Length > 0 && trimmed.Contains('|') && !trimmed.StartsWith("```", StringComparison.Ordinal);

    /// <summary>Only dashes, colons and pipes — nothing prose would ever produce on its own line.</summary>
    private static bool IsTableDelimiterRow(string trimmed)
    {
        if (!IsTableRow(trimmed))
        {
            return false;
        }

        var cells = SplitTableRow(trimmed);
        return cells.Count > 0 && cells.All(cell =>
        {
            var body = cell.Trim();
            if (body.Length == 0)
            {
                return false;
            }

            var inner = body.Trim(':');
            return inner.Length > 0 && inner.All(c => c == '-');
        });
    }

    private static ColumnAlignment ParseAlignment(string cell)
    {
        var body = cell.Trim();
        var left = body.StartsWith(':');
        var right = body.EndsWith(':');

        return (left, right) switch
        {
            (true, true) => ColumnAlignment.Center,
            (false, true) => ColumnAlignment.Right,
            (true, false) => ColumnAlignment.Left,
            _ => ColumnAlignment.Default,
        };
    }

    /// <summary>
    /// Splits a table row on unescaped pipes, ignoring pipes inside inline code — a
    /// file path or shell snippet in a cell should not fracture the row.
    /// </summary>
    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        var start = trimmed.StartsWith('|') ? 1 : 0;
        var end = trimmed.Length - (trimmed.EndsWith('|') && trimmed.Length > start ? 1 : 0);

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;

        for (var i = start; i < end; i++)
        {
            var c = trimmed[i];

            if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
                continue;
            }

            if (c == '\\' && i + 1 < end && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static void FlushParagraph(List<MarkdownBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        blocks.Add(new ParagraphBlock(ParseInline(string.Join(' ', paragraph))));
        paragraph.Clear();
    }

    // ------------------------------------------------------------------ //
    // Line classification
    // ------------------------------------------------------------------ //

    private static bool TryReadHeading(string trimmed, out HeadingBlock heading)
    {
        heading = null!;

        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            return false;
        }

        heading = new HeadingBlock(level, ParseInline(trimmed[(level + 1)..].Trim()));
        return true;
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        return marker is '-' or '*' or '_' && trimmed.All(c => c == marker);
    }

    private static bool IsListItem(string trimmed, out bool ordered)
    {
        ordered = false;

        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            return true;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= trimmed.Length)
        {
            return false;
        }

        if (trimmed[digits] is not ('.' or ')') || trimmed[digits + 1] != ' ')
        {
            return false;
        }

        ordered = true;
        return true;
    }

    private static string StripListMarker(string trimmed)
    {
        if (trimmed[0] is '-' or '*' or '+')
        {
            return trimmed[2..].Trim();
        }

        var separator = trimmed.IndexOfAny(['.', ')']);
        return separator < 0 ? trimmed : trimmed[(separator + 1)..].Trim();
    }

    // ------------------------------------------------------------------ //
    // Inline
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Single left-to-right pass. Inline code wins over emphasis, matching
    /// CommonMark, so backticks around asterisks keep the asterisks literal.
    /// </summary>
    public static IReadOnlyList<InlineSpan> ParseInline(string? text)
    {
        var spans = new List<InlineSpan>();

        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var literal = new StringBuilder();
        var index = 0;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                spans.Add(new InlineSpan(literal.ToString()));
                literal.Clear();
            }
        }

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\' && index + 1 < text.Length)
            {
                // Backslash escape: take the next character literally.
                literal.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (current == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    FlushLiteral();
                    spans.Add(new InlineSpan(text[(index + 1)..close], InlineStyle.Code));
                    index = close + 1;
                    continue;
                }
            }

            if (current == '[' && TryReadLink(text, index, out var label, out var href, out var consumed))
            {
                FlushLiteral();
                spans.Add(new InlineSpan(label, InlineStyle.Link, href));
                index += consumed;
                continue;
            }

            if ((current is '*' or '_') && TryReadEmphasis(text, index, out var inner, out var style, out var length))
            {
                FlushLiteral();

                // Emphasis can nest, so the inner run is parsed again and the outer
                // style folded into each resulting span.
                foreach (var span in ParseInline(inner))
                {
                    spans.Add(span with { Style = span.Style | style });
                }

                index += length;
                continue;
            }

            literal.Append(current);
            index++;
        }

        FlushLiteral();
        return spans;
    }

    private static bool TryReadLink(string text, int start, out string label, out string href, out int consumed)
    {
        label = string.Empty;
        href = string.Empty;
        consumed = 0;

        var labelEnd = text.IndexOf(']', start + 1);
        if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            return false;
        }

        var hrefEnd = text.IndexOf(')', labelEnd + 2);
        if (hrefEnd < 0)
        {
            return false;
        }

        label = text[(start + 1)..labelEnd];
        href = text[(labelEnd + 2)..hrefEnd].Trim();
        consumed = hrefEnd - start + 1;
        return true;
    }

    private static bool TryReadEmphasis(string text, int start, out string inner, out InlineStyle style, out int length)
    {
        inner = string.Empty;
        style = InlineStyle.None;
        length = 0;

        var marker = text[start];
        var isDouble = start + 1 < text.Length && text[start + 1] == marker;
        var delimiter = isDouble ? new string(marker, 2) : marker.ToString();
        var contentStart = start + delimiter.Length;

        if (contentStart >= text.Length)
        {
            return false;
        }

        var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (close < 0 || close == contentStart)
        {
            return false;
        }

        inner = text[contentStart..close];
        style = isDouble ? InlineStyle.Bold : InlineStyle.Italic;
        length = close - start + delimiter.Length;
        return true;
    }
}

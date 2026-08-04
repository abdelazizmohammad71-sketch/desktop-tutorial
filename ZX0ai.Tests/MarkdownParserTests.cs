using Xunit;
using ZX0ai.Core.Text;

namespace ZX0ai.Tests;

/// <summary>Covers the markdown subset assistant messages actually use.</summary>
public sealed class MarkdownParserTests
{
    private static string PlainText(IReadOnlyList<InlineSpan> spans) =>
        string.Concat(spans.Select(s => s.Text));

    [Fact]
    public void EmptyInput_ProducesNoBlocks()
    {
        Assert.Empty(MarkdownParser.Parse(null));
        Assert.Empty(MarkdownParser.Parse(string.Empty));
    }

    [Fact]
    public void PlainText_BecomesOneParagraph()
    {
        var block = Assert.IsType<ParagraphBlock>(Assert.Single(MarkdownParser.Parse("Hello there.")));
        Assert.Equal("Hello there.", PlainText(block.Spans));
    }

    [Fact]
    public void BlankLine_SeparatesParagraphs()
    {
        var blocks = MarkdownParser.Parse("First.\n\nSecond.");

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void ConsecutiveLines_JoinIntoOneParagraph()
    {
        var block = Assert.IsType<ParagraphBlock>(Assert.Single(MarkdownParser.Parse("one\ntwo")));
        Assert.Equal("one two", PlainText(block.Spans));
    }

    // ------------------------------------------------------------------ //
    // Code
    // ------------------------------------------------------------------ //

    [Fact]
    public void FencedCode_KeepsLanguageAndBody()
    {
        var block = Assert.IsType<CodeBlock>(Assert.Single(
            MarkdownParser.Parse("```csharp\nvar x = 1;\n```")));

        Assert.Equal("csharp", block.Language);
        Assert.Equal("var x = 1;", block.Code);
    }

    [Fact]
    public void FencedCode_PreservesInternalBlankLinesAndIndentation()
    {
        var block = Assert.IsType<CodeBlock>(Assert.Single(
            MarkdownParser.Parse("```\nif (x)\n{\n\n    y();\n}\n```")));

        Assert.Equal("if (x)\n{\n\n    y();\n}", block.Code);
    }

    [Fact]
    public void UnterminatedFence_StillRendersAsCode()
    {
        // Messages are parsed while they stream, so this is the common case.
        var block = Assert.IsType<CodeBlock>(Assert.Single(
            MarkdownParser.Parse("```python\nprint(1)")));

        Assert.Equal("python", block.Language);
        Assert.Equal("print(1)", block.Code);
    }

    [Fact]
    public void MarkdownInsideCode_IsNotInterpreted()
    {
        var block = Assert.IsType<CodeBlock>(Assert.Single(
            MarkdownParser.Parse("```\n# not a heading\n- not a list\n```")));

        Assert.Contains("# not a heading", block.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void FencedHtml_KeepsLanguageMetadataInsideTheCodeBlock()
    {
        var blocks = MarkdownParser.Parse(
            "قبل الكود.\n\n```html\n<section dir=\"rtl\">مرحبا</section>\n```\n\nبعد الكود.");

        Assert.Collection(
            blocks,
            before => Assert.IsType<ParagraphBlock>(before),
            block =>
            {
                var code = Assert.IsType<CodeBlock>(block);
                Assert.Equal("html", code.Language);
                Assert.Equal("<section dir=\"rtl\">مرحبا</section>", code.Code);
            },
            after => Assert.IsType<ParagraphBlock>(after));

        Assert.DoesNotContain(
            blocks.OfType<ParagraphBlock>(),
            paragraph => PlainText(paragraph.Spans).Contains("html", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ //
    // Headings, lists, quotes, rules
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("# One", 1)]
    [InlineData("### Three", 3)]
    [InlineData("###### Six", 6)]
    public void AtxHeadings_ParseWithTheirLevel(string source, int level)
    {
        var block = Assert.IsType<HeadingBlock>(Assert.Single(MarkdownParser.Parse(source)));
        Assert.Equal(level, block.Level);
    }

    [Fact]
    public void HashWithoutASpace_IsNotAHeading()
    {
        Assert.IsType<ParagraphBlock>(Assert.Single(MarkdownParser.Parse("#hashtag")));
    }

    [Fact]
    public void SevenHashes_IsNotAHeading()
    {
        Assert.IsType<ParagraphBlock>(Assert.Single(MarkdownParser.Parse("####### too deep")));
    }

    [Fact]
    public void UnorderedList_CollectsItems()
    {
        var block = Assert.IsType<ListBlock>(Assert.Single(
            MarkdownParser.Parse("- alpha\n- beta\n- gamma")));

        Assert.False(block.Ordered);
        Assert.Equal(3, block.Items.Count);
        Assert.Equal("beta", PlainText(block.Items[1]));
    }

    [Fact]
    public void OrderedList_IsMarkedOrdered()
    {
        var block = Assert.IsType<ListBlock>(Assert.Single(
            MarkdownParser.Parse("1. first\n2. second")));

        Assert.True(block.Ordered);
        Assert.Equal("first", PlainText(block.Items[0]));
    }

    [Fact]
    public void ArabicIndicOrderedList_IsRecognized()
    {
        var block = Assert.IsType<ListBlock>(Assert.Single(
            MarkdownParser.Parse("١. العنصر الأول\n٢) العنصر الثاني")));

        Assert.True(block.Ordered);
        Assert.Collection(
            block.Items,
            item => Assert.Equal("العنصر الأول", PlainText(item)),
            item => Assert.Equal("العنصر الثاني", PlainText(item)));
    }

    [Fact]
    public void SwitchingListKind_StartsANewBlock()
    {
        var blocks = MarkdownParser.Parse("- bullet\n1. numbered");

        Assert.Equal(2, blocks.Count);
        Assert.False(Assert.IsType<ListBlock>(blocks[0]).Ordered);
        Assert.True(Assert.IsType<ListBlock>(blocks[1]).Ordered);
    }

    [Fact]
    public void Blockquote_JoinsItsLines()
    {
        var block = Assert.IsType<QuoteBlock>(Assert.Single(
            MarkdownParser.Parse("> quoted\n> continued")));

        Assert.Equal("quoted continued", PlainText(block.Spans));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    public void ThematicBreaks_ParseAsRules(string source)
    {
        Assert.IsType<RuleBlock>(Assert.Single(MarkdownParser.Parse(source)));
    }

    // ------------------------------------------------------------------ //
    // Inline
    // ------------------------------------------------------------------ //

    [Fact]
    public void BoldAndItalic_AreDistinguished()
    {
        var spans = MarkdownParser.ParseInline("**bold** and *italic*");

        Assert.Contains(spans, s => s.Text == "bold" && s.Style.HasFlag(InlineStyle.Bold));
        Assert.Contains(spans, s => s.Text == "italic" && s.Style.HasFlag(InlineStyle.Italic));
    }

    [Fact]
    public void NestedEmphasis_CombinesStyles()
    {
        var span = Assert.Single(MarkdownParser.ParseInline("**_both_**"));

        Assert.True(span.Style.HasFlag(InlineStyle.Bold));
        Assert.True(span.Style.HasFlag(InlineStyle.Italic));
    }

    [Fact]
    public void InlineCode_IsMarked()
    {
        var spans = MarkdownParser.ParseInline("call `Foo()` now");

        Assert.Contains(spans, s => s.Text == "Foo()" && s.Style == InlineStyle.Code);
    }

    [Fact]
    public void InlineCode_SuppressesEmphasisInside()
    {
        // CommonMark: code spans win, so the asterisks stay literal.
        var span = Assert.Single(MarkdownParser.ParseInline("`a * b * c`"));

        Assert.Equal(InlineStyle.Code, span.Style);
        Assert.Equal("a * b * c", span.Text);
    }

    [Fact]
    public void Links_CaptureLabelAndHref()
    {
        var span = Assert.Single(MarkdownParser.ParseInline("[docs](https://example.com)"));

        Assert.Equal("docs", span.Text);
        Assert.Equal("https://example.com", span.Href);
        Assert.True(span.Style.HasFlag(InlineStyle.Link));
    }

    [Fact]
    public void UnclosedLink_StaysLiteral()
    {
        Assert.Equal("[broken](", PlainText(MarkdownParser.ParseInline("[broken](")));
    }

    [Fact]
    public void UnmatchedAsterisk_StaysLiteral()
    {
        Assert.Equal("2 * 3 = 6", PlainText(MarkdownParser.ParseInline("2 * 3 = 6")));
    }

    [Fact]
    public void BackslashEscape_KeepsTheMarkerLiteral()
    {
        var spans = MarkdownParser.ParseInline(@"\*not italic\*");

        Assert.Equal("*not italic*", PlainText(spans));
        Assert.All(spans, s => Assert.Equal(InlineStyle.None, s.Style));
    }

    [Fact]
    public void RoundTrip_PreservesAllVisibleText()
    {
        const string source = "Use **bold**, *italic*, `code` and [links](https://x.dev).";

        Assert.Equal(
            "Use bold, italic, code and links.",
            PlainText(MarkdownParser.ParseInline(source)));
    }

    [Fact]
    public void MixedDocument_ProducesTheExpectedBlockSequence()
    {
        var blocks = MarkdownParser.Parse(
            """
            # Title

            Intro paragraph.

            - one
            - two

            ```js
            console.log(1);
            ```

            > note

            ---
            """);

        Assert.Collection(
            blocks,
            b => Assert.IsType<HeadingBlock>(b),
            b => Assert.IsType<ParagraphBlock>(b),
            b => Assert.IsType<ListBlock>(b),
            b => Assert.IsType<CodeBlock>(b),
            b => Assert.IsType<QuoteBlock>(b),
            b => Assert.IsType<RuleBlock>(b));
    }

    // ------------------------------------------------------------------ //
    // Tables
    // ------------------------------------------------------------------ //

    [Fact]
    public void SimpleTable_ParsesHeaderAndRows()
    {
        var block = Assert.IsType<TableBlock>(Assert.Single(MarkdownParser.Parse(
            "| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |")));

        Assert.Equal(2, block.Headers.Count);
        Assert.Equal("a", PlainText(block.Headers[0]));
        Assert.Equal("b", PlainText(block.Headers[1]));
        Assert.Equal(2, block.Rows.Count);
        Assert.Equal("1", PlainText(block.Rows[0][0]));
        Assert.Equal("4", PlainText(block.Rows[1][1]));
    }

    /// <summary>The exact shape a real reply produced: an Arabic heading directly above the table.</summary>
    [Fact]
    public void ArabicHeadingAboveTable_ParsesAsHeadingThenTable()
    {
        var blocks = MarkdownParser.Parse(
            "### الملفات الجذرية\n\n" +
            "| ملف | الغرض |\n" +
            "|------|-------|\n" +
            "| `Program.cs` | نقطة الدخول |\n" +
            "| `MainWindow.cs` | النافذة الرئيسية |");

        var heading = Assert.IsType<HeadingBlock>(blocks[0]);
        Assert.Equal(3, heading.Level);
        Assert.Equal("الملفات الجذرية", PlainText(heading.Spans));

        var table = Assert.IsType<TableBlock>(blocks[1]);
        Assert.Equal("ملف", PlainText(table.Headers[0]));
        Assert.Equal("الغرض", PlainText(table.Headers[1]));
        Assert.Equal(2, table.Rows.Count);

        // The cell keeps its own code styling — the file name is not just plain text.
        var firstCell = Assert.Single(table.Rows[0][0]);
        Assert.Equal(InlineStyle.Code, firstCell.Style);
        Assert.Equal("Program.cs", firstCell.Text);
    }

    [Theory]
    [InlineData("|---|", ColumnAlignment.Default)]
    [InlineData("|:--|", ColumnAlignment.Left)]
    [InlineData("|--:|", ColumnAlignment.Right)]
    [InlineData("|:-:|", ColumnAlignment.Center)]
    public void DelimiterRow_SetsColumnAlignment(string delimiter, ColumnAlignment expected)
    {
        var block = Assert.IsType<TableBlock>(Assert.Single(
            MarkdownParser.Parse($"| h |\n{delimiter}\n| v |")));

        Assert.Equal(expected, Assert.Single(block.Alignments));
    }

    [Fact]
    public void CodeSpanInsideCell_PipeDoesNotSplitTheRow()
    {
        var block = Assert.IsType<TableBlock>(Assert.Single(MarkdownParser.Parse(
            "| cmd | desc |\n|---|---|\n| `a | b` | pipes command |")));

        Assert.Equal(2, block.Rows[0].Count);
        Assert.Equal("a | b", PlainText(block.Rows[0][0]));
    }

    [Fact]
    public void ShortRow_IsPaddedToHeaderWidth()
    {
        var block = Assert.IsType<TableBlock>(Assert.Single(MarkdownParser.Parse(
            "| a | b | c |\n|---|---|---|\n| 1 |")));

        Assert.Equal(3, block.Rows[0].Count);
        Assert.Equal("1", PlainText(block.Rows[0][0]));
        Assert.Empty(block.Rows[0][1]);
    }

    /// <summary>A bare pipe in prose — a shell example, not a table — must stay a paragraph.</summary>
    [Fact]
    public void PipeWithoutADelimiterRow_StaysAParagraph()
    {
        var block = Assert.IsType<ParagraphBlock>(Assert.Single(
            MarkdownParser.Parse("run `ls | grep foo` to filter")));

        Assert.Contains("ls", PlainText(block.Spans));
    }

    [Fact]
    public void PartialTable_WhileStreaming_NeverThrows()
    {
        const string full = "| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";

        for (var length = 0; length <= full.Length; length++)
        {
            Assert.NotNull(MarkdownParser.Parse(full[..length]));
        }
    }

    [Fact]
    public void PartialDocument_NeverThrowsWhileStreaming()
    {
        // Every prefix of a message is parsed as tokens arrive.
        const string full = "# Title\n\nSome **bold** text\n\n```py\nx = 1\n```\n\n- item";

        for (var length = 0; length <= full.Length; length++)
        {
            var blocks = MarkdownParser.Parse(full[..length]);
            Assert.NotNull(blocks);
        }
    }
}

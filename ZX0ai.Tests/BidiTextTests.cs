using Xunit;
using ZX0ai.Core.Text;

namespace ZX0ai.Tests;

public sealed class BidiTextTests
{
    [Fact]
    public void EnglishText_IsLeftToRight()
    {
        var direction = BidiText.DetectParagraphDirection("Open the project settings.");

        Assert.Equal(ParagraphDirection.LeftToRight, direction);
    }

    [Fact]
    public void ArabicText_IsRightToLeft()
    {
        var direction = BidiText.DetectParagraphDirection("افتح إعدادات المشروع.");

        Assert.Equal(ParagraphDirection.RightToLeft, direction);
    }

    [Fact]
    public void LeadingPunctuation_IsIgnoredBeforeArabicText()
    {
        var direction = BidiText.DetectParagraphDirection("... (١٢٣) — افتح المشروع");

        Assert.Equal(ParagraphDirection.RightToLeft, direction);
    }

    [Theory]
    [InlineData("استخدم `dir=rtl` هنا", ParagraphDirection.RightToLeft)]
    [InlineData("`dir=rtl` ثم استخدم العربية", ParagraphDirection.LeftToRight)]
    public void MixedInlineCodeAndArabic_UsesTheFirstStrongLetter(
        string text,
        ParagraphDirection expected)
    {
        Assert.Equal(expected, BidiText.DetectParagraphDirection(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("... 123 — ()")]
    public void TextWithoutStrongLetters_HasNoDetectedDirection(string? text)
    {
        Assert.Null(BidiText.DetectParagraphDirection(text));
    }
}

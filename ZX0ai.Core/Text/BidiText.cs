using System.Globalization;
using System.Text;

namespace ZX0ai.Core.Text;

/// <summary>Logical paragraph direction inferred from the first strong letter.</summary>
public enum ParagraphDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// Small Unicode bidi helper shared by the composer and markdown renderer. It never
/// reverses text; it only chooses the paragraph base direction and leaves ordering to
/// the platform's Unicode bidi implementation.
/// </summary>
public static class BidiText
{
    public static ParagraphDirection? DetectParagraphDirection(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsLetter(Rune.GetUnicodeCategory(rune)))
            {
                continue;
            }

            return IsRightToLeftCodePoint(rune.Value)
                ? ParagraphDirection.RightToLeft
                : ParagraphDirection.LeftToRight;
        }

        return null;
    }

    private static bool IsLetter(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or
        UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter;

    private static bool IsRightToLeftCodePoint(int value) => value is
        >= 0x0590 and <= 0x08FF or
        >= 0xFB1D and <= 0xFDFF or
        >= 0xFE70 and <= 0xFEFF or
        >= 0x10800 and <= 0x10FFF or
        >= 0x1E800 and <= 0x1EDFF or
        >= 0x1EE00 and <= 0x1EEFF;
}

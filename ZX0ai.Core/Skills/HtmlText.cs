using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZX0ai.Core.Skills;

/// <summary>
/// Minimal HTML-to-text extraction for the fetch and search skills.
/// </summary>
/// <remarks>
/// Deliberately regex-based rather than a parser dependency: the goal is to hand a
/// model readable prose, not to build a DOM. Script and style content is dropped
/// first, because otherwise minified JavaScript dominates the extracted text.
/// </remarks>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex RepeatedSpaces();

    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex RepeatedNewlines();

    [GeneratedRegex(
        """<a[^>]*class="result__a"[^>]*href="([^"]+)"[^>]*>(.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SearchResultLink();

    [GeneratedRegex(
        """<a[^>]*class="result__snippet"[^>]*>(.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SearchResultSnippet();

    /// <summary>Strips markup and returns readable text.</summary>
    public static string Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = ScriptOrStyle().Replace(html, " ");
        text = BlockEnd().Replace(text, "\n");
        text = AnyTag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = RepeatedSpaces().Replace(text, " ");
        text = RepeatedNewlines().Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>Pulls result rows out of a DuckDuckGo HTML results page.</summary>
    public static IReadOnlyList<(string Title, string Url, string Snippet)> ExtractSearchResults(
        string html,
        int limit)
    {
        var links = SearchResultLink().Matches(html);
        var snippets = SearchResultSnippet().Matches(html);
        var results = new List<(string, string, string)>();

        for (var i = 0; i < links.Count && results.Count < limit; i++)
        {
            var href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            var title = Extract(links[i].Groups[2].Value);

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippet = i < snippets.Count ? Extract(snippets[i].Groups[1].Value) : string.Empty;
            results.Add((title, Unwrap(href), snippet));
        }

        return results;
    }

    /// <summary>
    /// DuckDuckGo wraps result links in its own redirector; the real URL is in the
    /// <c>uddg</c> query parameter.
    /// </summary>
    private static string Unwrap(string href)
    {
        const string marker = "uddg=";
        var index = href.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            return href;
        }

        var value = href[(index + marker.Length)..];
        var end = value.IndexOf('&');

        if (end >= 0)
        {
            value = value[..end];
        }

        return Uri.UnescapeDataString(value);
    }

    /// <summary>Wraps a bare fragment in a minimal dark document for the preview panel.</summary>
    public static string EnsureDocument(string html)
    {
        if (html.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        builder.Append("<style>body{background:#0a0a12;color:#f2f2f7;");
        builder.Append("font-family:'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;");
        builder.Append("margin:0;padding:24px;line-height:1.6}</style></head><body>");
        builder.Append(html);
        builder.Append("</body></html>");

        return builder.ToString();
    }
}

using System.Net;
using System.Text.RegularExpressions;

namespace HomeControl.Core.Assistant;

/// <summary>
/// Pulls readable text out of the HTML "screen" the Assistant renders for display devices.
/// Used when the reply has no supplemental display text.
/// </summary>
internal static partial class AssistantHtml
{
    private const int MaxLength = 400;

    public static string ExtractText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // The answer text is usually inside an element with this class.
        var answers = ShowTextContent().Matches(html)
            .Select(m => Clean(m.Groups["text"].Value))
            .Where(t => t.Length > 0)
            .ToList();

        var text = answers.Count > 0 ? string.Join(" ", answers) : Clean(BodyOrAll(html));
        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd() + "…";
    }

    private static string BodyOrAll(string html)
    {
        var body = Body().Match(html);
        return body.Success ? body.Groups["body"].Value : html;
    }

    private static string Clean(string fragment)
    {
        var withoutCode = NonContent().Replace(fragment, " ");
        var withoutTags = Tags().Replace(withoutCode, " ");
        return Whitespace().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    [GeneratedRegex(@"<(?<tag>div|span|p)\b[^>]*class=""[^""]*\bshow_text_content\b[^""]*""[^>]*>(?<text>.*?)</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShowTextContent();

    [GeneratedRegex(@"<body\b[^>]*>(?<body>.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Body();

    [GeneratedRegex(@"<(script|style|head|template|svg)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContent();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

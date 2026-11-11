using System.Net;
using System.Text.RegularExpressions;

namespace OutreachFlow.Services.Research;

/// <summary>
/// Pure HTML-content quality checks. A successful HTTP status is not evidence that
/// the intended website content was actually delivered to this read-only client.
/// </summary>
public sealed record ResearchPageDiagnostic(
    string Url, string Title, int VisibleCharacters,
    IReadOnlyList<string> MatchedTerms, string Warning)
{
    public bool IsInconclusive => !string.IsNullOrWhiteSpace(Warning);
}

public static class ResearchPageInspector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Hidden = new(
        @"<!--[\s\S]*?-->|<(script|style|noscript|template)\b[^>]*>[\s\S]*?</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled, Timeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, Timeout);
    private static readonly Regex Title = new(
        @"<title\b[^>]*>(?<value>[\s\S]*?)</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, Timeout);

    public static ResearchPageDiagnostic Inspect(Uri url, string html, ResearchProfile profile)
    {
        profile = ResearchProfile.Validate(profile);
        var titleMatch = Title.Match(html);
        var title = titleMatch.Success ? Plain(titleMatch.Groups["value"].Value) : "(untitled HTML)";
        if (title.Length > 120) title = title[..120] + "…";
        var visible = Plain(Hidden.Replace(html, " "));
        var matched = profile.PriorityTerms.Concat(profile.ProcessTerms)
            .Where(t => visible.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();

        var intro = visible[..Math.Min(350, visible.Length)];
        var challengeTitle = new[] { "access denied", "just a moment", "attention required",
            "verify you are human", "request blocked", "security check" }
            .Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
        var challengeIntro = new[] { "enable javascript to continue", "verify you are human",
            "checking your browser before accessing", "your request has been blocked",
            "unusual traffic from your network", "captcha required" }
            .Any(t => intro.Contains(t, StringComparison.OrdinalIgnoreCase));
        var pathImpliesRelevance = profile.ProcessTerms.Concat(profile.PriorityTerms)
            .Any(t => t.Length >= 4 && url.AbsolutePath.Contains(t, StringComparison.OrdinalIgnoreCase));
        string warning = challengeTitle || challengeIntro
            ? "The returned HTML resembles a block/challenge page, not reliable business content."
            : visible.Length < 120
                ? "The returned HTML has very little readable text (possibly a JavaScript-only shell)."
                : pathImpliesRelevance && matched.Length == 0
                    ? "The URL suggests a relevant page, but its returned HTML lacks the configured terms."
                    : "";
        return new ResearchPageDiagnostic(url.AbsoluteUri, title, visible.Length, matched, warning);
    }

    private static string Plain(string html) =>
        Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
}

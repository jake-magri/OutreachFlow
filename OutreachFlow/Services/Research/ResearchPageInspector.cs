using System.Net;
using System.Text.RegularExpressions;

namespace OutreachFlow.Services.Research;

/// <summary>
/// Content-quality diagnostics contain only bounded page metadata and matched
/// allowlisted rule phrases, never arbitrary HTML or authentication data.
/// </summary>
public sealed record ResearchPageDiagnostic(
    string Url, string Title, int VisibleCharacters,
    IReadOnlyList<string> MatchedTerms, string Warning)
{
    public bool IsInconclusive => !string.IsNullOrWhiteSpace(Warning);
    public int HttpStatusCode { get; init; } = 200;
    /// <summary>Rule that first classified the HTTP response (not a guess about the site).</summary>
    public string InitialRuleId { get; init; } = "none";
    /// <summary>Literal phrase from the scanner's allowlist; not raw website text.</summary>
    public string MatchedPhrase { get; init; } = "";
    public string BrowserDecision { get; init; } = "Not requested";
    public bool IsChallenge => InitialRuleId is "challenge-title" or "challenge-intro";
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

    private static readonly string[] ChallengeTitles =
    [
        "access denied", "just a moment", "attention required",
        "verify you are human", "request blocked", "security check"
    ];

    private static readonly string[] ChallengeIntros =
    [
        "enable javascript to continue", "verify you are human",
        "checking your browser before accessing", "your request has been blocked",
        "unusual traffic from your network", "captcha required"
    ];

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
        // Only the literal *rule phrase* is persisted; never save the raw title
        // or HTML snippet as a "matched phrase".
        var titleHit = ChallengeTitles.FirstOrDefault(t =>
            title.Contains(t, StringComparison.OrdinalIgnoreCase));
        var introHit = ChallengeIntros.FirstOrDefault(t =>
            intro.Contains(t, StringComparison.OrdinalIgnoreCase));
        var pathImpliesRelevance = profile.ProcessTerms.Concat(profile.PriorityTerms)
            .Any(t => t.Length >= 4 && url.AbsolutePath.Contains(t, StringComparison.OrdinalIgnoreCase));
        var rule = titleHit is not null ? "challenge-title" :
            introHit is not null ? "challenge-intro" :
            visible.Length < 120 ? "minimal-readable-text" :
            pathImpliesRelevance && matched.Length == 0 ? "url-content-mismatch" : "none";
        var warning = rule switch
        {
            "challenge-title" or "challenge-intro" =>
                "The returned HTML resembles a block/challenge page, not reliable business content.",
            "minimal-readable-text" =>
                "The returned HTML has very little readable text (possibly a JavaScript-only shell).",
            "url-content-mismatch" =>
                "The URL suggests a relevant page, but its returned HTML lacks the configured terms.",
            _ => ""
        };
        return new ResearchPageDiagnostic(url.AbsoluteUri, title, visible.Length, matched, warning)
        {
            InitialRuleId = rule,
            MatchedPhrase = titleHit ?? introHit ?? ""
        };
    }

    private static string Plain(string html) =>
        Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
}

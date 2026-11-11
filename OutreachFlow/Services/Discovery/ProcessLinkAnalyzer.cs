using System.Net;
using System.Text.RegularExpressions;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

/// <summary>
/// Describes links observed in retrieved HTML. It records what the source linked
/// to, NOT that the publisher is official or that a form is still valid.
/// </summary>
public sealed record ProcessLinkEvidence(
    string SourcePageUrl, string DestinationUrl, string AnchorText,
    string Relationship, string ProcessHint)
{
    public string VerificationState => "Unverified — source identity and process applicability require review";
}

public static class ProcessLinkAnalyzer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Anchors = new(
        @"<a\b(?<attributes>[^>]{0,1800})>(?<label>[\s\S]*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex Href = new(
        "\\bhref\\s*=\\s*(?:\\\"(?<url>[^\\\"]+)\\\"|'(?<url>[^']+)'|(?<url>[^\\s>]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, RegexTimeout);

    private static readonly string[] ProcessPhrases =
    [
        "vendor", "supplier", "procurement", "subcontractor", "trade partner",
        "prequalification", "prequalify", "register", "apply", "application",
        "bid", "solicitation", "opportunities", "join our network"
    ];
    private static readonly string[] EmploymentPhrases =
    [
        "career", "jobs", "employment", "job openings", "work for us"
    ];

    public static IReadOnlyList<ProcessLinkEvidence> Extract(
        Uri sourcePage, string html, ResearchProfile profile)
    {
        profile = ResearchProfile.Validate(profile);
        var items = new Dictionary<string, ProcessLinkEvidence>(StringComparer.OrdinalIgnoreCase);
        if (html.Length > 300_000) return [];
        foreach (Match anchor in Anchors.Matches(html))
        {
            if (items.Count >= 20) break;
            var match = Href.Match(anchor.Groups["attributes"].Value);
            if (!match.Success) continue;
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (href.Length > 2048 || !Uri.TryCreate(sourcePage, href, out var destination)) continue;
            Uri safe;
            try { safe = ResearchUrlPolicy.ParseWebsite(destination.AbsoluteUri); }
            catch (InvalidOperationException) { continue; }
            var canonical = new UriBuilder(safe) { Fragment = "" }.Uri.AbsoluteUri;
            var label = Spaces.Replace(WebUtility.HtmlDecode(
                Tags.Replace(anchor.Groups["label"].Value, " ")), " ").Trim();
            if (label.Length > 120) label = label[..120];
            var signals = label + " " + safe.AbsolutePath;
            if (profile.ExcludedTerms.Any(t => signals.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool potentialProcess = ProcessPhrases.Concat(profile.ProcessTerms)
                .Any(t => signals.Contains(t, StringComparison.OrdinalIgnoreCase));
            bool job = EmploymentPhrases.Any(t => signals.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (!potentialProcess && !job) continue;

            var sameSite = ResearchUrlPolicy.IsSameSite(sourcePage, safe);
            var relationship = sameSite ? "Same-site link observed" : "Cross-domain link observed";
            var hint = job && !potentialProcess ? "Possible employment route" :
                signals.Contains("contact", StringComparison.OrdinalIgnoreCase)
                    ? "Contact page; not necessarily an application" :
                "Possible registration, procurement or application link";
            items.TryAdd(canonical, new ProcessLinkEvidence(sourcePage.AbsoluteUri,
                canonical, label, relationship, hint));
        }
        return items.Values.ToArray();
    }
}

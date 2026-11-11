using System.Globalization;
using System.Text.RegularExpressions;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

/// <summary>
/// Suggested research next steps based only on unverified indexed title, URL and snippet.
/// These are work-queue hints, not claims about official identity or eligibility.
/// </summary>
public enum SuggestedNextStep
{
    ApplicationReview,
    InquiryReview,
    FurtherResearch,
    Deprioritize
}

public sealed record TriageSuggestion(SuggestedNextStep Step, string Explanation)
{
    public string Label => OpportunityTriage.Label(Step);
    public bool HasSuggestedRoute => Step != SuggestedNextStep.Deprioritize;
    public DiscoveryRoute? Route => Step switch
    {
        SuggestedNextStep.ApplicationReview => DiscoveryRoute.ApplicationReview,
        SuggestedNextStep.InquiryReview => DiscoveryRoute.InquiryReview,
        SuggestedNextStep.FurtherResearch => DiscoveryRoute.FurtherResearch,
        _ => null
    };
}

public static class OpportunityTriage
{
    public static readonly IReadOnlyList<SuggestedNextStep> DisplayOrder =
    [
        SuggestedNextStep.ApplicationReview,
        SuggestedNextStep.InquiryReview,
        SuggestedNextStep.FurtherResearch,
        SuggestedNextStep.Deprioritize
    ];

    private static readonly string[] EmploymentProcessTerms =
        ["job openings", "career opportunities", "open positions",
         "job listing", "employment", "driver hiring"];

    // Deliberately narrower than a generic "contact us": buyer/recipient relevance
    // remains unverified, but these phrases warrant researching an inquiry route.
    private static readonly string[] InquirySignals =
        ["vendor inquiries", "supplier inquiries", "procurement contact",
         "contact procurement", "contact purchasing", "purchasing department",
         "subcontractor inquiries", "trade partner inquiries"];


    private static readonly string[] StrongApplicationSignals =
    [
        "vendor setup form", "new vendor setup", "vendor onboarding",
        "supplier registration", "vendor registration", "register as a vendor",
        "register to do business", "become a vendor", "vendor application",
        "supplier application", "subcontractor prequalification", "trade partner registration"
    ];

    private static readonly string[] FleetServiceProviderSignals =
    [
        "fleet washing company", "fleet washing service", "fleet washing services",
        "onsite fleet washing", "on-site fleet washing", "recurring fleet washing",
        "we wash trucks", "commercial fleet cleaning company"
    ];

    private static readonly string[] EndedProcessSignals =
    [
        "past due", "no longer accepting responses", "submission period has ended",
        "solicitation closed", "bidding closed", "closed for bids"
    ];

    // Only dates explicitly identified as deadlines may change the recommendation.
    // A posting or publication date is not a deadline.
    private static readonly Regex DeadlinePattern = new(
        @"\b(?:due|deadline|closing|closes|response\s+deadline)(?:\s+(?:on|by|date))?\s*[:\-]?\s*(?<date>\d{1,2}/\d{1,2}/\d{2,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static bool ContainsAny(string haystack, IEnumerable<string> phrases) =>
        phrases.Any(phrase => haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static DateOnly? PastDeadline(string indexedText, DateOnly today)
    {
        foreach (Match match in DeadlinePattern.Matches(indexedText))
        {
            if (DateOnly.TryParseExact(match.Groups["date"].Value,
                ["M/d/yy", "M/d/yyyy", "MM/dd/yy", "MM/dd/yyyy"],
                CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None,
                out var day) && day < today)
                return day;
        }
        return null;
    }

    public static bool IsEmploymentProfile(ResearchProfile profile) =>
        profile.ProcessTerms.Any(term => EmploymentProcessTerms.Any(needle =>
            term.Contains(needle, StringComparison.OrdinalIgnoreCase)));

    public static string Label(SuggestedNextStep step) => step switch
    {
        SuggestedNextStep.ApplicationReview => "Review application process",
        SuggestedNextStep.InquiryReview => "Review inquiry eligibility",
        SuggestedNextStep.FurtherResearch => "Research further",
        SuggestedNextStep.Deprioritize => "Deprioritize / possible false match",
        _ => "Research further"
    };

    public static TriageSuggestion Recommend(string indexedText, ResearchProfile profile,
        bool buyerProcess, bool seller, bool guide, bool employmentListing,
        DateOnly? todayUtc = null)
    {
        // Never infer a confirmed process or a contact solely from search metadata.
        var today = todayUtc ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var deadline = PastDeadline(indexedText, today);
        if (deadline is not null || ContainsAny(indexedText, EndedProcessSignals))
            return new(SuggestedNextStep.Deprioritize,
                deadline is DateOnly date
                    ? $"Indexed result reports a deadline of {date:yyyy-MM-dd}, which has passed. Treat as historical and verify any future reissue."
                    : "Indexed result describes a closed or expired process. Treat as historical; verify any new solicitation separately.");
        if (guide)
            return new(SuggestedNextStep.Deprioritize,
                "Indexed text resembles a guide or third-party explanation. Find and inspect the organization's own instructions before treating it as an opportunity.");
        if (seller && !buyerProcess)
            return new(SuggestedNextStep.Deprioritize,
                "Indexed text suggests a business selling products or supplies, not an organization inviting vendor applications.");
        var employmentProfile = IsEmploymentProfile(profile);
        if (employmentProfile)
        {
            if (employmentListing && !buyerProcess)
                return new(SuggestedNextStep.ApplicationReview,
                    "Possible employment application for this workspace profile. Verify the employer, position and official application page.");
            if (buyerProcess && !employmentListing)
                return new(SuggestedNextStep.Deprioritize,
                    "This appears to describe business procurement, not a position for the employment-focused workspace.");
            return new(SuggestedNextStep.FurtherResearch,
                "Employment and business-process signals are missing or mixed; investigate the original source.");
        }
        if (employmentListing && !buyerProcess)
            return new(SuggestedNextStep.Deprioritize,
                "A hiring or careers listing is not itself a business contracting opportunity for this workspace.");
        var fleetWorkspace = profile.PriorityTerms.Any(term =>
            term.Contains("fleet washing", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("truck washing", StringComparison.OrdinalIgnoreCase));
        if (fleetWorkspace && !buyerProcess &&
            ContainsAny(indexedText, FleetServiceProviderSignals))
            return new(SuggestedNextStep.Deprioritize,
                "Indexed source appears to provide fleet-washing services itself, rather than purchase them. Keep as competitor or partner intelligence.");
        if (buyerProcess && (seller || employmentListing))
            return new(SuggestedNextStep.FurtherResearch,
                "Indexed text mixes buyer registration with selling or hiring language. Check the source before choosing an action.");
        if (buyerProcess)
        {
            var strongProcess = ContainsAny(indexedText, StrongApplicationSignals);
            var industryRelevant = profile.PriorityTerms.Any(term =>
                term.Length > 5 &&
                !new[] { "business opportunities", "supplier", "vendor", "contractor",
                    "partners", "services", "careers", "jobs" }
                    .Contains(term, StringComparer.OrdinalIgnoreCase) &&
                indexedText.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (!strongProcess && !industryRelevant)
                return new(SuggestedNextStep.FurtherResearch,
                    "A general procurement page or RFP reference was found, but no specific industry match or registration step is established. Check for a relevant active opportunity.");
            return new(SuggestedNextStep.ApplicationReview,
                "Indexed text indicates a possible vendor application or relevant procurement process. Verify the buyer, deadline and eligibility before acting.");
        }
        if (!seller && !employmentListing && InquirySignals.Any(term =>
                indexedText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return new(SuggestedNextStep.InquiryReview,
                "Indexed text mentions a vendor or procurement contact but no clear application process. Verify recipient and inquiry suitability.");
        return new(SuggestedNextStep.FurtherResearch,
            "No specific application or inquiry process is established by the indexed result. Research the organization's official source.");
    }
}

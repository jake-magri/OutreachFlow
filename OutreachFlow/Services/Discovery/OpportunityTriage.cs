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
        bool buyerProcess, bool seller, bool guide, bool employmentListing)
    {
        // Never infer a confirmed process or a contact solely from search metadata.
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
        if (buyerProcess && (seller || employmentListing))
            return new(SuggestedNextStep.FurtherResearch,
                "Indexed text mixes buyer registration with selling or hiring language. Check the source before choosing an action.");
        if (buyerProcess)
            return new(SuggestedNextStep.ApplicationReview,
                "Indexed text contains buyer-side registration or procurement terms. Verify the buyer, real process and eligibility before acting.");
        if (!seller && !employmentListing && InquirySignals.Any(term =>
                indexedText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return new(SuggestedNextStep.InquiryReview,
                "Indexed text mentions a vendor or procurement contact but no clear application process. Verify recipient and inquiry suitability.");
        return new(SuggestedNextStep.FurtherResearch,
            "No specific application or inquiry process is established by the indexed result. Research the organization's official source.");
    }
}

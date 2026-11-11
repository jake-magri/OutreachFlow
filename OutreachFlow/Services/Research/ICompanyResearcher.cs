namespace OutreachFlow.Services.Research;

/// <summary>A candidate is a lead for human review, NOT a verified supplier process.</summary>
public sealed record ResearchCandidate(string Title, string Url, string Observation);

/// <summary>A business email exposed on a checked public page. It is not automatically approved for outreach.</summary>
public sealed record ResearchContact(string Address, string SourceUrl, string Observation);

public sealed record ResearchScan(IReadOnlyList<ResearchCandidate> Candidates,
    IReadOnlyList<string> PagesChecked, string Limitations)
{
    // Optional additions preserve compatibility with existing fake research adapters.
    public IReadOnlyList<ResearchContact> Contacts { get; init; } = [];
    public string SuggestedReview { get; init; } = "";
}

public interface ICompanyResearcher
{
    Task<ResearchScan> SearchAsync(string website, CancellationToken cancellationToken = default);

    // Existing research adapters continue working; production crawler honors the explicit profile.
    Task<ResearchScan> SearchAsync(string website, ResearchProfile profile, CancellationToken cancellationToken = default)
        => SearchAsync(website, cancellationToken);
}

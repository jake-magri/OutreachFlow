namespace OutreachFlow.Services.Research;

/// <summary>A candidate is a lead for a human researcher, NOT a verified supplier process.</summary>
public sealed record ResearchCandidate(string Title, string Url, string Observation);
public sealed record ResearchScan(IReadOnlyList<ResearchCandidate> Candidates,
    IReadOnlyList<string> PagesChecked, string Limitations);

public interface ICompanyResearcher
{
    Task<ResearchScan> SearchAsync(string website, CancellationToken cancellationToken = default);
}

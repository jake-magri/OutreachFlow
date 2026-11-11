using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

public sealed record OpportunityDiscoveryBatch(
    int WorkspaceId, string ProviderName, DateTime RetrievedUtc,
    IReadOnlyList<string> Queries, IReadOnlyList<OpportunityCandidate> Candidates,
    SearchBudgetSnapshot Budget, IReadOnlyList<string> Warnings);

/// <summary>
/// All search is on-demand. Search results stay in the requesting UI session
/// and are NEVER automatically treated as a confirmed official process.
/// </summary>
public sealed class OpportunityDiscoveryService(
    IWebSearchProvider provider, ISearchUsageBudget budget,
    IResearchProfileStore profiles, IWorkspaceAccess access,
    IDbContextFactory<OutreachDbContext> factory, OutreachService outreach)
{
    public bool IsSearchConfigured => provider.IsConfigured;
    public string ProviderName => provider.Name;
    public Task<SearchBudgetSnapshot> GetBudgetAsync() => budget.GetAsync();

    public async Task<OpportunityDiscoveryBatch> DiscoverAsync(CancellationToken ct = default)
    {
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        if (!provider.IsConfigured)
            throw new InvalidOperationException("Web discovery is not configured. The app owner must configure the search provider.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(
            x => x.Id == workspaceId, ct);
        var profile = await profiles.LoadAsync(workspaceId, ct);
        var queries = DeterministicQueryPlanner.Plan(profile, workspace.ServiceArea);
        var results = new List<(string Query, SearchListing Listing)>();
        var warnings = new List<string>();
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            // Reserve one provider request at a time. Charge attempts, not results.
            await budget.ReserveAsync(ct);
            try
            {
                var listings = await provider.SearchAsync(query, maxResults: 5, ct);
                results.AddRange(listings.Select(x => (query, x)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                       or TaskCanceledException or System.Text.Json.JsonException)
            {
                // Missing one search does not erase other indexed candidates,
                // but both failures must be reported rather than concealed.
                warnings.Add("Search request failed: " + ex.GetType().Name +
                    ". Review provider status and retry manually.");
            }
        }
        return new OpportunityDiscoveryBatch(workspaceId, provider.Name, DateTime.UtcNow,
            queries, OpportunityRanker.Rank(results, profile), await budget.GetAsync(ct), warnings);
    }

    public async Task<(int Added, int Skipped)> ImportReviewedAsync(
        int workspaceIdAtSearch, OpportunityCandidate selected, CancellationToken ct = default)
    {
        var currentWorkspace = await access.ActiveWorkspaceIdAsync();
        if (workspaceIdAtSearch != currentWorkspace)
            throw new InvalidOperationException("Workspace changed since discovery. Search again before importing.");

        var uri = ResearchUrlPolicy.ParseWebsite(selected.Url);
        // Indexed metadata is not official evidence, and cannot authorize outreach.
        // Strip the pipe delimiter used by the existing import format.
        var name = (selected.Title ?? "").Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (name.Length > 140) name = name[..140];
        if (name.Length == 0) name = uri.Host;
        var line = name + " | Indexed search result | " + uri.AbsoluteUri + " | ";
        var (added, skipped) = await outreach.ImportAsync(line);
        return (added, skipped);
    }
}

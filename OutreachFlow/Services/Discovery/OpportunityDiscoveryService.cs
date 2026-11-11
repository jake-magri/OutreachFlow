using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

public enum DiscoveryRoute
{
    FurtherResearch,
    ApplicationReview,
    InquiryReview
}

public sealed record DiscoveryRouteResult(int ProspectId, bool Added);

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
    IDbContextFactory<OutreachDbContext> factory)
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

    // Retained for older callers: the default route always means further research.
    public async Task<(int Added, int Skipped)> ImportReviewedAsync(
        int workspaceIdAtSearch, OpportunityCandidate selected, CancellationToken ct = default)
    {
        var result = await RouteReviewedAsync(workspaceIdAtSearch, selected,
            DiscoveryRoute.FurtherResearch, ct);
        return result.Added ? (1, 0) : (0, 1);
    }

    /// <summary>
    /// Explicit operator triage. Indexed results never establish official process
    /// status, recipient approval, or permission to send. The selected action,
    /// source evidence and history are committed together in the current workspace.
    /// </summary>
    public async Task<DiscoveryRouteResult> RouteReviewedAsync(
        int workspaceIdAtSearch, OpportunityCandidate selected, DiscoveryRoute route,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!Enum.IsDefined(route))
            throw new InvalidOperationException("Unknown discovery route.");

        var workspaceId = await access.ActiveWorkspaceIdAsync();
        if (workspaceId != workspaceIdAtSearch)
            throw new InvalidOperationException("Workspace changed since discovery. Search again before routing.");

        // Recheck the source URL on the server; remove fragments to avoid duplicate
        // prospects for different anchors on the same indexed page.
        var url = new UriBuilder(ResearchUrlPolicy.ParseWebsite(selected.Url)) { Fragment = "" }
            .Uri.AbsoluteUri;
        var name = (selected.Title ?? "").Replace('|', ' ').Replace('\n', ' ')
            .Replace('\r', ' ').Trim();
        if (name.Length > 140) name = name[..140];
        if (name.Length == 0) name = new Uri(url).Host;
        const string site = "Indexed search result";

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Prospects.AsNoTracking().FirstOrDefaultAsync(
            p => p.WorkspaceId == workspaceId &&
                 (p.Website == url || (p.CompanyName == name && p.SiteName == site)), ct);
        // Never change existing ownership, suppression, or a previously selected route
        // through a repeat discovery click.
        if (existing is not null)
            return new DiscoveryRouteResult(existing.Id, false);

        var nextAction = route switch
        {
            DiscoveryRoute.ApplicationReview =>
                "Verify the official application or registration process; apply manually if relevant",
            DiscoveryRoute.InquiryReview =>
                "Verify the process and recipient; decide whether to approve an inquiry",
            _ => "Research the official source and determine which process applies"
        };
        var status = route switch
        {
            DiscoveryRoute.ApplicationReview => ProspectStatus.ApplicationTask,
            DiscoveryRoute.InquiryReview => ProspectStatus.NeedsAction,
            _ => ProspectStatus.ResearchNeeded
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var prospect = new Prospect
        {
            WorkspaceId = workspaceId,
            CompanyName = name,
            SiteName = site,
            Website = url,
            ResearchOutcome = ResearchOutcome.NotReviewed,
            Status = status,
            NextAction = nextAction,
            UpdatedUtc = now,
            CreatedUtc = now
        };
        db.Prospects.Add(prospect);
        await db.SaveChangesAsync(ct); // Obtain generated key within transaction.
        // Never store an indexed snippet as verified instructions, and do not
        // copy the search result URL into the confirmed ProcessUrl field.
        var observation = $"Indexed search snippet only (unverified). " +
            $"Intent hint: {selected.IntentCategory}. Query: {selected.Query}. " +
            $"Summary: {selected.Summary}";
        db.ResearchEvidence.Add(new ResearchEvidence
        {
            ProspectId = prospect.Id,
            SourceTitle = ("Search result: " + name)[..Math.Min(200, 15 + name.Length)],
            SourceUrl = url,
            ExcerptOrObservation = observation[..Math.Min(1200, observation.Length)],
            IsOfficial = false,
            CheckedUtc = now
        });
        db.Activities.Add(new ActivityEntry
        {
            ProspectId = prospect.Id,
            Kind = "Discovery triage",
            Detail = $"Owner selected {route} from an unverified indexed result. " +
                "No process, recipient, or sending approval was granted.",
            OccurredUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DiscoveryRouteResult(prospect.Id, true);
    }
}

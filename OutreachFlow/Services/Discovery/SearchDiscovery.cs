using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

public sealed record SearchListing(string Title, string Url, string Summary);

/// <summary>Search results are unverified index candidates, not official process evidence.</summary>
public sealed record OpportunityCandidate(string Title, string Url, string Host, string Query,
    int RelevanceScore, string Summary)
{
    public string SourceTier => "Search-index candidate — unverified";
}

public interface IWebSearchProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<SearchListing>> SearchAsync(string query, int maxResults,
        CancellationToken cancellationToken = default);
}

public static class DeterministicQueryPlanner
{
    public static IReadOnlyList<string> Plan(ResearchProfile profile, string? serviceArea, int maxQueries = 2)
    {
        profile = ResearchProfile.Validate(profile);
        if (maxQueries is < 1 or > 3)
            throw new InvalidOperationException("A search must contain 1–3 bounded queries.");
        var area = Clean((serviceArea ?? "").Trim(), 65);
        var proposals = new List<string>();
        // Form compact, predictable queries. No LLM, no arbitrary regex, no unbounded expansion.
        for (int i = 0; i < maxQueries; i++)
        {
            var topic = profile.PriorityTerms[i % profile.PriorityTerms.Length];
            var action = profile.ProcessTerms[i % profile.ProcessTerms.Length];
            var query = Clean(string.Join(" ", new[] { area, topic, action }
                .Where(s => !string.IsNullOrWhiteSpace(s))), 280);
            if (!proposals.Contains(query, StringComparer.OrdinalIgnoreCase))
                proposals.Add(query);
        }
        return proposals;
    }

    private static string Clean(string input, int limit)
    {
        var normalized = string.Join(" ", input.Split((char[]?)null,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > limit ? normalized[..limit].TrimEnd() : normalized;
    }
}

public static class OpportunityRanker
{
    public static IReadOnlyList<OpportunityCandidate> Rank(
        IEnumerable<(string Query, SearchListing Listing)> items, ResearchProfile profile)
    {
        profile = ResearchProfile.Validate(profile);
        var candidates = new Dictionary<string, OpportunityCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (query, item) in items.Take(60))
        {
            Uri uri;
            try { uri = ResearchUrlPolicy.ParseWebsite(item.Url); }
            catch (InvalidOperationException) { continue; }

            // Keep a full safe source URL so the owner can inspect the exact process page.
            var normalized = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
            var title = (item.Title ?? "").Trim();
            if (title.Length > 180) title = title[..180];
            if (title.Length == 0) title = uri.Host;
            var summary = (item.Summary ?? "").Trim();
            if (summary.Length > 300) summary = summary[..300];
            var text = title + " " + summary + " " + uri.AbsolutePath;
            if (profile.ExcludedTerms.Any(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;

            // This ranks relevant indexed results. It is not a probability of legitimacy,
            // a verification of ownership, or permission to send an inquiry.
            var priority = profile.PriorityTerms.Count(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase));
            var process = profile.ProcessTerms.Count(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase));
            var score = Math.Min(100, 10 * Math.Min(priority, 4) +
                8 * Math.Min(process, 4) + (uri.Scheme == "https" ? 5 : 0));
            var candidate = new OpportunityCandidate(title, normalized, uri.Host, query, score, summary);
            if (!candidates.TryGetValue(normalized, out var existing) ||
                candidate.RelevanceScore > existing.RelevanceScore)
                candidates[normalized] = candidate;
        }

        return candidates.Values.OrderByDescending(x => x.RelevanceScore)
            .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Take(20).ToArray();
    }
}

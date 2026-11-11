using OutreachFlow.Services.Research;

namespace OutreachFlow.Services.Discovery;

public sealed record SearchListing(string Title, string Url, string Summary);

/// <summary>Search results are unverified index candidates, not official process evidence.</summary>
public sealed record OpportunityCandidate(string Title, string Url, string Host, string Query,
    int RelevanceScore, string Summary)
{
    public string SourceTier => "Search-index candidate — unverified";
    public string IntentCategory { get; init; } = "Unclassified";
    public string RankingReason { get; init; } = "Keyword relevance only; verify the source.";
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
    private static readonly HashSet<string> GenericTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "business opportunities", "supplier", "vendor", "contractor", "partners",
        "services", "careers", "jobs", "application", "register", "procurement"
    };

    public static IReadOnlyList<string> Plan(ResearchProfile profile, string? serviceArea, int maxQueries = 2)
    {
        profile = ResearchProfile.Validate(profile);
        if (maxQueries is < 1 or > 3)
            throw new InvalidOperationException("A search must contain 1–3 bounded queries.");

        var area = Clean(serviceArea ?? "", 65);
        // The two queries target different *buyer actions*, not an arbitrary pairing
        // of "supplier" and "supplier" from two independently editable keyword lists.
        var actions = ChooseActions(profile);
        var topics = profile.PriorityTerms
            .Select(t => Clean(t, 80))
            .Where(t => t.Length > 0 && !GenericTopics.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3).ToArray();
        var queries = new List<string>();
        for (int i = 0; i < maxQueries; i++)
        {
            var action = actions[i % actions.Length];
            var topic = topics.Length == 0 ? "" : topics[i % topics.Length];
            // Avoid repeating a topic already expressed by the process phrase.
            if (topic.Equals(action, StringComparison.OrdinalIgnoreCase) ||
                action.Contains(topic, StringComparison.OrdinalIgnoreCase))
                topic = "";
            var query = Clean(string.Join(" ", new[] { area, topic, "\"" + action + "\"" }
                .Where(x => !string.IsNullOrWhiteSpace(x))), 280);
            if (!queries.Contains(query, StringComparer.OrdinalIgnoreCase))
                queries.Add(query);
        }
        return queries;
    }

    private static string[] ChooseActions(ResearchProfile profile)
    {
        var terms = profile.ProcessTerms;
        bool Has(params string[] needles) => terms.Any(term =>
            needles.Any(needle => term.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        var isEmployment = Has("job openings", "career opportunities", "open positions",
            "job listing", "employment", "driver hiring");
        if (isEmployment)
            return ["job openings", "apply now", "career opportunities"];

        var actions = new List<string>();
        if (Has("trade partner", "subcontract", "prequalify"))
            actions.Add("subcontractor prequalification");
        if (Has("vendor", "supplier", "register", "procurement"))
            actions.Add("vendor registration");
        if (Has("bid", "rfp", "request for proposal", "procurement", "opportunities"))
            actions.Add("procurement opportunities");

        // General supplier research should still try two different buyer processes.
        if (actions.Count < 2) actions.Add("subcontractor opportunities");
        if (actions.Count < 3) actions.Add("supplier application");
        return actions.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
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
    private static readonly string[] BuyerProcessSignals =
    [
        "vendor registration", "supplier registration", "register as a vendor",
        "register to do business", "become a vendor", "vendor opportunities",
        "subcontractor prequalification", "trade partner registration",
        "procurement opportunities", "bid opportunities", "request for proposal",
        "supplier application", "supplier portal", "electronic vendor portal",
        "contract opportunities", "open solicitations"
    ];
    private static readonly string[] SellingGoodsSignals =
    [
        "wholesale", "wholesaler", "distributor", "wholesale supplier",
        "supply company", "pipe supply", "request pricing",
        "start a quote", "products & services", "product catalog"
    ];
    private static readonly string[] CommentarySignals =
    [
        "how to become", "how to register", "/blog/", "step-by-step guide",
        "guide to vendor", "everything you need to know"
    ];
    private static readonly string[] JobSignals =
    [
        "job openings", "apply for a job", "apply now", "career opportunities",
        "open positions", "employment opportunities", "delivery driver jobs"
    ];

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

            var normalized = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
            var title = (item.Title ?? "").Trim();
            if (title.Length > 180) title = title[..180];
            if (title.Length == 0) title = uri.Host;
            var summary = (item.Summary ?? "").Trim();
            if (summary.Length > 300) summary = summary[..300];
            var text = title + " " + summary + " " + uri.AbsolutePath;
            if (profile.ExcludedTerms.Any(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;

            var priority = profile.PriorityTerms.Count(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase));
            var process = profile.ProcessTerms.Count(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase));

            static bool Any(string haystack, string[] needles) =>
                needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
            bool buyer = Any(text, BuyerProcessSignals);
            bool seller = Any(text, SellingGoodsSignals);
            bool commentary = Any(text, CommentarySignals);
            bool job = Any(text, JobSignals);
            // These are explainable *hints*, not company ownership checks or
            // claims about availability. A supplier can also be a buyer.
            var intent = seller && !buyer ? "Selling goods / possible false match" :
                commentary ? "Third-party guide / verify official source" :
                buyer ? "Potential buyer registration / procurement" :
                job ? "Possible employment application" :
                "Unclear process — investigate";
            var score = Math.Min(100, 10 * Math.Min(priority, 4) +
                8 * Math.Min(process, 4) + (uri.Scheme == "https" ? 5 : 0) +
                (buyer ? 20 : 0));
            if (seller && !buyer) score = Math.Min(score, 15);
            if (commentary) score = Math.Min(score, 40);
            var reason = buyer ? "Contains buyer-side registration or procurement language. Link not verified." :
                seller && !buyer ? "Signals a company selling supplies, not a buyer onboarding vendors." :
                commentary ? "An informational guide may describe a process but is not itself an official form." :
                job ? "May refer to hiring rather than a business contracting opportunity." :
                "Relevant keywords found; process type and source still unclear.";
            var candidate = new OpportunityCandidate(title, normalized, uri.Host, query, score, summary)
            {
                IntentCategory = intent,
                RankingReason = reason
            };
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

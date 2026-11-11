namespace OutreachFlow.Services.Research;

/// <summary>
/// Literal keyword lists used only to prioritize bounded, read-only website research.
/// No user-provided regular expressions or automatic eligibility decisions.
/// </summary>
public sealed class ResearchProfile
{
    public string Name { get; set; } = "General business opportunities";
    public string Objective { get; set; } = "Locate relevant official instructions and business contacts.";
    public string[] PriorityTerms { get; set; } = [];
    public string[] ProcessTerms { get; set; } = [];
    public string[] ExcludedTerms { get; set; } = [];

    public static ResearchProfile Validate(ResearchProfile profile)
    {
        if (profile is null) throw new InvalidOperationException("A research profile is required.");
        var name = (profile.Name ?? "").Trim();
        var objective = (profile.Objective ?? "").Trim();
        if (name.Length is < 2 or > 100 || objective.Length is < 5 or > 500)
            throw new InvalidOperationException("Profile name must be 2–100 characters and objective 5–500 characters.");
        return new ResearchProfile
        {
            Name = name, Objective = objective,
            PriorityTerms = Terms(profile.PriorityTerms, "Priority"),
            ProcessTerms = Terms(profile.ProcessTerms, "Process"),
            ExcludedTerms = Terms(profile.ExcludedTerms, "Excluded", required: false)
        };
    }

    private static string[] Terms(string[]? source, string label, bool required = true)
    {
        if (source is null) throw new InvalidOperationException(label + " keywords are required.");
        if (source.Length > 30) throw new InvalidOperationException(label + " keywords are limited to 30.");
        var cleaned = new List<string>();
        foreach (var raw in source)
        {
            var term = (raw ?? "").Trim();
            if (term.Length == 0) continue;
            if (term.Length > 80 || term.Any(char.IsControl))
                throw new InvalidOperationException(label + " terms must be at most 80 characters, without control characters.");
            if (!cleaned.Contains(term, StringComparer.OrdinalIgnoreCase)) cleaned.Add(term);
        }
        if (required && cleaned.Count == 0)
            throw new InvalidOperationException(label + " keywords require at least one term.");
        return cleaned.ToArray();
    }
}

public static class ResearchProfileCatalog
{
    public static IReadOnlyList<(string Key, string Label)> Choices { get; } =
    [
        ("general", "General B2B supplier & partner research"),
        ("fleet", "Commercial fleet services"),
        ("construction", "General contracting & construction"),
        ("software", "Independent software development contracts"),
        ("junior-dev", "Junior developer employment research"),
        ("delivery", "Delivery driver employment research")
    ];

    public static ResearchProfile Default() => Preset("general");

    public static ResearchProfile Preset(string key) => key switch
    {
        "fleet" => Build("Commercial fleet services",
            "Find fleet-service vendor onboarding, recurring service and subcontractor opportunities.",
            ["fleet washing", "fleet maintenance", "mobile washing", "vehicle cleaning", "commercial fleet", "equipment wash", "fleet service"],
            ["vendor", "supplier", "procurement", "subcontractor", "register", "application", "bids", "become a vendor"],
            ["privacy policy", "investor relations"]),
        "construction" => Build("General contracting and construction",
            "Find construction bid invitations, general contractor supplier onboarding and executive partnership contacts.",
            ["general contractor", "construction", "projects", "prequalification", "trade partner", "subcontractor", "commercial property"],
            ["bid opportunities", "subcontractor registration", "prequalify", "supplier", "procurement", "vendor", "trade partners"],
            ["careers", "investor relations"]),
        "software" => Build("Independent software contracting",
            "Find paid .NET and software automation services RFPs, IT supplier programs and subcontracting contacts.",
            [".net", "software development", "web application", "C#", "automation", "technology services", "IT consulting", "RFP"],
            ["vendor", "procurement", "supplier", "request for proposal", "subcontract", "professional services", "partner registration"],
            ["internship", "student careers"]),
        "junior-dev" => Build("Junior developer opportunities",
            "Identify relevant junior developer positions on permitted public employer websites for manual applications.",
            ["junior developer", "entry level developer", "software engineer", "C#", ".net", "careers", "jobs", "graduate developer"],
            ["apply", "application", "job openings", "career opportunities", "open positions", "job listing"],
            ["senior developer", "staff engineer", "principal engineer", "unpaid internship"]),
        "delivery" => Build("Delivery driver opportunities",
            "Identify delivery and courier positions on permitted employer websites for manual applications.",
            ["delivery driver", "courier", "non-cdl", "route driver", "driver opportunities", "careers", "jobs"],
            ["apply", "application", "open positions", "employment", "driver hiring"],
            ["cdl required", "class a required", "long-haul"]),
        "general" => Build("General business opportunities",
            "Locate supplier applications, partnerships and publicly listed business contacts.",
            ["business opportunities", "supplier", "vendor", "contractor", "partners", "services"],
            ["vendor registration", "supplier", "procurement", "subcontractor", "bids", "register", "application", "become a vendor"],
            ["privacy policy", "investor relations"]),
        _ => throw new InvalidOperationException("Unknown research profile preset.")
    };

    private static ResearchProfile Build(string name, string objective, string[] priority,
        string[] process, string[] exclusions) => ResearchProfile.Validate(new ResearchProfile
        {
            Name = name, Objective = objective, PriorityTerms = priority,
            ProcessTerms = process, ExcludedTerms = exclusions
        });
}

public static class ResearchProfileRules
{
    public static bool ContainsProcessSignal(string text, ResearchProfile profile)
        => ContainsAny(text, profile.ProcessTerms);

    public static bool ContainsRelevantSignal(string text, ResearchProfile profile)
        => ContainsAny(text, profile.ProcessTerms) || ContainsAny(text, profile.PriorityTerms);

    public static int ScoreLink(string text, ResearchProfile profile)
    {
        if (ContainsAny(text, profile.ExcludedTerms)) return -10;
        int score = 0;
        if (ContainsAny(text, profile.ProcessTerms)) score += 6;
        if (ContainsAny(text, profile.PriorityTerms)) score += 4;
        if (ContainsAny(text, ["contact", "contact us", "get in touch"])) score += 2;
        return score;
    }

    public static int FirstMatchIndex(string text, ResearchProfile profile)
    {
        var all = profile.PriorityTerms.Concat(profile.ProcessTerms);
        int earliest = int.MaxValue;
        foreach (var term in all)
        {
            var found = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && found < earliest) earliest = found;
        }
        return earliest == int.MaxValue ? -1 : earliest;
    }

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(t => !string.IsNullOrWhiteSpace(t) &&
            text.Contains(t, StringComparison.OrdinalIgnoreCase));
}

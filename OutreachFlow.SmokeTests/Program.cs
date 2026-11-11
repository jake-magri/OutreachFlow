using Microsoft.EntityFrameworkCore;
using System.Net;
using OutreachFlow.Services.Research;
using OutreachFlow.Services.Discovery;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services;

static void Check(bool ok, string scenario)
{
    if (!ok) throw new Exception($"FAILED: {scenario}");
    Console.WriteLine($"PASS: {scenario}");
}

static async Task MustReject(Func<Task> action, string scenario)
{
    try { await action(); }
    catch (InvalidOperationException)
    {
        Console.WriteLine($"PASS: {scenario}");
        return;
    }
    throw new Exception($"FAILED: {scenario}");
}

// Explicit developer-only live probe: same downloader, user-agent, redirect/robots rules
// as the app. Logs only bounded public page metadata, not full HTML or script URLs.
if (Environment.GetEnvironmentVariable("OUTREACHFLOW_LIVE_CITY_PROBE") == "1")
{
    const string cityUrl =
        "https://www.charlottenc.gov/Growth-and-Development/Doing-Business/Supplier-Registration";
    try
    {
        using var liveResearcher = new OfficialSiteResearcher();
        var liveProbe = await liveResearcher.ProbeAsync(cityUrl);
        Console.WriteLine("LIVE CITY HTTP PROBE: " +
            System.Text.Json.JsonSerializer.Serialize(liveProbe));
    }
    catch (Exception ex)
    {
        Console.WriteLine("LIVE CITY HTTP PROBE INCONCLUSIVE: " +
            ex.GetType().Name + "; " + ex.Message[..Math.Min(ex.Message.Length, 180)]);
    }
}

// Deterministic search does not require an LLM or live provider calls.
var queriesForFleet = DeterministicQueryPlanner.Plan(ResearchProfileCatalog.Preset("fleet"), "Charlotte NC");
var queriesForJobs = DeterministicQueryPlanner.Plan(ResearchProfileCatalog.Preset("junior-dev"), "Charlotte NC");
Check(queriesForFleet.Count == 2 &&
      queriesForFleet.All(q => q.Contains("Charlotte NC", StringComparison.Ordinal)) &&
      queriesForJobs.Any(q => q.Contains("junior developer", StringComparison.OrdinalIgnoreCase)) &&
      !queriesForFleet.SequenceEqual(queriesForJobs),
    "workspace profiles produce distinct bounded search query plans");
var candidateRank = OpportunityRanker.Rank(new[]
{
    ("fleet washing vendor", new SearchListing("Fleet washing vendor registration",
        "https://city.example.org/Suppliers#top", "Register as a fleet washing vendor")),
    ("vendor registration", new SearchListing("Duplicate listing",
        "https://city.example.org/Suppliers#other", "Register as a vendor")),
    ("irrelevant", new SearchListing("Privacy policy",
        "https://city.example.org/privacy", "privacy policy")),
    ("malicious", new SearchListing("Internal network", "http://127.0.0.1/private", "supplier")),
    ("malicious", new SearchListing("Script destination", "javascript:alert(1)", "supplier"))
}, ResearchProfileCatalog.Preset("fleet"));
Check(candidateRank.Count == 1 &&
      candidateRank[0].Url == "https://city.example.org/Suppliers" &&
      candidateRank[0].RelevanceScore > 0 &&
      candidateRank[0].SourceTier.Contains("unverified", StringComparison.OrdinalIgnoreCase),
    "indexed candidates are deduplicated, validated, scored and labeled unverified");
var generalQueries = DeterministicQueryPlanner.Plan(ResearchProfileCatalog.Default(), "Charlotte North Carolina");
Check(generalQueries.Count == 2 &&
      generalQueries.All(q => !q.Contains("supplier supplier", StringComparison.OrdinalIgnoreCase)) &&
      generalQueries.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
      generalQueries.Any(q => q.Contains("vendor registration", StringComparison.OrdinalIgnoreCase)),
    "general query planner avoids duplicate supplier terms and selects different buyer processes");
var intentResults = OpportunityRanker.Rank(new[]
{
    ("procurement", new SearchListing("Registering in eVP | NC eProcurement",
        "https://eprocurement.nc.gov/training/vendor-training/registering-evp",
        "Electronic Vendor Portal allows vendors to register to do business with North Carolina")),
    ("wholesale", new SearchListing("Pipe Supply Charlotte - Wholesale Products",
        "https://americanstainlessandsupply.com/about/locations/pipe-supply-in-charlotte-nc",
        "Request pricing for our wholesale pipe supply products and services")),
    ("guide", new SearchListing("How to become a City of Charlotte Vendor",
        "https://supplierdiversity.com/blog/how-to-become-a-vendor-for-the-city-of-charlotte",
        "Guide to vendor registration and procurement"))
}, ResearchProfileCatalog.Default());
Check(intentResults.Count == 3 &&
      intentResults[0].Host == "eprocurement.nc.gov" &&
      intentResults[0].IntentCategory.Contains("buyer", StringComparison.OrdinalIgnoreCase) &&
      intentResults.Single(x => x.Host.Contains("americanstainlessandsupply", StringComparison.OrdinalIgnoreCase))
          .RelevanceScore <= 15 &&
      intentResults.Single(x => x.Host.Contains("supplierdiversity", StringComparison.OrdinalIgnoreCase))
          .IntentCategory.Contains("guide", StringComparison.OrdinalIgnoreCase),
    "official-indexed process outranks irrelevant wholesale products and separates third-party guides");
var sampleProcessLinks = ProcessLinkAnalyzer.Extract(
    new Uri("https://city.example.org/vendor-process"),
    "<html><body><a href='/vendors'>Vendor registration</a>" +
    "<a href='https://forms.example.org/onboard'>Apply to become a vendor</a>" +
    "<a href='http://127.0.0.1/private'>Supplier portal</a>" +
    "<a href='javascript:alert(1)'>Supplier</a>" +
    "<a href='https://city.example.org/privacy'>Privacy policy</a></body></html>",
    ResearchProfileCatalog.Default());
Check(sampleProcessLinks.Count == 2 &&
      sampleProcessLinks.Single(x => x.DestinationUrl.Contains("forms.example.org"))
          .Relationship == "Cross-domain link observed" &&
      sampleProcessLinks.All(x => x.VerificationState.Contains("Unverified", StringComparison.Ordinal)),
    "source-to-form relationship records provenance but never automatically verifies the third-party form");

var tavilySample = TavilySearchProvider.ParseResults(
    """{"results":[{"title":"City supplier registration","url":"https://city.example.org/suppliers","content":"Public index summary"},{"title":"No URL"}]}""");
Check(tavilySample.Count == 1 &&
      tavilySample[0].Title == "City supplier registration" &&
      tavilySample[0].Summary == "Public index summary",
    "Tavily adapter maps structured search response without LLM fields");
await MustReject(() => Task.Run(() => TavilySearchProvider.ParseResults("{}")),
    "Tavily adapter fails on unexpected response shape");
var usageDir = Path.Combine(Path.GetTempPath(), "outreachflow-search-budget-" + Guid.NewGuid().ToString("N"));
try
{
    var usage = new LocalSearchUsageBudget(usageDir, 3);
    var claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => usage.ReserveAsync()));
    Check(claims.Select(x => x.Used).Order().SequenceEqual(new[] { 1, 2, 3 }) &&
          (await new LocalSearchUsageBudget(usageDir, 3).GetAsync()).Remaining == 0,
        "budget reservations are concurrency-safe and durable across instances");
    await MustReject(async () => { await usage.ReserveAsync(); }, "exhausted budget rejects additional requests");
    await File.WriteAllTextAsync(Path.Combine(usageDir, "search-requests.json"), "{ invalid");
    await MustReject(async () => { await usage.GetAsync(); }, "corrupt usage ledger fails closed");
}
finally { if (Directory.Exists(usageDir)) Directory.Delete(usageDir, recursive: true); }

// Browser-render path is explicitly opt-in and must never follow unsafe destinations.
var browserRoot = new Uri("https://www.example.org/");
Check(BrowserResearchPolicy.AllowResource(browserRoot,
    "https://www.example.org/supplier-registration", "GET"),
    "browser policy permits same-site read-only document requests");
Check(!BrowserResearchPolicy.AllowResource(browserRoot,
    "http://127.0.0.1/secret", "GET") &&
    !BrowserResearchPolicy.AllowResource(browserRoot,
    "https://api.example.org/private", "GET") &&
    !BrowserResearchPolicy.AllowResource(browserRoot,
    "https://www.example.org/apply", "POST") &&
    !BrowserResearchPolicy.AllowResource(browserRoot,
    "file:///etc/passwd", "GET") &&
    !BrowserResearchPolicy.AllowResource(browserRoot,
    "https://user:pass@www.example.org/", "GET"),
    "browser policy blocks other hosts, credentials, local files and mutating methods");
Check(!BrowserResearchPolicy.AllowMediaType("application/octet-stream") &&
    BrowserResearchPolicy.AllowMediaType("text/html") &&
    BrowserResearchPolicy.AllowMediaType("application/javascript"),
    "browser policy limits proxy content to textual resources");
var renderableShell = new ResearchPageDiagnostic(browserRoot.AbsoluteUri,
    "Supplier registration", 40, Array.Empty<string>(),
    "The returned HTML has very little readable text (possibly a JavaScript-only shell).");
var blockedPage = ResearchPageInspector.Inspect(
    new Uri("https://www.example.org/supplier-registration"),
    "<html><head><title>Just a moment...</title></head>" +
    "<body>Please wait while we check your browser.</body></html>",
    ResearchProfileCatalog.Default());
var introBlocked = ResearchPageInspector.Inspect(
    new Uri("https://www.example.org/supplier-registration"),
    "<html><head><title>Supplier registration</title></head>" +
    "<body>Verify you are human to proceed.</body></html>",
    ResearchProfileCatalog.Default());
Check(BrowserResearchPolicy.MayRender(renderableShell) &&
    !BrowserResearchPolicy.MayRender(blockedPage) &&
    !BrowserResearchPolicy.MayRender(introBlocked),
    "browser experiment attempts JavaScript shells but never retries site access challenges");
Check(blockedPage.InitialRuleId == "challenge-title" &&
      blockedPage.MatchedPhrase == "just a moment" &&
      introBlocked.InitialRuleId == "challenge-intro" &&
      introBlocked.MatchedPhrase == "verify you are human",
    "challenge diagnostics specify whether title or body rule matched and its allowlisted phrase");
Check(blockedPage.HttpStatusCode == 200 &&
      !blockedPage.MatchedPhrase.Contains("<") &&
      !introBlocked.MatchedPhrase.Contains("<"),
    "challenge metadata uses explicit status and sanitized allowlisted rule matches");

// The opt-in browser integration test runs only when Chromium is installed and explicitly enabled.
if (Environment.GetEnvironmentVariable("OUTREACHFLOW_BROWSER_TESTS") == "1")
{
    var expectedContent = "Rendered supplier instructions ready for human review";
    var fixture = "<html><head><title>Portal</title></head><body>" +
        "<div id='app'>Loading</div><script>" +
        "setTimeout(() => { document.getElementById('app').textContent = '" +
        expectedContent + "'; }, 100);" +
        "</script></body></html>";
    var renderer = new PlaywrightHtmlBrowserRenderer();
    var fetched = 0;
    var html = await renderer.RenderAsync(browserRoot,
        new Uri("https://www.example.org/supplier-registration"),
        (resource, _) =>
        {
            fetched++;
            Check(resource.Host == "www.example.org",
                "browser resource requests cannot leave the approved host");
            return Task.FromResult(new BrowserResource(200, "text/html", fixture));
        });
    Check(html.Html.Contains(expectedContent, StringComparison.Ordinal) && fetched > 0 &&
          html.FulfilledRequests > 0 && html.BlockedRequests == 0,
        "Chromium executes JavaScript and returns rendered HTML through the safe fetch callback");
}

// New workspaces must never inherit another owner's personal or business contact details.
var blankWorkspace = new Workspace();
Check(blankWorkspace.BusinessName == "New workspace" &&
      string.IsNullOrWhiteSpace(blankWorkspace.SenderName) &&
      string.IsNullOrWhiteSpace(blankWorkspace.SenderEmail) &&
      string.IsNullOrWhiteSpace(blankWorkspace.SenderPhone) &&
      string.IsNullOrWhiteSpace(blankWorkspace.ServiceDescription) &&
      string.IsNullOrWhiteSpace(blankWorkspace.ServiceArea) &&
      string.IsNullOrWhiteSpace(blankWorkspace.TargetDescription),
    "new workspace defaults contain no personal or niche-specific business contact data");

// Workspace-specific research profiles must not leak between businesses or depend on SQLite migrations.
var profileDirectory = Path.Combine(Path.GetTempPath(), "outreachflow-profile-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var profileStore = new JsonResearchProfileStore(profileDirectory);
    Check((await profileStore.LoadAsync(1)).Name == ResearchProfileCatalog.Default().Name,
        "missing profile loads a safe general preset");
    var fleetProfile = ResearchProfileCatalog.Preset("fleet");
    var jobProfile = ResearchProfileCatalog.Preset("junior-dev");
    await profileStore.SaveAsync(1, fleetProfile);
    await profileStore.SaveAsync(2, jobProfile);
    var retained = new JsonResearchProfileStore(profileDirectory);
    Check((await retained.LoadAsync(1)).Name == fleetProfile.Name &&
          (await retained.LoadAsync(2)).Name == jobProfile.Name &&
          ResearchProfileRules.ScoreLink("fleet washing service", fleetProfile) >
              ResearchProfileRules.ScoreLink("fleet washing service", jobProfile),
        "two workspaces retain different research criteria across store instances");
    Check(ResearchProfileRules.ScoreLink("career openings for junior developer", jobProfile) > 0 &&
          ResearchProfileRules.ScoreLink("career openings for junior developer", fleetProfile) < 1,
        "jobs and commercial supplier research use different keyword policies");
    Check(ResearchProfileRules.ScoreLink("privacy policy", fleetProfile) < 0 &&
          ResearchProfileRules.ScoreLink("vendor registration", fleetProfile) > 0,
        "exclusions override otherwise relevant link text");
    await MustReject(() => profileStore.SaveAsync(1, new ResearchProfile
    {
        Name = "Invalid", Objective = "Invalid profile",
        PriorityTerms = ["valid", new string('x', 81)], ProcessTerms = ["apply"]
    }), "oversized research keyword rejected");
    await File.WriteAllTextAsync(Path.Combine(profileDirectory, "workspace-2.json"), "{broken");
    await MustReject(() => retained.LoadAsync(2), "corrupted profile fails closed rather than resetting silently");
    Check((await retained.LoadAsync(1)).Name == fleetProfile.Name,
        "corrupted profile for one business does not affect another business");
}
finally { if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, recursive: true); }

// A 200 OK status can return misleading HTML. Test quality checks without external HTTP.
var generalResearch = ResearchProfileCatalog.Default();
var suspectUri = new Uri("https://www.example.org/doing-business/supplier-registration");
var shell = ResearchPageInspector.Inspect(suspectUri,
    "<html><head><title>Supplier Registration</title></head><body><div id='app'></div><script>renderApp()</script></body></html>",
    generalResearch);
Check(shell.IsInconclusive && shell.VisibleCharacters < 120,
    "JavaScript-only HTML shell cannot be treated as a complete researched page");
var challenge = ResearchPageInspector.Inspect(suspectUri,
    "<html><title>Just a moment...</title><body>Checking your browser before accessing the site. Verify you are human.</body></html>",
    generalResearch);
Check(challenge.IsInconclusive && challenge.IsChallenge &&
      challenge.InitialRuleId == "challenge-title" &&
      challenge.MatchedPhrase == "just a moment",
    "HTTP 200 challenge page identifies its exact triggering title rule");
var realPage = ResearchPageInspector.Inspect(suspectUri,
    "<html><head><title>Supplier Registration</title></head><body><main><h1>Supplier registration</h1>" +
    "<p>Vendors interested in doing business should complete the supplier registration process.</p>" +
    "<p>Register your business using the official vendor portal and review requirements before submission.</p>" +
    "</main></body></html>", generalResearch);
Check(!realPage.IsInconclusive && realPage.MatchedTerms.Any(),
    "meaningful supplier registration HTML passes content quality assessment");
var mismatchedPage = ResearchPageInspector.Inspect(suspectUri,
    "<html><head><title>Welcome</title></head><body><main>" + new string('x', 200) +
    "</main></body></html>", generalResearch);
Check(mismatchedPage.IsInconclusive,
    "a supplier-registration URL with unrelated HTML requires manual review");

// NY daylight-saving time starts on Sunday, March 8, 2026.
var thursday = new DateTime(2026, 3, 6, 15, 0, 0, DateTimeKind.Utc);
var monday = BusinessCalendar.AddBusinessDays(thursday, 1, "America/New_York", 9);
Check(monday == new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc),
    "business-day preview skips weekend and converts across DST");

var temporaryPath = Path.Combine(Path.GetTempPath(), $"outreachflow-smoketest-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<OutreachDbContext>()
        .UseSqlite($"Data Source={temporaryPath}").Options;
    var factory = new TestFactory(options);
    await using (var db = factory.CreateDbContext())
    {
        await db.Database.EnsureCreatedAsync();
        db.Workspaces.Add(new Workspace());
        await db.SaveChangesAsync();
    }
    var flow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(1));
    var (added, skipped) = await flow.ImportAsync("Portal Co | South | https://portal.example | vendors@portal.example\n" +
        "Fleet Co | HQ | https://fleet.example | vendor@fleet.example\n" +
        "Fleet Co | HQ | https://fleet.example | vendor@fleet.example\n" +
        "NoReply Co | South | https://noreply.example | vendor@noreply.example\n" +
        "OptOut Co | North | https://opt.example | vendor@opt.example\n" +
        "=Formula Co | West | https://safe.example | vendor@safe.example");
    Check(added == 5 && skipped == 1, "batch import removes duplicate company/site");
    var prospects = await flow.ListProspectsAsync();
    int Id(string company) => prospects.Single(p => p.CompanyName == company).Id;

    // The research adapter is replaced with an offline fake. No network or mailbox access.
    Check(!ResearchUrlPolicy.IsPublicAddress(IPAddress.Loopback) &&
          !ResearchUrlPolicy.IsPublicAddress(IPAddress.Parse("169.254.169.254")) &&
          !ResearchUrlPolicy.IsPublicAddress(IPAddress.Parse("192.168.1.25")) &&
          ResearchUrlPolicy.IsPublicAddress(IPAddress.Parse("8.8.8.8")),
          "research network policy blocks local and metadata IPs");
    await MustReject(() =>
    {
        ResearchUrlPolicy.ParseWebsite("http://localhost/");
        return Task.CompletedTask;
    }, "research rejects localhost websites");
    var contacts = PublicContactExtractor.Extract(
        "<html><body>Contact procurement@public.example and <a href='mailto:vendors@public.example'>Email team</a>" +
        "<script>var secret='hidden@public.example';</script><style>.a{content:'style@public.example'}</style>" +
        "no-reply@public.example</body></html>", new Uri("https://public.example/contact"));
    Check(contacts.Select(c => c.Address).Order().SequenceEqual(
        new[] { "procurement@public.example", "vendors@public.example" }.Order()),
        "public HTML contact extraction includes mailto and visible addresses, excludes scripts and no-reply");
    // No network access: fake indexed results are never stored until reviewed/imported.
    var discoveryPath = Path.Combine(Path.GetTempPath(), "outreachflow-discovery-" + Guid.NewGuid().ToString("N"));
    try
    {
        var profileStoreForDiscovery = new JsonResearchProfileStore(Path.Combine(discoveryPath, "profiles"));
        await profileStoreForDiscovery.SaveAsync(1, ResearchProfileCatalog.Preset("fleet"));
        var fakeSearch = new FakeWebSearchProvider();
        var discovery = new OpportunityDiscoveryService(fakeSearch,
            new LocalSearchUsageBudget(Path.Combine(discoveryPath, "budget"), 5),
            profileStoreForDiscovery, new FixedWorkspaceAccess(1), factory, flow);
        var prior = (await flow.ListProspectsAsync()).Count;
        var discovered = await discovery.DiscoverAsync();
        Check(discovered.Candidates.Count == 1 && discovered.Queries.Count == 2 &&
              discovered.Budget.Used == 2 && fakeSearch.Calls == 2 &&
              (await flow.ListProspectsAsync()).Count == prior,
            "on-demand search consumes budget but doesn't create or message prospects");
        await MustReject(async () =>
            { await discovery.ImportReviewedAsync(777, discovered.Candidates[0]); },
            "discovery import refuses stale or switched workspace");
        var imported = await discovery.ImportReviewedAsync(1, discovered.Candidates[0]);
        var repeated = await discovery.ImportReviewedAsync(1, discovered.Candidates[0] with
        {
            Title = "Renamed search index listing"
        });
        Check(repeated.Added == 0 && repeated.Skipped == 1,
            "repeated index URL does not import duplicate records after title changes");
        var newProspect = (await flow.ListProspectsAsync()).Single(p =>
            p.Website == "https://city.example.org/supplier-registration");
        Check(imported.Added == 1 &&
              newProspect.ResearchOutcome == ResearchOutcome.NotReviewed &&
              newProspect.Status == ProspectStatus.ResearchNeeded &&
              newProspect.Attempts == 0,
            "owner-reviewed indexed URL imports with no verified process or outreach approval");

        var applicationCandidate = discovered.Candidates[0] with
        {
            Title = "Application review candidate",
            Url = "https://city.example.org/apply",
            Summary = "Search result mentions a possible registration process"
        };
        var appRoute = await discovery.RouteReviewedAsync(1, applicationCandidate,
            DiscoveryRoute.ApplicationReview);
        var appDetails = await flow.GetDetailsAsync(appRoute.ProspectId);
        Check(appRoute.Added && appDetails is not null &&
              appDetails.Prospect.Status == ProspectStatus.ApplicationTask &&
              appDetails.Prospect.ResearchOutcome == ResearchOutcome.NotReviewed &&
              appDetails.Prospect.ProcessUrl == "" &&
              appDetails.Prospect.ContactEmail == "" &&
              appDetails.Prospect.Attempts == 0 &&
              appDetails.Evidence.Count == 1 &&
              !appDetails.Evidence[0].IsOfficial &&
              appDetails.Evidence[0].SourceUrl == applicationCandidate.Url &&
              appDetails.Activity.Any(a => a.Kind == "Discovery triage"),
            "application review route persists unverified source and audit, but never marks a confirmed process");
        var duplicateRoute = await discovery.RouteReviewedAsync(1,
            applicationCandidate with { Title = "A different indexed title" },
            DiscoveryRoute.InquiryReview);
        Check(!duplicateRoute.Added && duplicateRoute.ProspectId == appRoute.ProspectId &&
              (await flow.GetDetailsAsync(appRoute.ProspectId))!.Prospect.Status ==
                  ProspectStatus.ApplicationTask,
            "repeat indexed URL does not silently change an existing prospect's review queue");
        var inquiryRoute = await discovery.RouteReviewedAsync(1,
            applicationCandidate with { Title = "Inquiry route candidate", Url = "https://city.example.org/inquiry" },
            DiscoveryRoute.InquiryReview);
        var inquiryDetails = await flow.GetDetailsAsync(inquiryRoute.ProspectId);
        Check(inquiryRoute.Added && inquiryDetails is not null &&
              inquiryDetails.Prospect.Status == ProspectStatus.NeedsAction &&
              inquiryDetails.Prospect.ResearchOutcome == ResearchOutcome.NotReviewed &&
              inquiryDetails.Prospect.DraftBody == "" &&
              inquiryDetails.Prospect.NextAction.Contains("recipient", StringComparison.OrdinalIgnoreCase),
            "inquiry route asks for recipient and process verification without enabling drafting");
        await MustReject(() => discovery.RouteReviewedAsync(777, applicationCandidate,
            DiscoveryRoute.FurtherResearch), "review routing rejects a stale workspace");
        await MustReject(() => discovery.RouteReviewedAsync(1,
            applicationCandidate with { Url = "http://127.0.0.1/admin" },
            DiscoveryRoute.FurtherResearch), "review routing revalidates unsafe indexed destinations");
        await MustReject(() => discovery.RouteReviewedAsync(1, applicationCandidate,
            (DiscoveryRoute)999), "review routing rejects unknown queue choices");

    }
    finally { if (Directory.Exists(discoveryPath)) Directory.Delete(discoveryPath, recursive: true); }
    var researchFlow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(1), new FakeResearcher());
    await researchFlow.ImportAsync("Website Research Co | South | https://www.example.org | vendor@example.org");
    var researchId = (await researchFlow.ListProspectsAsync()).Single(p => p.CompanyName == "Website Research Co").Id;
    var scan = await researchFlow.ResearchCompanyAsync(researchId);
    var researched = (await researchFlow.GetDetailsAsync(researchId))!;
    Check(scan.Candidates.Count == 2 && researched.Evidence.Count == 5 &&
          researched.Prospect.Status == ProspectStatus.NeedsAction &&
          researched.Evidence.All(e => !e.IsOfficial),
          "research sources persist as unverified candidates requiring human review");
    Check(researched.Evidence.Any(e =>
          e.SourceUrl == "https://forms.example.org/vendor-application" &&
          e.SourceTitle.StartsWith("Process link:", StringComparison.Ordinal) &&
          e.ExcerptOrObservation.Contains("linked from https://www.example.org/vendors", StringComparison.Ordinal) &&
          !e.IsOfficial),
        "external vendor application link retains source provenance as unverified evidence");
    await MustReject(() => researchFlow.GenerateDraftAsync(researchId),
        "unreviewed research cannot generate an inquiry draft");
    await researchFlow.ResearchCompanyAsync(researchId);
    Check((await researchFlow.GetDetailsAsync(researchId))!.Evidence.Count == 5,
        "repeated website scan does not duplicate the same evidence");
    var contactEvidence = (await researchFlow.GetDetailsAsync(researchId))!.Evidence
        .Single(e => e.SourceTitle == "Email candidate: procurement@example.org");
    await researchFlow.ApproveResearchContactAsync(researchId, contactEvidence.Id);
    Check((await researchFlow.GetDetailsAsync(researchId))!.Prospect.ContactEmail == "procurement@example.org",
        "owner approves an evidence-backed recipient without automatically approving a vendor process");
    var arbitraryEvidence = (await researchFlow.GetDetailsAsync(researchId))!.Evidence
        .First(e => e.SourceTitle == "Research scan: page checked");
    await MustReject(() => researchFlow.ApproveResearchContactAsync(researchId, arbitraryEvidence.Id),
        "non-email research evidence cannot be used to change the recipient");
    await researchFlow.RecordResearchAsync(researchId, ResearchOutcome.ApplicationPortal,
        "https://www.example.org/vendors", "Verified supplier instructions",
        "Owner reviewed the source and confirmed portal applicability.", true);
    Check((await researchFlow.GetDetailsAsync(researchId))!.Prospect.Status == ProspectStatus.ApplicationTask,
        "human-verified supplier portal creates an application task");

    await flow.RecordResearchAsync(Id("Portal Co"), ResearchOutcome.ApplicationPortal,
        "https://portal.example/vendors", "Vendor page", "Register here", true);
    Check((await flow.GetDetailsAsync(Id("Portal Co")))!.Prospect.Status == ProspectStatus.ApplicationTask,
        "official portal creates manual application task");
    await MustReject(() => flow.GenerateDraftAsync(Id("Portal Co")),
        "official portal blocks generic email draft");

    foreach (var company in new[] { "Fleet Co", "NoReply Co", "OptOut Co", "=Formula Co" })
    {
        await flow.RecordResearchAsync(Id(company), ResearchOutcome.NoProcessFound, "", "", "Checked official site and vendor page; no published process found.", false);
        await flow.GenerateDraftAsync(Id(company));
        await flow.SimulateInitialSendAsync(Id(company));
    }
    // Pilot real-world workflow: actual send/reply is owner-entered, never transmitted by the app.
    await flow.ImportAsync("Real Pilot Co | HQ | https://pilot.example | vendor@pilot.example");
    var realId = (await flow.ListProspectsAsync()).Single(p => p.CompanyName == "Real Pilot Co").Id;
    await flow.RecordResearchAsync(realId, ResearchOutcome.NoProcessFound,
        "", "", "Owner checked official vendor pages; no application route found.", false);
    await flow.GenerateDraftAsync(realId);
    await flow.RecordExternalInitialSendAsync(realId);
    var realLogged = (await flow.GetDetailsAsync(realId))!;
    Check(realLogged.Prospect.Status == ProspectStatus.AwaitingReply &&
          realLogged.Prospect.AutomationPaused && realLogged.Prospect.NextActionDate.HasValue &&
          realLogged.Messages.Count(m => !m.IsSimulation && m.Direction == "Outbound") == 1,
          "owner-confirmed external send records actual outreach and a manual follow-up date");
    await MustReject(() => flow.RecordExternalInitialSendAsync(realId),
        "cannot log the same initial send twice");
    await MustReject(() => flow.SimulateFollowupAsync(realId),
        "real-world manual workflow cannot trigger simulated followups");
    await flow.RecordExternalReplyAsync(realId, "We would be happy to discuss a call.", ReplyCategory.Interested);
    var realReply = (await flow.GetDetailsAsync(realId))!;
    Check(realReply.Prospect.Status == ProspectStatus.WarmLead &&
          realReply.Prospect.NextStepDueUtc is null &&
          realReply.Messages.Any(m => !m.IsSimulation && m.Direction == "Inbound"),
          "owner-entered actual interested reply creates warm lead and stops automation");
    await flow.ImportAsync("Real OptOut Co | HQ | https://optout.example | vendor@optout.example");
    var optId = (await flow.ListProspectsAsync()).Single(p => p.CompanyName == "Real OptOut Co").Id;
    await flow.RecordResearchAsync(optId, ResearchOutcome.NoProcessFound,
        "", "", "Owner checked official site and found no application route.", false);
    await flow.GenerateDraftAsync(optId);
    await flow.RecordExternalInitialSendAsync(optId);
    await flow.RecordExternalReplyAsync(optId, "Please do not email again.", ReplyCategory.Declined);
    Check((await flow.GetDetailsAsync(optId))!.Prospect.Status == ProspectStatus.Suppressed,
        "actual opt-out suppresses further outreach");
    await MustReject(() => flow.RecordExternalReplyAsync(optId, "Repeat", ReplyCategory.Unclear),
        "suppressed actual recipient cannot be reactivated from reply logging");

    var fleet = (await flow.GetDetailsAsync(Id("Fleet Co")))!.Prospect;
    Check(fleet.Attempts == 1 && fleet.NextStepDueUtc.HasValue && fleet.PlannedFollowups == 2,
        "dry-run initial email snapshots policy and schedules preview");

    var settings = await flow.LoadWorkspaceAsync();
    settings.MaxFollowups = 1;
    await flow.SaveWorkspaceAsync(settings);
    await flow.SimulateFollowupAsync(Id("Fleet Co"));
    await flow.SimulateFollowupAsync(Id("Fleet Co"));
    Check((await flow.GetDetailsAsync(Id("Fleet Co")))!.Prospect.Attempts == 3,
        "changing workspace settings does not modify active sequence's limit");
    await MustReject(() => flow.SimulateFollowupAsync(Id("Fleet Co")),
        "follow-up cap enforced");
    await flow.MarkNoResponseAsync(Id("Fleet Co"));
    Check((await flow.GetDetailsAsync(Id("Fleet Co")))!.Prospect.Status == ProspectStatus.ManualNoResponse,
        "unanswered sequence goes to separate manual queue");
    await flow.SimulateReplyAsync(Id("Fleet Co"), "Yes, please call me.", ReplyCategory.Interested);
    var late = (await flow.GetDetailsAsync(Id("Fleet Co")))!.Prospect;
    Check(late.Status == ProspectStatus.WarmLead && late.NextStepDueUtc is null && late.AutomationPaused,
        "late interested reply changes no-response to warm and stops sequence");

    await flow.RecordManualActivityAsync(Id("NoReply Co"), "Internal note", "Call back about vendor registration",
        "Check vendor contact", new DateOnly(2026, 11, 20));
    var taken = (await flow.GetDetailsAsync(Id("NoReply Co")))!.Prospect;
    Check(taken.AutomationPaused && taken.NextStepDueUtc is null && taken.NextActionDate.HasValue,
        "manual owner takeover pauses simulation and persists next action");
    await MustReject(() => flow.SimulateFollowupAsync(Id("NoReply Co")),
        "manual owner takeover blocks follow-up");

    await flow.SimulateReplyAsync(Id("OptOut Co"), "Please remove this address.", ReplyCategory.Declined);
    var status = (await flow.GetDetailsAsync(Id("OptOut Co")))!.Prospect.Status;
    Check(status == ProspectStatus.Suppressed, "opt-out suppresses contact");
    var imp = await flow.ImportAsync("Another Co | HQ | https://other.example | vendor@opt.example");
    Check(imp.added == 0 && imp.skipped == 1,
        "suppressed email cannot re-enter through another company");
    await MustReject(() => flow.RecordManualActivityAsync(Id("OptOut Co"), "Phone call", "Tried calling", "", null),
        "suppressed lead cannot be assigned manual outreach");

    var csv = await flow.ExportCsvAsync();
    Check(csv.Contains("\"'=Formula Co\""), "CSV export escapes spreadsheet formulas");
    Check(csv.Contains("WarmLead") && csv.Contains("Suppressed"), "CSV export includes queue states");
    // Explicit tenant isolation tests: the second business cannot read or modify tenant one.
    int otherWorkspaceId;
    await using (var db = factory.CreateDbContext())
    {
        var second = new Workspace { BusinessName = "Second Workspace" };
        db.Workspaces.Add(second);
        await db.SaveChangesAsync();
        otherWorkspaceId = second.Id;
    }
    var secondFlow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(otherWorkspaceId), new FakeResearcher());
    Check((await secondFlow.ListProspectsAsync()).Count == 0, "second workspace cannot list first workspace's prospects");
    Check(await secondFlow.GetDetailsAsync(Id("Fleet Co")) is null,
        "second workspace cannot read first workspace's prospect details");
    await MustReject(() => secondFlow.CloseAsync(Id("Fleet Co")),
        "second workspace cannot mutate first workspace's prospect");
    await secondFlow.ImportAsync("Tenant Two Co | HQ | https://tenant2.example | hello@tenant2.example");
    Check((await secondFlow.ListProspectsAsync()).Count == 1 &&
          (await flow.ListProspectsAsync()).All(p => p.CompanyName != "Tenant Two Co"),
          "imports stay within the selected workspace");
    Check(!(await secondFlow.ExportCsvAsync()).Contains("Fleet Co"),
          "exports exclude other workspace data");
    var secondSettings = await secondFlow.LoadWorkspaceAsync();
    Check(secondSettings.BusinessName == "Second Workspace",
          "workspace settings are scoped to the selected business");
    Console.WriteLine("All OutreachFlow offline smoke tests passed.");
}
finally
{
    try { File.Delete(temporaryPath); } catch { /* SQLite may have left temporary files */ }
}

sealed class TestFactory(DbContextOptions<OutreachDbContext> options) : IDbContextFactory<OutreachDbContext>
{
    public OutreachDbContext CreateDbContext() => new(options);
    public Task<OutreachDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

sealed class FakeResearcher : ICompanyResearcher
{
    public Task<ResearchScan> SearchAsync(string website, CancellationToken cancellationToken = default)
        => Task.FromResult(new ResearchScan(
            new[] {
                new ResearchCandidate("Link: Suppliers", "https://www.example.org/vendors", "Unverified supplier page"),
                new ResearchCandidate("Link: Procurement", "https://www.example.org/procurement", "Unverified procurement page")
            },
            new[] { "https://www.example.org/" },
            "Only public HTML pages were inspected.")
        {
            ProcessLinks = [new ProcessLinkEvidence("https://www.example.org/vendors",
                "https://forms.example.org/vendor-application", "Submit supplier application",
                "Cross-domain link observed", "Possible registration, procurement or application link")],
            Contacts = [new ResearchContact("procurement@example.org", "https://www.example.org/contact",
                "Publicly listed vendor email; verify ownership and relevance.")]
        });
}

sealed class FakeWebSearchProvider : IWebSearchProvider
{
    public string Name => "Offline fixture search";
    public bool IsConfigured => true;
    public int Calls { get; private set; }
    public Task<IReadOnlyList<SearchListing>> SearchAsync(string query, int maxResults,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<SearchListing>>([
            new SearchListing("Fleet washing vendor registration",
                "https://city.example.org/supplier-registration",
                "Official vendor supplier registration information")
        ]);
    }
}

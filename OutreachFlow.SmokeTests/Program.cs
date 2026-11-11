using Microsoft.EntityFrameworkCore;
using System.Net;
using OutreachFlow.Services.Research;
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
    var flow = new OutreachService(factory, new DryRunEmail());
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
    var researchFlow = new OutreachService(factory, new DryRunEmail(), new FakeResearcher());
    await researchFlow.ImportAsync("Website Research Co | South | https://www.example.org | vendor@example.org");
    var researchId = (await researchFlow.ListProspectsAsync()).Single(p => p.CompanyName == "Website Research Co").Id;
    var scan = await researchFlow.ResearchCompanyAsync(researchId);
    var researched = (await researchFlow.GetDetailsAsync(researchId))!;
    Check(scan.Candidates.Count == 2 && researched.Evidence.Count == 3 &&
          researched.Prospect.Status == ProspectStatus.NeedsAction &&
          researched.Evidence.All(e => !e.IsOfficial),
          "research sources persist as unverified candidates requiring human review");
    await MustReject(() => researchFlow.GenerateDraftAsync(researchId),
        "unreviewed research cannot generate an inquiry draft");
    await researchFlow.ResearchCompanyAsync(researchId);
    Check((await researchFlow.GetDetailsAsync(researchId))!.Evidence.Count == 3,
        "repeated website scan does not duplicate the same evidence");
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
            "Only public HTML pages were inspected."));
}

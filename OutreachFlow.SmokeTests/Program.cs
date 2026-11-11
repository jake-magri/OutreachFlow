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
    var researchFlow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(1), new FakeResearcher());
    await researchFlow.ImportAsync("Website Research Co | South | https://www.example.org | vendor@example.org");
    var researchId = (await researchFlow.ListProspectsAsync()).Single(p => p.CompanyName == "Website Research Co").Id;
    var scan = await researchFlow.ResearchCompanyAsync(researchId);
    var researched = (await researchFlow.GetDetailsAsync(researchId))!;
    Check(scan.Candidates.Count == 2 && researched.Evidence.Count == 4 &&
          researched.Prospect.Status == ProspectStatus.NeedsAction &&
          researched.Evidence.All(e => !e.IsOfficial),
          "research sources persist as unverified candidates requiring human review");
    await MustReject(() => researchFlow.GenerateDraftAsync(researchId),
        "unreviewed research cannot generate an inquiry draft");
    await researchFlow.ResearchCompanyAsync(researchId);
    Check((await researchFlow.GetDetailsAsync(researchId))!.Evidence.Count == 4,
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
            Contacts = [new ResearchContact("procurement@example.org", "https://www.example.org/contact",
                "Publicly listed vendor email; verify ownership and relevance.")]
        });
}

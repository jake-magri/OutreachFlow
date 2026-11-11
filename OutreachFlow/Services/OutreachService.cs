using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Text;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services.Research;

namespace OutreachFlow.Services;

public sealed record ProspectDetails(Prospect Prospect, List<ResearchEvidence> Evidence,
    List<OutreachMessage> Messages, List<ActivityEntry> Activity);

public sealed class OutreachService(IDbContextFactory<OutreachDbContext> factory, IOutboundEmail mail, IWorkspaceAccess access, ICompanyResearcher? researcher = null)
{
    public async Task<Workspace> LoadWorkspaceAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        return await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
    }

    public async Task SaveWorkspaceAsync(Workspace updated)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var s = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        s.BusinessName = updated.BusinessName.Trim();
        s.SenderName = updated.SenderName.Trim();
        s.SenderEmail = updated.SenderEmail.Trim();
        s.SenderPhone = updated.SenderPhone.Trim();
        s.PostalAddress = updated.PostalAddress.Trim();
        s.ServiceDescription = updated.ServiceDescription.Trim();
        s.ServiceArea = updated.ServiceArea.Trim();
        s.TargetDescription = updated.TargetDescription.Trim();
        s.MaxFollowups = Math.Clamp(updated.MaxFollowups, 1, 2);
        s.FirstFollowupBusinessDays = Math.Clamp(updated.FirstFollowupBusinessDays, 1, 30);
        s.SecondFollowupBusinessDays = Math.Clamp(updated.SecondFollowupBusinessDays, 1, 30);
        s.NoResponseAfterBusinessDays = Math.Clamp(updated.NoResponseAfterBusinessDays, 1, 30);
        s.DailySendLimit = Math.Clamp(updated.DailySendLimit, 1, 100);
        s.SendingStartHour = Math.Clamp(updated.SendingStartHour, 8, 17);
        s.SendingEndHour = Math.Clamp(updated.SendingEndHour, s.SendingStartHour + 1, 18);
        s.TimeZoneId = string.IsNullOrWhiteSpace(updated.TimeZoneId) ? "America/New_York" : updated.TimeZoneId.Trim();
        _ = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        s.InitialSubjectTemplate = updated.InitialSubjectTemplate.Trim();
        s.InitialBodyTemplate = updated.InitialBodyTemplate;
        s.FollowupOneTemplate = updated.FollowupOneTemplate;
        s.FollowupTwoTemplate = updated.FollowupTwoTemplate;
        await db.SaveChangesAsync();
    }

    public async Task<List<Prospect>> ListProspectsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        return await db.Prospects.AsNoTracking().Where(p => p.WorkspaceId == workspaceId)
            .OrderByDescending(p => p.UpdatedUtc).ToListAsync();
    }

    public async Task<ProspectDetails?> GetDetailsAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p is null) return null;
        return new ProspectDetails(p,
            await db.ResearchEvidence.AsNoTracking().Where(x => x.ProspectId == id).OrderByDescending(x => x.CheckedUtc).ToListAsync(),
            await db.Messages.AsNoTracking().Where(x => x.ProspectId == id).OrderByDescending(x => x.OccurredUtc).ToListAsync(),
            await db.Activities.AsNoTracking().Where(x => x.ProspectId == id).OrderByDescending(x => x.OccurredUtc).ToListAsync());
    }

    public async Task<(int added, int skipped)> ImportAsync(string lines)
    {
        await using var db = await factory.CreateDbContextAsync();
        int added = 0, skipped = 0;
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var seen = await db.Prospects.Where(p => p.WorkspaceId == workspaceId)
            .Select(p => new { p.CompanyName, p.SiteName }).ToListAsync();
        var keys = new HashSet<string>(seen.Select(p => Key(p.CompanyName, p.SiteName)), StringComparer.OrdinalIgnoreCase);
        // Never reimport a suppressed address under a newly named company/site.
        var blockedEmails = (await db.Prospects.Where(p => p.WorkspaceId == workspaceId && p.Status == ProspectStatus.Suppressed)
            .Select(p => p.ContactEmail).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(150))
        {
            var cells = line.TrimEnd('\r').Split('|');
            if (cells.Length < 2 || string.IsNullOrWhiteSpace(cells[0])) { skipped++; continue; }
            var name = cells[0].Trim();
            var site = cells.Length > 1 ? cells[1].Trim() : "";
            var k = Key(name, site);
            if (keys.Contains(k)) { skipped++; continue; }
            var website = cells.Length > 2 ? cells[2].Trim() : "";
            var email = cells.Length > 3 ? cells[3].Trim() : "";
            if (email.Length > 0 && blockedEmails.Contains(email)) { skipped++; continue; }
            keys.Add(k);
            // No automatic external calls; input is stored for manual review.
            db.Prospects.Add(new Prospect { WorkspaceId = workspaceId, CompanyName = name, SiteName = site, Website = website, ContactEmail = email });
            added++;
        }
        await db.SaveChangesAsync();
        return (added, skipped);
    }

    // Online research is read-only. Discovered links and page snippets never authorize outreach.
    public async Task<ResearchScan> ResearchCompanyAsync(int id, CancellationToken ct = default)
    {
        if (researcher is null)
            throw new InvalidOperationException("No website research provider configured.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId, ct);
        if (p.AutomationPaused || p.Attempts > 0 ||
            p.Status is ProspectStatus.Suppressed or ProspectStatus.Closed)
            throw new InvalidOperationException("Research is disabled for closed, suppressed, or owner-controlled conversations.");
        if (string.IsNullOrWhiteSpace(p.Website))
            throw new InvalidOperationException("Add a public company website to the prospect import before running research.");

        // Network work completes before any database state is changed.
        var scan = await researcher.SearchAsync(p.Website, ct);
        // Recheck after the network request in case another tab took ownership meanwhile.
        await db.Entry(p).ReloadAsync(ct);
        if (p.AutomationPaused || p.Attempts > 0 ||
            p.Status is ProspectStatus.Suppressed or ProspectStatus.Closed)
            throw new InvalidOperationException("Prospect ownership or status changed during research; results were not saved.");
        var checkedUtc = DateTime.UtcNow;
        var existing = await db.ResearchEvidence.Where(e => e.ProspectId == id)
            .Select(e => new { e.SourceUrl, e.SourceTitle, e.ExcerptOrObservation }).ToListAsync(ct);
        foreach (var candidate in scan.Candidates.Take(12))
        {
            if (existing.Any(e => string.Equals(e.SourceUrl, candidate.Url, StringComparison.OrdinalIgnoreCase)
                && e.ExcerptOrObservation == candidate.Observation)) continue;
            db.ResearchEvidence.Add(new ResearchEvidence
            {
                ProspectId = id,
                SourceTitle = candidate.Title[..Math.Min(candidate.Title.Length, 200)],
                SourceUrl = candidate.Url[..Math.Min(candidate.Url.Length, 2048)],
                ExcerptOrObservation = candidate.Observation[..Math.Min(candidate.Observation.Length, 1200)],
                CheckedUtc = checkedUtc,
                IsOfficial = false // Site identity and process applicability are NOT verified by a crawl.
            });
        }
        foreach (var checkedPage in scan.PagesChecked.Take(6))
        {
            if (existing.Any(e => e.SourceUrl == checkedPage && e.SourceTitle == "Research scan: page checked"))
                continue;
            db.ResearchEvidence.Add(new ResearchEvidence
            {
                ProspectId = id, SourceTitle = "Research scan: page checked",
                SourceUrl = checkedPage, ExcerptOrObservation = "Read-only scan; no official process confirmed.",
                IsOfficial = false, CheckedUtc = checkedUtc
            });
        }
        if (p.ResearchOutcome == ResearchOutcome.NotReviewed && p.Status == ProspectStatus.ResearchNeeded)
        {
            p.ResearchOutcome = ResearchOutcome.ReviewNeeded;
            p.Status = ProspectStatus.NeedsAction;
        }
        p.UpdatedUtc = checkedUtc;
        AddActivity(db, id, "Website scan", $"Checked {scan.PagesChecked.Count} HTML page(s), " +
            $"found {scan.Candidates.Count} candidate reference(s). Review required. {scan.Limitations}");
        await db.SaveChangesAsync(ct);
        return scan;
    }

    public async Task RecordResearchAsync(int id, ResearchOutcome outcome, string url, string title, string note, bool official)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status == ProspectStatus.Suppressed) throw new InvalidOperationException("Suppressed prospects cannot be reactivated by research.");
        if (p.Attempts > 0 || p.AutomationPaused)
            throw new InvalidOperationException("Research changes after outreach or human takeover require a separate audited review workflow.");
        if ((outcome == ResearchOutcome.ApplicationPortal || outcome == ResearchOutcome.EmailInstructions)
            && (!official || string.IsNullOrWhiteSpace(url)))
            throw new InvalidOperationException("Official application/email instructions require a verified source URL.");
        if (outcome == ResearchOutcome.NoProcessFound && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("Record which sources you checked before concluding no process was found.");
        if (!string.IsNullOrWhiteSpace(url) && !IsSafeHttpUrl(url)) throw new InvalidOperationException("Evidence URLs must start with https:// or http://.");
        p.ResearchOutcome = outcome;
        p.ProcessUrl = url.Trim();
        p.ResearchNotes = note.Trim();
        p.Status = outcome switch
        {
            ResearchOutcome.ApplicationPortal => ProspectStatus.ApplicationTask,
            ResearchOutcome.EmailInstructions or ResearchOutcome.NoProcessFound => ProspectStatus.ReadyToInquire,
            ResearchOutcome.NotEligible => ProspectStatus.Closed,
            ResearchOutcome.ReviewNeeded => ProspectStatus.NeedsAction,
            _ => ProspectStatus.ResearchNeeded
        };
        p.DraftBody = "";
        p.DraftSubject = "";
        p.UpdatedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(note))
            db.ResearchEvidence.Add(new ResearchEvidence { ProspectId = id, SourceTitle = title.Trim(), SourceUrl = url.Trim(), ExcerptOrObservation = note.Trim(), IsOfficial = official });
        AddActivity(db, id, "Research", $"Outcome: {outcome}. {(official ? "Official source indicated." : "Source not confirmed official.")}");
        await db.SaveChangesAsync();
    }

    public async Task GenerateDraftAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status != ProspectStatus.ReadyToInquire || p.Attempts != 0 || p.AutomationPaused)
            throw new InvalidOperationException("Drafts require an inquiry-eligible research result with no outreach or owner takeover.");
        var s = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        p.DraftSubject = Expand(s.InitialSubjectTemplate, s, p);
        p.DraftBody = Expand(s.InitialBodyTemplate, s, p);
        p.Status = ProspectStatus.DraftReady;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, "Draft", "Inquiry prepared for owner review; nothing sent.");
        await db.SaveChangesAsync();
    }

    // Records a human-confirmed send made in an external mailbox. This method NEVER sends email.
    public async Task RecordExternalInitialSendAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status != ProspectStatus.DraftReady || p.Attempts != 0 || p.AutomationPaused ||
            string.IsNullOrWhiteSpace(p.DraftBody))
            throw new InvalidOperationException("Prepare and review an eligible inquiry draft before logging an external send.");
        await GuardRecipientAsync(db, p);
        var settings = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        var now = DateTime.UtcNow;
        p.Attempts = 1;
        p.PlannedFollowups = settings.MaxFollowups;
        p.FirstFollowupDelayDays = settings.FirstFollowupBusinessDays;
        p.SecondFollowupDelayDays = settings.SecondFollowupBusinessDays;
        p.NoResponseDelayDays = settings.NoResponseAfterBusinessDays;
        p.SequenceTimeZoneId = settings.TimeZoneId;
        p.SequenceStartHour = settings.SendingStartHour;
        p.Status = ProspectStatus.AwaitingReply;
        p.AutomationPaused = true; // An external mailbox is not connected; automation must not send.
        p.NextStepDueUtc = null;
        p.NextActionDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            BusinessCalendar.AddBusinessDays(now, settings.FirstFollowupBusinessDays,
                settings.TimeZoneId, settings.SendingStartHour), TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId)));
        p.NextAction = "Review external mailbox and prepare first follow-up if no reply";
        p.UpdatedUtc = now;
        db.Messages.Add(new OutreachMessage
        {
            ProspectId = id, Direction = "Outbound", Classification = "Manually confirmed sent",
            Subject = p.DraftSubject, Body = p.DraftBody, IsSimulation = false, OccurredUtc = now
        });
        AddActivity(db, id, "MANUAL SEND CONFIRMED",
            "Owner confirmed sending the reviewed inquiry in an external mailbox. No email was sent by OutreachFlow.");
        await db.SaveChangesAsync();
    }

    // An actual reply pasted by the owner; never claims automatic mailbox synchronization.
    public async Task RecordExternalReplyAsync(int id, string body, ReplyCategory category)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 10000)
            throw new InvalidOperationException("Paste a received reply (1–10,000 characters).");
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Attempts == 0 || p.Status == ProspectStatus.Suppressed || p.Status == ProspectStatus.Closed)
            throw new InvalidOperationException("A previously contacted, open prospect is required.");
        if (!await db.Messages.AnyAsync(m => m.ProspectId == id && !m.IsSimulation && m.Direction == "Outbound"))
            throw new InvalidOperationException("Log a real outgoing message before recording a real reply.");
        db.Messages.Add(new OutreachMessage
        {
            ProspectId = id, Direction = "Inbound", Classification = category.ToString(),
            Body = body.Trim(), IsSimulation = false, OccurredUtc = DateTime.UtcNow
        });
        p.Status = category switch
        {
            ReplyCategory.Interested => ProspectStatus.WarmLead,
            ReplyCategory.Declined => ProspectStatus.Suppressed,
            ReplyCategory.ApplicationInstructions => ProspectStatus.ApplicationTask,
            _ => ProspectStatus.NeedsAction
        };
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.NextAction = category switch
        {
            ReplyCategory.Interested => "Personally follow up with interested contact",
            ReplyCategory.ApplicationInstructions => "Complete official supplier application",
            ReplyCategory.Declined => "",
            _ => "Review received reply and decide next step"
        };
        p.NextActionDate = null;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, "MANUAL REPLY RECORDED",
            $"Owner entered actual response and classified it as {category}. Review before further action.");
        await db.SaveChangesAsync();
    }

    public async Task SimulateInitialSendAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status != ProspectStatus.DraftReady || p.Attempts != 0 || p.AutomationPaused || string.IsNullOrWhiteSpace(p.DraftBody))
            throw new InvalidOperationException("A reviewed, unpaused draft is required.");
        await GuardRecipientAsync(db, p);
        var settings = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        var result = await mail.SendAsync(p.ContactEmail, p.DraftSubject, p.DraftBody);
        db.Messages.Add(new OutreachMessage { ProspectId = id, Subject = p.DraftSubject, Body = p.DraftBody });
        p.Attempts = 1;
        p.PlannedFollowups = settings.MaxFollowups;
        p.FirstFollowupDelayDays = settings.FirstFollowupBusinessDays;
        p.SecondFollowupDelayDays = settings.SecondFollowupBusinessDays;
        p.NoResponseDelayDays = settings.NoResponseAfterBusinessDays;
        p.SequenceTimeZoneId = settings.TimeZoneId;
        p.SequenceStartHour = settings.SendingStartHour;
        p.Status = ProspectStatus.AwaitingReply;
        p.UpdatedUtc = DateTime.UtcNow;
        p.NextStepDueUtc = BusinessCalendar.AddBusinessDays(p.UpdatedUtc, p.FirstFollowupDelayDays,
            p.SequenceTimeZoneId, p.SequenceStartHour);
        AddActivity(db, id, "SIMULATED OUTBOUND", result.Detail);
        await db.SaveChangesAsync();
    }

    public async Task SimulateFollowupAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        var s = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        if (p.Status != ProspectStatus.AwaitingReply || p.AutomationPaused || p.Attempts < 1 || p.Attempts >= 1 + p.PlannedFollowups)
            throw new InvalidOperationException("No further follow-up is allowed in this sequence.");
        await GuardRecipientAsync(db, p);
        var template = p.Attempts == 1 ? s.FollowupOneTemplate : s.FollowupTwoTemplate;
        var body = Expand(template, s, p);
        var result = await mail.SendAsync(p.ContactEmail, p.DraftSubject, body);
        db.Messages.Add(new OutreachMessage { ProspectId = id, Subject = p.DraftSubject, Body = body });
        p.Attempts++;
        p.UpdatedUtc = DateTime.UtcNow;
        var waitDays = p.Attempts <= p.PlannedFollowups ? p.SecondFollowupDelayDays : p.NoResponseDelayDays;
        p.NextStepDueUtc = BusinessCalendar.AddBusinessDays(p.UpdatedUtc, waitDays,
            p.SequenceTimeZoneId, p.SequenceStartHour);
        AddActivity(db, id, "SIMULATED FOLLOW-UP", result.Detail);
        await db.SaveChangesAsync();
    }

    public async Task MarkNoResponseAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status != ProspectStatus.AwaitingReply || p.AutomationPaused || p.PlannedFollowups < 1 || p.Attempts < 1 + p.PlannedFollowups)
            throw new InvalidOperationException("Complete the snapshotted simulated sequence first.");
        p.Status = ProspectStatus.ManualNoResponse;
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, "Handoff", "No reply after full dry-run sequence. Manual queue, NOT a warm lead.");
        await db.SaveChangesAsync();
    }

    public async Task SimulateReplyAsync(int id, string body, ReplyCategory category)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Enter a test reply.");
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Attempts == 0) throw new InvalidOperationException("Simulate an initial message before a reply.");
        if (p.Status == ProspectStatus.Suppressed) throw new InvalidOperationException("This recipient is suppressed.");
        db.Messages.Add(new OutreachMessage { ProspectId = id, Direction = "Inbound", Classification = category.ToString(), Body = body.Trim() });
        p.Status = category switch
        {
            ReplyCategory.Interested => ProspectStatus.WarmLead,
            ReplyCategory.Declined => ProspectStatus.Suppressed,
            ReplyCategory.ApplicationInstructions => ProspectStatus.ApplicationTask,
            _ => ProspectStatus.NeedsAction
        };
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, "TEST REPLY", $"Classified {category}; sequence stopped. Human review required.");
        await db.SaveChangesAsync();
    }

    public async Task RecordManualActivityAsync(int id, string activityType, string note, string nextAction, DateOnly? nextDate)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("Enter a note describing the manual action.");
        if (note.Length > 3000) throw new InvalidOperationException("Keep the note under 3,000 characters.");
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status == ProspectStatus.Suppressed &&
            (activityType != "Internal note" || !string.IsNullOrWhiteSpace(nextAction) || nextDate.HasValue))
            throw new InvalidOperationException("Suppressed prospects accept only internal notes, not outreach or next-contact tasks.");
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.NextAction = nextAction.Trim();
        p.NextActionDate = nextDate;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, $"Manual: {activityType}", note.Trim());
        await db.SaveChangesAsync();
    }

    public async Task CloseAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var workspaceId = await access.ActiveWorkspaceIdAsync();
        var p = await db.Prospects.SingleAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (p.Status == ProspectStatus.Suppressed)
            throw new InvalidOperationException("A suppression must remain recorded as suppressed.");
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.Status = ProspectStatus.Closed;
        p.NextAction = "";
        p.NextActionDate = null;
        p.UpdatedUtc = DateTime.UtcNow;
        AddActivity(db, id, "Closed", "Manually closed. Automatic outreach remains disabled.");
        await db.SaveChangesAsync();
    }

    public async Task<string> ExportCsvAsync()
    {
        var prospects = await ListProspectsAsync();
        var output = new StringBuilder("Company,Site,Website,Email,Research,Status,Attempts,NextDueUtc,NextAction,NextActionDate,OwnerPaused\r\n");
        foreach (var p in prospects)
        {
            var cells = new[] { p.CompanyName, p.SiteName, p.Website, p.ContactEmail,
                p.ResearchOutcome.ToString(), p.Status.ToString(), p.Attempts.ToString(),
                p.NextStepDueUtc?.ToString("O") ?? "", p.NextAction,
                p.NextActionDate?.ToString("yyyy-MM-dd") ?? "", p.AutomationPaused.ToString() };
            output.AppendLine(string.Join(",", cells.Select(EncodeCsvCell)));
        }
        return output.ToString();
    }

    private static string EncodeCsvCell(string input)
    {
        // Protect spreadsheet consumers from CSV formula injection without changing stored values.
        var trimmed = input.TrimStart(' ', '\t', '\r', '\n');
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) input = "'" + input;
        return "\"" + input.Replace("\"", "\"\"") + "\"";
    }

    private static async Task GuardRecipientAsync(OutreachDbContext db, Prospect p)
    {
        if (!MailAddress.TryCreate(p.ContactEmail, out var recipient) || recipient.Address != p.ContactEmail)
            throw new InvalidOperationException("A valid business recipient email is needed even for a simulation.");
        var others = await db.Prospects.AsNoTracking()
            .Where(other => other.Id != p.Id && other.WorkspaceId == p.WorkspaceId &&
                other.ContactEmail.ToLower() == p.ContactEmail.ToLower())
            .Select(other => new { other.Status, other.Attempts }).ToListAsync();
        if (others.Any(o => o.Status == ProspectStatus.Suppressed))
            throw new InvalidOperationException("This recipient is suppressed on another prospect record.");
        if (others.Any(o => o.Attempts > 0))
            throw new InvalidOperationException("This recipient already has outreach history on another prospect. Review manually.");
    }

    private static string Expand(string template, Workspace s, Prospect p)
        => template.Replace("[business]", s.BusinessName, StringComparison.OrdinalIgnoreCase)
            .Replace("[service]", s.ServiceDescription, StringComparison.OrdinalIgnoreCase)
            .Replace("[area]", s.ServiceArea, StringComparison.OrdinalIgnoreCase)
            .Replace("[company]", p.CompanyName, StringComparison.OrdinalIgnoreCase)
            .Replace("[sender]", s.SenderName, StringComparison.OrdinalIgnoreCase)
            .Replace("[team]", "team", StringComparison.OrdinalIgnoreCase);

    private static string Key(string name, string site) => $"{name.Trim()}|{site.Trim()}";
    private static bool IsSafeHttpUrl(string u) => Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static void AddActivity(OutreachDbContext db, int id, string kind, string details)
        => db.Activities.Add(new ActivityEntry { ProspectId = id, Kind = kind, Detail = details });
}

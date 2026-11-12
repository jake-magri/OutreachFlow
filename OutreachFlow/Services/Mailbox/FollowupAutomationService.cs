using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services;

namespace OutreachFlow.Services.Mailbox;

public interface IAutomationInbox
{
    Task<IReadOnlyList<GmailInboxItem>> ScanAsync(int workspaceId,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct);
    Task<bool> ImportUnclassifiedAsync(int workspaceId, int prospectId, string messageId,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct);
}

public sealed class GmailAutomationInbox(GmailMailboxService gmail) : IAutomationInbox
{
    public Task<IReadOnlyList<GmailInboxItem>> ScanAsync(int workspaceId,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct) =>
        gmail.ScanAutomationAsync(workspaceId, factory, ct);

    public Task<bool> ImportUnclassifiedAsync(int workspaceId, int prospectId, string messageId,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct) =>
        gmail.ImportReviewedInboundAsync(workspaceId, prospectId, messageId,
            ReplyCategory.Unclear, factory, ct, detectedAutomatically: true);
}

public sealed record FollowupPreview(string From, string To, string Subject,
    string FirstBody, string SecondBody, DateTime FirstDueUtc,
    int FollowupCount, string Hash);

/// <summary>
/// Local, owner-approved and fail-closed scheduled Gmail follow-ups.
/// Sends are claimed durably BEFORE calling Gmail; once attempted, no retry
/// happens without human reconciliation. Unreadable inbox -> held, not sent.
/// </summary>
public sealed class FollowupAutomationService(
    IDbContextFactory<OutreachDbContext> factory,
    IDbContextFactory<AccountDbContext> accounts,
    IGmailSender gmail, IAutomationInbox inbox,
    IConfiguration config, TimeProvider clock)
{
    public bool Enabled => config.GetValue<bool>("Followups:Enabled");

    private async Task<int> OwnerAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await using var auth = await accounts.CreateDbContextAsync(ct);
        return await MailboxOwner.RequireAsync(user, auth, ct);
    }

    private static string Expand(string template, Workspace w, Prospect p) =>
        template
            .Replace("[team]", "team", StringComparison.OrdinalIgnoreCase)
            .Replace("[business]", w.BusinessName, StringComparison.OrdinalIgnoreCase)
            .Replace("[service]", w.ServiceDescription, StringComparison.OrdinalIgnoreCase)
            .Replace("[company]", p.CompanyName, StringComparison.OrdinalIgnoreCase)
            .Replace("[area]", w.ServiceArea, StringComparison.OrdinalIgnoreCase)
            .Replace("[sender]", w.SenderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsEligible(Prospect p) =>
        p.Status == ProspectStatus.AwaitingReply && p.Attempts == 1 &&
        p.ResearchOutcome is ResearchOutcome.NoProcessFound or ResearchOutcome.EmailInstructions;

    private static async Task<bool> NoInboundAsync(OutreachDbContext db, int prospectId, CancellationToken ct) =>
        !await db.Messages.AnyAsync(m => m.ProspectId == prospectId &&
            m.Direction == "Inbound" && !m.IsSimulation, ct);

    private static async Task<FollowupPreview> MakePreviewAsync(
        OutreachDbContext db, int workspace, int prospectId, DateTime now, CancellationToken ct)
    {
        var p = await db.Prospects.SingleAsync(p => p.Id == prospectId && p.WorkspaceId == workspace, ct);
        var w = await db.Workspaces.SingleAsync(w => w.Id == workspace, ct);
        var outgoing = await db.OutboundDispatches.AsNoTracking().SingleOrDefaultAsync(d =>
            d.ProspectId == prospectId && d.WorkspaceId == workspace && d.State == "Sent", ct);
        var anchor = await (from m in db.ProviderMessages.AsNoTracking()
            join history in db.Messages.AsNoTracking() on m.OutreachMessageId equals history.Id
            where m.WorkspaceId == workspace && m.ProspectId == prospectId &&
                history.Direction == "Outbound" && !history.IsSimulation
            select m).FirstOrDefaultAsync(ct);
        if (!IsEligible(p) || !p.AutomationPaused || outgoing is null || anchor is null ||
            anchor.ThreadId.Length == 0 || !await NoInboundAsync(db, prospectId, ct) ||
            await db.FollowupJobs.AnyAsync(j => j.WorkspaceId == workspace && j.ProspectId == prospectId, ct))
            throw new InvalidOperationException("Only an unanswered, confirmed Gmail inquiry without a previous follow-up sequence can be scheduled.");
        if (!w.SenderEmail.Equals(outgoing.From, StringComparison.OrdinalIgnoreCase) ||
            !p.ContactEmail.Equals(outgoing.To, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sender or recipient changed; follow-up approval blocked.");
        var count = Math.Clamp(w.MaxFollowups, 1, 2);
        var body1 = Expand(w.FollowupOneTemplate, w, p);
        var body2 = count == 2 ? Expand(w.FollowupTwoTemplate, w, p) : "";
        if (body1.Length is < 1 or > 10000 || (count == 2 && body2.Length is < 1 or > 10000))
            throw new InvalidOperationException("Configure valid follow-up templates in Settings.");
        var due = BusinessCalendar.AddBusinessDays(now, Math.Clamp(w.FirstFollowupBusinessDays, 1, 30),
            w.TimeZoneId, w.SendingStartHour);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { workspace, prospectId, w.SenderEmail, p.ContactEmail,
                outgoing.RfcMessageId, anchor.ThreadId, count, body1, body2,
                w.FirstFollowupBusinessDays, w.SecondFollowupBusinessDays,
                w.NoResponseAfterBusinessDays, w.TimeZoneId, w.SendingStartHour, w.SendingEndHour,
                w.DailySendLimit, p.UpdatedUtc, FirstDueUtc = due }))));
        return new(w.SenderEmail, p.ContactEmail, outgoing.Subject, body1, body2, due, count, hash);
    }

    public async Task<FollowupPreview> PreviewAsync(ClaimsPrincipal user, int prospectId,
        CancellationToken ct = default)
    {
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await MakePreviewAsync(db, workspace, prospectId, clock.GetUtcNow().UtcDateTime, ct);
    }

    public async Task ApproveAsync(ClaimsPrincipal user, int prospectId, string previewHash,
        CancellationToken ct = default)
    {
        if (!Enabled || !gmail.SendingAvailable)
            throw new InvalidOperationException("Scheduled Gmail is disabled in local configuration.");
        var workspace = await OwnerAsync(user, ct);
        var preview = await PreviewAsync(user, prospectId, ct);
        if (preview.Hash != previewHash)
            throw new InvalidOperationException("Follow-up settings or prospect changed. Review the sequence again.");
        _ = await gmail.PrepareAsync(workspace, preview.From, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        using var reservation = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var tx = await db.Database.UseTransactionAsync(reservation, ct);
        if (workspace != await OwnerAsync(user, ct))
            throw new UnauthorizedAccessException("Workspace changed. Approval cancelled.");
        var current = await MakePreviewAsync(db, workspace, prospectId, clock.GetUtcNow().UtcDateTime, ct);
        if (current.Hash != previewHash)
            throw new InvalidOperationException("Sequence changed during approval. Review again.");
        var p = await db.Prospects.SingleAsync(p => p.Id == prospectId, ct);
        var w = await db.Workspaces.SingleAsync(w => w.Id == workspace, ct);
        var original = await db.OutboundDispatches.SingleAsync(d => d.ProspectId == prospectId, ct);
        var thread = await db.ProviderMessages.Where(m => m.ProspectId == prospectId &&
            m.MessageId != "").OrderBy(m => m.Id).Select(m => m.ThreadId).FirstAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        FollowupJob Job(int step, string kind, string body, DateTime due, string refId) => new()
        {
            WorkspaceId = workspace, ProspectId = p.Id, Step = step, Kind = kind,
            DueUtc = due, From = preview.From, To = preview.To, Subject = preview.Subject,
            Body = body, ThreadId = thread, ReplyToMessageId = refId,
            RfcMessageId = "<" + Guid.NewGuid().ToString("N") + "@outreachflow.local>",
            ApprovedByUserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            ApprovedUtc = now, Result = "Owner approved exact follow-up snapshot; not yet dispatched."
        };
        var first = Job(1, "Email", current.FirstBody, current.FirstDueUtc, original.RfcMessageId);
        db.FollowupJobs.Add(first);
        if (current.FollowupCount == 2)
        {
            var second = Job(2, "Email", current.SecondBody, DateTime.MaxValue, first.RfcMessageId);
            db.FollowupJobs.Add(second);
        }
        db.FollowupJobs.Add(Job(3, "ManualHandoff", "", DateTime.MaxValue,
            current.FollowupCount == 2
                ? db.FollowupJobs.Local.Single(j => j.Step == 2 && j.ProspectId == prospectId).RfcMessageId
                : first.RfcMessageId));
        p.PlannedFollowups = current.FollowupCount;
        p.FirstFollowupDelayDays = Math.Clamp(w.FirstFollowupBusinessDays, 1, 30);
        p.SecondFollowupDelayDays = Math.Clamp(w.SecondFollowupBusinessDays, 1, 30);
        p.NoResponseDelayDays = Math.Clamp(w.NoResponseAfterBusinessDays, 1, 30);
        p.SequenceTimeZoneId = w.TimeZoneId;
        p.SequenceStartHour = w.SendingStartHour;
        p.AutomationPaused = false;
        p.NextStepDueUtc = first.DueUtc;
        p.NextAction = "Follow-up sequence approved; automated inbox checks precede each send";
        db.Activities.Add(new() { ProspectId = p.Id, Kind = "Gmail follow-ups approved",
            Detail = $"Owner approved {current.FollowupCount} exact message(s) and final manual handoff." });
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FollowupJob>> ListAsync(ClaimsPrincipal user, int prospectId,
        CancellationToken ct = default)
    {
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.FollowupJobs.AsNoTracking()
            .Where(j => j.WorkspaceId == workspace && j.ProspectId == prospectId)
            .OrderBy(j => j.Step).ToListAsync(ct);
    }

    /// <summary>
    /// Read-only provider reconciliation. An absent Sent match never authorizes a
    /// retry. A confirmed match completes the recorded send without resending.
    /// Future steps stay held for owner review after an uncertain result.
    /// </summary>
    public async Task ReconcileAsync(ClaimsPrincipal user, int prospectId, int jobId,
        CancellationToken ct = default)
    {
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.FollowupJobs.AsNoTracking().SingleAsync(j =>
            j.Id == jobId && j.ProspectId == prospectId && j.WorkspaceId == workspace, ct);
        if (job.State != "Uncertain" || job.Kind != "Email")
            throw new InvalidOperationException("Only an uncertain Gmail follow-up can be reconciled.");
        var snapshot = new OutboundDispatch {
            From = job.From, To = job.To, Subject = job.Subject, Body = job.Body,
            RfcMessageId = job.RfcMessageId, ApprovedUtc = job.ApprovedUtc
        };
        var found = await gmail.FindSentAsync(workspace, snapshot, ct);
        if (found is null)
            throw new InvalidOperationException("No verified Sent match; leave the step blocked and inspect Gmail.");
        if (!found.ThreadId.Equals(job.ThreadId, StringComparison.Ordinal))
            throw new InvalidOperationException("Sent match has a different thread; manual review is required.");
        await CompleteAsync(jobId, found, ct);
    }

    public async Task ClassifyDetectedReplyAsync(ClaimsPrincipal user, int prospectId,
        int messageId, ReplyCategory category, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(category)) throw new InvalidOperationException("Unknown reply category.");
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        using var reservation = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var tx = await db.Database.UseTransactionAsync(reservation, ct);
        var p = await db.Prospects.SingleAsync(p => p.Id == prospectId && p.WorkspaceId == workspace, ct);
        if (p.Status != ProspectStatus.NeedsAction)
            throw new InvalidOperationException("Only an open, unclassified detected reply can be classified.");
        var message = await db.Messages.SingleAsync(m => m.Id == messageId &&
            m.ProspectId == prospectId && m.Direction == "Inbound" && !m.IsSimulation &&
            m.Classification == "Gmail auto-detected: Unclassified", ct);
        message.Classification = "Gmail reviewed: " + category;
        p.Status = category switch
        {
            ReplyCategory.Interested => ProspectStatus.WarmLead,
            ReplyCategory.ApplicationInstructions => ProspectStatus.ApplicationTask,
            ReplyCategory.Declined => ProspectStatus.Suppressed,
            _ => ProspectStatus.NeedsAction
        };
        p.AutomationPaused = true;
        p.NextStepDueUtc = null;
        p.NextAction = category switch
        {
            ReplyCategory.Interested => "Personally follow up with interested contact",
            ReplyCategory.ApplicationInstructions => "Complete official supplier application",
            ReplyCategory.Declined => "",
            _ => "Manually review the reply and decide next step"
        };
        foreach (var queued in await db.FollowupJobs.Where(j => j.ProspectId == prospectId &&
            j.WorkspaceId == workspace && j.State == "Queued").ToListAsync(ct))
        {
            queued.State = "Cancelled";
            queued.Result = "Owner classified incoming Gmail reply";
        }
        db.Activities.Add(new() { ProspectId = p.Id, Kind = "Detected Gmail reply classified",
            Detail = "Owner classified existing inbound message #" + messageId + " as " + category });
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
    }

    public async Task ControlAsync(ClaimsPrincipal user, int prospectId, string action, CancellationToken ct = default)
    {
        if (action is not ("pause" or "resume" or "cancel"))
            throw new InvalidOperationException("Unknown schedule action.");
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        using var reservation = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var tx = await db.Database.UseTransactionAsync(reservation, ct);
        var p = await db.Prospects.SingleAsync(p => p.Id == prospectId && p.WorkspaceId == workspace, ct);
        var jobs = await db.FollowupJobs.Where(j => j.WorkspaceId == workspace &&
            j.ProspectId == prospectId).ToListAsync(ct);
        if (jobs.Count == 0) throw new InvalidOperationException("No follow-up sequence exists.");
        if (jobs.Any(j => j.State is "Sending" or "Uncertain"))
            throw new InvalidOperationException("An attempted send needs Gmail reconciliation before any schedule change.");
        if (action == "resume")
        {
            if (!Enabled || !gmail.SendingAvailable || p.Status != ProspectStatus.AwaitingReply ||
                !await NoInboundAsync(db, prospectId, ct) ||
                jobs.Any(j => j.State == "Cancelled") ||
                !jobs.Any(j => j.State == "Held" && j.AttemptedUtc is null))
                throw new InvalidOperationException("This sequence is not eligible to resume.");
            foreach (var j in jobs.Where(j => j.State == "Held" && j.AttemptedUtc is null))
            { j.State = "Queued"; j.Result = "Owner resumed pending step; rechecks required before sending."; }
            p.AutomationPaused = false;
            p.NextStepDueUtc = jobs.Where(j => j.State == "Queued").Min(j => (DateTime?)j.DueUtc);
        }
        else
        {
            foreach (var j in jobs.Where(j => j.State is "Queued" or "Held"))
            {
                j.State = action == "cancel" ? "Cancelled" : "Held";
                j.Result = action == "cancel" ? "Owner cancelled" : "Owner paused";
            }
            p.AutomationPaused = true;
            p.NextStepDueUtc = null;
        }
        db.Activities.Add(new() { ProspectId = p.Id, Kind = "Follow-up " + action,
            Detail = "Owner controlled the durable pending sequence." });
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Enabled || !gmail.SendingAvailable) return;
        var now = clock.GetUtcNow().UtcDateTime;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            // A process killed mid-flight cannot prove whether Gmail accepted mail.
            var stale = await db.FollowupJobs
                .Where(j => j.State == "Sending").ToListAsync(ct);
            foreach (var j in stale)
            {
                j.State = "Uncertain";
                j.Result = "Process interrupted after Gmail send reservation. Reconcile manually; no retry.";
                var p = await db.Prospects.SingleAsync(p => p.Id == j.ProspectId, ct);
                p.AutomationPaused = true;
                p.NextStepDueUtc = null;
            }
            await db.SaveChangesAsync(ct);
        }
        List<int> workspaces;
        await using (var db = await factory.CreateDbContextAsync(ct))
            workspaces = await db.FollowupJobs.AsNoTracking()
                .Where(j => j.State == "Queued").Select(j => j.WorkspaceId).Distinct().ToListAsync(ct);
        foreach (var workspace in workspaces)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var replies = await inbox.ScanAsync(workspace, factory, ct);
                foreach (var reply in replies)
                {
                    await inbox.ImportUnclassifiedAsync(workspace, reply.ProspectId, reply.MessageId, factory, ct);
                    await CancelProspectPendingAsync(reply.ProspectId, workspace, "Gmail reply detected", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await HoldWorkspaceAsync(workspace, "Inbox check or provider safety verification failed; review Gmail and resume manually.", ct);
                continue;
            }
            List<int> dueIds;
            await using (var db = await factory.CreateDbContextAsync(ct))
                dueIds = await db.FollowupJobs.AsNoTracking()
                    .Where(j => j.WorkspaceId == workspace && j.State == "Queued" && j.DueUtc <= now)
                    .OrderBy(j => j.DueUtc).Select(j => j.Id).Take(10).ToListAsync(ct);
            foreach (var id in dueIds)
            {
                ct.ThrowIfCancellationRequested();
                try { await DispatchAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    await HoldJobAsync(id, "Dispatch preflight failed; owner review required.", ct);
                }
            }
        }
    }

    private async Task CancelProspectPendingAsync(int prospectId, int workspace, string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var jobs = await db.FollowupJobs.Where(j => j.WorkspaceId == workspace &&
            j.ProspectId == prospectId && j.State == "Queued").ToListAsync(ct);
        foreach (var j in jobs) { j.State = "Cancelled"; j.Result = reason; }
        var p = await db.Prospects.SingleOrDefaultAsync(p => p.Id == prospectId && p.WorkspaceId == workspace, ct);
        if (p is not null) { p.AutomationPaused = true; p.NextStepDueUtc = null; }
        await db.SaveChangesAsync(ct);
    }

    private async Task HoldWorkspaceAsync(int workspace, string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var jobs = await db.FollowupJobs.Where(j => j.WorkspaceId == workspace &&
            j.State == "Queued").ToListAsync(ct);
        foreach (var j in jobs) { j.State = "Held"; j.Result = reason; }
        var ids = jobs.Select(j => j.ProspectId).Distinct().ToArray();
        var prospects = await db.Prospects.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in prospects) { p.AutomationPaused = true; p.NextStepDueUtc = null; }
        await db.SaveChangesAsync(ct);
    }

    private async Task HoldJobAsync(int id, string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var j = await db.FollowupJobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (j is null || j.State != "Queued") return; // Never change Sent/Sending/Uncertain.
        j.State = "Held"; j.Result = reason;
        var p = await db.Prospects.SingleAsync(p => p.Id == j.ProspectId, ct);
        p.AutomationPaused = true; p.NextStepDueUtc = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task DispatchAsync(int id, CancellationToken ct)
    {
        FollowupJob claim;
        // Network token refresh happens before the durable send reservation.
        int workspace;
        string sender;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var j = await db.FollowupJobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
            if (j.State != "Queued") return;
            workspace = j.WorkspaceId; sender = j.From;
        }
        // The original approving user must still own this workspace at
        // send time. Revoked owner privileges invalidate unattended approval.
        await using (var auth = await accounts.CreateDbContextAsync(ct))
        {
            var job = await factory.CreateDbContextAsync(ct);
            await using (job)
            {
                var approved = await job.FollowupJobs.AsNoTracking()
                    .SingleAsync(j => j.Id == id, ct);
                if (!await auth.Memberships.AsNoTracking().AnyAsync(m =>
                    m.UserId == approved.ApprovedByUserId &&
                    m.WorkspaceId == approved.WorkspaceId && m.Role == "Owner", ct))
                {
                    await HoldJobAsync(id, "Approving owner no longer has workspace permission.", ct);
                    return;
                }
            }
        }
        string? token = null;
        // The workspace poll notices replies while waiting. Repeat the read-only
        // check for EACH due email, so a long multi-recipient batch cannot rely
        // solely on a stale workspace-level result.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            if (await db.FollowupJobs.AnyAsync(j => j.Id == id && j.Kind == "Email", ct))
            {
                var freshReplies = await inbox.ScanAsync(workspace, factory, ct);
                foreach (var reply in freshReplies)
                {
                    await inbox.ImportUnclassifiedAsync(workspace, reply.ProspectId,
                        reply.MessageId, factory, ct);
                    await CancelProspectPendingAsync(reply.ProspectId, workspace,
                        "Gmail reply detected before dispatch", ct);
                }
                token = await gmail.PrepareAsync(workspace, sender, ct);
            }
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.OpenConnectionAsync(ct);
            using var reservation = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await using var tx = await db.Database.UseTransactionAsync(reservation, ct);
            var j = await db.FollowupJobs.SingleAsync(j => j.Id == id, ct);
            if (j.State != "Queued" || j.DueUtc > clock.GetUtcNow().UtcDateTime) return;
            var p = await db.Prospects.SingleAsync(p => p.Id == j.ProspectId && p.WorkspaceId == j.WorkspaceId, ct);
            var w = await db.Workspaces.SingleAsync(w => w.Id == j.WorkspaceId, ct);
            if (p.Status != ProspectStatus.AwaitingReply || p.AutomationPaused ||
                !await NoInboundAsync(db, p.Id, ct) ||
                !w.SenderEmail.Equals(j.From, StringComparison.OrdinalIgnoreCase) ||
                !p.ContactEmail.Equals(j.To, StringComparison.OrdinalIgnoreCase))
            {
                j.State = "Cancelled"; j.Result = "Eligibility or reply state changed";
                p.AutomationPaused = true; p.NextStepDueUtc = null;
                await db.SaveChangesAsync(ct); await reservation.CommitAsync(ct);
                return;
            }
            var now = clock.GetUtcNow().UtcDateTime;
            if (now - j.DueUtc > TimeSpan.FromHours(24))
            {
                j.State = "Held"; j.Result = "Overdue after downtime; reapprove timing.";
                p.AutomationPaused = true; p.NextStepDueUtc = null;
                await db.SaveChangesAsync(ct); await reservation.CommitAsync(ct);
                return;
            }
            if (j.Kind == "ManualHandoff")
            {
                p.Status = ProspectStatus.ManualNoResponse;
                p.AutomationPaused = true; p.NextStepDueUtc = null;
                p.NextAction = "Manually review no-response account; never classify as a warm lead";
                j.State = "Sent"; j.Result = "Manual queue handoff; no email sent"; j.CompletedUtc = now;
                db.Activities.Add(new() { ProspectId = p.Id, Kind = "Manual no-response handoff",
                    Detail = "Approved sequence ended without a recorded response." });
                await db.SaveChangesAsync(ct); await reservation.CommitAsync(ct);
                return;
            }
            if (p.Attempts != j.Step || j.Step > p.PlannedFollowups ||
                !await db.FollowupJobs.Where(x => x.ProspectId == p.Id && x.Step < j.Step)
                    .AllAsync(x => x.State == "Sent", ct))
            {
                j.State = "Held"; j.Result = "Sequence order mismatch";
                p.AutomationPaused = true; p.NextStepDueUtc = null;
                await db.SaveChangesAsync(ct); await reservation.CommitAsync(ct);
                return;
            }
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(p.SequenceTimeZoneId));
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                local.Hour < w.SendingStartHour || local.Hour >= w.SendingEndHour) return;
            var day = now.AddDays(-1); var hour = now.AddHours(-1);
            var hourlyLimit = Math.Clamp(config.GetValue<int?>("Gmail:HourlySendLimit") ?? 10, 1, 100);
            var initialDay = await db.OutboundDispatches.CountAsync(d => d.From == j.From && d.ApprovedUtc >= day, ct);
            var initialHour = await db.OutboundDispatches.CountAsync(d => d.From == j.From && d.ApprovedUtc >= hour, ct);
            var moreDay = await db.FollowupJobs.CountAsync(x => x.From == j.From && x.AttemptedUtc >= day, ct);
            var moreHour = await db.FollowupJobs.CountAsync(x => x.From == j.From && x.AttemptedUtc >= hour, ct);
            if (initialDay + moreDay >= w.DailySendLimit || initialHour + moreHour >= hourlyLimit) return;
            // This commit is the non-retryable handoff to the external provider.
            j.State = "Sending"; j.AttemptedUtc = now;
            j.Result = "Gmail dispatch reserved; do not resend after a crash.";
            await db.SaveChangesAsync(ct); await reservation.CommitAsync(ct);
            claim = j;
        }
        // The DB claim is durable before any Gmail send; uncertain results are terminal
        // pending owner reconciliation. Never call Gmail twice for one job.
        try
        {
            var result = await gmail.SendFollowupAsync(token!, claim, ct);
            await CompleteAsync(id, result, CancellationToken.None);
        }
        catch (GmailRejectedException ex)
        {
            await MarkAttemptAsync(id, "Held", "Gmail rejected send: " + ex.Message);
        }
        catch (Exception)
        {
            await MarkAttemptAsync(id, "Uncertain", "Gmail acceptance is unknown. Inspect Sent mail; no automatic retry.");
        }
    }

    private async Task MarkAttemptAsync(int id, string state, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        var j = await db.FollowupJobs.SingleAsync(j => j.Id == id);
        if (j.State == "Sent") return;
        j.State = state; j.Result = reason;
        var p = await db.Prospects.SingleAsync(p => p.Id == j.ProspectId);
        p.AutomationPaused = true; p.NextStepDueUtc = null;
        await db.SaveChangesAsync();
    }

    private async Task CompleteAsync(int id, GmailDelivery delivery, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        using var reservation = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var tx = await db.Database.UseTransactionAsync(reservation, ct);
        var j = await db.FollowupJobs.SingleAsync(j => j.Id == id, ct);
        if (j.State == "Sent") return;
        if (j.State is not ("Sending" or "Uncertain") ||
            !delivery.ThreadId.Equals(j.ThreadId, StringComparison.Ordinal))
            throw new InvalidOperationException("Follow-up provider state changed. Manual reconciliation required.");
        var reconciled = j.State == "Uncertain";
        var p = await db.Prospects.SingleAsync(p => p.Id == j.ProspectId && p.WorkspaceId == j.WorkspaceId, ct);
        db.ProviderMessages.Add(new() { WorkspaceId = j.WorkspaceId, ProspectId = p.Id,
            MailboxEmail = j.From.ToLowerInvariant(), MessageId = delivery.MessageId, ThreadId = delivery.ThreadId,
            Message = new OutreachMessage { ProspectId = p.Id, Direction = "Outbound",
                Classification = "Gmail follow-up accepted", Subject = j.Subject,
                Body = j.Body, IsSimulation = false, OccurredUtc = clock.GetUtcNow().UtcDateTime }
        });
        j.State = "Sent"; j.CompletedUtc = clock.GetUtcNow().UtcDateTime;
        j.GmailMessageId = delivery.MessageId; j.Result = "Gmail accepted follow-up; inbox delivery is unconfirmed.";
        p.Attempts++;
        // A reply or manual takeover can happen while Gmail accepts a reserved
        // message. Record Gmail acceptance but never reopen pending follow-ups.
        // Uncertain delivery intentionally pauses the prospect until the
        // owner reconciles it. That pause is not itself an inbound reply:
        // reconciliation must retain the next step as Held, never Queued.
        var interrupted = p.Status != ProspectStatus.AwaitingReply ||
            !await NoInboundAsync(db, p.Id, ct) ||
            (p.AutomationPaused && !reconciled);
        if (interrupted)
        {
            foreach (var pending in await db.FollowupJobs.Where(x =>
                x.WorkspaceId == j.WorkspaceId && x.ProspectId == p.Id &&
                x.Step > j.Step && (x.State == "Queued" || x.State == "Held")).ToListAsync(ct))
            {
                pending.State = "Cancelled";
                pending.Result = "Sequence stopped while Gmail accepted an in-flight message.";
            }
            p.AutomationPaused = true;
            p.NextStepDueUtc = null;
            // Preserve the reply classification and next action from the owner.
        }
        else
        {
            var next = await db.FollowupJobs.SingleOrDefaultAsync(x => x.WorkspaceId == j.WorkspaceId &&
                x.ProspectId == p.Id && x.Step == (j.Step == p.PlannedFollowups ? 3 : j.Step + 1), ct);
            if (next is not null)
            {
                var days = j.Step == p.PlannedFollowups ? p.NoResponseDelayDays : p.SecondFollowupDelayDays;
                next.DueUtc = BusinessCalendar.AddBusinessDays(clock.GetUtcNow().UtcDateTime,
                    Math.Clamp(days, 1, 30), p.SequenceTimeZoneId, p.SequenceStartHour);
                if (reconciled)
                {
                    next.State = "Held";
                    next.Result = "Previous Gmail acceptance reconciled; review before resuming.";
                    p.AutomationPaused = true;
                    p.NextStepDueUtc = null;
                    p.NextAction = "Review reconciled Gmail result and explicitly resume pending schedule";
                }
                else
                {
                    p.NextStepDueUtc = next.DueUtc;
                    p.NextAction = next.Kind == "ManualHandoff"
                        ? "Automatic sequence complete; handoff when final response window closes"
                        : "Next approved Gmail follow-up queued";
                }
            }
        }
        db.Activities.Add(new() { ProspectId = p.Id, Kind = "Gmail follow-up accepted",
            Detail = "Step " + j.Step + ": gmail:" + delivery.MessageId });
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
    }
}

public sealed class FollowupWorker(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<FollowupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Followups:Enabled")) return;
        var minutes = Math.Clamp(config.GetValue<int?>("Followups:PollMinutes") ?? 3, 1, 15);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FollowupAutomationService>()
                    .TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Expose useful error type, not raw Gmail message content or credentials.
                logger.LogWarning("Follow-up worker cycle held: {ExceptionType}", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services.Mailbox;

public sealed record SendPreview(int ProspectId, int WorkspaceId, string From, string To, string Subject, string Body, string Hash);

public sealed class ApprovedSendService(IDbContextFactory<OutreachDbContext> factory,
    IDbContextFactory<AccountDbContext> accounts, IGmailSender gmail, IConfiguration config, TimeProvider clock)
{
    private async Task<int> OwnerAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await using var auth = await accounts.CreateDbContextAsync(ct);
        return await MailboxOwner.RequireAsync(user, auth, ct);
    }

    public async Task<OutboundDispatch?> GetAsync(ClaimsPrincipal user, int prospectId, CancellationToken ct = default)
    {
        var workspace = await OwnerAsync(user, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.OutboundDispatches.AsNoTracking().SingleOrDefaultAsync(d => d.WorkspaceId == workspace && d.ProspectId == prospectId, ct);
    }

    public async Task<SendPreview> PreviewAsync(ClaimsPrincipal user, int prospectId, CancellationToken ct = default)
    {
        var workspace = await OwnerAsync(user, ct);
        if (!gmail.SendingAvailable) throw new InvalidOperationException("Enable the controlled Gmail sending pilot and connect your mailbox first.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.Prospects.SingleAsync(p => p.Id == prospectId && p.WorkspaceId == workspace, ct);
        var w = await db.Workspaces.SingleAsync(w => w.Id == workspace, ct);
        await EligibleAsync(db, p, w, ct);
        return Preview(p, w);
    }

    private static SendPreview Preview(Prospect p, Workspace w)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            p.Id, p.WorkspaceId, w.SenderEmail, p.ContactEmail, p.DraftSubject, p.DraftBody,
            p.ResearchOutcome, p.ResearchNotes, p.ProcessUrl, p.UpdatedUtc,
            w.DailySendLimit, w.SendingStartHour, w.SendingEndHour, w.TimeZoneId
        }))));
        return new(p.Id, p.WorkspaceId, w.SenderEmail, p.ContactEmail, p.DraftSubject, p.DraftBody, hash);
    }

    private static async Task EligibleAsync(OutreachDbContext db, Prospect p, Workspace w, CancellationToken ct)
    {
        if (p.Status != ProspectStatus.DraftReady || p.Attempts != 0 || p.AutomationPaused ||
            p.ResearchOutcome is not (ResearchOutcome.NoProcessFound or ResearchOutcome.EmailInstructions))
            throw new InvalidOperationException("Only a reviewed initial inquiry with no previous outreach or owner takeover can be sent.");
        foreach (var address in new[] { p.ContactEmail, w.SenderEmail })
            if (address.Length > 254 || address.Any(char.IsControl) || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address)
                throw new InvalidOperationException("Sender and recipient must be single valid email addresses.");
        if (string.IsNullOrWhiteSpace(p.DraftSubject) || p.DraftSubject.Length > 500 || p.DraftSubject.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(p.DraftBody) || p.DraftBody.Length > 10000)
            throw new InvalidOperationException("Review a subject of 1–500 characters and body of 1–10,000 characters.");
        if (await db.Messages.AnyAsync(m => m.ProspectId == p.Id, ct) ||
            await db.OutboundDispatches.AnyAsync(d => d.ProspectId == p.Id, ct))
            throw new InvalidOperationException("This prospect already has message or dispatch history. Review its delivery record.");
        var email = p.ContactEmail.ToLower();
        if (await db.Prospects.AnyAsync(o => o.WorkspaceId == p.WorkspaceId && o.Id != p.Id && o.ContactEmail.ToLower() == email &&
            (o.Status == ProspectStatus.Suppressed || o.Attempts > 0), ct) ||
            await db.OutboundDispatches.AnyAsync(d => d.WorkspaceId == p.WorkspaceId && d.To.ToLower() == email, ct))
            throw new InvalidOperationException("Recipient is suppressed or already has outreach/dispatch history in this workspace.");
    }

    public async Task<OutboundDispatch> SendAsync(ClaimsPrincipal user, SendPreview approved, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicitly approve the displayed sender, recipient, process decision and message before sending.");
        var workspace = await OwnerAsync(user, ct);
        if (workspace != approved.WorkspaceId) throw new UnauthorizedAccessException("Workspace changed. Review again.");
        // All network preflight occurs before the claim. Nothing is transmitted until a fresh DB check and durable claim.
        var token = await gmail.PrepareAsync(workspace, approved.From, ct);
        OutboundDispatch dispatch;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.OpenConnectionAsync(ct);
            using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await using var enlisted = await db.Database.UseTransactionAsync(transaction, ct);
            if (workspace != await OwnerAsync(user, ct)) throw new UnauthorizedAccessException("Workspace changed. Review again.");
            var p = await db.Prospects.SingleAsync(p => p.Id == approved.ProspectId && p.WorkspaceId == workspace, ct);
            var w = await db.Workspaces.SingleAsync(w => w.Id == workspace, ct);
            await EligibleAsync(db, p, w, ct);
            var current = Preview(p, w);
            if (current != approved) throw new InvalidOperationException("Draft, recipient, sender or research changed. Review the new inquiry before sending.");
            var now = clock.GetUtcNow().UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(w.TimeZoneId));
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || local.Hour < w.SendingStartHour || local.Hour >= w.SendingEndHour)
                throw new InvalidOperationException("Outside this workspace's weekday sending hours. Review Settings and try during the configured window.");
            // Rolling windows also count rejected and uncertain requests, across workspaces sharing this mailbox.
            var day = now.AddDays(-1); var hour = now.AddHours(-1);
            var hourlyLimit = Math.Clamp(config.GetValue<int?>("Gmail:HourlySendLimit") ?? 10, 1, 100);
            if (await db.OutboundDispatches.CountAsync(d => d.From == approved.From && d.ApprovedUtc >= day, ct) >= w.DailySendLimit ||
                await db.OutboundDispatches.CountAsync(d => d.From == approved.From && d.ApprovedUtc >= hour, ct) >= hourlyLimit)
                throw new InvalidOperationException("Mailbox daily or hourly send limit reached. No message was sent.");
            dispatch = new() { WorkspaceId = workspace, ProspectId = p.Id, From = approved.From, To = approved.To,
                Subject = approved.Subject, Body = approved.Body, ApprovalHash = approved.Hash,
                ResearchSnapshot = JsonSerializer.Serialize(new { p.ResearchOutcome, p.ResearchNotes, p.ProcessUrl,
                    Evidence = await db.ResearchEvidence.AsNoTracking().Where(e => e.ProspectId == p.Id).ToListAsync(ct) }),
                PolicySnapshot = JsonSerializer.Serialize(new { w.DailySendLimit, HourlySendLimit = hourlyLimit,
                    w.SendingStartHour, w.SendingEndHour, w.TimeZoneId, Followups = "Manual only" }),
                ApprovedByUserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!, ApprovedUtc = now,
                RfcMessageId = "<" + Guid.NewGuid().ToString("N") + "@outreachflow.local>" };
            db.OutboundDispatches.Add(dispatch);
            p.AutomationPaused = true;
            p.NextStepDueUtc = null;
            p.NextAction = "Check the Gmail delivery record before further outreach";
            db.Activities.Add(new() { ProspectId = p.Id, Kind = "Gmail send approved", Detail = "Owner approved the saved inquiry snapshot. Dispatch reserved; delivery pending.", OccurredUtc = now });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        // No automatic resend, including after cancellation, restart or a lost successful response.
        try
        {
            var delivered = await gmail.SendAsync(token, dispatch, ct);
            await CompleteAsync(dispatch.Id, delivered, CancellationToken.None);
        }
        catch (GmailRejectedException ex) { await ResultAsync(dispatch.Id, "Rejected", ex.Message); }
        catch (Exception) { await ResultAsync(dispatch.Id, "Uncertain", "Gmail acceptance could not be confirmed. Check Sent mail using the button below. Never resend until resolved."); }
        return (await GetAsync(user, dispatch.ProspectId, CancellationToken.None))!;
    }

    private async Task ResultAsync(int id, string state, string result)
    {
        await using var db = await factory.CreateDbContextAsync();
        // A simultaneous reconciliation may already have confirmed acceptance.
        await db.OutboundDispatches.Where(d => d.Id == id && d.State != "Sent")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, state).SetProperty(d => d.Result, result));
    }

    public async Task<OutboundDispatch> ReconcileAsync(ClaimsPrincipal user, int prospectId, CancellationToken ct = default)
    {
        var dispatch = await GetAsync(user, prospectId, ct) ?? throw new InvalidOperationException("No dispatch to reconcile.");
        if (dispatch.State == "Sent") return dispatch;
        var found = await gmail.FindSentAsync(dispatch.WorkspaceId, dispatch, ct);
        if (found is null) throw new InvalidOperationException("No exact Sent match found. This does not prove sending failed. Leave this dispatch blocked and inspect Gmail manually.");
        await CompleteAsync(dispatch.Id, found, ct);
        return (await GetAsync(user, prospectId, ct))!;
    }

    private async Task CompleteAsync(int id, GmailDelivery delivered, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var enlisted = await db.Database.UseTransactionAsync(transaction, ct);
        var d = await db.OutboundDispatches.SingleAsync(d => d.Id == id, ct);
        if (d.State == "Sent") return;
        var p = await db.Prospects.SingleAsync(p => p.Id == d.ProspectId && p.WorkspaceId == d.WorkspaceId, ct);
        var message = new OutreachMessage { ProspectId = p.Id, Direction = "Outbound", Classification = "Gmail accepted",
            Subject = d.Subject, Body = d.Body, IsSimulation = false, OccurredUtc = d.ApprovedUtc };
        db.ProviderMessages.Add(new() { WorkspaceId = d.WorkspaceId, ProspectId = p.Id, Message = message,
            MailboxEmail = d.From.ToLowerInvariant(), MessageId = delivered.MessageId, ThreadId = delivered.ThreadId });
        p.Attempts = Math.Max(p.Attempts, 1);
        p.AutomationPaused = true; p.NextStepDueUtc = null;
        if (p.Status == ProspectStatus.DraftReady && !await db.Messages.AnyAsync(m => m.ProspectId == p.Id && m.Direction == "Inbound", ct))
        {
            p.Status = ProspectStatus.AwaitingReply;
            p.NextAction = "Review Gmail replies and personally prepare any follow-up";
        }
        d.State = "Sent";
        d.Result = "Gmail accepted this inquiry. Inbox delivery is not guaranteed. Follow-ups remain manual.";
        db.Activities.Add(new() { ProspectId = p.Id, Kind = "Gmail outbound accepted", Detail = "gmail:" + delivered.MessageId });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services.Mailbox;

internal static class FollowupAutomationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "outreachflow-followups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var business = new BusinessFactory(new DbContextOptionsBuilder<OutreachDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "business.db") + ";Pooling=False").Options);
            var auth = new AuthFactory(new DbContextOptionsBuilder<AccountDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "accounts.db") + ";Pooling=False").Options);
            await using (var db = business.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.Workspaces.Add(new Workspace {
                    Id = 1, BusinessName = "Fixture Wash",
                    SenderName = "Owner", SenderEmail = "owner@example.org",
                    ServiceDescription = "fleet washing", ServiceArea = "Charlotte",
                    FirstFollowupBusinessDays = 2, SecondFollowupBusinessDays = 3,
                    NoResponseAfterBusinessDays = 2, MaxFollowups = 2,
                    SendingStartHour = 9, SendingEndHour = 17, DailySendLimit = 10
                });
                await db.SaveChangesAsync();
            }
            await using (var db = auth.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.Add(new ApplicationUser { Id = "owner", ActiveWorkspaceId = 1 });
                db.Memberships.Add(new WorkspaceMembership { UserId = "owner", WorkspaceId = 1, Role = "Owner" });
                await db.SaveChangesAsync();
            }
            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "owner") }, "fixture"));
            var clock = new MutableClock(new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero));
            var gmail = new FakeGmail();
            var inbox = new FakeInbox();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Followups:Enabled"] = "true", ["Gmail:HourlySendLimit"] = "10"
            }).Build();
            var service = new FollowupAutomationService(business, auth, gmail, inbox, config, clock);

            async Task<int> NewProspect(string company)
            {
                await using var db = business.CreateDbContext();
                var p = new Prospect { WorkspaceId = 1, CompanyName = company,
                    ContactEmail = company.ToLowerInvariant().Replace(" ", "") + "@example.org",
                    Status = ProspectStatus.AwaitingReply, Attempts = 1, AutomationPaused = true,
                    ResearchOutcome = ResearchOutcome.NoProcessFound };
                db.Prospects.Add(p);
                await db.SaveChangesAsync();
                var outbound = new OutreachMessage { ProspectId = p.Id, Direction = "Outbound",
                    IsSimulation = false, Classification = "Gmail accepted", Subject = "Inquiry", Body = "Original",
                    OccurredUtc = clock.GetUtcNow().UtcDateTime.AddDays(-3) };
                db.OutboundDispatches.Add(new OutboundDispatch { WorkspaceId = 1, ProspectId = p.Id,
                    From = "owner@example.org", To = p.ContactEmail, Subject = "Inquiry",
                    Body = "Original", State = "Sent",
                    ApprovedUtc = clock.GetUtcNow().UtcDateTime.AddDays(-3),
                    RfcMessageId = "<initial-" + p.Id + "@example.org>" });
                db.ProviderMessages.Add(new ProviderMessage { WorkspaceId = 1, ProspectId = p.Id,
                    Message = outbound, MailboxEmail = "owner@example.org",
                    MessageId = "initial-" + p.Id, ThreadId = "thread-" + p.Id });
                await db.SaveChangesAsync();
                return p.Id;
            }

            var first = await NewProspect("Sequence A");
            var preview = await service.PreviewAsync(owner, first);
            Check(preview.FollowupCount == 2 && preview.FirstBody.Contains("fleet washing"),
                "owner previews snapshotted exact follow-up message bodies");
            await Reject(() => service.ApproveAsync(owner, first, "wrong-hash"),
                "unreviewed sequence cannot be authorized");
            await service.ApproveAsync(owner, first, preview.Hash);
            var jobs = await service.ListAsync(owner, first);
            Check(jobs.Count == 3 && jobs[0].State == "Queued" &&
                jobs[1].DueUtc == DateTime.MaxValue && jobs[2].Kind == "ManualHandoff" &&
                gmail.Sends == 0, "approval persists two messages plus manual handoff without sending");
            await Reject(() => service.ApproveAsync(owner, first, preview.Hash),
                "duplicate approval cannot create a second job sequence");
            await service.ControlAsync(owner, first, "pause");
            Check((await service.ListAsync(owner, first)).All(j => j.State == "Held"),
                "owner pause persists across fresh contexts");
            await service.ControlAsync(owner, first, "resume");
            Check((await service.ListAsync(owner, first)).All(j => j.State == "Queued"),
                "owner can explicitly resume only unattempted held steps");

            clock.UtcNow = new DateTimeOffset(jobs[0].DueUtc.AddMinutes(1), TimeSpan.Zero);
            await service.TickAsync();
            jobs = await service.ListAsync(owner, first);
            Check(jobs[0].State == "Sent" && jobs[1].State == "Queued" &&
                jobs[1].DueUtc != DateTime.MaxValue && gmail.Sends == 1,
                "first due step is accepted exactly once and unlocks next due step");
            // Simulate process restart: new scheduler sees same durable state and does not resend.
            var restarted = new FollowupAutomationService(business, auth, gmail, inbox, config, clock);
            await restarted.TickAsync();
            Check(gmail.Sends == 1, "restart never duplicates an accepted follow-up");
            clock.UtcNow = new DateTimeOffset(jobs[1].DueUtc.AddMinutes(1), TimeSpan.Zero);
            await restarted.TickAsync();
            jobs = await restarted.ListAsync(owner, first);
            Check(gmail.Sends == 2 && jobs[1].State == "Sent" &&
                jobs[2].DueUtc != DateTime.MaxValue &&
                gmail.ThreadIds.All(t => t == "thread-" + first),
                "second follow-up stays in original Gmail thread");
            clock.UtcNow = new DateTimeOffset(jobs[2].DueUtc.AddMinutes(1), TimeSpan.Zero);
            await restarted.TickAsync();
            await using (var db = business.CreateDbContext())
                Check((await db.Prospects.FindAsync(first))!.Status == ProspectStatus.ManualNoResponse &&
                    (await db.FollowupJobs.SingleAsync(j => j.ProspectId == first && j.Step == 3)).State == "Sent" &&
                    gmail.Sends == 2, "final no-reply step hands off without sending another email");

            var replyProspect = await NewProspect("Sequence B");
            var replyPreview = await service.PreviewAsync(owner, replyProspect);
            await service.ApproveAsync(owner, replyProspect, replyPreview.Hash);
            var replyJobs = await service.ListAsync(owner, replyProspect);
            inbox.Reply = new GmailInboxItem("reply-1", "sequenceb@example.org", "Re: Inquiry",
                "Please stop.", clock.GetUtcNow().UtcDateTime, replyProspect, "Sequence B",
                "thread-" + replyProspect);
            clock.UtcNow = new DateTimeOffset(replyJobs[0].DueUtc.AddMinutes(1), TimeSpan.Zero);
            await service.TickAsync();
            Check(gmail.Sends == 2 &&
                (await service.ListAsync(owner, replyProspect)).All(j => j.State == "Cancelled"),
                "detected inbound reply cancels future steps before any send");
            inbox.Reply = null;

            var unsafeProspect = await NewProspect("Sequence C");
            await service.ApproveAsync(owner, unsafeProspect, (await service.PreviewAsync(owner, unsafeProspect)).Hash);
            inbox.Throw = true;
            clock.UtcNow = new DateTimeOffset((await service.ListAsync(owner, unsafeProspect))[0].DueUtc.AddMinutes(1), TimeSpan.Zero);
            await service.TickAsync();
            Check(gmail.Sends == 2 &&
                (await service.ListAsync(owner, unsafeProspect))[0].State == "Held",
                "inbox outage fails closed instead of assuming no reply");
            inbox.Throw = false;
            await service.ControlAsync(owner, unsafeProspect, "resume");
            gmail.FailUnknown = true;
            await service.TickAsync();
            Check((await service.ListAsync(owner, unsafeProspect))[0].State == "Uncertain",
                "provider timeout is persistent and non-retryable");
            gmail.FailUnknown = false;
            var before = gmail.Sends;
            await service.TickAsync();
            Check(gmail.Sends == before, "uncertain sends are never replayed");
            var uncertainId = (await service.ListAsync(owner, unsafeProspect))[0].Id;
            await Reject(() => service.ReconcileAsync(owner, unsafeProspect, uncertainId),
                "absence of exact Gmail Sent match cannot authorize a retry");
            gmail.Found = new GmailDelivery("confirmed-followup", "thread-" + unsafeProspect);
            await service.ReconcileAsync(owner, unsafeProspect, uncertainId);
            var recovered = await service.ListAsync(owner, unsafeProspect);
            Check(recovered[0].State == "Sent" && recovered[1].State == "Held" &&
                gmail.Sends == before, "confirmed Sent match reconciles without resending and holds next step");
            gmail.Found = null;

            var blockedProspect = await NewProspect("Sequence D");
            await service.ApproveAsync(owner, blockedProspect,
                (await service.PreviewAsync(owner, blockedProspect)).Hash);
            await using (var db = business.CreateDbContext())
            {
                var p = await db.Prospects.FindAsync(blockedProspect);
                p!.Status = ProspectStatus.Suppressed;
                await db.SaveChangesAsync();
            }
            clock.UtcNow = new DateTimeOffset(
                (await service.ListAsync(owner, blockedProspect))[0].DueUtc.AddMinutes(1),
                TimeSpan.Zero);
            await service.TickAsync();
            Check((await service.ListAsync(owner, blockedProspect))[0].State == "Cancelled" &&
                gmail.Sends == before, "suppressed prospect cannot dispatch a queued follow-up");

            var revoked = await NewProspect("Sequence E");
            await service.ApproveAsync(owner, revoked,
                (await service.PreviewAsync(owner, revoked)).Hash);
            var revokedDue = (await service.ListAsync(owner, revoked))[0].DueUtc;
            await using (var authDb = auth.CreateDbContext())
            {
                var membership = await authDb.Memberships.SingleAsync();
                authDb.Memberships.Remove(membership);
                await authDb.SaveChangesAsync();
            }
            clock.UtcNow = new DateTimeOffset(revokedDue.AddMinutes(1), TimeSpan.Zero);
            await service.TickAsync();
            await using (var db = business.CreateDbContext())
                Check((await db.FollowupJobs.SingleAsync(j => j.ProspectId == revoked &&
                    j.Step == 1)).State == "Held" && gmail.Sends == before,
                    "revoked approving owner permission blocks scheduled Gmail sends");
        }
        finally { await TestDataCleanup.DirectoryAsync(root); }
    }

    private static async Task Reject(Func<Task> action, string label)
    {
        try { await action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: " + label); return; }
        throw new Exception("FAILED: " + label);
    }
    private static void Check(bool success, string label)
    {
        if (!success) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private sealed class BusinessFactory(DbContextOptions<OutreachDbContext> options)
        : IDbContextFactory<OutreachDbContext>
    {
        public OutreachDbContext CreateDbContext() => new(options);
    }
    private sealed class AuthFactory(DbContextOptions<AccountDbContext> options)
        : IDbContextFactory<AccountDbContext>
    {
        public AccountDbContext CreateDbContext() => new(options);
    }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class FakeInbox : IAutomationInbox
    {
        public GmailInboxItem? Reply { get; set; }
        public bool Throw { get; set; }
        public Task<IReadOnlyList<GmailInboxItem>> ScanAsync(int workspaceId,
            IDbContextFactory<OutreachDbContext> factory, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("fixture inbox unavailable");
            return Task.FromResult<IReadOnlyList<GmailInboxItem>>(
                Reply is null ? [] : [Reply]);
        }
        public async Task<bool> ImportUnclassifiedAsync(int workspaceId, int prospectId,
            string messageId, IDbContextFactory<OutreachDbContext> factory, CancellationToken ct)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var p = await db.Prospects.SingleAsync(p => p.WorkspaceId == workspaceId && p.Id == prospectId, ct);
            p.AutomationPaused = true; p.Status = ProspectStatus.NeedsAction;
            db.Messages.Add(new OutreachMessage { ProspectId = p.Id, Direction = "Inbound",
                Body = "Unread fixture reply", IsSimulation = false });
            await db.SaveChangesAsync(ct);
            return true;
        }
    }
    private sealed class FakeGmail : IGmailSender
    {
        public bool SendingAvailable => true;
        public bool FailUnknown { get; set; }
        public int Sends;
        public List<string> ThreadIds = [];
        public GmailDelivery? Found { get; set; }
        public Task<string> PrepareAsync(int workspaceId, string sender, CancellationToken ct) =>
            Task.FromResult("fixture-access-token");
        public Task<GmailDelivery> SendAsync(string token, OutboundDispatch d, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GmailDelivery?> FindSentAsync(int workspaceId, OutboundDispatch d, CancellationToken ct) =>
            Task.FromResult(Found);
        public Task<GmailDelivery> SendFollowupAsync(string token, FollowupJob job, CancellationToken ct)
        {
            if (FailUnknown) throw new TaskCanceledException("unknown Gmail result");
            Sends++;
            ThreadIds.Add(job.ThreadId);
            return Task.FromResult(new GmailDelivery("followup-" + Sends, job.ThreadId));
        }
    }
}

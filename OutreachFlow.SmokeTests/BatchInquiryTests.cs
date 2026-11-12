using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services;
using OutreachFlow.Services.Mailbox;

internal static class BatchInquiryTests
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "outreachflow-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<OutreachDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(folder, "business.db") + ";Pooling=False").Options;
            var authOptions = new DbContextOptionsBuilder<AccountDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(folder, "auth.db") + ";Pooling=False").Options;
            var factory = new BusinessFactory(dbOptions);
            var authFactory = new AuthFactory(authOptions);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.Workspaces.Add(new Workspace {
                    Id = 1, BusinessName = "Fixture Wash",
                    SenderName = "Fixture Owner", SenderEmail = "owner@example.org",
                    ServiceDescription = "fleet cleaning", ServiceArea = "South Charlotte",
                    DailySendLimit = 10
                });
                await db.SaveChangesAsync();
            }
            await using (var db = authFactory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.AddRange(
                    new ApplicationUser { Id = "owner", ActiveWorkspaceId = 1 },
                    new ApplicationUser { Id = "outsider", ActiveWorkspaceId = null });
                db.Memberships.Add(new WorkspaceMembership { UserId = "owner", WorkspaceId = 1, Role = "Owner" });
                await db.SaveChangesAsync();
            }
            var gmail = new FakeSender();
            var config = new ConfigurationBuilder().Build();
            var clock = new TestClock();
            var flow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(1));
            var one = new ApprovedSendService(factory, authFactory, gmail, config, clock);
            var batches = new BatchInquiryService(factory, authFactory, flow, one, gmail);
            var owner = Principal("owner");
            var outsider = Principal("outsider");
            const string sample = "Test Fleet A | South | https://example.org | a@example.org\n" +
                                  "Test Fleet B | North | https://example.org | b@example.org";
            await Reject(() => batches.RunAsync(outsider, sample, BatchInquiryMode.Simulated, false),
                "nonowner cannot run a batch");
            var simulated = await batches.RunAsync(owner, sample, BatchInquiryMode.Simulated, false);
            Check(simulated.Simulated == 2 && simulated.Skipped == 0 && gmail.Sends == 0,
                "pasted rows create and simulate inquiries without Gmail calls");
            var replay = await batches.RunAsync(owner, sample, BatchInquiryMode.Simulated, false);
            Check(replay.Skipped == 2 && gmail.Sends == 0,
                "replaying an import cannot create or resend prospects");
            await using (var db = factory.CreateDbContext())
                Check(await db.Prospects.CountAsync() == 2 &&
                    await db.Messages.CountAsync(m => m.IsSimulation && m.Direction == "Outbound") == 2 &&
                    await db.Prospects.AllAsync(p => p.Attempts == 1 && p.Status == ProspectStatus.AwaitingReply),
                    "simulated history persists in migrated SQLite without live email");

            const string live = "Test Fleet C | Main | https://example.org | c@example.org";
            await Reject(() => batches.RunAsync(owner, live, BatchInquiryMode.Gmail, false),
                "live dispatch requires affirmative batch approval");
            await Reject(() => batches.RunAsync(owner, live, BatchInquiryMode.Gmail, true),
                "live dispatch requires exact preview hash");
            var review = await batches.PreviewAsync(owner, live);
            Check(review.Lines.Count == 1 && review.From == "owner@example.org" &&
                review.Lines[0].Body.Contains("Fixture Wash", StringComparison.Ordinal) &&
                review.Lines[0].To == "c@example.org", "preview binds sender, recipient and expanded template");
            await using (var db = factory.CreateDbContext())
            {
                var w = await db.Workspaces.SingleAsync();
                w.InitialBodyTemplate += "\nUpdated";
                await db.SaveChangesAsync();
            }
            await Reject(() => batches.RunAsync(owner, live, BatchInquiryMode.Gmail, true, review.Hash),
                "changed template invalidates preapproved batch");
            review = await batches.PreviewAsync(owner, live);
            var sent = await batches.RunAsync(owner, live, BatchInquiryMode.Gmail, true, review.Hash);
            Check(sent.Sent == 1 && gmail.Sends == 1,
                "single approved batch uses the real provider-backed dispatch boundary once");
            var again = await batches.RunAsync(owner, live, BatchInquiryMode.Gmail, true, review.Hash);
            Check(again.Skipped == 1 && gmail.Sends == 1,
                "duplicate live import never requests another Gmail send");
            await using (var db = factory.CreateDbContext())
            {
                Check(await db.OutboundDispatches.CountAsync(d => d.State == "Sent") == 1 &&
                    await db.ProviderMessages.CountAsync(m => m.Provider == "gmail") == 1 &&
                    await db.Messages.CountAsync(m => !m.IsSimulation && m.Direction == "Outbound") == 1,
                    "live approval creates one durable provider-confirmed dispatch with audit identity");
            }

            await Reject(() => batches.RunAsync(owner,
                "Invalid | Site | https://example.org | not-an-address",
                BatchInquiryMode.Simulated, false), "invalid email fails before database writes");
            await Reject(() => batches.RunAsync(owner,
                "Injected | Site | http://localhost | other@example.org",
                BatchInquiryMode.Simulated, false), "non-HTTPS company input is rejected");
        }
        finally { await TestDataCleanup.DirectoryAsync(folder); }
    }

    private static ClaimsPrincipal Principal(string id) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id) }, "fixture"));
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private static async Task Reject(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Console.WriteLine("PASS: " + label); return;
        }
        throw new Exception("FAILED: " + label);
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
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 11, 16, 0, 0, TimeSpan.Zero);
    }
    private sealed class FakeSender : IGmailSender
    {
        public bool SendingAvailable => true;
        public int Sends;
        public Task<string> PrepareAsync(int workspaceId, string sender, CancellationToken ct) =>
            Task.FromResult("test-only-token");
        public Task<GmailDelivery> SendAsync(string token, OutboundDispatch dispatch, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref Sends);
            return Task.FromResult(new GmailDelivery("batch-fixture-" + id, "thread-batch-" + id));
        }
        public Task<GmailDelivery?> FindSentAsync(int workspaceId, OutboundDispatch dispatch, CancellationToken ct)
            => Task.FromResult<GmailDelivery?>(null);
    }
}

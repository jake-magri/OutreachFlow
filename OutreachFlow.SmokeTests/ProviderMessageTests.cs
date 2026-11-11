using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services.Mailbox;

internal static class ProviderMessageTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "outreachflow-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<OutreachDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "test.db") + ";Pooling=False;Default Timeout=15")
                .Options;
            var factory = new TestFactory(options);
            int firstId, secondId, workspaceId, otherWorkspaceId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                var workspace = new Workspace { BusinessName = "Fixture", SenderEmail = "owner@example.org" };
                var other = new Workspace { BusinessName = "Other", SenderEmail = "other@example.org" };
                db.Workspaces.AddRange(workspace, other);
                await db.SaveChangesAsync();
                workspaceId = workspace.Id;
                otherWorkspaceId = other.Id;
                var first = new Prospect { WorkspaceId = workspaceId, CompanyName = "First",
                    ContactEmail = "contact@example.org", Attempts = 1, Status = ProspectStatus.AwaitingReply };
                var second = new Prospect { WorkspaceId = workspaceId, CompanyName = "Second",
                    ContactEmail = "contact@example.org", Attempts = 1, Status = ProspectStatus.AwaitingReply };
                db.Prospects.AddRange(first, second);
                await db.SaveChangesAsync();
                firstId = first.Id;
                secondId = second.Id;
                var acceptedAt = DateTime.UtcNow.AddMinutes(-1);
                var firstOutbound = new OutreachMessage { ProspectId = firstId, IsSimulation = false,
                    Direction = "Outbound", OccurredUtc = acceptedAt };
                db.Messages.AddRange(firstOutbound,
                    new OutreachMessage { ProspectId = secondId, IsSimulation = false,
                        Direction = "Outbound", OccurredUtc = acceptedAt });
                db.OutboundDispatches.Add(new OutboundDispatch {
                    WorkspaceId = workspaceId, ProspectId = firstId, State = "Sent",
                    From = "owner@example.org", To = "contact@example.org",
                    RfcMessageId = "<sent-fixture@example.org>", ApprovedUtc = acceptedAt
                });
                db.ProviderMessages.Add(new ProviderMessage {
                    WorkspaceId = workspaceId, ProspectId = firstId, Message = firstOutbound,
                    MailboxEmail = "owner@example.org", MessageId = "sent-message-1", ThreadId = "thread-1"
                });
                await db.SaveChangesAsync();
            }
            var store = new GmailConnectionStore(Path.Combine(root, "tokens"), new EphemeralDataProtectionProvider());
            await store.SaveAsync(workspaceId, new GmailConnection("owner@example.org", "fixture-token"));
            await store.SaveAsync(otherWorkspaceId, new GmailConnection("other@example.org", "fixture-token"));
            var service = new GmailMailboxService(new ConfigurationBuilder().Build(), new FakeClients(), store);
            // Two independently created contexts race to import the same provider message.
            var imports = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                service.ImportReviewedInboundAsync(workspaceId, firstId, "message-1",
                    ReplyCategory.Declined, factory))));
            Check(imports.Count(x => x) == 1, "concurrent reviewers commit exactly one Gmail import");
            await using (var db = factory.CreateDbContext())
            {
                var provider = await db.ProviderMessages.SingleAsync(m => m.MessageId == "message-1");
                Check(provider.ThreadId == "thread-1" && provider.MailboxEmail == "owner@example.org" &&
                    provider.ProspectId == firstId && provider.WorkspaceId == workspaceId,
                    "Gmail message and conversation identity are persisted with tenant ownership");
                Check(await db.Messages.CountAsync(m => m.Direction == "Inbound") == 1 &&
                    await db.Activities.CountAsync(a => a.Kind == "Gmail inbound imported") == 1 &&
                    (await db.Prospects.FindAsync(firstId))!.Status == ProspectStatus.Suppressed,
                    "reply, audit and suppression commit together");
            }
            Check(!await service.ImportReviewedInboundAsync(workspaceId, secondId, "message-1",
                ReplyCategory.Interested, factory), "same message cannot be assigned to another prospect");
            // Reconnecting the same mailbox preserves its provider identity.
            await store.SaveAsync(workspaceId, new GmailConnection("OWNER@example.org", "replacement-token"));
            var restarted = new GmailMailboxService(new ConfigurationBuilder().Build(), new FakeClients(), store);
            Check(!await restarted.ImportReviewedInboundAsync(workspaceId, firstId, "message-1",
                ReplyCategory.Interested, new TestFactory(options)), "reconnect and new contexts preserve deduplication");
            Check((await restarted.ReviewInboxAsync(workspaceId, factory)).Count == 0,
                "review excludes already-imported message even after prospect suppression");
            await RejectAsync(() => restarted.ImportReviewedInboundAsync(otherWorkspaceId, secondId,
                "message-2", ReplyCategory.Interested, factory), "cross-workspace import is rejected");
            await using (var db = factory.CreateDbContext())
            {
                db.Activities.Add(new ActivityEntry { ProspectId = firstId,
                    Kind = "Gmail inbound imported", Detail = "gmail:message-2" });
                await db.SaveChangesAsync();
            }
            Check(!await restarted.ImportReviewedInboundAsync(workspaceId, secondId, "message-2",
                ReplyCategory.Interested, factory), "legacy activity marker still blocks a duplicate on another prospect");
            await RejectConstraintAsync(factory, workspaceId, secondId, "message-1",
                "database rejects duplicate provider identity independently of service checks");
            await RejectConstraintAsync(factory, otherWorkspaceId, secondId, "message-3",
                "database rejects provider records with inconsistent prospect workspace");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RejectConstraintAsync(TestFactory factory, int workspaceId,
        int prospectId, string messageId, string scenario)
    {
        await using var db = factory.CreateDbContext();
        db.ProviderMessages.Add(new ProviderMessage { WorkspaceId = workspaceId, ProspectId = prospectId,
            MailboxEmail = "OWNER@example.org", MessageId = messageId, ThreadId = "thread-1",
            Message = new OutreachMessage { ProspectId = prospectId, Direction = "Inbound", IsSimulation = false } });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            Console.WriteLine("PASS: " + scenario);
            return;
        }
        throw new Exception("FAILED: " + scenario);
    }

    private static async Task RejectAsync(Func<Task> action, string scenario)
    {
        try { await action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: " + scenario); return; }
        throw new Exception("FAILED: " + scenario);
    }

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new Exception("FAILED: " + scenario);
        Console.WriteLine("PASS: " + scenario);
    }

    private sealed class TestFactory(DbContextOptions<OutreachDbContext> options) : IDbContextFactory<OutreachDbContext>
    {
        public OutreachDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeGmail());
    }

    private sealed class FakeGmail : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var uri = request.RequestUri!;
            object result;
            if (uri.AbsolutePath.EndsWith("/token")) result = new { access_token = "fixture-access" };
            else if (uri.AbsolutePath.EndsWith("/messages"))
                result = new { messages = new[] { new { id = "message-1", threadId = "thread-1" } } };
            else
            {
                result = new { id = uri.Segments.Last(), threadId = "thread-1", labelIds = new[] { "INBOX" },
                    internalDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    payload = new { mimeType = "text/plain", headers = new[] {
                        new { name = "From", value = "contact@example.org" },
                        new { name = "To", value = "owner@example.org" },
                        new { name = "In-Reply-To", value = "<sent-fixture@example.org>" },
                        new { name = "Subject", value = "Fixture reply" } },
                        body = new { data = GmailMailboxService.Base64Url(Encoding.UTF8.GetBytes("No thanks.")) } } };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
            };
        }
    }
}

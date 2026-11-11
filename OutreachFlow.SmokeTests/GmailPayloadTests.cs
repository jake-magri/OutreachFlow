using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services.Mailbox;

internal static class GmailPayloadTests
{
    public static async Task RunAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "outreachflow-gmail-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var options = new DbContextOptionsBuilder<OutreachDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(temp, "fixture.db") + ";Pooling=False").Options;
            var factory = new TestFactory(options);
            int workspaceId, prospectId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                var workspace = new Workspace { SenderEmail = "owner@example.org" };
                db.Workspaces.Add(workspace);
                await db.SaveChangesAsync();
                workspaceId = workspace.Id;
                var prospect = new Prospect { WorkspaceId = workspaceId, CompanyName = "Fixture",
                    ContactEmail = "contact@example.org", Attempts = 1, Status = ProspectStatus.AwaitingReply };
                db.Prospects.Add(prospect);
                await db.SaveChangesAsync();
                prospectId = prospect.Id;
                var sentAt = DateTime.UtcNow.AddMinutes(-5);
                var message = new OutreachMessage { ProspectId = prospectId, IsSimulation = false,
                    Direction = "Outbound", OccurredUtc = sentAt };
                db.OutboundDispatches.Add(new OutboundDispatch { ProspectId = prospectId, WorkspaceId = workspaceId,
                    State = "Sent", From = "owner@example.org", To = prospect.ContactEmail,
                    RfcMessageId = "<fixture-outbound@example.org>", ApprovedUtc = sentAt });
                db.ProviderMessages.Add(new ProviderMessage { ProspectId = prospectId, WorkspaceId = workspaceId,
                    Message = message, MailboxEmail = "owner@example.org",
                    MessageId = "sent-fixture", ThreadId = "thread-fixture" });
                await db.SaveChangesAsync();
            }
            var store = new GmailConnectionStore(Path.Combine(temp, "connections"),
                new EphemeralDataProtectionProvider());
            await store.SaveAsync(workspaceId, new GmailConnection("owner@example.org", "test-refresh"));
            var transport = new FakeTransport();
            var gmail = new GmailMailboxService(new ConfigurationBuilder().Build(), transport, store);

            Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Single().Body == "Genuine fixture text",
                "bounded read path includes valid provider-confirmed replies");

            transport.Mode = ResponseMode.InvalidHeader;
            Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Count == 0,
                "malformed provider header never becomes a reply");
            transport.Mode = ResponseMode.WrongReference;
            Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Count == 0,
                "unrelated reply reference is not correlated");
            transport.Mode = ResponseMode.BadBase64;
            Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Single().Body == "",
                "invalid base64 is not presented as genuine text");
            await RejectAsync(() => gmail.ImportReviewedInboundAsync(workspaceId, prospectId, "inbound-fixture",
                ReplyCategory.Interested, factory), "unreadable reply cannot update a prospect");
            transport.Mode = ResponseMode.InvalidUtf8;
            Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Single().Body == "",
                "invalid UTF-8 is not silently replaced and imported");

            foreach (var mode in new[] { ResponseMode.TooManyParts, ResponseMode.DeepMime, ResponseMode.EncodedBodyTooLarge })
            {
                transport.Mode = mode;
                Check((await gmail.ReviewInboxAsync(workspaceId, factory)).Single().Body == "",
                    mode + " cannot bypass MIME parsing limits");
            }
            transport.Mode = ResponseMode.HeaderOversize;
            await RejectAsync(() => gmail.ReviewInboxAsync(workspaceId, factory),
                "oversized Content-Length is rejected");
            transport.Mode = ResponseMode.StreamOversize;
            await RejectAsync(() => gmail.ReviewInboxAsync(workspaceId, factory),
                "oversized chunked/unknown-length JSON is rejected");
            transport.Mode = ResponseMode.DeepJson;
            await RejectJsonAsync(() => gmail.ReviewInboxAsync(workspaceId, factory),
                "excessive JSON nesting is rejected");
            transport.Mode = ResponseMode.MalformedJson;
            await RejectJsonAsync(() => gmail.ReviewInboxAsync(workspaceId, factory),
                "malformed provider JSON is rejected");

            transport.Mode = ResponseMode.HeaderOversize;
            await RejectAsync(() => gmail.SendAsync("fake-token", new OutboundDispatch {
                From = "owner@example.org", To = "contact@example.org",
                Subject = "Fixture", Body = "Fixture",
                RfcMessageId = "<outbound-fixture@example.org>"
            }, CancellationToken.None), "oversized Gmail send acknowledgement is rejected");

            transport.Mode = ResponseMode.Valid;
            Check(await gmail.ImportReviewedInboundAsync(workspaceId, prospectId, "inbound-fixture",
                ReplyCategory.Interested, factory), "valid provider-correlated reply imports once");
            Check(!await gmail.ImportReviewedInboundAsync(workspaceId, prospectId, "inbound-fixture",
                ReplyCategory.Interested, factory), "provider import remains idempotent");
            await using (var db = factory.CreateDbContext())
                Check(await db.Messages.CountAsync(m => m.Direction == "Inbound") == 1 &&
                    (await db.Prospects.FindAsync(prospectId))!.Status == ProspectStatus.WarmLead,
                    "invalid payload attempts did not change prospect state before valid import");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task RejectAsync(Func<Task> action, string scenario)
    {
        try { await action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS: " + scenario); return; }
        throw new Exception("FAILED: " + scenario);
    }

    private static async Task RejectJsonAsync(Func<Task> action, string scenario)
    {
        try { await action(); }
        catch (JsonException) { Console.WriteLine("PASS: " + scenario); return; }
        throw new Exception("FAILED: " + scenario);
    }

    private static void Check(bool ok, string scenario)
    {
        if (!ok) throw new Exception("FAILED: " + scenario);
        Console.WriteLine("PASS: " + scenario);
    }

    private sealed class TestFactory(DbContextOptions<OutreachDbContext> options) : IDbContextFactory<OutreachDbContext>
    {
        public OutreachDbContext CreateDbContext() => new(options);
    }

    private enum ResponseMode
    {
        Valid, InvalidHeader, WrongReference, BadBase64, InvalidUtf8, HeaderOversize,
        StreamOversize, DeepJson, MalformedJson, TooManyParts, DeepMime, EncodedBodyTooLarge
    }

    private sealed class FakeTransport : IHttpClientFactory
    {
        public ResponseMode Mode { get; set; }
        public HttpClient CreateClient(string name) => new(new FakeHandler(this));
    }

    private sealed class FakeHandler(FakeTransport owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/token"))
                return Task.FromResult(Response(new { access_token = "test-access" }));
            if (route.EndsWith("/messages/send"))
                return Task.FromResult(owner.Mode == ResponseMode.HeaderOversize
                    ? TooLarge(declaredLength: true) : Response(new { id = "sent", threadId = "thread-fixture" }));
            if (route.EndsWith("/messages"))
            {
                if (owner.Mode == ResponseMode.HeaderOversize)
                    return Task.FromResult(TooLarge(declaredLength: true));
                if (owner.Mode == ResponseMode.StreamOversize)
                    return Task.FromResult(TooLarge(declaredLength: false));
                if (owner.Mode == ResponseMode.DeepJson)
                    return Task.FromResult(Raw(new string('[', 35) + "0" + new string(']', 35)));
                if (owner.Mode == ResponseMode.MalformedJson)
                    return Task.FromResult(Raw("{broken"));
                return Task.FromResult(Response(new { messages = new[] { new { id = "inbound-fixture" } } }));
            }

            object body = owner.Mode switch
            {
                ResponseMode.BadBase64 => new { data = "invalid++base64!" },
                ResponseMode.InvalidUtf8 => new { data = GmailMailboxService.Base64Url(new byte[] { 0xFF, 0xFE }) },
                ResponseMode.EncodedBodyTooLarge => new { data = new string('A', 90001) },
                _ => new { data = GmailMailboxService.Base64Url(Encoding.UTF8.GetBytes("Genuine fixture text")) }
            };
            object[] headers =
            [
                new { name = "From", value = "contact@example.org" },
                owner.Mode == ResponseMode.InvalidHeader
                    ? new { name = "To", value = new { broken = true } }
                    : new { name = "To", value = (object)"owner@example.org" },
                new { name = "In-Reply-To", value = (object)(owner.Mode == ResponseMode.WrongReference
                    ? "<not-our-message@example.org>" : "<fixture-outbound@example.org>") }
            ];
            object payload = owner.Mode switch
            {
                ResponseMode.TooManyParts => new {
                    headers, mimeType = "multipart/mixed",
                    parts = Enumerable.Range(0, 21).Select(_ => new {
                        mimeType = "text/plain", body
                    }).ToArray()
                },
                ResponseMode.DeepMime => NestedPart(8, headers, body),
                _ => new { headers, mimeType = "text/plain", body }
            };
            return Task.FromResult(Response(new {
                id = "inbound-fixture", threadId = "thread-fixture", labelIds = new[] { "INBOX" },
                internalDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                payload
            }));
        }

        private static object NestedPart(int depth, object[] headers, object body) =>
            depth == 0
                ? new { headers, mimeType = "text/plain", body }
                : (object)new { headers, mimeType = "multipart/mixed",
                    parts = new[] { NestedPart(depth - 1, headers, body) } };

        private static HttpResponseMessage Response(object obj) => Raw(JsonSerializer.Serialize(obj));
        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        private static HttpResponseMessage TooLarge(bool declaredLength)
        {
            var response = Raw(new string('x', 600_000));
            if (!declaredLength) response.Content.Headers.ContentLength = null;
            return response;
        }
    }
}

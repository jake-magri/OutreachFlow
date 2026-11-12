using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OutreachFlow.Data;
using OutreachFlow.Models;
using OutreachFlow.Services;
using OutreachFlow.Services.Mailbox;

internal static class ApprovedSendTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "outreachflow-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new TestFactory(new DbContextOptionsBuilder<OutreachDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "business.db") + ";Pooling=False;Default Timeout=30").Options);
            var auth = new AuthFactory(new DbContextOptionsBuilder<AccountDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "identity.db") + ";Pooling=False").Options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.Workspaces.AddRange(new Workspace { Id = 1, SenderEmail = "owner@example.org", DailySendLimit = 100 }, new Workspace { Id = 2 });
                await db.SaveChangesAsync();
            }
            await using (var db = auth.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.AddRange(new ApplicationUser { Id = "owner", ActiveWorkspaceId = 1 }, new ApplicationUser { Id = "member", ActiveWorkspaceId = 1 });
                db.Memberships.AddRange(new WorkspaceMembership { UserId = "owner", WorkspaceId = 1, Role = "Owner" }, new WorkspaceMembership { UserId = "member", WorkspaceId = 1 });
                await db.SaveChangesAsync();
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gmail:HourlySendLimit"] = "100" }).Build();
            var now = new FixedClock();
            var transport = new FakeSender();
            var service = new ApprovedSendService(factory, auth, transport, config, now);
            var owner = Principal("owner");
            async Task<int> NewAsync()
            {
                await using var db = factory.CreateDbContext();
                var p = new Prospect { WorkspaceId = 1, CompanyName = "Fixture", ContactEmail = Guid.NewGuid().ToString("N") + "@example.org", Status = ProspectStatus.DraftReady,
                    ResearchOutcome = ResearchOutcome.NoProcessFound, ResearchNotes = "Controlled fixture", DraftSubject = "Inquiry — café", DraftBody = "Hello,\nThis is an approved fixture." };
                db.Prospects.Add(p); await db.SaveChangesAsync(); return p.Id;
            }
            async Task ChangeAsync(int id, Action<Prospect> edit)
            {
                await using var db = factory.CreateDbContext(); var p = await db.Prospects.FindAsync(id); edit(p!); await db.SaveChangesAsync();
            }
            var first = await NewAsync();
            var preview = await service.PreviewAsync(owner, first);
            await Reject(() => service.SendAsync(owner, preview, false), "send requires explicit owner approval");
            await Reject(() => service.PreviewAsync(Principal("member"), first), "workspace members cannot approve Gmail sends");
            await Reject(() => service.SendAsync(owner, preview with { WorkspaceId = 2 }, true), "stale workspace approval is rejected");
            await ChangeAsync(first, p => p.DraftBody += " changed");
            await Reject(() => service.SendAsync(owner, preview, true), "changed draft invalidates the approved snapshot");
            preview = await service.PreviewAsync(owner, first);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () => {
                try { await service.SendAsync(owner, preview, true); return true; }
                catch (InvalidOperationException) { return false; }
            })));
            Check(concurrent.Count(x => x) == 1 && transport.Sends == 1, "double click sends exactly one Gmail request");
            await using (var db = factory.CreateDbContext())
            {
                var d = await db.OutboundDispatches.SingleAsync();
                var m = await db.Messages.SingleAsync();
                var p = await db.Prospects.FindAsync(first);
                Check(d.State == "Sent" && d.ApprovedByUserId == "owner" && d.ResearchSnapshot.Contains("Controlled fixture") && d.PolicySnapshot.Contains("Manual only") &&
                    m.Body == preview.Body && !m.IsSimulation && (await db.ProviderMessages.SingleAsync()).MessageId == "sent-1" &&
                    p!.Status == ProspectStatus.AwaitingReply && p.AutomationPaused, "accepted Gmail send atomically records immutable approval, provider identity and manual handoff");
            }
            var restarted = new ApprovedSendService(factory, auth, transport, config, now);
            await Reject(() => restarted.SendAsync(owner, preview, true), "reload or retry cannot send an accepted inquiry again");
            var timeoutId = await NewAsync(); var timeoutPreview = await service.PreviewAsync(owner, timeoutId);
            transport.Failure = new TaskCanceledException();
            var uncertain = await service.SendAsync(owner, timeoutPreview, true);
            Check(uncertain.State == "Uncertain", "timeout persists uncertain delivery and stops retries");
            await Reject(() => restarted.SendAsync(owner, timeoutPreview, true), "timeout retry cannot create another Gmail request");
            var before = transport.Sends;
            await Reject(() => restarted.ReconcileAsync(owner, timeoutId), "empty Sent search cannot authorize a resend");
            transport.Found = new GmailDelivery("recovered", "thread-recovered");
            await ChangeAsync(timeoutId, p => p.Status = ProspectStatus.Suppressed);
            await restarted.ReconcileAsync(owner, timeoutId);
            await restarted.ReconcileAsync(owner, timeoutId);
            await using (var db = factory.CreateDbContext())
                Check(transport.Sends == before && (await db.Prospects.FindAsync(timeoutId))!.Status == ProspectStatus.Suppressed &&
                    await db.Messages.CountAsync(m => m.ProspectId == timeoutId) == 1, "reconciliation is idempotent and preserves suppression without resending");
            transport.Failure = new GmailRejectedException(429);
            var rejectedId = await NewAsync();
            var rejected = await service.SendAsync(owner, await service.PreviewAsync(owner, rejectedId), true);
            Check(rejected.State == "Rejected" && rejected.Result.Contains("429"), "Gmail quota rejection stays visible and blocks automatic retries");
            transport.Failure = null;
            var blocked = await NewAsync(); var blockedPreview = await service.PreviewAsync(owner, blocked);
            transport.Preflight = () => ChangeAsync(blocked, p => p.Status = ProspectStatus.Suppressed);
            before = transport.Sends;
            await Reject(() => service.SendAsync(owner, blockedPreview, true), "suppression during authorization preflight blocks dispatch");
            Check(transport.Sends == before, "suppressed inquiry never reaches provider"); transport.Preflight = null;
            foreach (var status in new[] { ProspectStatus.Closed, ProspectStatus.NeedsAction })
            {
                var id = await NewAsync(); await ChangeAsync(id, p => p.Status = status);
                await Reject(() => service.PreviewAsync(owner, id), "closed and unresolved prospects cannot approve sending: " + status);
            }
            var bad = await NewAsync(); await ChangeAsync(bad, p => p.ContactEmail = "victim@example.org\r\nBcc: extra@example.org");
            await Reject(() => service.PreviewAsync(owner, bad), "mail header injection is blocked");
            var revoked = await NewAsync(); var revokedPreview = await service.PreviewAsync(owner, revoked);
            transport.Preflight = () => throw new InvalidOperationException("Authorization revoked");
            await Reject(() => service.SendAsync(owner, revokedPreview, true), "revoked authorization blocks before outbox reservation"); transport.Preflight = null;
            now.Value = new DateTimeOffset(2026, 11, 14, 16, 0, 0, TimeSpan.Zero);
            await Reject(() => service.SendAsync(owner, revokedPreview, true), "weekend sending is blocked"); now.Reset();
            var limited = new ApprovedSendService(factory, auth, transport, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Gmail:HourlySendLimit"] = "1" }).Build(), now);
            await Reject(() => limited.SendAsync(owner, revokedPreview, true), "mailbox hourly quota includes uncertain and rejected attempts");
            var flow = new OutreachService(factory, new DryRunEmail(), new FixedWorkspaceAccess(1));
            await Reject(() => flow.RecordExternalInitialSendAsync(timeoutId), "manual send cannot bypass an existing Gmail dispatch");
            await Reject(() => flow.SimulateInitialSendAsync(timeoutId), "simulation cannot restart a real dispatch");
            await TransportAsync(root);
        }
        finally { await TestDataCleanup.DirectoryAsync(root); }
    }

    private static async Task TransportAsync(string root)
    {
        var store = new GmailConnectionStore(Path.Combine(root, "tokens"), new EphemeralDataProtectionProvider());
        await store.SaveAsync(1, new GmailConnection("owner@example.org", "fixture"));
        var handler = new FakeHttp();
        var gmail = new GmailMailboxService(new ConfigurationBuilder().Build(), handler, store);
        Check(!await gmail.SendApprovedAsync(1), "legacy read-only mailbox connections do not acquire sending permission");
        Check(!GmailMailboxService.HasScope(GmailMailboxService.SendScope + ".other", GmailMailboxService.SendScope), "granted scopes require exact scope names");
        var d = new OutboundDispatch { From = "owner@example.org", To = "test@example.org", Subject = "Inquiry — café", Body = "Hello\n世界", ApprovedUtc = DateTime.UtcNow, RfcMessageId = "<fixture@outreachflow.local>" };
        handler.Dispatch = d;
        var result = await gmail.SendAsync("fixture", d, default);
        Check(result.MessageId == "provider-1" && handler.Raw.Contains("To: test@example.org\r\n") && handler.Raw.Contains("Message-ID: <fixture@outreachflow.local>"), "Gmail adapter sends base64url MIME with exact recipient and stable Message-ID");
        Check(handler.Raw.EndsWith(Convert.ToBase64String(Encoding.UTF8.GetBytes(d.Body)) + "\r\n"), "UTF-8 MIME preserves the approved body");
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Reject(() => gmail.SendAsync("fixture", d, default), "provider 503 makes a single request with no hidden retry");
        handler.Status = HttpStatusCode.OK;
        Check((await gmail.FindSentAsync(1, d, default))?.MessageId == "provider-1", "Sent reconciliation validates message identity, headers and complete body");
        Check(handler.Posts == 2, "Gmail HTTP failure never invokes an implicit retry");
        if (OperatingSystem.IsWindows())
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Gmail:ClientId"] = "fixture", ["Gmail:ClientSecret"] = "fixture",
                ["Gmail:RedirectUri"] = "https://localhost:7117/mailbox/google/callback", ["Gmail:EnableSending"] = "true"
            }).Build();
            var configured = new GmailMailboxService(config, handler, store);
            Check(!configured.AuthorizationUrl("state", "challenge").Contains(Uri.EscapeDataString(GmailMailboxService.SendScope)) &&
                configured.AuthorizationUrl("state", "challenge", true).Contains(Uri.EscapeDataString(GmailMailboxService.SendScope)),
                "Gmail send scope is requested only by explicit upgrade");
            await Reject(() => configured.ConnectAsync(1, "fixture-code", GmailMailboxService.NewVerifier(), "owner@example.org", requestSend: true),
                "partial Google grant cannot activate sending");
            Check(!await configured.SendApprovedAsync(1), "failed scope upgrade preserves read-only authorization");
            handler.Scopes = GmailMailboxService.ReadScope + " " + GmailMailboxService.SendScope;
            await configured.ConnectAsync(1, "fixture-code", GmailMailboxService.NewVerifier(), "owner@example.org", requestSend: true);
            Check(await configured.SendApprovedAsync(1), "explicit complete Google grant enables send permission");
            handler.Scopes = GmailMailboxService.ReadScope;
            await Reject(() => configured.PrepareAsync(1, "owner@example.org", default), "refresh token scope downgrade blocks dispatch");
            await configured.ConnectAsync(1, "fixture-code", GmailMailboxService.NewVerifier(), "owner@example.org");
            Check(!await configured.SendApprovedAsync(1), "read-only reconnect clears previous send approval");
        }
        handler.WrongBody = true;
        await Reject(() => gmail.FindSentAsync(1, d, default), "Sent search mismatch cannot confirm an unrelated message");
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id) }, "test"));
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAILED: " + name); Console.WriteLine("PASS: " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or HttpRequestException) { Check(true, name); return; }
        throw new Exception("FAILED: accepted " + name);
    }
    private sealed class AuthFactory(DbContextOptions<AccountDbContext> options) : IDbContextFactory<AccountDbContext>
    { public AccountDbContext CreateDbContext() => new(options); }
    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Value = new(2026, 11, 11, 16, 0, 0, TimeSpan.Zero);
        public void Reset() => Value = new(2026, 11, 11, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Value;
    }
    private sealed class FakeSender : IGmailSender
    {
        public bool SendingAvailable => true;
        public int Sends;
        public Exception? Failure;
        public Func<Task>? Preflight;
        public GmailDelivery? Found;
        public async Task<string> PrepareAsync(int workspaceId, string sender, CancellationToken ct) { if (Preflight is not null) await Preflight(); return "fixture"; }
        public Task<GmailDelivery> SendAsync(string token, OutboundDispatch d, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Sends); if (Failure is not null) throw Failure;
            return Task.FromResult(new GmailDelivery("sent-" + n, "thread-" + n));
        }
        public Task<GmailDelivery?> FindSentAsync(int workspaceId, OutboundDispatch d, CancellationToken ct) => Task.FromResult(Found);
    }
    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Posts;
        public string Scopes = GmailMailboxService.ReadScope;
        public string Raw = "";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public OutboundDispatch Dispatch = new();
        public bool WrongBody;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token")) body = new { access_token = "fixture", refresh_token = "fixture-refresh", scope = Scopes };
            else if (path.EndsWith("/profile")) body = new { emailAddress = "owner@example.org" };
            else if (path.EndsWith("/send"))
            {
                Posts++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var raw = json.RootElement.GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/');
                Raw = Encoding.UTF8.GetString(Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '=')));
                return new(Status) { Content = new StringContent("{\"id\":\"provider-1\",\"threadId\":\"thread-1\"}") };
            }
            else if (path.EndsWith("/messages")) body = new { messages = new[] { new { id = "provider-1" } } };
            else body = new { id = "provider-1", threadId = "thread-1", labelIds = new[] { "SENT" }, payload = new {
                mimeType = "text/plain", headers = new[] { new { name = "From", value = Dispatch.From }, new { name = "To", value = Dispatch.To },
                    new { name = "Subject", value = Dispatch.Subject }, new { name = "Message-ID", value = Dispatch.RfcMessageId } },
                body = new { data = GmailMailboxService.Base64Url(Encoding.UTF8.GetBytes(WrongBody ? "unrelated" : Dispatch.Body)) } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }
    }
}

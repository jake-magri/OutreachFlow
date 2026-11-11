using System.Net;
using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Models;
using OutreachFlow.Data;

namespace OutreachFlow.Services.Mailbox;

public sealed record GmailConnection(string Email, string RefreshToken, bool SendApproved = false);
public sealed record GmailInboxItem(string MessageId, string Sender, string Subject,
    string Body, DateTime ReceivedUtc, int ProspectId, string CompanyName, string ThreadId);

/// <summary>A local workspace-owned mailbox record; its refresh token is encrypted at rest.</summary>
public sealed class GmailConnectionStore(string folder, IDataProtectionProvider keys)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDataProtector protector = keys.CreateProtector("OutreachFlow.GmailConnection.v1");

    private string FilePath(int workspaceId)
    {
        if (workspaceId < 1) throw new InvalidOperationException("Invalid workspace ID.");
        return Path.Combine(folder, $"workspace-{workspaceId}.protected");
    }

    public async Task<GmailConnection?> LoadAsync(int workspaceId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var path = FilePath(workspaceId);
            if (!File.Exists(path)) return null;
            // Never reset a damaged or undecryptable token file silently.
            var envelope = await File.ReadAllTextAsync(path, ct);
            var record = JsonSerializer.Deserialize<GmailConnection>(protector.Unprotect(envelope));
            if (record is null || string.IsNullOrWhiteSpace(record.Email) ||
                string.IsNullOrWhiteSpace(record.RefreshToken))
                throw new InvalidOperationException("Stored mailbox authorization is invalid; reconnect.");
            return record;
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Mailbox authorization cannot be decrypted on this machine. Reconnect.");
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(int workspaceId, GmailConnection connection, CancellationToken ct = default)
    {
        if (!MailAddress.TryCreate(connection.Email, out var parsed) ||
            parsed.Address != connection.Email ||
            string.IsNullOrWhiteSpace(connection.RefreshToken))
            throw new InvalidOperationException("Invalid connected mailbox.");
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(folder);
            var path = FilePath(workspaceId);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp,
                    protector.Protect(JsonSerializer.Serialize(connection)), ct);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(int workspaceId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var path = FilePath(workspaceId);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { gate.Release(); }
    }
}

/// <summary>Never trust a workspace member to connect a mailbox to a business they do not own.</summary>
public static class MailboxOwner
{
    public static async Task<int> RequireAsync(System.Security.Claims.ClaimsPrincipal user,
        AccountDbContext accounts, CancellationToken ct = default)
    {
        var userId = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId)) throw new UnauthorizedAccessException("Sign in first.");
        var active = await accounts.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => u.ActiveWorkspaceId).SingleOrDefaultAsync(ct);
        if (active is null || !await accounts.Memberships.AsNoTracking().AnyAsync(
            m => m.UserId == userId && m.WorkspaceId == active.Value && m.Role == "Owner", ct))
            throw new UnauthorizedAccessException("Only a workspace owner can connect or read a mailbox.");
        return active.Value;
    }
}

public sealed partial class GmailMailboxService(IConfiguration config, IHttpClientFactory clients,
    GmailConnectionStore store)
{
    public const string SendScope = "https://www.googleapis.com/auth/gmail.send";
    public const string ReadScope = "https://www.googleapis.com/auth/gmail.readonly";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GmailEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private const int MaxGmailJsonBytes = 512 * 1024;
    private const int MaxJsonDepth = 32;

    public bool IsConfigured =>
        OperatingSystem.IsWindows() &&
        !string.IsNullOrWhiteSpace(config["Gmail:ClientId"]) &&
        !string.IsNullOrWhiteSpace(config["Gmail:ClientSecret"]) &&
        IsAllowedRedirectUri(config["Gmail:RedirectUri"]);

    public static bool IsAllowedRedirectUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))) &&
        uri.AbsolutePath == "/mailbox/google/callback" &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string AuthorizationUrl(string state, string challenge, bool requestSend = false)
    {
        if (!IsConfigured) throw new InvalidOperationException("Gmail OAuth client and local callback are not configured.");
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config["Gmail:ClientId"]!,
            ["redirect_uri"] = config["Gmail:RedirectUri"]!,
            ["response_type"] = "code",
            ["scope"] = requestSend ? ReadScope + " " + SendScope : ReadScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthEndpoint + "?" + string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        parameters["client_id"] = config["Gmail:ClientId"] ?? "";
        parameters["client_secret"] = config["Gmail:ClientSecret"] ?? "";
        using var response = await clients.CreateClient("GmailOAuth").PostAsync(TokenEndpoint,
            new FormUrlEncodedContent(parameters), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google authorization failed; reconnect or review OAuth settings.");
        return await BoundedJsonAsync(response, ct);
    }

    public async Task ConnectAsync(int workspaceId, string code, string verifier,
        string expectedEmail, CancellationToken ct = default, bool requestSend = false)
    {
        if (!IsConfigured || code.Length is < 6 or > 2048 ||
            verifier.Length is < 43 or > 128)
            throw new InvalidOperationException("Invalid Gmail authorization state.");
        using var tokens = await PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = config["Gmail:RedirectUri"]!
        }, ct);
        var root = tokens.RootElement;
        if (!root.TryGetProperty("refresh_token", out var refresh) ||
            string.IsNullOrWhiteSpace(refresh.GetString()) ||
            !root.TryGetProperty("access_token", out var access))
            throw new InvalidOperationException("Google did not return offline access. Reconnect with consent.");
        // Do not trust the account name selected at Google until Gmail confirms it.
        var mailbox = await ReadProfileEmailAsync(access.GetString()!, ct);
        if (!mailbox.Equals(expectedEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connected Gmail account does not match this workspace's sender email.");
        var scopes = root.TryGetProperty("scope", out var granted) ? granted.GetString() ?? "" : "";
        if (!HasScope(scopes, ReadScope) || (requestSend && !HasScope(scopes, SendScope)))
            throw new InvalidOperationException("Required Gmail permission was not granted. Existing connection retained.");
        await store.SaveAsync(workspaceId, new GmailConnection(mailbox, refresh.GetString()!, requestSend), ct);
    }

    private async Task<string> ReadProfileEmailAsync(string accessToken, CancellationToken ct)
    {
        using var profile = await GetAsync("profile", accessToken, ct);
        var email = profile.RootElement.GetProperty("emailAddress").GetString() ?? "";
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new InvalidOperationException("Google returned an invalid mailbox identity.");
        return email;
    }

    private async Task<JsonDocument> GetAsync(string route, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GmailEndpoint + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await clients.CreateClient("GmailApi").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Gmail read failed. The authorization may have expired or been revoked.");
        return await BoundedJsonAsync(response, ct);
    }

    private static async Task<JsonDocument> BoundedJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxGmailJsonBytes)
            throw new InvalidOperationException("Gmail response exceeds the permitted size. Review manually.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + count > MaxGmailJsonBytes)
                throw new InvalidOperationException("Gmail response exceeds the permitted size. Review manually.");
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions
        {
            MaxDepth = MaxJsonDepth, CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        }, ct);
    }

    private async Task<string> TokenAsync(GmailConnection connection, CancellationToken ct)
    {
        using var response = await PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = connection.RefreshToken
        }, ct);
        return response.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google did not return a usable access token.");
    }

    public async Task<string?> ConnectedEmailAsync(int workspaceId, CancellationToken ct = default)
        => (await store.LoadAsync(workspaceId, ct))?.Email;

    public Task DisconnectAsync(int workspaceId, CancellationToken ct = default) =>
        store.DisconnectAsync(workspaceId, ct);

    public static GmailInboxItem? ParseInbound(string id, JsonElement message, int prospectId,
        string companyName, string expectedFrom)
    {
        // A malformed or non-email response can never authorize a new contact.
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array || headers.GetArrayLength() > 100) return null;
        string Header(string name)
        {
            foreach (var header in headers.EnumerateArray())
                if (header.TryGetProperty("name", out var n) &&
                    n.GetString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                    header.TryGetProperty("value", out var value))
                    return value.GetString() ?? "";
            return "";
        }
        if (!message.TryGetProperty("id", out var sourceId) ||
            sourceId.ValueKind != JsonValueKind.String || sourceId.GetString() != id ||
            !message.TryGetProperty("threadId", out var thread) ||
            thread.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(thread.GetString()) || thread.GetString()!.Length > 100)
            return null;
        var from = Header("From");
        if (!MailAddress.TryCreate(from, out var sender) ||
            !sender.Address.Equals(expectedFrom, StringComparison.OrdinalIgnoreCase))
            return null;
        var subject = Header("Subject");
        if (subject.Length > 4000) return null;
        var body = PlainBody(payload); // Never promote Gmail snippets to actual mail content.
        if (body.Length > 4000) body = body[..4000];
        if (!message.TryGetProperty("internalDate", out var stamp) ||
            !long.TryParse(stamp.ValueKind == JsonValueKind.String ? stamp.GetString() :
                stamp.GetRawText(), out var millis)) return null;
        DateTime received;
        try { received = DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
        return new GmailInboxItem(id, sender.Address, subject[..Math.Min(subject.Length, 300)],
            body, received, prospectId, companyName, thread.GetString()!);
    }

    private static string PlainBody(JsonElement payload) => PlainBody(payload, 0, new int[] { 0 });

    private static string PlainBody(JsonElement payload, int depth, int[] visited)
    {
        if (depth > 6 || ++visited[0] > 50 || payload.ValueKind != JsonValueKind.Object)
            return "";
        if (payload.TryGetProperty("mimeType", out var mime) &&
            mime.ValueKind == JsonValueKind.String &&
            mime.GetString()?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true &&
            payload.TryGetProperty("body", out var body) &&
            body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("data", out var raw) &&
            raw.ValueKind == JsonValueKind.String)
            return DecodeBody(raw.GetString() ?? "");
        if (payload.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            if (parts.GetArrayLength() > 20) return "";
            foreach (var part in parts.EnumerateArray())
            {
                var found = PlainBody(part, depth + 1, visited);
                if (found.Length > 0) return found;
            }
        }
        return "";
    }

    private static string DecodeBody(string data)
    {
        if (data.Length > 90000) return "";
        try
        {
            var raw = data.Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch (FormatException) { return ""; }
    }

    /// <summary>
    /// Owner-confirmed import of a single Gmail message. Freshly refetches the source,
    /// revalidates its exact sender and business relationship, then records an actual
    /// reply and audited provider ID in one database transaction. NEVER sends email.
    /// </summary>
    public async Task<bool> ImportReviewedInboundAsync(int workspaceId, int prospectId,
        string messageId, ReplyCategory category,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct = default)
    {
        if (messageId.Length is < 1 or > 100 || !messageId.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("Invalid Gmail message identifier.");
        if (!Enum.IsDefined(category))
            throw new InvalidOperationException("Invalid reply classification.");
        var connection = await store.LoadAsync(workspaceId, ct) ??
            throw new InvalidOperationException("Connect Gmail before importing replies.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleAsync(w => w.Id == workspaceId, ct);
        if (!connection.Email.Equals(workspace.SenderEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The connected mailbox no longer matches the workspace sender.");
        var token = await TokenAsync(connection, ct);
        using var source = await GetAsync("messages/" + Uri.EscapeDataString(messageId) +
            "?format=full", token, ct);
        if (!source.RootElement.TryGetProperty("labelIds", out var labels) ||
            labels.ValueKind != JsonValueKind.Array ||
            !labels.EnumerateArray().Any(l => l.GetString() == "INBOX"))
            throw new InvalidOperationException("Selected Gmail message is not currently in the inbox.");

        // Acquire SQLite's write reservation before reading mutable eligibility or
        // duplicate state. Network access has completed before this short transaction.
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);
        await using var tx = await db.Database.UseTransactionAsync(transaction, ct);
        workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId, ct);
        var currentConnection = await store.LoadAsync(workspaceId, ct);
        if (currentConnection is null ||
            !currentConnection.Email.Equals(connection.Email, StringComparison.OrdinalIgnoreCase) ||
            !connection.Email.Equals(workspace.SenderEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mailbox changed during review. Review again.");
        var marker = "gmail:" + messageId;
        if (await db.ProviderMessages.AnyAsync(m => m.WorkspaceId == workspaceId &&
                m.Provider == "gmail" && m.MailboxEmail == connection.Email && m.MessageId == messageId, ct) ||
            await LegacyImportedAsync(db, workspaceId, marker, ct))
            return false;

        var prospect = await db.Prospects.SingleOrDefaultAsync(p =>
            p.WorkspaceId == workspaceId && p.Id == prospectId, ct);
        if (prospect is null || prospect.Attempts < 1 ||
            prospect.Status is ProspectStatus.Suppressed or ProspectStatus.Closed ||
            string.IsNullOrWhiteSpace(prospect.ContactEmail))
            throw new InvalidOperationException("An open, previously contacted prospect is required.");
        var anchors = await SentAnchorsAsync(db, workspaceId, connection.Email, ct);
        var threadMap = GmailReplyCorrelation.UniqueThreads(anchors);
        var threadId = source.RootElement.TryGetProperty("threadId", out var sourceThread) &&
            sourceThread.ValueKind == JsonValueKind.String ? sourceThread.GetString() : null;
        if (threadId is null || !threadMap.TryGetValue(threadId, out var anchor) ||
            anchor.ProspectId != prospectId)
            throw new InvalidOperationException("Reply has no unique provider-confirmed sent conversation. Review in Gmail.");
        var item = GmailReplyCorrelation.Match(messageId, source.RootElement, anchor) ??
            throw new InvalidOperationException("Gmail thread, sender, recipient or reply references do not match the sent inquiry.");
        if (string.IsNullOrWhiteSpace(item.Body))
            throw new InvalidOperationException("No readable message text was found; review in Gmail manually.");

        var importedMessage = new OutreachMessage
        {
            ProspectId = prospectId, Direction = "Inbound",
            Classification = "Gmail reviewed: " + category,
            Subject = item.Subject, Body = item.Body,
            IsSimulation = false, OccurredUtc = item.ReceivedUtc
        };
        db.ProviderMessages.Add(new ProviderMessage
        {
            WorkspaceId = workspaceId, ProspectId = prospectId, Message = importedMessage,
            MailboxEmail = connection.Email.Trim().ToLowerInvariant(),
            MessageId = item.MessageId, ThreadId = item.ThreadId
        });
        db.Activities.Add(new ActivityEntry
        {
            ProspectId = prospectId, Kind = "Gmail inbound imported",
            Detail = marker, OccurredUtc = DateTime.UtcNow
        });
        prospect.Status = category switch
        {
            ReplyCategory.Interested => ProspectStatus.WarmLead,
            ReplyCategory.ApplicationInstructions => ProspectStatus.ApplicationTask,
            ReplyCategory.Declined => ProspectStatus.Suppressed,
            _ => ProspectStatus.NeedsAction
        };
        prospect.AutomationPaused = true;
        prospect.NextStepDueUtc = null;
        prospect.NextAction = category switch
        {
            ReplyCategory.Interested => "Personally follow up with interested contact",
            ReplyCategory.ApplicationInstructions => "Complete official supplier application",
            ReplyCategory.Declined => "",
            _ => "Review Gmail reply and decide next step"
        };
        prospect.NextActionDate = null;
        prospect.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static Task<bool> LegacyImportedAsync(OutreachDbContext db, int workspaceId,
        string marker, CancellationToken ct) =>
        (from activity in db.Activities
         join prospect in db.Prospects on activity.ProspectId equals prospect.Id
         where prospect.WorkspaceId == workspaceId && activity.Kind == "Gmail inbound imported" &&
             activity.Detail == marker &&
             !db.ProviderMessages.Any(m => m.ProspectId == activity.ProspectId &&
                 "gmail:" + m.MessageId == activity.Detail)
         select activity.Id).AnyAsync(ct);

    private static async Task<IReadOnlyList<GmailSentAnchor>> SentAnchorsAsync(
        OutreachDbContext db, int workspaceId, string mailbox, CancellationToken ct)
    {
        // Only an actual Gmail accepted initial dispatch is a trustworthy anchor.
        // Legacy owner-attested external sends lack provider identity and remain manual.
        var sent = await (from d in db.OutboundDispatches.AsNoTracking()
                          join p in db.Prospects.AsNoTracking() on d.ProspectId equals p.Id
                          join m in db.ProviderMessages.AsNoTracking() on d.ProspectId equals m.ProspectId
                          join history in db.Messages.AsNoTracking() on m.OutreachMessageId equals history.Id
                          where d.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId &&
                              m.WorkspaceId == workspaceId && d.State == "Sent" &&
                              m.Provider == "gmail" && m.MailboxEmail == mailbox &&
                              history.Direction == "Outbound" && !history.IsSimulation &&
                              p.Status != ProspectStatus.Closed && p.Status != ProspectStatus.Suppressed
                          select new { d, p, m }).ToListAsync(ct);
        return sent.Where(x => x.d.From.Equals(mailbox, StringComparison.OrdinalIgnoreCase) &&
                    x.d.To.Equals(x.p.ContactEmail, StringComparison.OrdinalIgnoreCase))
            .Select(x => new GmailSentAnchor(x.p.Id, x.p.CompanyName, x.p.ContactEmail,
                x.d.From, x.m.ThreadId, x.d.RfcMessageId, x.d.ApprovedUtc)).ToArray();
    }

    /// <summary>
    /// Reviews only provider-confirmed, unambiguous sent-conversation replies.
    /// Ambiguous/legacy sender-only mail remains in Gmail for manual investigation.
    /// </summary>
    public async Task<IReadOnlyList<GmailInboxItem>> ReviewInboxAsync(int workspaceId,
        IDbContextFactory<OutreachDbContext> factory, CancellationToken ct = default)
    {
        var connection = await store.LoadAsync(workspaceId, ct) ??
            throw new InvalidOperationException("Connect Gmail before reviewing the inbox.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleAsync(w => w.Id == workspaceId, ct);
        if (!connection.Email.Equals(workspace.SenderEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The connected mailbox no longer matches the workspace sender.");
        var anchors = GmailReplyCorrelation.UniqueThreads(
            await SentAnchorsAsync(db, workspaceId, connection.Email, ct));
        if (anchors.Count == 0) return [];
        var token = await TokenAsync(connection, ct);
        using var list = await GetAsync("messages?labelIds=INBOX&maxResults=20&q=" +
            Uri.EscapeDataString("newer_than:30d"), token, ct);
        if (!list.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array) return [];
        // Legacy audit markers lack mailbox identity. Preserve their conservative
        // workspace-wide dedupe until an operator can verify that provenance.
        var legacySeen = await (from activity in db.Activities.AsNoTracking()
            join prospect in db.Prospects on activity.ProspectId equals prospect.Id
            where prospect.WorkspaceId == workspaceId && activity.Kind == "Gmail inbound imported" &&
                !db.ProviderMessages.Any(m => m.ProspectId == activity.ProspectId &&
                    "gmail:" + m.MessageId == activity.Detail)
            select activity.Detail).ToListAsync(ct);
        var seen = await db.ProviderMessages.AsNoTracking().Where(m => m.WorkspaceId == workspaceId &&
            m.Provider == "gmail" && m.MailboxEmail == connection.Email)
            .Select(m => m.MessageId).ToListAsync(ct);
        var processed = legacySeen.Concat(seen.Select(id => "gmail:" + id)).ToHashSet(StringComparer.Ordinal);
        var found = new List<GmailInboxItem>();
        foreach (var item in messages.EnumerateArray().Take(20))
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String) continue;
            var id = idElement.GetString() ?? "";
            if (id.Length is < 1 or > 100 ||
                !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                processed.Contains("gmail:" + id)) continue;
            using var message = await GetAsync("messages/" + Uri.EscapeDataString(id) +
                "?format=full", token, ct);
            var payload = message.RootElement;
            var thread = payload.TryGetProperty("threadId", out var threadElement) &&
                threadElement.ValueKind == JsonValueKind.String ? threadElement.GetString() : null;
            if (thread is not null && anchors.TryGetValue(thread, out var anchor))
            {
                var candidate = GmailReplyCorrelation.Match(id, payload, anchor);
                if (candidate is not null) found.Add(candidate);
            }
        }
        return found.OrderByDescending(x => x.ReceivedUtc).ToArray();
    }
}

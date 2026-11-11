using System.Net;
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

public sealed record GmailConnection(string Email, string RefreshToken);
public sealed record GmailInboxItem(string MessageId, string Sender, string Subject,
    string Body, DateTime ReceivedUtc, int ProspectId, string CompanyName);

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

public sealed class GmailMailboxService(IConfiguration config, IHttpClientFactory clients,
    GmailConnectionStore store)
{
    public const string ReadScope = "https://www.googleapis.com/auth/gmail.readonly";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GmailEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config["Gmail:ClientId"]) &&
        !string.IsNullOrWhiteSpace(config["Gmail:ClientSecret"]) &&
        IsAllowedRedirectUri(config["Gmail:RedirectUri"]);

    public static bool IsAllowedRedirectUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && IPAddress.TryParse(uri.Host, out var ip) &&
        IPAddress.IsLoopback(ip) && uri.AbsolutePath == "/mailbox/google/callback" &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string AuthorizationUrl(string state, string challenge)
    {
        if (!IsConfigured) throw new InvalidOperationException("Gmail OAuth client and local callback are not configured.");
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config["Gmail:ClientId"]!,
            ["redirect_uri"] = config["Gmail:RedirectUri"]!,
            ["response_type"] = "code",
            ["scope"] = ReadScope,
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
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task ConnectAsync(int workspaceId, string code, string verifier,
        string expectedEmail, CancellationToken ct = default)
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
        await store.SaveAsync(workspaceId, new GmailConnection(mailbox, refresh.GetString()!), ct);
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
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
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
        if (!message.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array) return null;
        string Header(string name)
        {
            foreach (var header in headers.EnumerateArray())
                if (header.TryGetProperty("name", out var n) &&
                    n.GetString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                    header.TryGetProperty("value", out var value))
                    return value.GetString() ?? "";
            return "";
        }
        var from = Header("From");
        if (!MailAddress.TryCreate(from, out var sender) ||
            !sender.Address.Equals(expectedFrom, StringComparison.OrdinalIgnoreCase))
            return null;
        var subject = Header("Subject");
        var body = PlainBody(payload);
        if (string.IsNullOrWhiteSpace(body) &&
            message.TryGetProperty("snippet", out var snippet))
            body = snippet.GetString() ?? "";
        if (body.Length > 4000) body = body[..4000];
        var received = DateTime.UtcNow;
        if (message.TryGetProperty("internalDate", out var stamp) &&
            long.TryParse(stamp.ValueKind == JsonValueKind.String ? stamp.GetString() : stamp.GetRawText(), out var millis))
        {
            try { received = DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return new GmailInboxItem(id, sender.Address, subject[..Math.Min(subject.Length, 300)],
            body, received, prospectId, companyName);
    }

    private static string PlainBody(JsonElement payload)
    {
        if (payload.TryGetProperty("mimeType", out var mime) &&
            mime.GetString()?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true &&
            payload.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var raw))
            return DecodeBody(raw.GetString() ?? "");
        if (payload.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray().Take(20))
            {
                var found = PlainBody(part);
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
    /// Only returns inbox entries whose From address matches an existing business
    /// recipient with a recorded REAL outbound email. Never modifies Gmail labels.
    /// Returned previews live in the user's interactive session, not in SQLite.
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
        var all = await db.Prospects.AsNoTracking().Where(p =>
            p.WorkspaceId == workspaceId && p.Attempts > 0 &&
            p.Status != ProspectStatus.Closed &&
            p.Status != ProspectStatus.Suppressed &&
            p.ContactEmail != "").ToListAsync(ct);
        var eligibleIds = all.Select(p => p.Id).ToArray();
        var sent = await db.Messages.AsNoTracking().Where(m =>
            !m.IsSimulation && m.Direction == "Outbound" &&
            eligibleIds.Contains(m.ProspectId))
            .Select(m => m.ProspectId).Distinct().ToListAsync(ct);
        var people = all.Where(p => sent.Contains(p.Id))
            .GroupBy(p => p.ContactEmail, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(),
                StringComparer.OrdinalIgnoreCase);
        if (people.Count == 0) return [];
        var token = await TokenAsync(connection, ct);
        using var list = await GetAsync("messages?labelIds=INBOX&maxResults=20&q=" +
            Uri.EscapeDataString("newer_than:30d"), token, ct);
        if (!list.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array) return [];
        var seen = await db.Activities.AsNoTracking()
            .Where(a => a.Kind == "Gmail inbound imported")
            .Select(a => a.Detail).ToListAsync(ct);
        var processed = seen.ToHashSet(StringComparer.Ordinal);
        var found = new List<GmailInboxItem>();
        foreach (var item in messages.EnumerateArray().Take(20))
        {
            var id = item.GetProperty("id").GetString() ?? "";
            if (id.Length == 0 || id.Length > 100 || processed.Contains("gmail:" + id)) continue;
            using var message = await GetAsync("messages/" + Uri.EscapeDataString(id) +
                "?format=full", token, ct);
            var payload = message.RootElement;
            foreach (var prospect in people.Values)
            {
                var candidate = ParseInbound(id, payload, prospect.Id, prospect.CompanyName,
                    prospect.ContactEmail);
                if (candidate is not null)
                {
                    found.Add(candidate);
                    break;
                }
            }
        }
        return found.OrderByDescending(x => x.ReceivedUtc).ToArray();
    }
}

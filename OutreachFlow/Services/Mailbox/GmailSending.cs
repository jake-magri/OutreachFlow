using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OutreachFlow.Models;

namespace OutreachFlow.Services.Mailbox;

public sealed record GmailDelivery(string MessageId, string ThreadId);
public sealed class GmailRejectedException(int status) : Exception($"Gmail rejected the send (HTTP {status}). Review mailbox permissions and quota. This pilot will not retry automatically.");

public interface IGmailSender
{
    bool SendingAvailable { get; }
    Task<string> PrepareAsync(int workspaceId, string sender, CancellationToken ct);
    Task<GmailDelivery> SendAsync(string token, OutboundDispatch dispatch, CancellationToken ct);
    Task<GmailDelivery?> FindSentAsync(int workspaceId, OutboundDispatch dispatch, CancellationToken ct);
}

public sealed partial class GmailMailboxService : IGmailSender
{
    public bool SendingAvailable => IsConfigured && config.GetValue<bool>("Gmail:EnableSending");
    public static bool HasScope(string scopes, string scope) => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);
    public async Task<bool> SendApprovedAsync(int workspaceId) => (await store.LoadAsync(workspaceId))?.SendApproved == true;

    public async Task<string> PrepareAsync(int workspaceId, string sender, CancellationToken ct)
    {
        if (!SendingAvailable) throw new InvalidOperationException("Controlled Gmail sending is disabled in local configuration.");
        var connection = await store.LoadAsync(workspaceId, ct);
        if (connection is null || !connection.SendApproved || !connection.Email.Equals(sender, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connect this workspace's sender mailbox and explicitly grant Gmail sending permission first.");
        using var tokens = await PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = connection.RefreshToken }, ct);
        if (tokens.RootElement.TryGetProperty("scope", out var refreshedScopes) &&
            (!HasScope(refreshedScopes.GetString() ?? "", SendScope) || !HasScope(refreshedScopes.GetString() ?? "", ReadScope)))
            throw new InvalidOperationException("Google no longer grants the required read and send scopes. Reconnect with consent.");
        var token = tokens.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google authorization must be renewed.");
        if (!(await ReadProfileEmailAsync(token, ct)).Equals(sender, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google mailbox identity no longer matches the approved sender.");
        var current = await store.LoadAsync(workspaceId, ct);
        if (current != connection) throw new InvalidOperationException("Mailbox authorization changed. Review again.");
        return token;
    }

    public static string Mime(OutboundDispatch d)
    {
        // All header values are validated before a durable dispatch is created.
        var subjectLines = new List<string>();
        var chunk = new StringBuilder();
        foreach (var rune in d.Subject.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(chunk.ToString()) + rune.Utf8SequenceLength > 36)
            {
                subjectLines.Add("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) + "?=");
                chunk.Clear();
            }
            chunk.Append(rune.ToString());
        }
        if (chunk.Length > 0) subjectLines.Add("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) + "?=");
        return $"From: {d.From}\r\nTo: {d.To}\r\nSubject: {string.Join("\r\n ", subjectLines)}\r\n" +
            $"Message-ID: {d.RfcMessageId}\r\nDate: {d.ApprovedUtc.ToString("r")}\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(d.Body), Base64FormattingOptions.InsertLineBreaks) + "\r\n";
    }

    public async Task<GmailDelivery> SendAsync(string token, OutboundDispatch dispatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GmailEndpoint + "messages/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { raw = Base64Url(Encoding.UTF8.GetBytes(Mime(dispatch))) });
        // Dedicated client has no retry handler. A successful response is acceptance, not delivery to an inbox.
        using var response = await clients.CreateClient("GmailApi").SendAsync(request, ct);
        if ((int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode != 408)
            throw new GmailRejectedException((int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Delivery(json.RootElement);
    }

    private static GmailDelivery Delivery(JsonElement message)
    {
        var id = message.GetProperty("id").GetString();
        var thread = message.GetProperty("threadId").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(thread) || id.Length > 100 || thread.Length > 100)
            throw new InvalidOperationException("Gmail returned incomplete message identity.");
        return new(id, thread);
    }

    public async Task<GmailDelivery?> FindSentAsync(int workspaceId, OutboundDispatch dispatch, CancellationToken ct)
    {
        // Reconciliation only reads; it remains available if the local send toggle is off.
        var connection = await store.LoadAsync(workspaceId, ct) ?? throw new InvalidOperationException("Reconnect Gmail to check Sent mail.");
        if (!connection.Email.Equals(dispatch.From, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connect the original sender mailbox to reconcile this send.");
        var token = await TokenAsync(connection, ct);
        if (!(await ReadProfileEmailAsync(token, ct)).Equals(dispatch.From, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connected mailbox identity changed.");
        using var list = await GetAsync("messages?maxResults=2&q=" + Uri.EscapeDataString("in:sent rfc822msgid:" + dispatch.RfcMessageId.Trim('<', '>')), token, ct);
        if (!list.RootElement.TryGetProperty("messages", out var items) || items.GetArrayLength() == 0) return null;
        if (items.GetArrayLength() != 1 || list.RootElement.TryGetProperty("nextPageToken", out _))
            throw new InvalidOperationException("Multiple Sent messages match. Manual investigation is required.");
        var id = items[0].GetProperty("id").GetString()!;
        using var full = await GetAsync("messages/" + Uri.EscapeDataString(id) + "?format=full", token, ct);
        var root = full.RootElement;
        var payload = root.GetProperty("payload");
        string Header(string name) => payload.GetProperty("headers").EnumerateArray()
            .Where(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.GetProperty("value").GetString() ?? "").SingleOrDefault() ?? "";
        if (!root.GetProperty("labelIds").EnumerateArray().Any(l => l.GetString() == "SENT") ||
            root.GetProperty("id").GetString() != id || Header("Message-ID") != dispatch.RfcMessageId ||
            !Header("From").Equals(dispatch.From, StringComparison.OrdinalIgnoreCase) ||
            !Header("To").Equals(dispatch.To, StringComparison.OrdinalIgnoreCase) ||
            Header("Subject") != dispatch.Subject || PlainBody(payload) != dispatch.Body)
            throw new InvalidOperationException("Sent message does not exactly match the approved inquiry. Manual investigation is required.");
        return Delivery(root);
    }
}

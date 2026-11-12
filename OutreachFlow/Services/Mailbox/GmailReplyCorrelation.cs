using System.Net.Mail;
using System.Text.Json;

namespace OutreachFlow.Services.Mailbox;

/// <summary>A provider-confirmed outbound inquiry belonging to exactly one workspace/prospect.</summary>
public sealed record GmailSentAnchor(int ProspectId, string CompanyName, string ContactEmail,
    string MailboxEmail, string ThreadId, string RfcMessageId, DateTime ApprovedUtc, IReadOnlyList<string>? FollowupMessageIds = null);

/// <summary>
/// Conservative Gmail-thread evidence for owner-reviewed inbound messages.
/// A Gmail thread ID alone, or a matching From header alone, cannot prove a reply.
/// This deliberately does not authenticate the human behind the From header.
/// </summary>
public static class GmailReplyCorrelation
{
    public static IReadOnlyDictionary<string, GmailSentAnchor> UniqueThreads(IEnumerable<GmailSentAnchor> anchors) =>
        anchors.Where(a => !string.IsNullOrWhiteSpace(a.ThreadId))
            .GroupBy(a => a.ThreadId, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    public static GmailInboxItem? Match(string id, JsonElement message, GmailSentAnchor anchor)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("threadId", out var thread) ||
            thread.ValueKind != JsonValueKind.String || thread.GetString() != anchor.ThreadId ||
            !message.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array || headers.GetArrayLength() > 100 ||
            !ValidHeader(headers, "From", out _) ||
            !ValidHeader(headers, "To", out var to, required: false) ||
            !ValidHeader(headers, "Cc", out var cc, required: false) ||
            !ValidHeader(headers, "In-Reply-To", out var replyTo, required: false) ||
            !ValidHeader(headers, "References", out var references, required: false))
            return null;

        if (!ContainsRecipient(to, anchor.MailboxEmail) && !ContainsRecipient(cc, anchor.MailboxEmail))
            return null;
        if (!new[] { anchor.RfcMessageId }.Concat(anchor.FollowupMessageIds ?? [])
                .Any(id => ContainsExactId(replyTo, id) || ContainsExactId(references, id)))
            return null;

        var parsed = GmailMailboxService.ParseInbound(id, message, anchor.ProspectId,
            anchor.CompanyName, anchor.ContactEmail);
        return parsed is not null && parsed.ReceivedUtc >= anchor.ApprovedUtc.AddMinutes(-5)
            ? parsed : null;
    }

    private static bool ValidHeader(JsonElement headers, string name, out string value,
        bool required = true)
    {
        value = "";
        var found = false;
        foreach (var header in headers.EnumerateArray())
        {
            if (header.ValueKind != JsonValueKind.Object ||
                !header.TryGetProperty("name", out var n) ||
                n.ValueKind != JsonValueKind.String ||
                !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found || !header.TryGetProperty("value", out var v) ||
                v.ValueKind != JsonValueKind.String)
                return false;
            value = v.GetString() ?? "";
            if (value.Length > 4096 || value.Any(c => c is '\r' or '\n'))
                return false;
            found = true;
        }
        return !required || (found && !string.IsNullOrWhiteSpace(value));
    }

    private static bool ContainsExactId(string header, string rfcMessageId)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(rfcMessageId) ||
            !rfcMessageId.StartsWith('<') || !rfcMessageId.EndsWith('>'))
            return false;
        return header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Trim(',', ';').Equals(rfcMessageId, StringComparison.Ordinal));
    }

    private static bool ContainsRecipient(string header, string mailbox)
    {
        if (string.IsNullOrWhiteSpace(header) || mailbox.Length > 254)
            return false;
        try
        {
            var addresses = new MailAddressCollection();
            addresses.Add(header);
            return addresses.Count is > 0 and <= 20 &&
                addresses.Any(address => address.Address.Equals(mailbox, StringComparison.OrdinalIgnoreCase));
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

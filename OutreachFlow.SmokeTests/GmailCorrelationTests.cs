using System.Text;
using System.Text.Json;
using OutreachFlow.Services.Mailbox;

internal static class GmailCorrelationTests
{
    public static Task RunAsync()
    {
        var anchor = new GmailSentAnchor(10, "Buyer Co", "buyer@example.org",
            "owner@example.org", "thread-good", "<sent-1@outreachflow.local>",
            DateTime.UtcNow.AddMinutes(-10));
        var other = anchor with { ProspectId = 11, ThreadId = "thread-other" };
        Check(GmailReplyCorrelation.UniqueThreads(new[] { anchor, other }).Count == 2,
            "unique provider-confirmed threads remain independently eligible");
        Check(GmailReplyCorrelation.UniqueThreads(new[] { anchor, anchor with { ProspectId = 12 } }).Count == 0,
            "shared thread IDs across prospects are ambiguous");

        using var valid = Fixture(anchor);
        Check(GmailReplyCorrelation.Match("reply-1", valid.RootElement, anchor)?.ProspectId == 10,
            "genuine Gmail thread with matching reply headers maps to the sent prospect");
        using var unrelated = Fixture(anchor, reference: "<unrelated@elsewhere.org>");
        Check(GmailReplyCorrelation.Match("reply-1", unrelated.RootElement, anchor) is null,
            "unrelated email in a matching thread is rejected");
        using var wrongThread = Fixture(anchor, thread: "thread-other");
        Check(GmailReplyCorrelation.Match("reply-1", wrongThread.RootElement, anchor) is null,
            "unrelated Gmail thread is rejected");
        using var wrongSender = Fixture(anchor, from: "attacker@example.org");
        Check(GmailReplyCorrelation.Match("reply-1", wrongSender.RootElement, anchor) is null,
            "mismatched sender is rejected");
        using var forwarded = Fixture(anchor, to: "thirdparty@example.org");
        Check(GmailReplyCorrelation.Match("reply-1", forwarded.RootElement, anchor) is null,
            "forwards to another mailbox are rejected");
        using var old = Fixture(anchor, millis: new DateTimeOffset(anchor.ApprovedUtc.AddHours(-1)).ToUnixTimeMilliseconds());
        Check(GmailReplyCorrelation.Match("reply-1", old.RootElement, anchor) is null,
            "messages preceding sent inquiry are rejected");
        using var snippetsOnly = Fixture(anchor, hasBody: false);
        Check(GmailReplyCorrelation.Match("reply-1", snippetsOnly.RootElement, anchor)?.Body == "",
            "untrusted Gmail snippet cannot be treated as full body");
        using var duplicatedHeaders = Fixture(anchor, duplicateReplyHeader: true);
        Check(GmailReplyCorrelation.Match("reply-1", duplicatedHeaders.RootElement, anchor) is null,
            "duplicate reply headers are ambiguous");
        using var wrongSuffix = Fixture(anchor, reference: "<sent-1@outreachflow.local.evil>");
        Check(GmailReplyCorrelation.Match("reply-1", wrongSuffix.RootElement, anchor) is null,
            "reply identity requires an exact RFC message ID");
        return Task.CompletedTask;
    }

    private static JsonDocument Fixture(GmailSentAnchor a, string? thread = null,
        string? from = null, string? to = null, string? reference = null,
        long? millis = null, bool hasBody = true, bool duplicateReplyHeader = false)
    {
        var headers = new List<object> {
            new { name = "From", value = from ?? a.ContactEmail },
            new { name = "To", value = to ?? a.MailboxEmail },
            new { name = "Subject", value = "Re: Testing" },
            new { name = "In-Reply-To", value = reference ?? a.RfcMessageId }
        };
        if (duplicateReplyHeader)
            headers.Add(new { name = "In-Reply-To", value = "<other@example.org>" });
        return JsonDocument.Parse(JsonSerializer.Serialize(new {
            id = "reply-1", threadId = thread ?? a.ThreadId,
            internalDate = (millis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString(),
            payload = new {
                headers, mimeType = "text/plain",
                body = hasBody ? new { data = GmailMailboxService.Base64Url(
                    Encoding.UTF8.GetBytes("Confirmed interest.")) } : null
            },
            snippet = "Do not import this as text."
        }));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }
}

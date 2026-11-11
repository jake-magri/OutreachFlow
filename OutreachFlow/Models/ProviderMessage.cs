namespace OutreachFlow.Models;

// Gmail identity belongs to a mailbox address, which survives OAuth reconnects.
// Historical imports without a verified mailbox/thread remain in the activity log.
public sealed class ProviderMessage
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int ProspectId { get; set; }
    public int OutreachMessageId { get; set; }
    public OutreachMessage Message { get; set; } = null!;
    public string Provider { get; set; } = "gmail";
    public string MailboxEmail { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string ThreadId { get; set; } = "";
}

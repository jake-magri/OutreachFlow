namespace OutreachFlow.Models;

/// <summary>
/// Durable, owner-approved email or manual-handoff step. "Sending" or "Uncertain"
/// must NEVER be retried automatically after a crash or network failure.
/// </summary>
public sealed class FollowupJob
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int ProspectId { get; set; }
    public int Step { get; set; }
    public string Kind { get; set; } = "Email"; // Email / ManualHandoff
    public string State { get; set; } = "Queued"; // Queued/Sending/Sent/Held/Uncertain/Cancelled
    public DateTime DueUtc { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string ReplyToMessageId { get; set; } = "";
    public string RfcMessageId { get; set; } = "";
    public string ApprovedByUserId { get; set; } = "";
    public DateTime ApprovedUtc { get; set; }
    public DateTime? AttemptedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Result { get; set; } = "";
}

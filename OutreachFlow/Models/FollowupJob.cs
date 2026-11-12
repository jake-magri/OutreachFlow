namespace OutreachFlow.Models;

/// <summary>
/// Owner-approved, single-use scheduled Gmail follow-up. A claimed job is never
/// automatically reclaimed after restart: Gmail could have accepted its request.
/// This sacrifices liveness rather than risking duplicate customer contact.
/// </summary>
public sealed class FollowupJob
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int ProspectId { get; set; }
    public int Step { get; set; } // 1 or 2
    public string State { get; set; } = "Queued"; // Queued, Sending, Sent, Uncertain, Rejected, Cancelled, ReviewRequired
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public string RfcMessageId { get; set; } = "";
    public string ApprovedByUserId { get; set; } = "";
    public string PolicySnapshot { get; set; } = "";
    public DateTime ApprovedUtc { get; set; }
    public DateTime DueUtc { get; set; }
    public DateTime? ClaimedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string ProviderMessageId { get; set; } = "";
    public string Result { get; set; } = "";
}

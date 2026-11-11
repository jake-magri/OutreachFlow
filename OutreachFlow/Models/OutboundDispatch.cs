namespace OutreachFlow.Models;

// One immutable, owner-approved initial inquiry per prospect. No background retries.
public sealed class OutboundDispatch
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int ProspectId { get; set; }
    public string State { get; set; } = "Sending";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string ResearchSnapshot { get; set; } = "";
    public string PolicySnapshot { get; set; } = "";
    public string ApprovalHash { get; set; } = "";
    public string ApprovedByUserId { get; set; } = "";
    public DateTime ApprovedUtc { get; set; }
    public string RfcMessageId { get; set; } = "";
    public string Result { get; set; } = "Delivery has not yet been confirmed. Check Gmail before any further outreach.";
}

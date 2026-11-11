namespace OutreachFlow.Models;

public sealed class Workspace
{
    public int Id { get; set; }
    // Generic first-run defaults: each workspace owner supplies their own identifying data.
    // Changing model defaults never rewrites previously persisted SQLite workspace rows.
    public string BusinessName { get; set; } = "New workspace";
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderPhone { get; set; } = "";
    public string PostalAddress { get; set; } = ""; // Must be configured before live commercial email.
    public string ServiceDescription { get; set; } = "";
    public string ServiceArea { get; set; } = "";
    public string TargetDescription { get; set; } = "";
    public int MaxFollowups { get; set; } = 2;
    public int FirstFollowupBusinessDays { get; set; } = 5;
    public int SecondFollowupBusinessDays { get; set; } = 7;
    public int NoResponseAfterBusinessDays { get; set; } = 5;
    public int DailySendLimit { get; set; } = 10;
    public int SendingStartHour { get; set; } = 9;
    public int SendingEndHour { get; set; } = 17;
    public string TimeZoneId { get; set; } = "America/New_York";
    public string InitialSubjectTemplate { get; set; } = "Business supplier application inquiry";
    public string InitialBodyTemplate { get; set; } = "Hi [team],\n\nI run [business], a [service] business serving [area]. How can I apply or register to be considered as a vendor for [company]?\n\nIs there a form or person I should contact?\n\nThanks,\n[sender]";
    public string FollowupOneTemplate { get; set; } = "Hi, checking back on my question about the process for being considered as a [service] vendor for [company]. Is there a form or a more appropriate contact?\n\nThanks,\n[sender]";
    public string FollowupTwoTemplate { get; set; } = "Hi, one final follow-up on how to apply or register for [service] opportunities with [company]. If there's a supplier portal or another contact I should use, I'd appreciate the direction.\n\nThanks,\n[sender]";
}

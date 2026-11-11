namespace OutreachFlow.Models;

public enum ResearchOutcome
{
    NotReviewed,
    ApplicationPortal,
    EmailInstructions,
    NoProcessFound,
    NotEligible,
    ReviewNeeded
}

public enum ProspectStatus
{
    ResearchNeeded,
    ApplicationTask,
    ReadyToInquire,
    DraftReady,
    AwaitingReply,
    WarmLead,
    NeedsAction,
    ManualNoResponse,
    Closed,
    Suppressed
}

public enum ReplyCategory
{
    Interested,
    ApplicationInstructions,
    HasQuestion,
    Declined,
    OutOfOffice,
    Unclear
}

public sealed class Prospect
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; } = 1;
    public string CompanyName { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string Website { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public ResearchOutcome ResearchOutcome { get; set; } = ResearchOutcome.NotReviewed;
    public ProspectStatus Status { get; set; } = ProspectStatus.ResearchNeeded;
    public string ResearchNotes { get; set; } = "";
    public string ProcessUrl { get; set; } = "";
    public string DraftSubject { get; set; } = "";
    public string DraftBody { get; set; } = "";
    public int Attempts { get; set; }
    // The current dry-run sequence captures its policy at the time it begins.
    public int PlannedFollowups { get; set; }
    public int FirstFollowupDelayDays { get; set; }
    public int SecondFollowupDelayDays { get; set; }
    public int NoResponseDelayDays { get; set; }
    public string SequenceTimeZoneId { get; set; } = "America/New_York";
    public int SequenceStartHour { get; set; } = 9;
    public DateTime? NextStepDueUtc { get; set; }
    public bool AutomationPaused { get; set; }
    public string OwnerNotes { get; set; } = "";
    public string NextAction { get; set; } = "";
    // Date only, in the owner's local calendar (not an instant or UTC).
    public DateOnly? NextActionDate { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ResearchEvidence
{
    public int Id { get; set; }
    public int ProspectId { get; set; }
    public string SourceTitle { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string ExcerptOrObservation { get; set; } = "";
    public bool IsOfficial { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OutreachMessage
{
    public int Id { get; set; }
    public int ProspectId { get; set; }
    public string Direction { get; set; } = "Outbound";
    public string Classification { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
    public bool IsSimulation { get; set; } = true;
}

public sealed class ActivityEntry
{
    public int Id { get; set; }
    public int ProspectId { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}

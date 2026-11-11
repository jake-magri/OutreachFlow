namespace OutreachFlow.Services;

public interface IOutboundEmail
{
    Task<EmailDispatchResult> SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed record EmailDispatchResult(bool Sent, string Detail);

// This intentionally NEVER connects to a mailbox and NEVER transmits email.
public sealed class DryRunEmail : IOutboundEmail
{
    public Task<EmailDispatchResult> SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        => Task.FromResult(new EmailDispatchResult(false, "Dry run: message recorded locally; zero email transmitted."));
}

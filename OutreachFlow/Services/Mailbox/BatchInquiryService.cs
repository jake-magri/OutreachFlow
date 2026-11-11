using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services.Mailbox;

public enum BatchInquiryMode { Simulated, Gmail }
public sealed record BatchInquiryLine(string Company, string To, string Subject, string Body);
public sealed record BatchInquiryPreview(string From, IReadOnlyList<BatchInquiryLine> Lines, string Hash);
public sealed record BatchInquiryOutcome(string Company, int? ProspectId, string Result);
public sealed record BatchInquiryResult(IReadOnlyList<BatchInquiryOutcome> Outcomes)
{
    public int Sent => Outcomes.Count(x => x.Result == "Gmail accepted");
    public int Simulated => Outcomes.Count(x => x.Result == "Simulated");
    public int Held => Outcomes.Count(x => x.Result.StartsWith("Held:", StringComparison.Ordinal));
    public int Skipped => Outcomes.Count(x => x.Result.StartsWith("Skipped:", StringComparison.Ordinal));
}

/// <summary>
/// One deliberately submitted batch: create prospects, prepare drafts, then dispatch
/// via the existing durable, owner-approved Gmail outbox or an offline simulator.
/// No scheduler, implicit retry, or automatic verification of a company's website.
/// </summary>
public sealed class BatchInquiryService(
    IDbContextFactory<OutreachDbContext> factory,
    IDbContextFactory<AccountDbContext> accounts,
    OutreachService flow, ApprovedSendService sender, IGmailSender gmail)
{
    private sealed record Row(string Company, string Site, string Website, string Email);

    public async Task<BatchInquiryPreview> PreviewAsync(ClaimsPrincipal user, string input,
        CancellationToken ct = default)
    {
        var rows = Parse(input);
        await using var auth = await accounts.CreateDbContextAsync(ct);
        var workspaceId = await MailboxOwner.RequireAsync(user, auth, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var w = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId, ct);
        var lines = rows.Select(r => new BatchInquiryLine(r.Company, r.Email,
            Render(w.InitialSubjectTemplate, w, r), Render(w.InitialBodyTemplate, w, r))).ToArray();
        if (string.IsNullOrWhiteSpace(w.SenderEmail) ||
            lines.Any(x => string.IsNullOrWhiteSpace(x.Subject) || string.IsNullOrWhiteSpace(x.Body)))
            throw new InvalidOperationException("Configure the sending identity and templates in Settings.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { WorkspaceId = workspaceId, w.SenderEmail, rows, lines }))));
        return new(w.SenderEmail, lines, hash);
    }

    public async Task<BatchInquiryResult> RunAsync(ClaimsPrincipal user, string input,
        BatchInquiryMode mode, bool approved, string? approvalHash = null, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode)) throw new InvalidOperationException("Invalid batch mode.");
        if (mode == BatchInquiryMode.Gmail && !approved)
            throw new InvalidOperationException("Live Gmail requires owner approval of the complete batch, recipients, official-process decisions and saved message template.");
        var rows = Parse(input);
        if (mode == BatchInquiryMode.Gmail)
        {
            var current = await PreviewAsync(user, input, ct);
            if (string.IsNullOrWhiteSpace(approvalHash) || current.Hash != approvalHash)
                throw new InvalidOperationException("The reviewed batch or message template changed. Review the exact messages again.");
        }
        await using var auth = await accounts.CreateDbContextAsync(ct);
        var workspaceId = await MailboxOwner.RequireAsync(user, auth, ct);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, ct);
            if (string.IsNullOrWhiteSpace(workspace.InitialBodyTemplate) ||
                string.IsNullOrWhiteSpace(workspace.InitialSubjectTemplate))
                throw new InvalidOperationException("Configure the inquiry subject and body in Settings before running a batch.");
            if (mode == BatchInquiryMode.Gmail)
            {
                if (!gmail.SendingAvailable)
                    throw new InvalidOperationException("Controlled Gmail sending is disabled. Connect the authorized sender and enable the local pilot, or use Simulation.");
                _ = await gmail.PrepareAsync(workspaceId, workspace.SenderEmail, ct);
            }
        }

        var outcomes = new List<BatchInquiryOutcome>();
        var created = new List<(Row Row, Prospect Prospect)>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.OpenConnectionAsync(ct);
            using var transaction = ((SqliteConnection)db.Database.GetDbConnection())
                .BeginTransaction(deferred: false);
            await using var enlisted = await db.Database.UseTransactionAsync(transaction, ct);
            var existing = await db.Prospects.AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId)
                .Select(p => new { p.CompanyName, p.SiteName, p.ContactEmail })
                .ToListAsync(ct);
            var keys = new HashSet<string>(existing.Select(p => Key(p.CompanyName, p.SiteName)),
                StringComparer.OrdinalIgnoreCase);
            // Strict recipient-wide duplicate prevention: never create a new send target
            // under a second company name, even when the old record is suppressed.
            var emails = new HashSet<string>(existing.Where(p => !string.IsNullOrWhiteSpace(p.ContactEmail))
                .Select(p => p.ContactEmail), StringComparer.OrdinalIgnoreCase);
            emails.UnionWith(await db.OutboundDispatches.AsNoTracking()
                .Where(d => d.WorkspaceId == workspaceId).Select(d => d.To).ToListAsync(ct));

            foreach (var row in rows)
            {
                if (!keys.Add(Key(row.Company, row.Site)) || !emails.Add(row.Email))
                {
                    outcomes.Add(new(row.Company, null, "Skipped: already recorded company/site or recipient"));
                    continue;
                }

                // Simulation uses a clearly labeled fixture decision. Gmail uses the
                // owner's explicit process/contact attestation; a crawler cannot safely
                // infer "NoProcessFound" from partial public HTML.
                var note = mode == BatchInquiryMode.Gmail
                    ? "Owner explicitly attested that official company instructions were checked, no applicable application-only route was identified, and this recipient/template were approved for this specific batch."
                    : "SIMULATION ONLY: test fixture; official company process and recipient not verified. No real email is authorized.";
                var prospect = new Prospect
                {
                    WorkspaceId = workspaceId, CompanyName = row.Company,
                    SiteName = row.Site, Website = row.Website, ContactEmail = row.Email,
                    ResearchOutcome = ResearchOutcome.NoProcessFound, ResearchNotes = note,
                    Status = ProspectStatus.ReadyToInquire
                };
                db.Prospects.Add(prospect);
                created.Add((row, prospect));
            }
            await db.SaveChangesAsync(ct);
            foreach (var (row, prospect) in created)
            {
                db.ResearchEvidence.Add(new ResearchEvidence
                {
                    ProspectId = prospect.Id, SourceTitle = "Owner-provided batch import",
                    SourceUrl = row.Website,
                    ExcerptOrObservation = prospect.ResearchNotes, IsOfficial = false
                });
                db.Activities.Add(new ActivityEntry
                {
                    ProspectId = prospect.Id,
                    Kind = mode == BatchInquiryMode.Gmail ? "Owner batch approved" : "SIMULATED batch import",
                    Detail = mode == BatchInquiryMode.Gmail
                        ? "Owner authorized only this specific, bounded initial inquiry batch. No follow-ups authorized."
                        : "Fixture only: test send is simulated and cannot be promoted to a real send."
                });
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var (row, prospect) in created)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var freshAuth = await accounts.CreateDbContextAsync(ct);
                if (await MailboxOwner.RequireAsync(user, freshAuth, ct) != workspaceId)
                    throw new UnauthorizedAccessException("Workspace changed during batch processing.");
                await flow.GenerateDraftAsync(prospect.Id);
                if (mode == BatchInquiryMode.Simulated)
                {
                    await flow.SimulateInitialSendAsync(prospect.Id);
                    outcomes.Add(new(row.Company, prospect.Id, "Simulated"));
                }
                else
                {
                    var snapshot = await sender.PreviewAsync(user, prospect.Id, ct);
                    var dispatch = await sender.SendAsync(user, snapshot, confirmed: true, ct);
                    outcomes.Add(new(row.Company, prospect.Id, dispatch.State == "Sent"
                        ? "Gmail accepted" : "Held: " + dispatch.State));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                // No automatic retry. An uncertain dispatch stays locked and must be
                // reconciled in the existing Gmail delivery record.
                outcomes.Add(new(row.Company, prospect.Id, "Held: " + ex.Message));
            }
        }
        return new(outcomes);
    }

    private static List<Row> Parse(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 1 or > 3)
            throw new InvalidOperationException("Provide one to three reviewed prospects per batch.");
        var rows = new List<Row>(lines.Length);
        foreach (var line in lines)
        {
            var parts = line.TrimEnd('\r').Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || parts[0].Length is < 2 or > 150 ||
                parts[1].Length > 150 || parts[2].Length > 2048 ||
                !Uri.TryCreate(parts[2], UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                parts[3].Length is < 3 or > 254 || parts[3].Any(char.IsControl) ||
                !MailAddress.TryCreate(parts[3], out var email) || email.Address != parts[3])
                throw new InvalidOperationException(
                    "Use Company | Site | https://company-website | valid business email for every row.");
            rows.Add(new(parts[0], parts[1], parts[2], parts[3]));
        }
        return rows;
    }

    private static string Render(string template, Workspace w, Row row) =>
        template.Replace("[business]", w.BusinessName, StringComparison.OrdinalIgnoreCase)
            .Replace("[service]", w.ServiceDescription, StringComparison.OrdinalIgnoreCase)
            .Replace("[area]", w.ServiceArea, StringComparison.OrdinalIgnoreCase)
            .Replace("[company]", row.Company, StringComparison.OrdinalIgnoreCase)
            .Replace("[sender]", w.SenderName, StringComparison.OrdinalIgnoreCase)
            .Replace("[team]", "team", StringComparison.OrdinalIgnoreCase);

    private static string Key(string company, string site) => company.Trim() + "|" + site.Trim();
}

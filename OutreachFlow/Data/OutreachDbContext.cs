using Microsoft.EntityFrameworkCore;
using OutreachFlow.Models;

namespace OutreachFlow.Data;

public sealed class OutreachDbContext(DbContextOptions<OutreachDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Prospect> Prospects => Set<Prospect>();
    public DbSet<ResearchEvidence> ResearchEvidence => Set<ResearchEvidence>();
    public DbSet<OutreachMessage> Messages => Set<OutreachMessage>();
    public DbSet<ActivityEntry> Activities => Set<ActivityEntry>();
    public DbSet<ProviderMessage> ProviderMessages => Set<ProviderMessage>();

    public DbSet<OutboundDispatch> OutboundDispatches => Set<OutboundDispatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Prospect>().HasIndex(p => new { p.WorkspaceId, p.CompanyName, p.SiteName });
        modelBuilder.Entity<Prospect>().HasIndex(p => new { p.WorkspaceId, p.NextStepDueUtc });
        modelBuilder.Entity<ResearchEvidence>().HasIndex(e => e.ProspectId);
        modelBuilder.Entity<OutreachMessage>().HasIndex(m => new { m.ProspectId, m.OccurredUtc });
        modelBuilder.Entity<ActivityEntry>().HasIndex(a => new { a.ProspectId, a.OccurredUtc });
        var provider = modelBuilder.Entity<ProviderMessage>();
        provider.Property(m => m.MailboxEmail).UseCollation("NOCASE");
        provider.HasIndex(m => new { m.WorkspaceId, m.Provider, m.MailboxEmail, m.MessageId })
            .IsUnique();
        provider.HasIndex(m => m.OutreachMessageId).IsUnique();
        provider.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);
        provider.HasOne<Prospect>().WithMany()
            .HasForeignKey(m => new { m.WorkspaceId, m.ProspectId })
            .HasPrincipalKey(p => new { p.WorkspaceId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        provider.HasOne(m => m.Message).WithMany()
            .HasForeignKey(m => new { m.ProspectId, m.OutreachMessageId })
            .HasPrincipalKey(m => new { m.ProspectId, m.Id }).OnDelete(DeleteBehavior.Restrict);
        provider.ToTable(t => t.HasCheckConstraint("CK_ProviderMessages_Identity",
            "\"Provider\" = 'gmail' AND length(trim(\"MailboxEmail\")) > 0 " +
            "AND length(trim(\"MessageId\")) > 0 AND length(trim(\"ThreadId\")) > 0"));
        var dispatch = modelBuilder.Entity<OutboundDispatch>();
        dispatch.HasIndex(d => d.ProspectId).IsUnique();
        dispatch.HasIndex(d => d.RfcMessageId).IsUnique();
        dispatch.Property(d => d.From).UseCollation("NOCASE");
        dispatch.HasOne<Prospect>().WithMany().HasForeignKey(d => new { d.WorkspaceId, d.ProspectId })
            .HasPrincipalKey(p => new { p.WorkspaceId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        dispatch.ToTable(t => t.HasCheckConstraint("CK_OutboundDispatch_State",
            "\"State\" IN ('Sending','Sent','Uncertain','Rejected')"));
        base.OnModelCreating(modelBuilder);
    }
}

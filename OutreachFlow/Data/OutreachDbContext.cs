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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Prospect>().HasIndex(p => new { p.WorkspaceId, p.CompanyName, p.SiteName });
        modelBuilder.Entity<Prospect>().HasIndex(p => new { p.WorkspaceId, p.NextStepDueUtc });
        modelBuilder.Entity<ResearchEvidence>().HasIndex(e => e.ProspectId);
        modelBuilder.Entity<OutreachMessage>().HasIndex(m => new { m.ProspectId, m.OccurredUtc });
        modelBuilder.Entity<ActivityEntry>().HasIndex(a => new { a.ProspectId, a.OccurredUtc });
        base.OnModelCreating(modelBuilder);
    }
}

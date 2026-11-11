using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Models;

namespace OutreachFlow.Data;

// Separate database preserves the prototype's existing SQLite business schema.
public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WorkspaceMembership> Memberships => Set<WorkspaceMembership>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<WorkspaceMembership>()
            .HasIndex(m => new { m.UserId, m.WorkspaceId }).IsUnique();
        builder.Entity<WorkspaceMembership>()
            .HasIndex(m => m.WorkspaceId);
        builder.Entity<WorkspaceMembership>()
            .Property(m => m.Role).HasMaxLength(16);
    }
}

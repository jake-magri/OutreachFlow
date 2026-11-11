using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OutreachFlow.Data;

/// <summary>
/// Design-time factory for reproducible EF migrations. Never points tooling
/// at the operator's real SQLite files. Only the explicit migration tooling
/// uses this factory; the running app retains its configured connections.
/// </summary>
public sealed class OutreachDbContextFactory : IDesignTimeDbContextFactory<OutreachDbContext>
{
    public OutreachDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<OutreachDbContext>();
        builder.UseSqlite("Data Source=:memory:");
        return new OutreachDbContext(builder.Options);
    }
}

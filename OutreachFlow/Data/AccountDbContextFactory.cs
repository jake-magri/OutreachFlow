using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OutreachFlow.Data;

/// <summary>
/// Schema scaffolding is disconnected from local production-like Identity data.
/// Both context migrations are maintained separately.
/// </summary>
public sealed class AccountDbContextFactory : IDesignTimeDbContextFactory<AccountDbContext>
{
    public AccountDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AccountDbContext>();
        builder.UseSqlite("Data Source=:memory:");
        return new AccountDbContext(builder.Options);
    }
}

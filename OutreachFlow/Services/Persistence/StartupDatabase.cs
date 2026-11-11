using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace OutreachFlow.Services.Persistence;

public static class StartupDatabase
{
    public static async Task InitializeAsync(DbContext context, CancellationToken ct = default)
    {
        var database = context.Database;
        var migrations = database.GetMigrations().ToArray();
        var contextName = context.GetType().Name;
        if (migrations.Length == 0)
            throw new InvalidOperationException($"No compiled migrations were found for {contextName}.");

        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct) || !await creator.HasTablesAsync(ct))
        {
            await database.MigrateAsync(ct);
            return;
        }

        var applied = (await database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.SequenceEqual(migrations, StringComparer.Ordinal))
            return;

        // Existing EnsureCreated installations can run against the baseline schema
        // until the operator explicitly adopts it through the recovery CLI.
        if (applied.Length == 0 && migrations.Length == 1)
            return;

        throw new InvalidOperationException(
            $"Migration history for {contextName} differs from the compiled migrations. " +
            "Stop the app and run the recovery CLI upgrade command. See MIGRATION-BASELINE.md.");
    }
}
